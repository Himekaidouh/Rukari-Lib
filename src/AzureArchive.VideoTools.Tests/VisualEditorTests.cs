using AzureArchive.VideoTools.Core.Characters;
using AzureArchive.VideoTools.Core.Cameras;
using AzureArchive.VideoTools.Core.VisualEditor;

namespace AzureArchive.VideoTools.Tests;

internal static class VisualEditorTests
{
    public static void MapsStoryCoordinatesThroughDynamicViewport()
    {
        float storyHeight = VisualEditorCanvasMath.LogicalStoryHeight(2560, 1541).Value;
        var viewport = new VisualEditorRect(30, 200, 620, 373.1547f);

        VisualEditorPoint center = VisualEditorCanvasMath.StoryToViewport(
            new VisualEditorPoint(0, 0),
            storyHeight,
            viewport).Value;
        AssertNear(340f, center.X);
        AssertNear(386.57735f, center.Y);

        VisualEditorPoint roundTrip = VisualEditorCanvasMath.ViewportToStory(
            center,
            storyHeight,
            viewport).Value;
        AssertNear(0f, roundTrip.X);
        AssertNear(0f, roundTrip.Y);
    }

    public static void ViewportZoomOutKeepsOffScreenCharactersReachable()
    {
        float storyHeight = VisualEditorCanvasMath.LogicalStoryHeight(2560, 1541).Value;
        var viewport = new VisualEditorRect(30, 200, 620, 373.1547f);
        float reach = VisualEditorCanvasMath.StoryHalfWidth
            * (VisualEditorCanvasMath.ViewportZoomOutFactor - 1f);

        // A character dragged past the story edge stays inside the frame.
        VisualEditorPoint offScreen = VisualEditorCanvasMath.StoryToViewport(
            new VisualEditorPoint(-VisualEditorCanvasMath.StoryHalfWidth - reach + 1f, 0f),
            storyHeight,
            viewport).Value;
        AssertEx.True(offScreen.X > viewport.X);
        AssertEx.True(offScreen.X < viewport.X + viewport.Width);

        // And the mapping round-trips.
        VisualEditorPoint roundTrip = VisualEditorCanvasMath.ViewportToStory(
            offScreen,
            storyHeight,
            viewport).Value;
        AssertNear(-VisualEditorCanvasMath.StoryHalfWidth - reach + 1f, roundTrip.X);

        // The story edge itself maps strictly inside the frame now.
        VisualEditorPoint edge = VisualEditorCanvasMath.StoryToViewport(
            new VisualEditorPoint(-VisualEditorCanvasMath.StoryHalfWidth, 0f),
            storyHeight,
            viewport).Value;
        AssertEx.True(edge.X > viewport.X);
    }

    public static void ComposesSlotAnchorsWithLocalCharacterOffsets()
    {
        float[] expectedAnchors = { -1040f, -520f, 0f, 520f, 1040f };
        for (int publicSlot = 1; publicSlot <= 5; publicSlot++)
        {
            VisualEditorPoint anchor = VisualEditorCanvasMath.SlotAnchor(publicSlot).Value;
            AssertNear(expectedAnchors[publicSlot - 1], anchor.X);
            AssertNear(0f, anchor.Y);

            var local = new VisualEditorPoint(125.5f, -300f);
            VisualEditorPoint story = VisualEditorCanvasMath.SlotLocalToStory(
                publicSlot,
                local).Value;
            AssertNear(anchor.X + local.X, story.X);
            AssertNear(local.Y, story.Y);

            VisualEditorPoint roundTrip = VisualEditorCanvasMath.StoryToSlotLocal(
                publicSlot,
                story).Value;
            AssertNear(local.X, roundTrip.X);
            AssertNear(local.Y, roundTrip.Y);
        }

        AssertEx.False(VisualEditorCanvasMath.SlotAnchor(0).Success);
        AssertEx.False(VisualEditorCanvasMath.SlotAnchor(6).Success);
    }

