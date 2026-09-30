namespace AzureArchive.VideoTools.Formats.Aap;

public sealed record AapProjectReaderOptions(
    long MaxFileSizeBytes = 64L * 1024L * 1024L,
    int MaxJsonDepth = 128,
    int StableReadAttempts = 2);
