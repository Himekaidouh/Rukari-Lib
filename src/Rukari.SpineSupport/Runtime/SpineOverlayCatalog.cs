extern alias unitycore;

using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Rukari.SpineSupport.Spines;
using Spine;
using Spine.Unity;

namespace Rukari.SpineSupport.Runtime;

/// <summary>
/// One character on stage that actually has a Spine skeleton, addressed by the same public slot
/// number the AAVT character commands use (1-based; the engine's array is 0-based).
/// </summary>
internal sealed record SpineOverlayTarget(int PublicSlot, SkeletonAnimation Animation, SkeletonData Data, string Label);

/// <summary>One row of the overlay console: the verdict plus whether its timelines could be read.</summary>
internal sealed record SpineOverlayRow(string Name, SpineAnimationVerdict Verdict, bool TimelinesReadable);

/// <summary>
/// What one timeline drives, once its real class has been asked for
/// (see <see cref="SpineOverlayCatalog.Resolve"/>). <see cref="Index"/> is negative for a timeline
/// that keys no bone and no slot, which is a real answer for constraints and events.
/// </summary>
internal readonly record struct SpineTimelineTarget(
    bool Recognised,
    bool IsBone,
    int Index,
    bool KeysDrawOrder,
    bool KeysDeform)
{
    internal static readonly SpineTimelineTarget Unknown = new(false, true, -1, false, false);
    internal static readonly SpineTimelineTarget Ignored = new(true, true, -1, false, false);
    internal static readonly SpineTimelineTarget DrawOrderOnly = new(true, true, -1, true, false);
    internal static SpineTimelineTarget Bone(int index) => new(true, true, index, false, false);
    internal static SpineTimelineTarget Slot(int index) => new(true, false, index, false, false);
    internal static SpineTimelineTarget Deform(int slotIndex) => new(true, false, slotIndex, false, true);
}

/// <summary>What one catalog build saw besides the rows: the page shows verdicts, the log gets this.</summary>
internal sealed class SpineOverlayScan
{
    /// <summary>IL2CPP class names of timelines this build could not explain (capped, for the log).</summary>
    internal HashSet<string> UnknownTimelineTypes { get; } = new(StringComparer.Ordinal);

    /// <summary>Animations whose timeline list could not even be obtained; still listed by name.</summary>
    internal List<string> UnreadableAnimations { get; } = new();

    /// <summary>
    /// Animations whose <c>Animation.Timelines</c> came back null. Kept apart from an unknown
    /// timeline class on purpose: "the list is null" and "the list has types we do not know" are
    /// different failures, and the log has to say which one happened.
    /// </summary>
    internal List<string> NullTimelineLists { get; } = new();
}

/// <summary>
/// Reads a live skeleton's animation list and says what each animation is good for (2026-09-21).
/// <para>
/// Everything here is read-only and defensive: a skeleton that is being torn down throws on member
/// access, and one unreadable timeline must not cost the page its whole list. The targets are never
/// cached — the list is rebuilt from the skeleton that is on stage right now, which is the only
/// source that cannot disagree with what the game is actually playing. The verdict summary is
/// logged once per skeleton instead, because the shared drawer re-reads a page several times a
/// second and a log must not.
/// </para>
/// </summary>
internal static class SpineOverlayCatalog
{
    /// <summary>Pointer of the last skeleton-data object this module logged a summary for.</summary>
    private static IntPtr _reportedData;

    /// <summary>Every slot whose character currently has a readable Spine skeleton.</summary>
    internal static IReadOnlyList<SpineOverlayTarget> Targets()
    {
        var found = new List<SpineOverlayTarget>();
        Test? test;
        try
        {
            test = Test.Instance;
        }
        catch (Exception)
        {
            return found;
        }

        if (ReferenceEquals(test, null)) return found;
        Character?[]? slots;
        try
        {
            slots = test.slots;
        }
        catch (Exception)
        {
            return found;
        }

        if (slots is null) return found;
        for (int index = 0; index < slots.Length; index++)
        {
            Character? candidate = slots[index];
            if (candidate is null || candidate.WasCollected || candidate.Pointer == IntPtr.Zero) continue;
            try
            {
                SkeletonAnimation? animation = candidate.anim;
                if (animation is null || animation.WasCollected || animation.Pointer == IntPtr.Zero) continue;
                Skeleton? skeleton = animation.Skeleton;
                SkeletonData? data = skeleton?.Data;
                if (data is null) continue;
                string label = data.Name ?? string.Empty;
                found.Add(new SpineOverlayTarget(index + 1, animation, data, label.Length == 0 ? $"槽位{index + 1}" : label));
            }
            catch (Exception)
            {
                // A destroyed or half-initialised skeleton is simply not a target.
            }
        }

        return found;
    }

