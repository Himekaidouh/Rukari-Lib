namespace AzureArchive.VideoTools.Formats.Aas;

public sealed record AasScenarioReaderOptions(
    long MaxFileSizeBytes = 64L * 1024L * 1024L,
    int MaxRecords = 1000000,
    int MaxStringBytes = 16 * 1024 * 1024,
    int StableReadAttempts = 2);
