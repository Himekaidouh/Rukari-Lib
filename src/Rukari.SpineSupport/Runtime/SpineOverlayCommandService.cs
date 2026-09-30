using System.Collections.Generic;
using Rukari.Lib;
using Rukari.Lib.Spines;
using Spine;

namespace Rukari.SpineSupport.Runtime;

/// <summary>
/// Executes the <c>#aavt;spine</c> family (2026-09-21): puts one animation of a character's own
/// skeleton on a reserved track, or fades that track out again.
/// <para>
/// This is the only place that writes an overlay during playback, and it is deliberately narrow. It
/// touches nothing but <see cref="SpineOverlayTracks"/>; it sets mix on the track entry instead of
/// on the shared <c>AnimationStateData</c>, so the asset-wide default mix never leaks; it never
/// hands the engine an animation name the skeleton does not own (an unknown name clears the whole
/// track); and every native access is guarded, because a skeleton that is being torn down throws
/// rather than returns.
/// </para>
/// <para>
/// An overlay stays until it is cleared or the scene rebuilds — that is the contract the author
/// chose — so the applied tracks are remembered per scene only in order to release them again.
/// </para>
/// </summary>
internal sealed class SpineOverlayCommandService : ISpineOverlayCommandService, IDisposable
{
    private readonly Func<bool> _isMainThread;
    private readonly bool _holdLastFrameByDefault;
    private readonly Dictionary<string, List<AppliedTrack>> _applied = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _disposed;

    /// <param name="isMainThread">Whether the caller is on the game main thread.</param>
    /// <param name="holdLastFrameByDefault">
    /// What a non-looping overlay does when the directive does not say. False leaves spine's own
    /// behaviour alone, which is the safe default: this switch only ever adds holding.
    /// </param>
    internal SpineOverlayCommandService(Func<bool> isMainThread, bool holdLastFrameByDefault)
    {
        _isMainThread = isMainThread ?? throw new ArgumentNullException(nameof(isMainThread));
        _holdLastFrameByDefault = holdLastFrameByDefault;
    }

    /// <summary>How many tracks this provider currently holds, for the registration log line.</summary>
    internal int AppliedCount
    {
        get
        {
            lock (_gate)
            {
                return _applied.Count;
            }
        }
    }

    public ModResult<SpineOverlayExecutionSnapshot> ApplyOverlay(SpineOverlayRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_disposed)
        {
            return ModResult<SpineOverlayExecutionSnapshot>.Fail(
                ModErrorCode.NotReady,
                "Spine 叠加服务已停止。");
        }

        if (!_isMainThread())
        {
            return ModResult<SpineOverlayExecutionSnapshot>.Fail(
                ModErrorCode.WrongThread,
                "Spine 叠加只能在游戏主线程上执行。");
        }

        if (!SpineOverlayTracks.IsReserved(request.TrackIndex))
        {
            return ModResult<SpineOverlayExecutionSnapshot>.Fail(
                ModErrorCode.InvalidArgument,
                $"轨道 {request.TrackIndex} 不在保留范围 "
                + $"{SpineOverlayTracks.First}..{SpineOverlayTracks.Last} 内。");
        }