    /// <summary>Classified rows for one skeleton, base loop first so the clashes read against it.</summary>
    internal static IReadOnlyList<SpineOverlayRow> Rows(SkeletonData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var rows = new List<SpineOverlayRow>();
        var scan = new SpineOverlayScan();
        int totalBones;
        int totalSlots;
        IReadOnlyCollection<string> ikDriven;
        IntPtr pointer;
        try
        {
            pointer = data.Pointer;
            totalBones = data.Bones?.Count ?? 0;
            totalSlots = data.Slots?.Count ?? 0;
            ikDriven = IkDrivenBones(data);
        }
        catch (Exception)
        {
            return rows;
        }

        SpineAnimationKeyProfile? baseProfile = null;
        var profiles = new List<(string Name, SpineAnimationKeyProfile Profile, bool Readable)>();
        try
        {
            ExposedList<Animation>? animations = data.Animations;
            if (animations is null) return rows;
            for (int index = 0; index < animations.Count; index++)
            {
                Animation? animation = animations.Items[index];
                if (animation is null || animation.WasCollected) continue;
                string name = animation.Name ?? string.Empty;
                if (name.Length == 0) continue;
                (SpineAnimationKeyProfile profile, bool readable) = ReadProfile(animation, data, name, scan);
                profiles.Add((name, profile, readable));
                if (baseProfile is null && SpineAnimationClassifier.IsBaseName(name))
                {
                    baseProfile = profile;
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"[spine-overlay] animation list unreadable: {ex.GetType().Name}: {ex.Message}");
            return rows;
        }

        foreach ((string name, SpineAnimationKeyProfile profile, bool readable) in profiles)
        {
            SpineAnimationVerdict verdict = readable
                ? SpineAnimationClassifier.Classify(profile, totalBones, totalSlots, baseProfile, ikDriven)
                : new SpineAnimationVerdict(
                    name,
                    SpineAnimationRole.OverlayCandidate,
                    0,
                    0,
                    0,
                    0,
                    "timelines unreadable; listed by name");
            rows.Add(new SpineOverlayRow(name, verdict, readable));
        }

        rows.Sort((left, right) =>
        {
            int byRole = ((int)left.Verdict.Role).CompareTo((int)right.Verdict.Role);
            return byRole != 0
                ? -byRole
                : string.Compare(left.Name, right.Name, StringComparison.Ordinal);
        });
        ReportOnce(pointer, data, totalBones, totalSlots, rows, scan);
        return rows;
    }

    /// <summary>Bones an IK constraint will overwrite each frame, so a key on them is discarded.</summary>
    internal static IReadOnlyCollection<string> IkDrivenBones(SkeletonData data)
    {
        var driven = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            ExposedList<IkConstraintData>? constraints = data.IkConstraints;
            if (constraints is null) return driven;
            for (int index = 0; index < constraints.Count; index++)
            {
                IkConstraintData? constraint = constraints.Items[index];
                if (constraint is null || constraint.WasCollected) continue;
                ExposedList<BoneData>? bones = constraint.Bones;
                if (bones is null) continue;
                for (int bone = 0; bone < bones.Count; bone++)
                {
                    string? name = bones.Items[bone]?.Name;
                    if (!string.IsNullOrEmpty(name)) driven.Add(name!);
                }
            }
        }
        catch (Exception)
        {
            // Without the constraint list we simply report no IK clashes rather than none at all.
        }

        return driven;
    }

    /// <summary>
    /// One line per skeleton on stage. The page is re-read several times a second, so the summary
    /// cannot be logged on every build; the first build of a skeleton is also the one that carries
    /// the answer to "did the timeline read actually work".
    /// </summary>
    private static void ReportOnce(
        IntPtr data,
        SkeletonData skeleton,
        int bones,
        int slots,
        IReadOnlyList<SpineOverlayRow> rows,
        SpineOverlayScan scan)
    {
        if (data == IntPtr.Zero || data == _reportedData) return;
        _reportedData = data;

        int overlays = 0;
        string sample = string.Empty;
        foreach (SpineOverlayRow row in rows)
        {
            if (row.Verdict.Role != SpineAnimationRole.OverlayCandidate) continue;
            overlays++;
            if (sample.Length == 0) sample = SpineAnimationClassifier.Describe(row.Verdict);
        }

        string summary =
            $"[spine-overlay] catalog: data='{skeleton.Name}'; bones={bones}; slots={slots}; "
            + $"animations={rows.Count}; overlayCandidates={overlays}; unreadable={scan.UnreadableAnimations.Count}"
            + (sample.Length == 0 ? string.Empty : $"; first={sample}");
        if (scan.UnreadableAnimations.Count == 0 && scan.UnknownTimelineTypes.Count == 0)
        {
            Plugin.Logger.LogInfo(summary);
            return;
        }

        Plugin.Logger.LogWarning(
            summary
            + $"; unreadableNames=[{string.Join(", ", scan.UnreadableAnimations)}]"
            + (scan.NullTimelineLists.Count == 0
                ? string.Empty
                : $"; nullTimelineLists=[{string.Join(", ", scan.NullTimelineLists)}]")
            + $"; unknownTimelineClasses=[{string.Join(", ", scan.UnknownTimelineTypes)}]");
    }