    public static void OccupiedSlotHitTestingFollowsItsRotatedVisual()
    {
        var center = new VisualEditorPoint(300f, 400f);

        AssertEx.True(VisualEditorCanvasMath.ContainsRotatedRect(
            center,
            center,
            width: 80f,
            height: 180f,
            rotationDegrees: 45f));
        AssertEx.True(VisualEditorCanvasMath.ContainsRotatedRect(
            new VisualEditorPoint(250.5f, 449.5f),
            center,
            width: 80f,
            height: 180f,
            rotationDegrees: 45f));
        AssertEx.False(VisualEditorCanvasMath.ContainsRotatedRect(
            new VisualEditorPoint(220f, 480f),
            center,
            width: 80f,
            height: 180f,
            rotationDegrees: 45f));
        AssertEx.False(VisualEditorCanvasMath.ContainsRotatedRect(
            center,
            center,
            width: float.NaN,
            height: 180f,
            rotationDegrees: 45f));
    }

    public static void SlotClickDoesNotBecomeADragUntilPointerMoves()
    {
        var pressed = new VisualEditorPoint(100f, 200f);

        AssertEx.False(VisualEditorCanvasMath.ExceedsDragThreshold(
            pressed,
            new VisualEditorPoint(105f, 200f),
            threshold: 6f));
        AssertEx.True(VisualEditorCanvasMath.ExceedsDragThreshold(
            pressed,
            new VisualEditorPoint(106f, 200f),
            threshold: 6f));
        AssertEx.False(VisualEditorCanvasMath.ExceedsDragThreshold(
            pressed,
            new VisualEditorPoint(float.NaN, 200f),
            threshold: 6f));
    }

    public static void ScreenTextSequenceKeepsDistinctMarkersAndFindsPreviousText()
    {
        var hues = new HashSet<float>();
        for (int index = 0; index < 64; index++)
        {
            AssertEx.True(hues.Add(MathF.Round(
                ScreenTextSequenceVisualMath.MarkerHue(index),
                6)));
        }

        AssertEx.Equal(-1, ScreenTextSequenceVisualMath.PreviousVisibleIndex(0, -1));
        AssertEx.Equal(2, ScreenTextSequenceVisualMath.PreviousVisibleIndex(3, -1));
        AssertEx.Equal(1, ScreenTextSequenceVisualMath.PreviousVisibleIndex(3, 2));
        AssertEx.Equal(-1, ScreenTextSequenceVisualMath.PreviousVisibleIndex(3, 0));
    }

    public static void ScreenTextTemplateKeepsTheLastCompleteUserSettings()
    {
        var remembered = new ScreenTextDirective(
            320f,
            -180f,
            ScreenTextAlignment.Center,
            ScreenTextRevealMode.Serial,
            72);
        AssertEx.Equal(
            remembered,
            ScreenTextSequenceVisualMath.ResolveTemplate(null, remembered));

        var currentScene = remembered with
        {
            X = -640f,
            FontSize = 96,
            RevealMode = ScreenTextRevealMode.Smooth
        };
        AssertEx.Equal(
            currentScene,
            ScreenTextSequenceVisualMath.ResolveTemplate(currentScene, remembered));

        ScreenTextDirective fallback =
            ScreenTextSequenceVisualMath.ResolveTemplate(null, null);
        AssertEx.Equal(0f, fallback.X);
        AssertEx.Equal(0f, fallback.Y);
        AssertEx.Equal(64, fallback.FontSize);
        AssertEx.Equal(ScreenTextAlignment.Left, fallback.Alignment);
        AssertEx.Equal(ScreenTextRevealMode.Instant, fallback.RevealMode);
    }

    public static void CameraFrameMovesIndependentlyFromTheStoryCanvas()
    {
        float storyHeight = VisualEditorCanvasMath.LogicalStoryHeight(2560, 1541).Value;
        var viewport = new VisualEditorRect(30f, 200f, 620f, 373.1547f);
        var camera = new SceneCameraState(400f, -120f, 2f);

        VisualEditorRect frame = VisualCameraFrameMath.CameraToViewportFrame(
            camera,
            storyHeight,
            viewport).Value;
        AssertNear(
            viewport.Width / (VisualEditorCanvasMath.ViewportZoomOutFactor * 2f),
            frame.Width);
        AssertNear(
            viewport.Height / (VisualEditorCanvasMath.ViewportZoomOutFactor * 2f),
            frame.Height);

        VisualEditorPoint center = new(
            frame.X + (frame.Width / 2f),
            frame.Y + (frame.Height / 2f));
        SceneCameraState roundTrip = VisualCameraFrameMath.MoveFrameCenter(
            center,
            camera,
            storyHeight,
            viewport).Value;
        AssertNear(camera.X, roundTrip.X);
        AssertNear(camera.Y, roundTrip.Y);
        AssertNear(camera.Zoom, roundTrip.Zoom);
        AssertNear(
            camera.Zoom,
            VisualCameraFrameMath.ZoomFromFrameWidth(frame.Width, viewport).Value);

        // Mapping the camera never mutates the fixed overview viewport.
        AssertEx.Equal(new VisualEditorRect(30f, 200f, 620f, 373.1547f), viewport);
    }