        string scene = request.SceneIdentity ?? string.Empty;
        string operation = request.Kind == SpineOverlayRequestKind.Clear ? "clear" : "play";
        try
        {
            SpineOverlayTarget? target = FindTarget(request.PublicSlot);
            if (target == null)
            {
                return Ok(request, operation, applied: false, detail: $"槽位 {request.PublicSlot} 上没有可读的 Spine 骨骼");
            }

            SpineOverlayTarget resolved = target;
            if (resolved.Animation.WasCollected || resolved.Animation.Pointer == IntPtr.Zero)
            {
                return Ok(request, operation, applied: false, detail: "骨骼已销毁");
            }

            AnimationState? state = resolved.Animation.AnimationState;
            if (state is null)
            {
                return Ok(request, operation, applied: false, detail: "AnimationState 不可用");
            }

            if (request.Kind == SpineOverlayRequestKind.Clear)
            {
                float fade = Seconds(request.FadeMilliseconds, SpineOverlayTracks.DefaultFadeMilliseconds);
                state.SetEmptyAnimation(request.TrackIndex, fade);
                Forget(scene, request.PublicSlot, request.TrackIndex);
                return Ok(request, operation, applied: true, detail: $"fade={fade:F2}s");
            }

            if (resolved.Data.FindAnimation(request.AnimationName) is null)
            {
                // The engine clears the whole track for a name it does not know, so never hand it one.
                return Ok(request, operation, applied: false, detail: $"骨骼里没有动画 '{request.AnimationName}'");
            }

            TrackEntry? entry = state.SetAnimation(
                request.TrackIndex,
                request.AnimationName,
                request.Loop);
            if (entry is null)
            {
                return Ok(request, operation, applied: false, detail: "SetAnimation 返回空");
            }

            entry.MixDuration = Seconds(request.MixMilliseconds, 0);
            entry.MixBlend = request.Additive ? MixBlend.Add : MixBlend.Replace;

            // Holding is opt-in per directive, with a provider-wide default behind it. Spine's own
            // behaviour for a finished entry is left untouched unless holding was asked for, so this
            // switch can only ever keep a pose that would otherwise be let go.
            bool hold = request.Hold ?? _holdLastFrameByDefault;
            if (hold && !request.Loop)
            {
                entry.TrackEnd = float.PositiveInfinity;
            }

            if (request.StartAtEnd)
            {
                // An inherited overlay shows where continuous playback already is: the entry is
                // placed at its last frame instead of replaying from the first one. With hold it
                // stays there; without hold it finishes immediately, which is the author's own
                // 'play once and let go' semantics.
                entry.TrackTime = entry.Animation?.Duration ?? 0f;
            }

            Remember(scene, request.PublicSlot, request.TrackIndex);
            return Ok(
                request,
                operation,
                applied: true,
                detail: $"mix={entry.MixDuration:F2}s; loop={request.Loop}; "
                    + $"hold={(hold && !request.Loop ? "last-frame" : "spine-default")}; "
                    + $"atEnd={request.StartAtEnd}");
        }
        catch (Exception ex)
        {
            // A destroyed or half-initialised skeleton is a per-command problem, not a batch failure:
            // the rest of the card still has to be dispatched.
            return Ok(request, operation, applied: false, detail: $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public ModResult<int> ReleaseScene(string sceneIdentity, int fadeMilliseconds)
    {
        if (_disposed) return ModResult<int>.Ok(0);
        if (!_isMainThread())
        {
            return ModResult<int>.Fail(
                ModErrorCode.WrongThread,
                "释放叠加层只能在游戏主线程上执行。");
        }

        List<AppliedTrack> tracks = Take(sceneIdentity ?? string.Empty);
        if (tracks.Count == 0) return ModResult<int>.Ok(0);
        float fade = Seconds(fadeMilliseconds, SpineOverlayTracks.DefaultFadeMilliseconds);
        int released = 0;
        foreach (AppliedTrack track in tracks)
        {
            try
            {
                SpineOverlayTarget? target = FindTarget(track.PublicSlot);
                if (target == null) continue;
                if (target.Animation.WasCollected || target.Animation.Pointer == IntPtr.Zero) continue;
                AnimationState? state = target.Animation.AnimationState;
                if (state is null) continue;
                state.SetEmptyAnimation(track.TrackIndex, fade);
                released++;
            }
            catch (Exception)
            {
                // The scene is going away; a skeleton that is already gone owes us nothing.
            }
        }

        return ModResult<int>.Ok(released);
    }

    /// <summary>Drops everything without touching a skeleton, for plugin shutdown.</summary>
    public void Dispose()
    {
        _disposed = true;
        lock (_gate)
        {
            _applied.Clear();
        }
    }

    private static ModResult<SpineOverlayExecutionSnapshot> Ok(
        SpineOverlayRequest request,
        string operation,
        bool applied,
        string detail) =>
        ModResult<SpineOverlayExecutionSnapshot>.Ok(new SpineOverlayExecutionSnapshot(
            request.PublicSlot,
            operation,
            request.AnimationName,
            request.TrackIndex,
            applied,
            detail));

    private static float Seconds(int milliseconds, int fallbackMilliseconds)
    {
        int value = milliseconds < 0 ? fallbackMilliseconds : milliseconds;
        return value / 1000f;
    }

    /// <summary>The live skeleton of one public slot; the same resolution the console and catalogue use.</summary>
    private static SpineOverlayTarget? FindTarget(int publicSlot)
    {
        foreach (SpineOverlayTarget candidate in SpineOverlayCatalog.Targets())
        {
            if (candidate.PublicSlot == publicSlot) return candidate;
        }

        return null;
    }

    private void Remember(string sceneIdentity, int publicSlot, int trackIndex)
    {
        var track = new AppliedTrack(publicSlot, trackIndex);
        lock (_gate)
        {
            if (!_applied.TryGetValue(sceneIdentity, out List<AppliedTrack>? tracks))
            {
                tracks = new List<AppliedTrack>();
                _applied[sceneIdentity] = tracks;
            }

            tracks.RemoveAll(existing => existing.TrackIndex == trackIndex && existing.PublicSlot == publicSlot);
            tracks.Add(track);
        }
    }

    private void Forget(string sceneIdentity, int publicSlot, int trackIndex)
    {
        lock (_gate)
        {
            if (!_applied.TryGetValue(sceneIdentity, out List<AppliedTrack>? tracks)) return;
            tracks.RemoveAll(existing => existing.TrackIndex == trackIndex && existing.PublicSlot == publicSlot);
            if (tracks.Count == 0) _applied.Remove(sceneIdentity);
        }
    }

    private List<AppliedTrack> Take(string sceneIdentity)
    {
        lock (_gate)
        {
            if (!_applied.TryGetValue(sceneIdentity, out List<AppliedTrack>? tracks))
            {
                return new List<AppliedTrack>();
            }

            _applied.Remove(sceneIdentity);
            return tracks;
        }
    }

    private readonly record struct AppliedTrack(int PublicSlot, int TrackIndex);
}