    private static (SpineAnimationKeyProfile Profile, bool Readable) ReadProfile(
        Animation animation,
        SkeletonData data,
        string name,
        SpineOverlayScan scan)
    {
        var bones = new List<string>();
        var slots = new List<string>();
        bool drawOrder = false;
        bool deform = false;
        int timelineCount = 0;
        int unrecognised = 0;
        try
        {
            ExposedList<Timeline>? timelines = animation.Timelines;
            if (timelines is null)
            {
                scan.NullTimelineLists.Add(name);
                scan.UnreadableAnimations.Add(name);
                return (SpineAnimationKeyProfile.Empty(name), false);
            }
            timelineCount = timelines.Count;
            for (int index = 0; index < timelines.Count; index++)
            {
                Timeline? timeline = timelines.Items[index];
                if (timeline is null || timeline.WasCollected) continue;

                SpineTimelineTarget target = Resolve(timeline);
                if (!target.Recognised)
                {
                    unrecognised++;
                    if (scan.UnknownTimelineTypes.Count < 8)
                    {
                        scan.UnknownTimelineTypes.Add(NativeTypeName(timeline));
                    }

                    continue;
                }

                if (target.KeysDrawOrder) drawOrder = true;
                if (target.KeysDeform) deform = true;
                if (target.Index < 0) continue;   // a constraint or an event: no bone and no slot
                if (target.IsBone) AddBone(data, target.Index, bones);
                else AddSlot(data, target.Index, slots);
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning(
                $"[spine-overlay] '{name}' timelines unreadable: {ex.GetType().Name}: {ex.Message}");
            return (SpineAnimationKeyProfile.Empty(name), false);
        }

        // Readable means every timeline it carries was understood. An animation that keys nothing is
        // readable too: that is the answer "this one is empty", not a gap in our reading.
        bool readable = unrecognised == 0;
        if (!readable) scan.UnreadableAnimations.Add(name);
        return (new SpineAnimationKeyProfile(name, bones, slots, drawOrder, deform, timelineCount), readable);
    }

    /// <summary>
    /// The bone, slot or constraint a timeline drives, resolved through its real IL2CPP class.
    /// <para>
    /// The concrete class has to be asked for with <c>TryCast</c>. The interop wraps every array
    /// element as the <em>declared</em> element type (<c>Il2CppReferenceArray&lt;T&gt;</c> hands the
    /// pointer to <c>Il2CppObjectPool.Get&lt;T&gt;</c>), so an <c>ExposedList&lt;Timeline&gt;</c>
    /// produces wrappers whose managed type is always <c>Spine.Timeline</c> and
    /// <c>timeline is RotateTimeline</c> is false even for a rotate timeline. That is exactly why
    /// every row read "0骨/0槽 · 时间线不可读" until 2026-09-21; <c>TryCast</c> asks IL2CPP for the
    /// real class. <c>Cast&lt;T&gt;</c> would throw for the types that do not apply, so each
    /// candidate is probed, most common first — the chain stops at the first hit.
    /// </para>
    /// </summary>
    private static SpineTimelineTarget Resolve(Timeline timeline)
    {
        if (timeline.TryCast<DrawOrderTimeline>() is not null) return SpineTimelineTarget.DrawOrderOnly;
        if (timeline.TryCast<DeformTimeline>() is { } deform) return SpineTimelineTarget.Deform(deform.SlotIndex);

        if (timeline.TryCast<RotateTimeline>() is { } rotate) return SpineTimelineTarget.Bone(rotate.BoneIndex);
        if (timeline.TryCast<TranslateTimeline>() is { } translate) return SpineTimelineTarget.Bone(translate.BoneIndex);
        if (timeline.TryCast<TranslateXTimeline>() is { } translateX) return SpineTimelineTarget.Bone(translateX.BoneIndex);
        if (timeline.TryCast<TranslateYTimeline>() is { } translateY) return SpineTimelineTarget.Bone(translateY.BoneIndex);
        if (timeline.TryCast<ScaleTimeline>() is { } scale) return SpineTimelineTarget.Bone(scale.BoneIndex);
        if (timeline.TryCast<ScaleXTimeline>() is { } scaleX) return SpineTimelineTarget.Bone(scaleX.BoneIndex);
        if (timeline.TryCast<ScaleYTimeline>() is { } scaleY) return SpineTimelineTarget.Bone(scaleY.BoneIndex);
        if (timeline.TryCast<ShearTimeline>() is { } shear) return SpineTimelineTarget.Bone(shear.BoneIndex);
        if (timeline.TryCast<ShearXTimeline>() is { } shearX) return SpineTimelineTarget.Bone(shearX.BoneIndex);
        if (timeline.TryCast<ShearYTimeline>() is { } shearY) return SpineTimelineTarget.Bone(shearY.BoneIndex);
        if (timeline.TryCast<InheritTimeline>() is { } inherit) return SpineTimelineTarget.Bone(inherit.BoneIndex);

        if (timeline.TryCast<AttachmentTimeline>() is { } attachment) return SpineTimelineTarget.Slot(attachment.SlotIndex);
        if (timeline.TryCast<SequenceTimeline>() is { } sequence) return SpineTimelineTarget.Slot(sequence.SlotIndex);
        if (timeline.TryCast<AlphaTimeline>() is { } alpha) return SpineTimelineTarget.Slot(alpha.SlotIndex);
        if (timeline.TryCast<RGBATimeline>() is { } rgba) return SpineTimelineTarget.Slot(rgba.SlotIndex);
        if (timeline.TryCast<RGBA2Timeline>() is { } rgba2) return SpineTimelineTarget.Slot(rgba2.SlotIndex);
        if (timeline.TryCast<RGB2Timeline>() is { } rgb2) return SpineTimelineTarget.Slot(rgb2.SlotIndex);
        if (timeline.TryCast<RGBTimeline>() is { } rgb) return SpineTimelineTarget.Slot(rgb.SlotIndex);

        if (IsTargetless(timeline)) return SpineTimelineTarget.Ignored;
        return SpineTimelineTarget.Unknown;
    }

    /// <summary>
    /// Timelines that key their constraint, or only events: understood, but they carry no bone and
    /// no slot, so they neither add to the counts nor mark an animation unreadable. The physics
    /// subclasses are covered by their base (damping, gravity, inertia, mass, mix, strength, wind);
    /// the reset timeline is the one that does not derive from it.
    /// </summary>
    private static bool IsTargetless(Timeline timeline) =>
        timeline.TryCast<IkConstraintTimeline>() is not null
        || timeline.TryCast<TransformConstraintTimeline>() is not null
        || timeline.TryCast<PathConstraintMixTimeline>() is not null
        || timeline.TryCast<PathConstraintPositionTimeline>() is not null
        || timeline.TryCast<PathConstraintSpacingTimeline>() is not null
        || timeline.TryCast<PhysicsConstraintTimeline>() is not null
        || timeline.TryCast<PhysicsConstraintResetTimeline>() is not null
        || timeline.TryCast<EventTimeline>() is not null;

    /// <summary>
    /// The real IL2CPP class name behind a wrapper. Only used for the diagnostic that names a
    /// timeline class this build did not handle, where the managed type would just say "Timeline".
    /// </summary>
    private static string NativeTypeName(Timeline timeline)
    {
        try
        {
            string? name = IL2CPP.il2cpp_class_get_name_(timeline.ObjectClass);
            return string.IsNullOrEmpty(name) ? "?" : name!;
        }
        catch (Exception)
        {
            return "?";
        }
    }

    private static void AddBone(SkeletonData data, int boneIndex, List<string> bones)
    {
        int count = data.Bones?.Count ?? 0;
        if (boneIndex < 0 || boneIndex >= count) return;
        string? boneName = data.Bones!.Items[boneIndex]?.Name;
        if (!string.IsNullOrEmpty(boneName)) bones.Add(boneName!);
    }

    private static void AddSlot(SkeletonData data, int slotIndex, List<string> slots)
    {
        int count = data.Slots?.Count ?? 0;
        if (slotIndex < 0 || slotIndex >= count) return;
        string? slotName = data.Slots!.Items[slotIndex]?.Name;
        if (!string.IsNullOrEmpty(slotName)) slots.Add(slotName!);
    }
}