    public static void BuildsAndReadsCameraGuiDrafts()
    {
        var builder = new VisualCameraDraftBuilder();
        string set = AssertEx.NotNull(builder.BuildSet(
            new SceneCameraState(400f, -120f, 1.5f),
            800,
            CharacterTransformEasing.EaseInOut).Value);
        AssertEx.Equal(
            "#aavt;camera;set;x=400;y=-120;zoom=1.5;duration=800;easing=easeInOut",
            set);

        SceneCameraCommand existing = AssertEx.NotNull(builder.ReadCanonical(
            "#camera;move;dx=200;dy=-50;dzoom=0.25;duration=300;easing=easeOut").Value);
        AssertEx.Equal(SceneCameraOperation.Move, existing.Operation);
        AssertEx.Equal(200f, existing.DeltaX);
        AssertEx.Equal(0.25f, existing.DeltaZoom);

        AssertEx.Equal(
            "#aavt;camera;reset;duration=600;easing=linear",
            AssertEx.NotNull(builder.BuildReset(
                600,
                CharacterTransformEasing.Linear).Value));
    }

    public static void BuildsOccupiedAndPendingDragDrafts()
    {
        var builder = new VisualCharacterDraftBuilder();

        string occupied = AssertEx.NotNull(
            builder.BuildAbsolutePosition(
                2,
                occupied: true,
                x: -512.5f,
                y: 220f,
                durationMilliseconds: 600,
                CharacterTransformEasing.EaseOut).Value);
        AssertEx.Equal(
            "#aavt;char;2;set;x=-512.5;y=220;duration=600;easing=easeOut",
            occupied);

        string pending = AssertEx.NotNull(
            builder.BuildAbsolutePosition(
                5,
                occupied: false,
                x: 1750f,
                y: -900.25f,
                durationMilliseconds: 0,
                CharacterTransformEasing.Linear).Value);
        AssertEx.Equal(
            "#aavt;charPending;5;set;x=1750;y=-900.25",
            pending);
    }

    public static void BuildsCombinedInspectorAndResetDrafts()
    {
        var builder = new VisualCharacterDraftBuilder();

        string combined = AssertEx.NotNull(builder.Build(new VisualCharacterDraftRequest(
            PublicSlot: 2,
            Occupied: true,
            VisualCharacterDraftOperation.Set,
            X: -620f,
            Y: -450.25f,
            RotationDegrees: 12f,
            FlipX: true,
            DurationMilliseconds: 1000,
            CharacterTransformEasing.EaseOut)).Value);
        AssertEx.Equal(
            "#aavt;char;2;set;x=-620;y=-450.25;rotation=12;flipX=true;duration=1000;easing=easeOut",
            combined);

        string pending = AssertEx.NotNull(builder.Build(new VisualCharacterDraftRequest(
            PublicSlot: 5,
            Occupied: false,
            VisualCharacterDraftOperation.Set,
            X: 1700f,
            Y: -900f,
            RotationDegrees: -15f,
            FlipX: false,
            DurationMilliseconds: 2500,
            CharacterTransformEasing.EaseInOut)).Value);
        AssertEx.Equal(
            "#aavt;charPending;5;set;x=1700;y=-900;rotation=-15;flipX=false",
            pending);

        string reset = AssertEx.NotNull(builder.Build(new VisualCharacterDraftRequest(
            PublicSlot: 4,
            Occupied: true,
            VisualCharacterDraftOperation.Reset,
            X: null,
            Y: null,
            RotationDegrees: null,
            FlipX: null,
            DurationMilliseconds: 300,
            CharacterTransformEasing.EaseIn)).Value);
        AssertEx.Equal(
            "#aavt;char;4;reset;duration=300;easing=easeIn",
            reset);

        AssertEx.False(builder.Build(new VisualCharacterDraftRequest(
            PublicSlot: 1,
            Occupied: false,
            VisualCharacterDraftOperation.Reset,
            X: null,
            Y: null,
            RotationDegrees: null,
            FlipX: null,
            DurationMilliseconds: 0,
            CharacterTransformEasing.Linear)).Success);
    }

