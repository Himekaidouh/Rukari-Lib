using Rukari.CharacterVoice.Runtime;

internal static class VoiceFolderTests
{
    internal static void FolderScanAcceptsSupportedFormatsAndKeepsDuplicateNamesDistinct()
    {
        using var folder = new TemporaryFolder();
        folder.Write("hello.WAV");
        folder.Write("music.ogg");
        folder.Write("ignored.flac");
        folder.Write("empty.mp3", Array.Empty<byte>());
        folder.Write("nested/hello.WAV");
        folder.Write("nested/second.MP3");
        var flat = VoiceFolderScanner.Scan(folder.Root, false, CancellationToken.None);
        Equal(2, flat.Files.Count);
        Equal("hello.WAV", flat.Files[0].RelativePath);
        Equal(".wav", flat.Files[0].Extension);
        var recursive = VoiceFolderScanner.Scan(folder.Root, true, CancellationToken.None);
        Equal(4, recursive.Files.Count);
        True(recursive.Files.Any(file => file.RelativePath == "nested/hello.WAV"));
        True(recursive.Files.Any(file => file.RelativePath == "hello.WAV"));
        Equal(2, recursive.SkippedFiles);
        True(recursive.Files.All(file => File.Exists(file.FullPath)));
    }

    internal static void ScanCancellationAndLimitsNeverPublishAPartialFolder()
    {
        using var folder = new TemporaryFolder();
        folder.Write("hello.wav");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Throws<OperationCanceledException>(() => VoiceFolderScanner.Scan(folder.Root, true, cancellation.Token));
        for (int i = 0; i < VoiceFolderScanner.MaximumFiles; i++) folder.Write(i + ".wav");
        Throws<IOException>(() => VoiceFolderScanner.Scan(folder.Root, false, CancellationToken.None));
        Equal(VoiceFolderScanner.MaximumFiles + 1, Directory.GetFiles(folder.Root).Length);
    }

    internal static void CancelledOrFailedFolderChoicePreservesThePreviousList()
    {
        using var folder = new TemporaryFolder();
        folder.Write("hello.wav");
        var picker = new TestPicker();
        using var controller = new VoiceFolderController(picker);
        picker.Next = Task.FromResult<string?>(folder.Root);
        controller.Choose();
        Drain(controller);
        var original = controller.Current;
        True(original is not null && original.Files.Count == 1);
        int revision = controller.Revision;

        picker.Next = Task.FromResult<string?>(null);
        controller.Choose();
        Drain(controller);
        True(ReferenceEquals(original, controller.Current));
        Equal(revision, controller.Revision);

        picker.Next = Task.FromResult<string?>(Path.Combine(folder.Root, "missing"));
        controller.Choose();
        Drain(controller);
        True(ReferenceEquals(original, controller.Current));
        Equal(revision, controller.Revision);
        True(controller.Status.StartsWith("无法读取文件夹", StringComparison.Ordinal));
    }

    internal static void FolderChoiceRunsOffTheCallerThreadAndAllowsCancellation()
    {
        var choice = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var picker = new TestPicker { Next = choice.Task };
        using var controller = new VoiceFolderController(picker);
        int callerThread = Environment.CurrentManagedThreadId;
        controller.Choose();
        controller.Choose();
        True(controller.IsBusy);
        True(SpinWait.SpinUntil(() => Volatile.Read(ref picker.Calls) > 0, TimeSpan.FromSeconds(3)));
        Equal(1, Volatile.Read(ref picker.Calls));
        True(picker.ThreadId != callerThread);
        controller.Cancel();
        Drain(controller);
        True(controller.Current is null && controller.Status.StartsWith("已取消", StringComparison.Ordinal));
    }

    internal static void UnloadDiscardsLateFolderResults()
    {
        var choice = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var picker = new TestPicker { Next = choice.Task };
        int scans = 0;
        var controller = new VoiceFolderController(picker, (root, recursive, token) =>
        {
            Interlocked.Increment(ref scans);
            return new(root, recursive, Array.Empty<VoiceFolderFile>(), 0);
        });
        controller.Choose();
        True(SpinWait.SpinUntil(() => Volatile.Read(ref picker.Calls) > 0, TimeSpan.FromSeconds(3)));
        controller.Dispose();
        choice.TrySetResult("unused");
        controller.Poll();
        Equal(0, Volatile.Read(ref scans));
        True(controller.Current is null);
    }

