using System.Reflection;
using Rukari.SpineSupport.Spines;
using BepInEx.Configuration;
using HarmonyLib;
using Spine;
using Spine.Unity;
using Studio.Scripts.Window.EmotionExplorer;

namespace Rukari.SpineSupport.Runtime;

/// <summary>Extends the official selector and playback paths without changing saved animation IDs.</summary>
internal static class SpineLobbySupportPatch
{
    private static readonly Dictionary<int, OwnedCompanion> Companions = new();
    private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);
    private static readonly HashSet<string> ExpandedAssets = new(StringComparer.Ordinal);
    private static int _mainThreadId;
    private static bool _installed;
    private static bool _fitThumbnails;
    private static bool _smoothTransitions;
    private static float _defaultMix;
    private static float _endToIdleMix;
    private static bool _muteAudioEvents;
    private static bool _expandAnimationList;

    /// <summary>Whether the official selector list is widened for every asset (see the config key).
    /// Read by the plugin's startup receipt so the log states what is actually on.</summary>
    internal static bool AnimationListExpanded => _expandAnimationList;

    public static bool Install(ConfigFile config)
    {
        if (_installed)
        {
            return true;
        }

        if (!config.Bind("SpineLobby", "Enabled", true,
                "Show existing lobby animations in the official selector and pair their M/A tracks. Restart required.").Value)
        {
            return false;
        }

        _mainThreadId = Environment.CurrentManagedThreadId;
        _muteAudioEvents = config.Bind("LobbyAudio", "MuteSpineAudioEvents", true,
            "Imported lobbies carry the source game's audio events, whose .wav/.ogg files nobody ships; blank their "
            + "audio path so the official resolver stops looking for them. Restart required.").Value;
        // Spine blends every SetAnimation by AnimationStateData.DefaultMix, and AA leaves that at zero, so lobby
        // animations cut into each other. These two numbers put the blend back, and only for lobby assets.
        _smoothTransitions = config.Bind("Playback", "SmoothTransitions", true,
            "Blend between a lobby's animations instead of cutting to the next one. AA leaves spine's default mix at "
            + "zero, so every change — the next line's Talk, a reaction, the entrance, the return to idle — is a hard "
            + "cut. Only lobby assets are touched. Restart required.").Value;
        _defaultMix = config.Bind("Playback", "TransitionMixSeconds", 0.2f,
            "Seconds spent blending into the next lobby animation. Keep it at or below 0.25: the entrance advance in "
            + "this module waits for the animation to play out, and a longer blend reads as two fades. Restart required.").Value;
        _endToIdleMix = config.Bind("Playback", "EndToIdleMixSeconds", 0.35f,
            "Seconds spent blending back into Idle_01 after a reaction or the entrance — the one transition the eye "
            + "notices most. Restart required.").Value;
        _fitThumbnails = config.Bind("SpineLobby", "FitLobbyThumbnails", true,
            "Fit lobby previews into the official cards using exported bounds. Does not hard-clip animated geometry. Restart required.").Value;
        // 2026-09-19: the official selector filters animation names down to its own numbered shape
        // ("^(?:S\d_)?\d\d$"), so anything a character author names descriptively — an arm gesture, a
        // prop, an overlay layer — is invisible and unselectable. This used to be turned on only for
        // assets recognised as memory lobbies, which meant a normal story character could never be
        // given a non-numeric animation. Seeing the full list changes nothing about playback: the
        // lobby behaviours (M/A pairing, one-shot reactions, mix, audio muting) stay behind IsLobby.
        _expandAnimationList = config.Bind("SpineSelector", "ExpandAnimationList", true,
            "Show every animation the selected skeleton actually has in the official selector, for story "
            + "characters as well as lobbies, once per asset per session. Only the list is widened; no "
            + "playback behaviour changes. Restart required.").Value;
        var harmony = new Harmony(Plugin.Guid + ".spine-lobby");
        try
        {
            // OpenEmotionSelector inlines the three-argument overload and calls Init() directly.
            MethodInfo selector = RequireMethod(typeof(EmotionExplorer), "Init", Type.EmptyTypes);
            MethodInfo character = RequireMethod(typeof(Character), "TrySetAnimation",
                new[] { typeof(string), typeof(int), typeof(bool) });
            MethodInfo tray = RequireMethod(typeof(CharacterTray), "Init",
                new[] { typeof(SkeletonDataAsset), typeof(string), typeof(EmotionExplorer), typeof(int), typeof(bool) });
            harmony.Patch(selector, prefix: new HarmonyMethod(typeof(SpineLobbySupportPatch), nameof(SelectorPrefix)));
            harmony.Patch(character, postfix: new HarmonyMethod(typeof(SpineLobbySupportPatch), nameof(CharacterPostfix)));
            harmony.Patch(tray, postfix: new HarmonyMethod(typeof(SpineLobbySupportPatch), nameof(TrayPostfix)));
            _installed = true;
            // The resolver guard is separate from the above: it answers callers that never look at the loaded
            // skeleton (the editor's resource scan included), so it must be in place even when the selector is not.
            LobbyBundledAudioPatch.Install(config);
            Plugin.Logger.LogInfo(
                "Spine lobby candidate installed: EmotionExplorer.Init() actual native call site; existing M/A pairs; one-shot entrance/talk; live acceptance pending.");
            return true;
        }
        catch (Exception ex)
        {
            harmony.UnpatchSelf();
            ReportFailure("install", ex);
            return false;
        }
    }

    public static void SelectorPrefix(EmotionExplorer __instance)
    {
        ReportOnce("selector-entry", $"Spine lobby selector callback reached; thread={Environment.CurrentManagedThreadId}; expectedThread={_mainThreadId}.");
        if (!IsMainThread()) return;
        try
        {
            Script.CharacterRecord record = __instance.record;
            if (ReferenceEquals(record, null) || string.IsNullOrEmpty(record.name))
            {
                ReportOnce("selector-no-record", "Spine lobby selector skipped: no current character record/name.", warning: true);
                return;
            }
            string identifier = record.name;
            CharacterManager manager = CharacterManager.Instance;
            if (ReferenceEquals(manager, null))
            {
                ReportOnce("selector-no-manager", "Spine lobby selector skipped: character manager unavailable.", warning: true);
                return;
            }
            SkeletonDataAsset asset = manager.GetSkeletonDataAsset(identifier, false);
            if (ReferenceEquals(asset, null))
            {
                ReportOnce("selector-no-asset", "Spine lobby selector skipped: current character asset unavailable.", warning: true);
                return;
            }
            SkeletonData data = asset.GetSkeletonData(true);
            if (ReferenceEquals(data, null))
            {
                ReportOnce("selector-no-data", "Spine lobby selector skipped: skeleton data unavailable.", warning: true);
                return;
            }
            bool isLobby = IsLobby(data);
            if (isLobby && _muteAudioEvents) LobbyAudioEvents.Mute(data);
            string[] names = SnapshotNames(data);
            ReportOnce("catalog-" + identifier,
                $"Spine selector catalog: identifier={identifier}; isLobby={isLobby}; animations={names.Length}; names={string.Join(",", names.Take(64))}.");
            if (!_expandAnimationList || ExpandedAssets.Contains(identifier)) return;
            UIToggle toggle = __instance.toggleShowAll;
            if (ReferenceEquals(toggle, null)) return;

            // Init() also runs when the user changes Show All. Auto-expand each asset only once
            // per session, so later manual changes are respected and no recursive Init is invoked.
            // 2026-09-19: no longer restricted to lobby assets — a story character's own list is
            // exactly as unreadable without this, and widening the list is not a behaviour change.
            toggle.Set(true, false);
            if (toggle.value)
            {
                ExpandedAssets.Add(identifier);
                ReportOnce("selector-show-all",
                    $"Spine selector: official Show All enabled and read back true; identifier={identifier}; "
                    + $"isLobby={isLobby}; animations={names.Length}.");
            }
            else
            {
                ReportOnce("toggle-rejected",
                    $"Spine selector: Show All read-back stayed false; identifier={identifier}; isLobby={isLobby}.",
                    warning: true);
            }
        }
        catch (Exception ex)
        {
            ReportFailure("selector", ex);
        }
    }

    public static void CharacterPostfix(Character __instance, string animationID, int layer, bool loop, bool __result)
    {
        // Native AdvanceScenario sends the selected face ID to track 2. Track 4 belongs to blinking.
        if (!__result || layer != 2 || !IsMainThread()) return;
        try
        {
            Apply(__instance.anim, animationID, layer, loop, rememberCompanion: true);
        }
        catch (Exception ex)
        {
            ReportFailure("character", ex);
        }
    }

    public static void TrayPostfix(CharacterTray __instance, string spineAnim)
    {
        if (!IsMainThread()) return;
        ReportOnce("thumbnail-entry", "Spine lobby thumbnail callback reached.");
        if (_fitThumbnails) SpineLobbyThumbnailFitPatch.Apply(__instance);
        try
        {
            // Native Init recreates the thumbnail AnimationState, puts Idle on 0 and selection on 1.
            Apply(__instance.anim, spineAnim, 1, requestedLoop: true, rememberCompanion: false);
        }
        catch (Exception ex)
        {
            ReportFailure("thumbnail", ex);
        }
    }

    /// <summary>
    /// Crossfade into the next animation instead of cutting to it. The pair for this exact transition is set
    /// explicitly; <c>DefaultMix</c> covers the changes AA starts on its own, the companion track included, so both
    /// tracks of an M/A pair blend for the same length and stay in step.
    /// </summary>
    private static void ApplyMix(AnimationState state, SkeletonData data, string next)
    {
        try
        {
            AnimationStateData? mixer = state.Data;
            if (ReferenceEquals(mixer, null) || string.IsNullOrEmpty(next)) return;
            mixer.DefaultMix = _defaultMix;
            TrackEntry? current = state.GetCurrent(2);
            string playing = ReferenceEquals(current, null) ? string.Empty : current!.Animation?.Name ?? string.Empty;
            float seconds = LobbyTransitionMixPolicy.MixFor(playing, next, _defaultMix, _endToIdleMix);
            if (seconds <= 0f) return;
            Animation? from = playing.Length == 0 ? null : data.FindAnimation(playing);
            Animation? to = data.FindAnimation(next);
            if (ReferenceEquals(from, null) || ReferenceEquals(to, null)) return;
            mixer.SetMix(from, to, seconds);
        }
        catch (Exception ex)
        {
            ReportFailure("mix", ex);
        }
    }

    private static void Apply(SkeletonAnimation anim, string selectedName, int mainLayer,
        bool requestedLoop, bool rememberCompanion)
    {
        if (ReferenceEquals(anim, null)) return;
        AnimationState state = anim.AnimationState;
        if (ReferenceEquals(state, null)) return;
        int instanceId = rememberCompanion ? anim.GetInstanceID() : 0;
        if (rememberCompanion) ReleaseCompanion(instanceId, state, mainLayer + 1);
        SkeletonData data = state.Data.SkeletonData;
        if (ReferenceEquals(data, null) || !IsLobby(data)) return;
        // Playback can happen without the selector ever opening, so the audio events are muted here too.
        if (_muteAudioEvents) LobbyAudioEvents.Mute(data);
        if (_smoothTransitions) ApplyMix(state, data, selectedName);
        SpineLobbyAnimationPlan? plan = SpineLobbyAnimationPolicy.BuildPlan(SnapshotNames(data), selectedName, requestedLoop);
        if (plan == null) return;

        TrackEntry primary = state.GetCurrent(mainLayer);
        if (ReferenceEquals(primary, null)
            || !string.Equals(primary.Animation?.Name, plan.PrimaryAnimationName, StringComparison.Ordinal))
        {
            primary = state.SetAnimation(mainLayer, plan.PrimaryAnimationName, plan.Loop);
        }
        else
        {
            primary.Loop = plan.Loop;
        }

        if (plan.CompanionAnimationName != null)
        {
            TrackEntry companion = state.SetAnimation(mainLayer + 1, plan.CompanionAnimationName, plan.Loop);
            IntPtr emptyPointer = plan.ReturnToIdle
                ? state.AddEmptyAnimation(mainLayer + 1, plan.FadeSeconds, 0f).Pointer
                : IntPtr.Zero;
            if (rememberCompanion)
            {
                // Keep scalar identities only, never native wrappers. A normal stage has five slots.
                if (Companions.Count >= 128) Companions.Remove(Companions.Keys.First());
                Companions[instanceId] = new OwnedCompanion(
                    state.Pointer, companion.Pointer, emptyPointer, plan.CompanionAnimationName);
            }
        }

        // Idle_01 stays on native track 0. Empty animation fades these overlays back to it.
        if (plan.ReturnToIdle) state.AddEmptyAnimation(mainLayer, plan.FadeSeconds, 0f);
        if (rememberCompanion && plan.AdvanceWhenFinished)
        {
            // The entrance is the one animation the playback should move on from; from here the watcher decides
            // that once it has played out.
            LobbyPlaybackWatcher.Current?.ArmEntrance(anim, mainLayer, plan.PrimaryAnimationName);
        }

        ReportOnce(rememberCompanion ? "playback" : "preview",
            $"Spine lobby {(rememberCompanion ? "playback" : "thumbnail")} rule applied; mainTrack={mainLayer}; companionTrack={mainLayer + 1}.");
    }

    private static void ReleaseCompanion(int instanceId, AnimationState state, int layer)
    {
        if (!Companions.Remove(instanceId, out OwnedCompanion? owned) || owned.StatePointer != state.Pointer) return;
        TrackEntry current = state.GetCurrent(layer);
        if (!ReferenceEquals(current, null)
            && ((current.Pointer == owned.EntryPointer
                    && string.Equals(current.Animation?.Name, owned.Name, StringComparison.Ordinal))
                || (owned.EmptyPointer != IntPtr.Zero && current.Pointer == owned.EmptyPointer)))
        {
            // Use an empty animation so attachment/bone timelines release back to lower tracks.
            state.SetEmptyAnimation(layer, 0f);
        }
    }

    private static bool IsLobby(SkeletonData data) =>
        !ReferenceEquals(data.FindAnimation("Idle_01"), null)
        && !ReferenceEquals(data.FindAnimation("Start_Idle_01"), null);

    private static string[] SnapshotNames(SkeletonData data)
    {
        var animations = data.Animations;
        int count = animations.Count;
        var items = animations.Items;
        var names = new string[count];
        for (int index = 0; index < count; index++) names[index] = items[index].Name;
        return names;
    }

    private static bool IsMainThread() => Environment.CurrentManagedThreadId == _mainThreadId;

    private static MethodInfo RequireMethod(Type type, string name, Type[] arguments) =>
        AccessTools.Method(type, name, arguments) ?? throw new MissingMethodException(type.FullName, name);

    private static void ReportFailure(string boundary, Exception ex) =>
        ReportOnce("error-" + boundary, $"Spine lobby {boundary} failed: {ex.GetType().Name}: {ex.Message}", warning: true);

    private static void ReportOnce(string key, string message, bool warning = false)
    {
        lock (Reported)
        {
            if (!Reported.Add(key)) return;
        }
        if (warning) Plugin.Logger.LogWarning(message);
        else Plugin.Logger.LogInfo(message);
    }

    private sealed record OwnedCompanion(IntPtr StatePointer, IntPtr EntryPointer, IntPtr EmptyPointer, string Name);
}
