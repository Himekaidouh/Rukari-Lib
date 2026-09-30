using Rukari.CharacterVoice.Core;

namespace Rukari.CharacterVoice.Runtime;

internal sealed record VoiceFolderFile(string FullPath, string RelativePath, long Length, string Extension, long LastWriteTimeUtcTicks = 0);
internal sealed record VoiceFolderScan(string RootPath, bool Recursive, IReadOnlyList<VoiceFolderFile> Files, int SkippedFiles);

/// <summary>Bounded, read-only scanning. No native objects or manifest writes are involved.</summary>
internal static class VoiceFolderScanner
{
    internal const int MaximumFiles = 2048;
    internal const int MaximumEntries = 50000;
    internal const int MaximumDepth = 16;
    internal const long MaximumFileBytes = 256L * 1024 * 1024;
    internal const long MaximumTotalBytes = 2L * 1024 * 1024 * 1024;

    internal static VoiceFolderScan Scan(string folder, bool recursive, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("请先选择语音文件夹。");
        string root = CheckedDirectory(folder);
        var files = new List<VoiceFolderFile>();
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        int entries = 0, skipped = 0;
        long totalBytes = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            try
            {
                // A queued child may have become a junction since discovery. Use the import
                // store's full ancestor policy again immediately before every directory traversal.
                CheckedDirectory(directory.Path);
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory.Path))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++entries > MaximumEntries)
                        throw new ScanLimitException("所选目录过大，请选择更具体的语音文件夹。");
                    try
                    {
                        FileAttributes attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if (recursive)
                            {
                                if (directory.Depth >= MaximumDepth) { skipped++; continue; }
                                pending.Push((entry, directory.Depth + 1));
                            }
                            continue;
                        }
                        string extension = Path.GetExtension(entry).ToLowerInvariant();
                        if (extension is not (".wav" or ".ogg" or ".mp3")) { skipped++; continue; }
                        CheckedDirectory(Path.GetDirectoryName(entry)!);
                        var info = new FileInfo(entry);
                        long length = info.Length;
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                        if (length <= 0 || length > MaximumFileBytes) { skipped++; continue; }
                        if (files.Count >= MaximumFiles || length > MaximumTotalBytes - totalBytes)
                            throw new ScanLimitException("音频超过 2048 个或合计 2 GB，请选择更小的文件夹。");
                        string fullPath = Path.GetFullPath(entry);
                        string relative = Path.GetRelativePath(root, fullPath);
                        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
                            throw new IOException("音频路径超出所选目录。");
                        files.Add(new(fullPath, relative.Replace('\\', '/'), length, extension, info.LastWriteTimeUtc.Ticks));
                        totalBytes += length;
                    }
                    catch (ScanLimitException) { throw; }
                    catch (IOException) { skipped++; }
                    catch (UnauthorizedAccessException) { skipped++; }
                }
            }
            catch (ScanLimitException) { throw; }
            catch (IOException) when (directory.Depth > 0) { skipped++; }
            catch (UnauthorizedAccessException) when (directory.Depth > 0) { skipped++; }
        }
        files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
        return new(root, recursive, files.AsReadOnly(), skipped);
    }

    private static string CheckedDirectory(string path)
    {
        try { return ProjectVoiceImportPolicy.NormalizeProjectRoot(path); }
        catch (IOException ex) { throw new IOException("语音文件夹不可读取，或其路径包含目录链接：" + ex.Message, ex); }
    }

    private sealed class ScanLimitException : IOException
    {
        internal ScanLimitException(string message) : base(message) { }
    }
}
