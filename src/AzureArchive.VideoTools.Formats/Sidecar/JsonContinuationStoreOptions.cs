namespace AzureArchive.VideoTools.Formats.Sidecar;

public sealed record JsonContinuationStoreOptions(
    long MaxFileSizeBytes = 1024L * 1024L,
    int MaxJsonDepth = 32,
    int MaxRules = 10000,
    int LockRetryAttempts = 40,
    int LockRetryDelayMilliseconds = 25);