    public static void ReadsExistingDraftsWithoutLosingTheirSemantics()
    {
        var builder = new VisualCharacterDraftBuilder();

        VisualCharacterDraftRequest occupied = AssertEx.NotNull(
            builder.ReadCanonical(
                "#char;2;set;x=-620;y=-450.25;rotation=12;flipX=true;duration=1000;easing=easeOut").Value);
        AssertEx.Equal(2, occupied.PublicSlot);
        AssertEx.True(occupied.Occupied);
        AssertEx.Equal(VisualCharacterDraftOperation.Set, occupied.Operation);
        AssertEx.Equal(
            "#aavt;char;2;set;x=-620;y=-450.25;rotation=12;flipX=true;duration=1000;easing=easeOut",
            AssertEx.NotNull(builder.Build(occupied).Value));

        VisualCharacterDraftRequest pending = AssertEx.NotNull(
            builder.ReadCanonical(
                "#charp;5;set;x=1700;y=-900;rotation=-15;flipX=false").Value);
        AssertEx.False(pending.Occupied);
        AssertEx.Equal(
            "#aavt;charPending;5;set;x=1700;y=-900;rotation=-15;flipX=false",
            AssertEx.NotNull(builder.Build(pending).Value));

        VisualCharacterDraftRequest relativeMove = AssertEx.NotNull(
            builder.ReadCanonical(
                "#char;4;move;dx=300;dy=-50;drotation=10;duration=600;easing=easeInOut").Value);
        AssertEx.Equal(VisualCharacterDraftOperation.Move, relativeMove.Operation);
        AssertEx.Equal(
            "#aavt;char;4;move;dx=300;dy=-50;drotation=10;duration=600;easing=easeInOut",
            AssertEx.NotNull(builder.Build(relativeMove).Value));
    }

    public static void SuppressesOnlyTheVisibleEditorInputRegion()
    {
        var panel = new VisualEditorRect(0f, 80f, 720f, 1600f);
        var collapsedHandle = new VisualEditorRect(0f, 770f, 74f, 140f);
        var expandedHandle = new VisualEditorRect(720f, 770f, 74f, 140f);

        AssertEx.False(VisualEditorInputGuardMath.ShouldSuppressOfficialMouse(
            new VisualEditorInputRegion(
                Visible: false,
                Expanded: true,
                Dragging: false,
                CanvasScale: 0.8f,
                panel,
                expandedHandle),
            new VisualEditorPoint(200f, 300f)));

        var collapsed = new VisualEditorInputRegion(
            Visible: true,
            Expanded: false,
            Dragging: false,
            CanvasScale: 0.8f,
            panel,
            collapsedHandle);
        AssertEx.True(VisualEditorInputGuardMath.ShouldSuppressOfficialMouse(
            collapsed,
            new VisualEditorPoint(24f, 680f)));
        AssertEx.False(VisualEditorInputGuardMath.ShouldSuppressOfficialMouse(
            collapsed,
            new VisualEditorPoint(200f, 300f)));

        var expanded = collapsed with
        {
            Expanded = true,
            Handle = expandedHandle
        };
        AssertEx.True(VisualEditorInputGuardMath.ShouldSuppressOfficialMouse(
            expanded,
            new VisualEditorPoint(200f, 300f)));
        AssertEx.False(VisualEditorInputGuardMath.ShouldSuppressOfficialMouse(
            expanded,
            new VisualEditorPoint(1200f, 300f)));

        AssertEx.True(VisualEditorInputGuardMath.ShouldSuppressOfficialMouse(
            expanded with { Dragging = true },
            new VisualEditorPoint(2200f, 1200f)));
    }

    private static void AssertNear(float expected, float actual)
    {
        AssertEx.True(
            Math.Abs(expected - actual) < 0.001f,
            $"Expected approximately {expected} but found {actual}.");
    }
}
