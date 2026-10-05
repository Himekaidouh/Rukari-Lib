using AzureArchive.VideoTools.Core.Commands;

namespace AzureArchive.VideoTools.Core.Cameras;

/// <summary>Failure evidence returned by one camera application, never stored on the service.</summary>
public readonly record struct SceneCameraMutationFailureEvidence(
    SceneCameraMotionFields AttemptedFields,
    bool HasResidualMutation);

/// <summary>Tracks the composition fields involved in setters that actually began.</summary>
public sealed class SceneCameraMutationAttempt
{
    private const SceneCameraMotionFields OverallFields =
        SceneCameraMotionFields.OverallPosition | SceneCameraMotionFields.OverallZoom;
    private const SceneCameraMotionFields BackgroundFields =
        SceneCameraMotionFields.BackgroundPosition | SceneCameraMotionFields.BackgroundZoom;
    private const SceneCameraMotionFields ZoomFields =
        SceneCameraMotionFields.OverallZoom | SceneCameraMotionFields.BackgroundZoom;

    private readonly SceneCameraMotionFields _fields;
    private SceneCameraMotionFields _attemptedFields;

    public SceneCameraMutationAttempt(SceneCameraMotionFields fields) =>
        _fields = fields & (OverallFields | BackgroundFields);

    public void BeginPositionWrite(bool isBack) =>
        _attemptedFields |= _fields & (isBack ? OverallFields | BackgroundFields : OverallFields);

    public void BeginScaleWrite(bool isBack) =>
        _attemptedFields |= _fields & (isBack ? ZoomFields : SceneCameraMotionFields.OverallZoom);

    public SceneCameraMutationFailureEvidence Evidence(bool hasResidualMutation) =>
        new(_attemptedFields, hasResidualMutation && _attemptedFields != SceneCameraMotionFields.None);

    public static IReadOnlyList<int> CleanupResourceSlots(SceneCameraMutationFailureEvidence failure)
    {
        if (!failure.HasResidualMutation) return Array.Empty<int>();

        var resources = new List<int>(2);
        if ((failure.AttemptedFields & OverallFields) != 0)
            resources.Add(SceneCameraCommandFamilyCompiler.SingletonResourceSlot);
        if ((failure.AttemptedFields & BackgroundFields) != 0)
            resources.Add(SceneCameraCommandFamilyCompiler.BackgroundResourceSlot);
        return resources;
    }
}
