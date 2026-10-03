using AzureArchive.VideoTools.Core.Commands;

namespace AzureArchive.VideoTools.Tests;

internal static class ManagedSelectionLogCorrelationTests
{
    private static readonly EditorPreviewSceneAddress Scene = new("project", "node", 9, "fingerprint");
    private static readonly CompiledScriptIdentity Script = CommandIdentity.CompiledScript("selected scene\n#5;h");

    private static ManagedSelectionLogCorrelation Open(int requestId = 401,
        EditorPreviewSceneAddress? scene = null)
    {
        var correlation = new ManagedSelectionLogCorrelation();
        correlation.Begin(requestId, 20, 11, 5, 4, scene ?? Scene);
        return correlation;
    }

    private static ManagedSelectionLogCandidate Capture(ManagedSelectionLogCorrelation correlation)
    {
        AssertEx.True(correlation.TryObserveFirst(21, Script, 11, 5, 4, out _));
        AssertEx.True(correlation.Pending != null);
        return correlation.Pending!;
    }

    public static void ZeroRequestIsOpaqueAndPreservesTheObservedScript()
    {
        ManagedSelectionLogCorrelation correlation = Open(0);
        ManagedSelectionLogCandidate candidate = Capture(correlation);
        AssertEx.Equal(0, candidate.Marker.RequestId);
        AssertEx.Equal(9, candidate.Marker.CapturedScene!.SceneIndex);
        AssertEx.Equal(Script, candidate.Script);
        AssertEx.True(correlation.TryComplete(candidate, 11, 5, Scene, out _));
    }

    public static void NoMarkerAndLaterMessagesNeverSupplyAnIdentity()
    {
        var noMarker = new ManagedSelectionLogCorrelation();
        AssertEx.False(noMarker.TryObserveFirst(21, Script, 11, 5, 4, out _));
        ManagedSelectionLogCorrelation correlation = Open();
        ManagedSelectionLogCandidate candidate = Capture(correlation);
        AssertEx.False(correlation.TryObserveFirst(22, CommandIdentity.CompiledScript("later"),
            11, 5, 4, out _));
        AssertEx.Equal(candidate, correlation.Pending!);
    }

    public static void ReusedRequestCannotReviveASupersededMarker()
    {
        ManagedSelectionLogCorrelation correlation = Open();
        ManagedSelectionLogCandidate old = Capture(correlation);
        correlation.Begin(401, 21, 11, 6, 5, Scene);
        AssertEx.True(correlation.TryObserveFirst(22, Script, 11, 6, 5, out _));
        ManagedSelectionLogCandidate current = correlation.Pending!;
        AssertEx.False(correlation.TryComplete(old, 11, 6, Scene, out _));
        AssertEx.Equal(current, correlation.Pending!);
        AssertEx.True(correlation.TryComplete(current, 11, 6, Scene, out _));
    }

    public static void BackgroundFirstMessageCannotBeReplacedByAMainThreadMessage()
    {
        ManagedSelectionLogCorrelation correlation = Open();
        AssertEx.False(correlation.TryObserveFirst(21, Script, 22, 5, 4, out _));
        AssertEx.False(correlation.TryObserveFirst(22, Script, 11, 5, 4, out _));
        AssertEx.True(correlation.Pending == null);
    }

    public static void MissingCaptureOrMainThreadProofCannotPublish()
    {
        var noScene = new ManagedSelectionLogCorrelation();
        noScene.Begin(0, 20, 11, 5, 4, null);
        AssertEx.False(noScene.TryObserveFirst(21, Script, 11, 5, 4, out _));
        AssertEx.True(noScene.Pending == null);
        var noThread = new ManagedSelectionLogCorrelation();
        noThread.Begin(401, 20, 0, 5, 4, Scene);
        AssertEx.False(noThread.TryObserveFirst(21, Script, 11, 5, 4, out _));
    }

    public static void LateClosedOrDifferentWindowMessagesAreRejected()
    {
        AssertEx.False(Open().TryObserveFirst(21, Script, 11, 5, 5, out _));
        AssertEx.False(Open().TryObserveFirst(21, Script, 11, 6, 4, out _));
        AssertEx.False(Open().TryObserveFirst(21, Script, 11, 5, 3, out _));
        var noOpenWindow = new ManagedSelectionLogCorrelation();
        noOpenWindow.Begin(401, 20, 11, 4, 4, Scene);
        AssertEx.False(noOpenWindow.TryObserveFirst(21, Script, 11, 5, 4, out _));
    }

    public static void InvalidOrMissingFirstScriptDoesNotFallBackToALaterOne()
    {
        CompiledScriptIdentity?[] invalid =
        {
            null,
            new("partial", 10, 1),
            new(new string('A', 64), -1, 1),
            new(new string('A', 64), ManagedSelectionLogCorrelation.MaximumScriptCharacters + 1, 1),
            new(new string('A', 64), 2, 4),
            new(new string('A', 64), 10, 0)
        };
        foreach (CompiledScriptIdentity? script in invalid)
        {
            ManagedSelectionLogCorrelation correlation = Open();
            AssertEx.False(correlation.TryObserveFirst(21, script, 11, 5, 4, out _));
            AssertEx.False(correlation.TryObserveFirst(22, Script, 11, 5, 4, out _));
            AssertEx.True(correlation.Pending == null);
        }
        AssertEx.False(Open().TryObserveFirst(22, Script, 11, 5, 4, out _));
    }

    public static void FreshSceneDriftOrUnavailableSceneConsumesOnlyThatCandidate()
    {
        EditorPreviewSceneAddress?[] drifts =
        {
            null,
            Scene with { ProjectKey = "other-project" },
            Scene with { NodeGuid = "other-node" },
            Scene with { SceneIndex = 0 },
            Scene with { Fingerprint = "changed-content" }
        };
        foreach (EditorPreviewSceneAddress? live in drifts)
        {
            ManagedSelectionLogCorrelation correlation = Open();
            ManagedSelectionLogCandidate candidate = Capture(correlation);
            AssertEx.False(correlation.TryComplete(candidate, 11, 5, live, out _));
            AssertEx.True(correlation.Pending == null);
            AssertEx.False(correlation.TryComplete(candidate, 11, 5, Scene, out _));
        }
    }

    public static void ConfirmationRejectsWrongThreadAndWindowRegression()
    {
        ManagedSelectionLogCorrelation wrongThread = Open();
        AssertEx.False(wrongThread.TryComplete(Capture(wrongThread), 22, 5, Scene, out _));
        ManagedSelectionLogCorrelation oldWindow = Open();
        AssertEx.False(oldWindow.TryComplete(Capture(oldWindow), 11, 4, Scene, out _));
    }

    public static void SameSelectionCascadeMayFinishBeforeThePumpButPublishesOnce()
    {
        ManagedSelectionLogCorrelation correlation = Open();
        ManagedSelectionLogCandidate candidate = Capture(correlation);
        // Windows 6..8 are observed siblings after the exact first-message
        // capture in 5. They do not themselves create or authorize an identity.
        AssertEx.True(correlation.TryComplete(candidate, 11, 8, Scene, out _));
        AssertEx.False(correlation.TryComplete(candidate, 11, 8, Scene, out _));
        AssertEx.True(correlation.Pending == null);
    }
}
