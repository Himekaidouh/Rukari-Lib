extern alias unitycore;

using Rukari.SpineSupport.Spines;
using Spine;
using Spine.Unity;
using Camera = unitycore::UnityEngine.Camera;
using RectInt = unitycore::UnityEngine.RectInt;
using Vector3 = unitycore::UnityEngine.Vector3;

namespace Rukari.SpineSupport.Runtime;

/// <summary>
/// The two playback decisions this package adds on top of the official lobby behaviour: what happens when the
/// entrance animation has played out, and where the official touch wait should be.
///
/// <para>
/// Both are read-only observations of official state — the player's own step list, its character slots and their
/// Spine animation state. The single official object written is the touch wait's own <c>rect</c>, a property the
/// official parser fills from the author's <c>#touch</c> directive; this only moves that rectangle onto the
/// lobby's own touch marker. Nothing is created, moved, re-parented or patched here.
/// </para>
/// </summary>
internal sealed class LobbyPlaybackWatcher : unitycore::UnityEngine.MonoBehaviour
{
    /// <summary>Scanning the player costs a slot walk; three frames is far below any animation boundary.</summary>
    private const int ScanIntervalFrames = 3;

    /// <summary>Markers a lobby spine can carry, best first. Whatever the skeleton actually has wins.</summary>
    private static readonly string[] TouchMarkers =
    {
        "Touch_Point", "Touch_Eye", "Touch_Point_Key", "Touch_Eye_Key",
    };

