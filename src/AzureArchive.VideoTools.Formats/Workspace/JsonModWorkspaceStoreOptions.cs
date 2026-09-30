namespace AzureArchive.VideoTools.Formats.Workspace;

public sealed record JsonModWorkspaceStoreOptions(
    long MaxFileSizeBytes = 256L * 1024L,
    int MaxJsonDepth = 16,
    int MaxCapabilities = 256,
    int LockRetryAttempts = 40,
    int LockRetryDelayMilliseconds = 25);
