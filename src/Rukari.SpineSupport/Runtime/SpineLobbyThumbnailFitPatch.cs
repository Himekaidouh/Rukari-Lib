extern alias unitycore;

using Rukari.SpineSupport.Spines;
using Spine;
using Spine.Unity;
using Studio.Scripts.Window.EmotionExplorer;
using Transform = unitycore::UnityEngine.Transform;
using Vector3 = unitycore::UnityEngine.Vector3;

namespace Rukari.SpineSupport.Runtime;

internal static class SpineLobbyThumbnailFitPatch
{
    private static bool _reportedFit;
    private static bool _reportedSkip;
    private static bool _reportedFailure;

    // Called only from CharacterTray.Init postfix on the captured main thread.
    public static void Apply(CharacterTray tray)
    {
        try
        {
            SkeletonAnimation anim = tray.anim;
            UISprite card = tray.sprite;
            if (ReferenceEquals(anim, null) || ReferenceEquals(card, null)) return;
            Skeleton skeleton = anim.Skeleton;
            if (ReferenceEquals(skeleton, null)) return;
            SkeletonData data = skeleton.Data;
            if (ReferenceEquals(data, null)
                || ReferenceEquals(data.FindAnimation("Idle_01"), null)
                || ReferenceEquals(data.FindAnimation("Start_Idle_01"), null)) return;

            Transform previewTransform = anim.transform;
            Transform cardTransform = card.transform;
            Transform previewParent = previewTransform.parent;
            // The shipped prefab puts the centered UISprite on the root and Spine directly below it.
            // Check this relationship before interpreting sprite and skeleton coordinates together.
            if (ReferenceEquals(previewParent, null) || previewParent.Pointer != cardTransform.Pointer
                || card.pivot != UIWidget.Pivot.Center)
            {
                ReportSkip("card hierarchy/pivot differs from the inspected prefab");
                return;
            }

            Vector3 scale = previewTransform.localScale;
            Vector3 position = previewTransform.localPosition;
            float width = data.Width;
            float height = data.Height;
            int cardWidth = card.width;
            int cardHeight = card.height;
            SpineLobbyThumbnailLayout? layout = SpineLobbyThumbnailFit.Calculate(
                data.X, data.Y, width, height, cardWidth, cardHeight, scale.x, scale.y, 16f);
            if (layout == null)
            {
                ReportSkip("exported skeleton bounds or card dimensions are invalid");
                return;
            }

            // These typed Transform accessors are already used by the existing camera/character tools.
            // Preserve depth. Never write SkeletonData, the imported asset, or the playback character.
            previewTransform.localScale = new Vector3(layout.ScaleX, layout.ScaleY, scale.z);
            previewTransform.localPosition = new Vector3(layout.PositionX, layout.PositionY, position.z);
            if (!_reportedFit)
            {
                _reportedFit = true;
                Plugin.Logger.LogInfo(
                    $"Spine lobby thumbnail fit applied: card={cardWidth}x{cardHeight}; "
                    + $"skeletonBounds={width:0.###}x{height:0.###}; "
                    + $"scale={scale.x:0.###},{scale.y:0.###}->{layout.ScaleX:0.###},{layout.ScaleY:0.###}; "
                    + "bounds=exported-setup; hardClip=false; playbackTransformWritten=false.");
            }
        }
        catch (Exception ex)
        {
            if (_reportedFailure) return;
            _reportedFailure = true;
            Plugin.Logger.LogWarning($"Spine lobby thumbnail fit failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ReportSkip(string reason)
    {
        if (_reportedSkip) return;
        _reportedSkip = true;
        Plugin.Logger.LogWarning($"Spine lobby thumbnail fit skipped: {reason}.");
    }
}