    private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);

    private readonly List<ArmedEntrance> _armed = new();
    private int _nextScanFrame;
    private bool _advanceAfterEntrance = true;
    private bool _followTouchMarkers = true;
    private int _lastX = int.MinValue;
    private int _lastY;
    private int _lastWidth;
    private int _lastHeight;

    /// <summary>Set while the component is alive; the support patch arms entrances through it.</summary>
    internal static LobbyPlaybackWatcher? Current { get; private set; }

    /// <summary>The interop requires this constructor; the component is added by <see cref="Plugin"/>.</summary>
    public LobbyPlaybackWatcher(IntPtr pointer)
        : base(pointer)
    {
    }

    internal void Configure(bool advanceAfterEntrance, bool followTouchMarkers, float voiceStallTimeoutSeconds = 10f)
    {
        _advanceAfterEntrance = advanceAfterEntrance;
        _followTouchMarkers = followTouchMarkers;
        // Keep the old configuration/caller shape, but elapsed time alone no longer authorizes a voice skip.
        // A missing clip cannot safely be distinguished from a completed clip in its official AUTO wait.
        _ = voiceStallTimeoutSeconds;
    }

    private void Awake() => Current = this;

    /// <summary>
    /// Called when a lobby entrance has just been started on the playback path: from here on it is watched until
    /// it has played out.
    /// </summary>
    internal void ArmEntrance(SkeletonAnimation anim, int layer, string animationName)
    {
        if (ReferenceEquals(anim, null) || anim.WasCollected) return;
        AnimationState? state;
        IntPtr trackPointer;
        int instanceId;
        try
        {
            // Read once, while the skeleton is certainly alive: SkeletonAnimation.AnimationState re-initializes the
            // renderer, which throws once the object has been destroyed mid-playback.
            state = anim.AnimationState;
            if (state is null || state.WasCollected || state.Pointer == IntPtr.Zero) return;
            TrackEntry? entry = state.GetCurrent(layer);
            if (entry is null || entry.WasCollected || entry.Pointer == IntPtr.Zero
                || !string.Equals(entry.Animation?.Name, animationName, StringComparison.Ordinal)) return;
            trackPointer = entry.Pointer;
            instanceId = anim.GetInstanceID();
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"[lobby] entrance '{animationName}' cannot be watched: {ex.GetType().Name}.");
            return;
        }

        Test? player = Test.Instance;
        if (state is null || !IsAlive(player)) return;
        _armed.RemoveAll(armed => armed.InstanceId == instanceId && armed.Layer == layer);
        _armed.Add(new ArmedEntrance(state, instanceId, layer, trackPointer, animationName,
            player!.Pointer, player.cur, player.previewMode));
        Plugin.Logger.LogInfo(
            $"[lobby] entrance '{animationName}' armed on track {layer}; advanceWhenFinished={_advanceAfterEntrance}.");
    }

    public void Update()
    {
        try
        {
            WatchEntrances();
            WatchTouchAreas();
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"Lobby playback watcher stopped: {ex.GetType().Name}: {ex.Message}");
            enabled = false;
        }
    }

    /// <summary>
    /// An armed entrance has played out only while its exact track entry still holds that animation and has
    /// explicitly reached its end. A cleared/replaced track is dropped, not treated as a completion. The official
    /// player is then asked to move on — unless a voice is still attached to this
    /// line, in which case the official voice step is already responsible for advancing.
    ///
    /// <para>
    /// The animation state is captured when the entrance is armed, because reading
    /// <c>SkeletonAnimation.AnimationState</c> after the object is gone throws from inside spine-unity. Each armed
    /// entrance is also guarded on its own: a skeleton that dies mid-line drops its own watch instead of stopping
    /// the watcher for the whole session.
    /// </para>
    /// </summary>
    private void WatchEntrances()
    {
        if (_armed.Count == 0) return;

        for (int index = _armed.Count - 1; index >= 0; index--)
        {
            ArmedEntrance armed = _armed[index];
            Test? test = Test.Instance;
            if (!IsCurrentLine(test, armed))
            {
                // A late entrance must never advance a different line or a replacement player.
                _armed.RemoveAt(index);
                continue;
            }

            LobbyEntranceTrackDecision track;
            try
            {
                track = ObserveTrack(armed);
            }
            catch (Exception ex)
            {
                _armed.RemoveAt(index);
                Plugin.Logger.LogInfo(
                    $"[lobby] entrance '{armed.AnimationName}' watch dropped: the skeleton is gone ({ex.GetType().Name}).");
                continue;
            }

            if (track == LobbyEntranceTrackDecision.Playing) continue;
            _armed.RemoveAt(index);
            // Automatic transition to the queued empty track may win the frame. There is then no reliable
            // completion proof for this exact entry, so the official player keeps responsibility for advancing.
            if (track == LobbyEntranceTrackDecision.Completed) OnEntranceFinished(test!, armed);
        }
    }

    private static LobbyEntranceTrackDecision ObserveTrack(ArmedEntrance armed)
    {
        AnimationState state = armed.State;
        if (state.WasCollected || state.Pointer == IntPtr.Zero) return LobbyEntranceTrackDecision.Dropped;
        TrackEntry? current = state.GetCurrent(armed.Layer);
        if (current is null || current.WasCollected || current.Pointer == IntPtr.Zero)
            return LobbyEntranceTrackDecision.Dropped;
        if (current.Pointer != armed.TrackPointer) return LobbyEntranceTrackDecision.Dropped;
        Animation? animation = current.Animation;
        if (animation is null) return LobbyEntranceTrackDecision.Dropped;
        return LobbyEntranceAdvancePolicy.ObserveTrack(armed.TrackPointer, current.Pointer,
            armed.AnimationName, animation.Name, current.TrackTime, current.AnimationEnd);
    }

    private void OnEntranceFinished(Test test, ArmedEntrance armed)
    {
        bool hasVoice = test.hasVoice;
        // A native voice flag already leaves ownership with the official player. Read the shared getter only
        // when needed to cover a mod-owned voice/tail wait that is not represented by that flag.
        bool? voicePlaying = _advanceAfterEntrance && !hasVoice ? ReadVoiceWait() : null;
        LobbyEntranceAdvanceDecision decision = LobbyEntranceAdvancePolicy.Decide(
            _advanceAfterEntrance, IsCurrentLine(test, armed), hasVoice, voicePlaying);
        string snapshot =
            $"track={armed.Layer}; cur={test.cur}; preview={test.previewMode}; hasVoice={hasVoice}; voiceWait={voicePlaying}; locked={test.locked}; "
            + $"steps={(test.currentAnims is null ? -1 : test.currentAnims.Count)}; stopped={test.stoppedAnimCount}";
        if (decision != LobbyEntranceAdvanceDecision.Advance)
        {
            ReportOnce("entrance-" + decision,
                $"[lobby] entrance '{armed.AnimationName}' played out; not forcing advance ({decision}; {snapshot}).");
            return;
        }

        test.AdvanceScenario(test.previewMode, null);
        Plugin.Logger.LogInfo($"[lobby] entrance '{armed.AnimationName}' played out; AdvanceScenario called ({snapshot}).");
    }

    /// <summary>
    /// Uses the same getter as the official voice wait. CharacterVoice extends it through its post-playback
    /// delay, so reading it normally also respects that delay without a dependency on the voice mod.
    /// </summary>
    private static bool? ReadVoiceWait()
    {
        try
        {
            AudioManager? audio = AudioManager.Instance;
            if (audio is null || audio.WasCollected || audio.Pointer == IntPtr.Zero) return null;
            return audio.isVoicePlaying;
        }
        catch (Exception ex)
        {
            ReportOnce("voice-state-unavailable",
                $"[lobby] voice wait cannot be observed; entrance advance stays with the official player ({ex.GetType().Name}).");
            return null;
        }
    }

    private static bool IsCurrentLine(Test? test, ArmedEntrance armed) =>
        IsAlive(test) && test!.Pointer == armed.PlayerPointer
        && test.cur == armed.Cursor && test.previewMode == armed.Preview;

    /// <summary>
    /// Moves the official touch wait onto the lobby's own touch marker, so the rectangle follows the animation
    /// instead of the coordinates typed into the line. The authored width and height are kept as the size.
    /// </summary>
    private void WatchTouchAreas()
    {
        if (!_followTouchMarkers) return;
        if (unitycore::UnityEngine.Time.frameCount < _nextScanFrame) return;
        _nextScanFrame = unitycore::UnityEngine.Time.frameCount + ScanIntervalFrames;

        Test? test = Test.Instance;
        if (!IsAlive(test)) return;
        var steps = test!.currentAnims;
        if (steps is null || steps.Count == 0) return;

        for (int index = 0; index < steps.Count; index++)
        {
            ScenarioAnimation.ScenarioAnimation? step = steps[index];
            if (step is null || step.WasCollected || step.Pointer == IntPtr.Zero) continue;
            ScenarioAnimation.WaitForTouchTransition? wait = step.TryCast<ScenarioAnimation.WaitForTouchTransition>();
            if (wait is null) continue;
            try
            {
                ApplyMarkerRect(test, wait);
            }
            catch (Exception ex)
            {
                // A skeleton mid-teardown throws on member access; the rectangle then simply keeps its last value.
                ReportOnce("touch-failed",
                    $"[lobby] the touch rectangle could not follow the marker this time: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void ApplyMarkerRect(Test test, ScenarioAnimation.WaitForTouchTransition wait)
    {
        SkeletonAnimation? anim = FindLobbyAnimation(test);
        if (anim is null) return;
        if (!TryMarkerScreenPoint(anim, out string marker, out Vector3 screen)) return;

        RectInt authored = wait.rect;
        int screenWidth = unitycore::UnityEngine.Screen.width;
        int screenHeight = unitycore::UnityEngine.Screen.height;
        SpineLobbyTouchRect? computed = SpineLobbyTouchArea.Around(
            (int)Math.Round(screen.x),
            (int)Math.Round(screen.y),
            authored.width,
            authored.height,
            screenWidth,
            screenHeight);
        if (computed is null) return;

        SpineLobbyTouchRect rect = computed.Value;
        if (rect.X == _lastX && rect.Y == _lastY && rect.Width == _lastWidth && rect.Height == _lastHeight) return;
        _lastX = rect.X;
        _lastY = rect.Y;
        _lastWidth = rect.Width;
        _lastHeight = rect.Height;

        wait.rect = new RectInt(rect.X, rect.Y, rect.Width, rect.Height);
        Plugin.Logger.LogInfo(
            $"[lobby] touch rect from marker '{marker}' at screen=({screen.x:F0},{screen.y:F0}) "
            + $"-> rect={rect.X},{rect.Y} {rect.Width}x{rect.Height} (bottom-left origin); "
            + $"authored={authored.x},{authored.y} {authored.width}x{authored.height}; "
            + $"screen={screenWidth}x{screenHeight}; "
            + $"top-left equivalent y={screenHeight - rect.Y - rect.Height}.");
    }

    /// <summary>
    /// The lobby character currently on stage: a slot whose skeleton is a lobby and carries a marker. Slots are
    /// walked defensively — a skeleton that is being torn down throws on member access, and that must not cost the
    /// touch rectangle its update for the other characters.
    /// </summary>
    private static SkeletonAnimation? FindLobbyAnimation(Test test)
    {
        var slots = test.slots;
        if (slots is null) return null;
        for (int index = 0; index < slots.Length; index++)
        {
            Character? candidate = slots[index];
            if (candidate is null || candidate.WasCollected || candidate.Pointer == IntPtr.Zero) continue;
            try
            {
                SkeletonAnimation? anim = candidate.anim;
                if (anim is null || anim.WasCollected) continue;
                SkeletonData? data = anim.Skeleton?.Data;
                if (data is null) continue;
                if (data.FindAnimation("Idle_01") is null || data.FindAnimation("Start_Idle_01") is null) continue;
                if (FindMarker(data) is not null) return anim;
            }
            catch (Exception)
            {
                // A destroyed or half-initialised skeleton is simply not the lobby character.
            }
        }

        return null;
    }

    private static string? FindMarker(SkeletonData data)
    {
        foreach (string marker in TouchMarkers)
        {
            if (data.FindBone(marker) is not null || data.FindSlot(marker) is not null) return marker;
        }

        return null;
    }

    /// <summary>
    /// The marker's screen position. The skeleton's bones are animated in place, so the skeleton instance — not
    /// the setup data — is what is read, and the transform chain decides the rest.
    /// </summary>
    private static bool TryMarkerScreenPoint(SkeletonAnimation anim, out string marker, out Vector3 screen)
    {
        marker = "";
        screen = default;
        Skeleton? skeleton = anim.Skeleton;
        SkeletonData? data = skeleton?.Data;
        if (skeleton is null || data is null) return false;

        string? found = FindMarker(data);
        if (found is null) return false;
        // The skeleton's own bones carry the current pose; SkeletonData only has the setup pose.
        Bone? bone = skeleton.FindBone(found);
        if (bone is null)
        {
            Slot? slot = skeleton.FindSlot(found);
            bone = slot?.Bone;
        }

        if (bone is null) return false;

        Vector3 world = anim.transform.TransformPoint(new Vector3(bone.WorldX, bone.WorldY, 0f));
        Camera? camera = UICamera.mainCamera is not null ? UICamera.mainCamera : Camera.main;
        screen = camera is not null ? camera.WorldToScreenPoint(world) : world;
        if (!float.IsFinite(screen.x) || !float.IsFinite(screen.y)) return false;
        marker = found;
        return true;
    }

    private static bool IsAlive(Test? test) =>
        test is not null && !test.WasCollected && test.Pointer != IntPtr.Zero;

    private static void ReportOnce(string key, string message)
    {
        lock (Reported)
        {
            if (!Reported.Add(key)) return;
        }

        Plugin.Logger.LogInfo(message);
    }

    public void OnDestroy()
    {
        _armed.Clear();
        if (ReferenceEquals(Current, this)) Current = null;
    }

    private sealed record ArmedEntrance(AnimationState State, int InstanceId, int Layer, IntPtr TrackPointer, string AnimationName,
        IntPtr PlayerPointer, int Cursor, bool Preview);
}
