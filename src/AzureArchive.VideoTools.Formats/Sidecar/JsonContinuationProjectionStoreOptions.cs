namespace AzureArchive.VideoTools.Formats.Sidecar;

public sealed record JsonContinuationProjectionStoreOptions(
    long MaxFileSizeBytes = 16L * 1024L * 1024L,
    int MaxJsonDepth = 32,
    int MaxInstructions = 10000,
    int MaxTextCharacters = 1000000,
    int MaxSchemaNameCharacters = 256,
    int LockRetryAttempts = 40,
    int LockRetryDelayMilliseconds = 25);