    internal static void PickerCancellationCompletesWhileNativeWorkerIsStillOpening()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        Task waiting = WindowsVoiceFolderPicker.RunOnStaAsync(() =>
        {
            started.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return "late-folder";
        }, cancellation.Token, finished.Set);
        try
        {
            True(started.Wait(TimeSpan.FromSeconds(3))); cancellation.Cancel();
            True(SpinWait.SpinUntil(() => waiting.IsCompleted, TimeSpan.FromSeconds(3)));
            Throws<OperationCanceledException>(() => waiting.GetAwaiter().GetResult());
            True(!finished.IsSet);
        }
        finally { release.Set(); True(finished.Wait(TimeSpan.FromSeconds(3))); }
    }

    internal static void NativeFolderWorkerUsesStaAndPropagatesFailures()
    {
        int caller = Environment.CurrentManagedThreadId;
        var work = WindowsVoiceFolderPicker.RunOnStaAsync(() =>
        {
            True(Environment.CurrentManagedThreadId != caller);
            Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
            return "C:\\audio ' 中文";
        }, default);
        Equal("C:\\audio ' 中文", work.GetAwaiter().GetResult());
        var failed = WindowsVoiceFolderPicker.RunOnStaAsync(() => throw new IOException("native-dialog-test"), default);
        try { failed.GetAwaiter().GetResult(); throw new InvalidOperationException("Expected failure"); }
        catch (IOException ex) { Equal("native-dialog-test", ex.Message); }

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool started = false;
        Throws<OperationCanceledException>(() => WindowsVoiceFolderPicker.RunOnStaAsync(() =>
        {
            started = true;
            return null;
        }, cancellation.Token));
        True(!started);
    }

    internal static void NativeFolderComSetupWorksWithoutOpeningAWindow()
    {
        // Exercise real Windows COM and the production event sink/vtable. Never call Show.
        WindowsVoiceFolderPicker.RunOnStaAsync(() =>
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            var type = typeof(WindowsVoiceFolderPicker);
            const System.Reflection.BindingFlags staticFlags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            const System.Reflection.BindingFlags instanceFlags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            int hr = (int)type.GetMethod("CoInitializeEx", staticFlags)!.Invoke(null, new object[] { IntPtr.Zero, (uint)2 })!;
            System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(hr);
            object? dialog = null;
            object?[] args = { CancellationToken.None, (Action)(() => { }), null, (uint)0 };
            var contract = type.GetNestedType("IFileDialog", System.Reflection.BindingFlags.NonPublic)!;
            try
            {
                dialog = type.GetMethod("CreateDialog", instanceFlags)!.Invoke(new WindowsVoiceFolderPicker(), args)!;
                object[] options = { (uint)0 };
                hr = (int)contract.GetMethod("GetOptions")!.Invoke(dialog, options)!;
                System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(hr);
                Equal((uint)0x860, (uint)options[0] & 0x860); // Existing filesystem folders.
                Equal((uint)0x02000008, (uint)options[0] & 0x02000008); // No working-directory change or recent-documents entry.
            }
            finally
            {
                try
                {
                    if (dialog is not null)
                    {
                        try
                        {
                            hr = (int)contract.GetMethod("Unadvise")!.Invoke(dialog, new[] { args[3] })!;
                            System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(hr);
                            GC.KeepAlive(args[2]);
                        }
                        finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(dialog); }
                    }
                }
                finally { type.GetMethod("CoUninitialize", staticFlags)!.Invoke(null, null); }
            }
            return "native-setup-ok";
        }, default).GetAwaiter().GetResult();
    }

    internal static void ClearAndDisposeDiscardAnAlreadyRunningScannersLateResult()
    {
        foreach (bool dispose in new[] { false, true })
        {
            using var started = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var completed = new ManualResetEventSlim();
            using var controller = new VoiceFolderController(new TestPicker { Next = Task.FromResult<string?>("unused") },
                (root, recursive, token) =>
                {
                    started.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                    var result = new VoiceFolderScan(root, recursive, new[] { new VoiceFolderFile("unused", "late.wav", 44, ".wav") }, 0);
                    completed.Set();
                    return result; // Intentionally ignores cancellation to simulate a blocked OS call.
                });
            try
            {
                controller.Choose();
                True(started.Wait(TimeSpan.FromSeconds(3)));
                if (dispose) controller.Dispose();
                else { controller.Clear(); Drain(controller); }
                release.Set();
                True(completed.Wait(TimeSpan.FromSeconds(3)));
                controller.Poll();
                True(controller.Current is null);
                if (!dispose) True(controller.Status.StartsWith("项目已切换", StringComparison.Ordinal));
            }
            finally { release.Set(); }
        }
    }

    internal static void ScannerRejectsLinkedAncestorsAndSkipsLinkedChildrenWhenSupported()
    {
        using var folder = new TemporaryFolder();
        folder.Write("actual/sub/hello.wav");
        string link = Path.Combine(folder.Root, "alias");
        try { Directory.CreateSymbolicLink(link, Path.Combine(folder.Root, "actual")); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            Console.WriteLine("SKIP scanner symlink fixture: platform permission unavailable");
            return;
        }
        try
        {
            Throws<IOException>(() => VoiceFolderScanner.Scan(Path.Combine(link, "sub"), false, CancellationToken.None));
            VoiceFolderScan scanned = VoiceFolderScanner.Scan(folder.Root, true, CancellationToken.None);
            Equal(1, scanned.Files.Count);
            Equal("actual/sub/hello.wav", scanned.Files[0].RelativePath);
        }
        finally { Directory.Delete(link); }
    }

    private static void Drain(VoiceFolderController controller)
    {
        True(SpinWait.SpinUntil(() => { controller.Poll(); return !controller.IsBusy; }, TimeSpan.FromSeconds(5)));
    }

    private sealed class TestPicker : IVoiceFolderPicker
    {
        internal Task<string?> Next = Task.FromResult<string?>(null);
        internal int Calls;
        internal int ThreadId;
        public Task<string?> PickAsync(CancellationToken cancellationToken)
        {
            ThreadId = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref Calls);
            return Next.WaitAsync(cancellationToken);
        }
    }

    private sealed class TemporaryFolder : IDisposable
    {
        internal string Root { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "RukariVoiceTests-" + Guid.NewGuid().ToString("N")));
        internal TemporaryFolder() => Directory.CreateDirectory(Root);
        internal void Write(string relative, byte[]? bytes = null)
        {
            string file = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, bytes ?? new byte[] { 1 });
        }
        public void Dispose()
        {
            string allowedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Root.StartsWith(allowedParent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(Root).StartsWith("RukariVoiceTests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Temporary test directory left its allowed parent.");
            Directory.Delete(Root, recursive: true);
        }
    }

    private static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, actual {actual}.");
    }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
