namespace AzureArchive.VideoTools.Formats.Workspace;

public sealed record JsonCommandWorkspaceStoreOptions(
    long MaxFileSizeBytes = 16L * 1024L * 1024L,
    int MaxJsonDepth = 32,
    int MaxEntries = 100000,
    int MaxCommands = 100000,
    int MaxDirectiveCharacters = 16384,
    int MaxSchemaNameCharacters = 256,
    int LockRetryAttempts = 40,
    int LockRetryDelayMilliseconds = 25);
