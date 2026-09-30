using HarmonyLib;
using Rukari.Lib.Tools;
using System.Reflection;

namespace Rukari.Lib.Runtime.Tools;

internal sealed class ToolInputService : IToolInputService, IDisposable
{
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly Dictionary<string, Region> _regions = new(StringComparer.Ordinal);
    private readonly Action<string> _log;
    private Harmony? _harmony;
    private bool _stopped;
    private bool _failureLogged;
    private bool _keyboardCaptured;
    private bool _keyboardWanted;
    private readonly HashSet<string> _keyboardOwners = new(StringComparer.Ordinal);
    private int _probeCount;
    private int _updateLogCount;
    internal bool SupportsKeyboardCapture { get; private set; }
    private static ToolInputService? _installed;

    internal ToolInputService(Action<string> log) => _log = log;

    internal bool Install()
    {
        var method = AccessTools.Method(typeof(UICamera), nameof(UICamera.ProcessMouse), Type.EmptyTypes);
        var prefix = AccessTools.Method(typeof(ToolInputService), nameof(MousePrefix), Type.EmptyTypes);
        if (method is null || prefix is null) return false;
        var harmony = new Harmony("rukari.lib.runtime.shared-tool-input");
        try
        {
            _installed = this;
            harmony.Patch(method, prefix: new HarmonyMethod(prefix));
            InstallKeyboardGuards(harmony);
            _harmony = harmony;
            return true;
        }
        catch (Exception ex)
        {
            harmony.UnpatchSelf();
            if (ReferenceEquals(_installed, this)) _installed = null;
            _log($"Shared tool mouse guard unavailable: {ex}");
            return false;
        }
    }

    private void InstallKeyboardGuards(Harmony harmony)
    {
        // NGUI input fields can consume text independently of UICamera navigation. Do not enable the
        // search box unless every known zero-argument path can be paused, without reading UIInput instances.
        MethodInfo?[] methods =
        {
            AccessTools.Method(typeof(UICamera), nameof(UICamera.ProcessOthers), Type.EmptyTypes),
            AccessTools.Method(typeof(UIInput), nameof(UIInput.Update), Type.EmptyTypes),
            AccessTools.Method(typeof(UIInputOnGUI), nameof(UIInputOnGUI.OnGUI), Type.EmptyTypes)
        };
        MethodInfo? prefix = AccessTools.Method(typeof(ToolInputService), nameof(KeyboardPrefix), Type.EmptyTypes);
        var installed = new List<MethodInfo>();
        try
        {
            if (prefix is null || methods.Any(m => m is null))
                throw new MissingMethodException("A required NGUI keyboard ownership entry point is missing.");
            foreach (MethodInfo? method in methods)
            {
                harmony.Patch(method!, prefix: new HarmonyMethod(prefix));
                installed.Add(method!);
            }
            SupportsKeyboardCapture = true;
        }
        catch (Exception ex)
        {
            SupportsKeyboardCapture = false;
            foreach (MethodInfo method in installed)
            {
                try { harmony.Unpatch(method, HarmonyPatchType.Prefix, harmony.Id); }
                catch (Exception cleanup) { _log($"An inactive search guard could not be removed: {cleanup.Message}"); }
            }
            _log($"Tool search disabled because complete native keyboard ownership could not be installed: {ex.Message}");
        }
    }

    public ModResult<IToolInputRegion> RegisterRegion(string ownerId, string regionId)
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            return ModResult<IToolInputRegion>.Fail(ModErrorCode.WrongThread, "Input registration requires the main thread.");
        if (_stopped || _harmony is null)
            return ModResult<IToolInputRegion>.Fail(ModErrorCode.NotReady, "Shared tool input is unavailable.");
        if (string.IsNullOrWhiteSpace(ownerId) || string.IsNullOrWhiteSpace(regionId))
            return ModResult<IToolInputRegion>.Fail(ModErrorCode.InvalidArgument, "An owner and input region ID are required.");
        string key = ownerId.Length + ":" + ownerId + regionId;
        foreach (string stale in _regions.Where(p => p.Value.Disposed).Select(p => p.Key).ToArray()) _regions.Remove(stale);
        if (_regions.ContainsKey(key))
            return ModResult<IToolInputRegion>.Fail(ModErrorCode.Conflict, "The input region already exists.");
        if (_regions.Count >= 64)
            return ModResult<IToolInputRegion>.Fail(ModErrorCode.Busy, "The shared input region limit was reached.");
        var region = new Region(this, key);
        _regions.Add(key, region);
        return ModResult<IToolInputRegion>.Ok(region);
    }

    public bool CapturesPointer(float x, float y)
    {
        if (_stopped || Environment.CurrentManagedThreadId != _threadId) return false;
        foreach (Region region in _regions.Values)
        {
            if (region.Disposed) continue;
            if (region.CaptureAll || region.Rectangles.Any(r => r.Contains(x, y))) return true;
        }
        return false;
    }

    private static bool MousePrefix()
    {
        ToolInputService? service = _installed;
        if (service is null)
        {
            Probe("prefix with no installed service");
            return true;
        }
        try
        {
            var pointer = UnityEngine.Input.mousePosition;
            bool captured = service.CapturesPointer(pointer.x, pointer.y);
            if (captured) Probe($"guard CONSUMED a pointer at {pointer.x:F0},{pointer.y:F0}");
            return !captured;
        }
        catch (Exception ex)
        {
            if (!service._failureLogged)
            {
                service._failureLogged = true;
                service._log($"Shared input guard failed open: {ex.Message}");
            }
            return true;
        }
    }

    /// <summary>
    /// Diagnostic: reports whether the native mouse guard is reached at all, and how many rectangles it owns.
    /// A guard that is installed but never called looks identical to a working guard with no regions, so the
    /// two cases must be distinguishable from the log alone. Logs at most a handful of times per session.
    /// </summary>
    private static void Probe(string message)
    {
        ToolInputService? service = _installed;
        if (service is null || service._probeCount >= 8) return;
        service._probeCount++;
        int rectangles = 0;
        foreach (Region region in service._regions.Values)
        {
            if (!region.Disposed) rectangles += region.Rectangles.Length;
        }
        service._log($"[input-guard probe {service._probeCount}] {message}; regions={service._regions.Count}; rects={rectangles}");
    }

    /// <summary>Called from the renderer so a guard that is never invoked is visible in the log.</summary>
    internal void ReportPublishedRegions()
    {
        if (_probeCount >= 8) return;
        _probeCount++;
        int rectangles = 0;
        foreach (Region region in _regions.Values)
        {
            if (!region.Disposed) rectangles += region.Rectangles.Length;
        }
        _log($"[input-guard probe {_probeCount}] renderer published regions={_regions.Count}; rects={rectangles}; source={RegionSource()}");
    }

    private string RegionSource()
    {
        foreach (Region region in _regions.Values)
        {
            if (region.Disposed || region.Rectangles.Length == 0) continue;
            ToolInputRect r = region.Rectangles[0];
            return $"{r.X:F0},{r.Y:F0} {r.Width:F0}x{r.Height:F0}";
        }
        return "none";
    }

    internal void BeginFrame()
    {
        if (Environment.CurrentManagedThreadId == _threadId)
            _keyboardCaptured = _keyboardWanted && SupportsKeyboardCapture && !_stopped;
    }

    internal void SetKeyboardCapture(bool capture)
        => SetKeyboardCapture("shared-toolbox", capture);

    internal void SetKeyboardCapture(string ownerId, bool capture)
    {
        if (Environment.CurrentManagedThreadId == _threadId)
        {
            if (string.IsNullOrWhiteSpace(ownerId)) throw new ArgumentException("An input owner is required.", nameof(ownerId));
            if (capture && SupportsKeyboardCapture && !_stopped) _keyboardOwners.Add(ownerId);
            else _keyboardOwners.Remove(ownerId);
            _keyboardWanted = SupportsKeyboardCapture && !_stopped && _keyboardOwners.Count != 0;
            // Acquire immediately; release at the next pump so the closing click or final character
            // cannot reach an original NGUI input field later in this same frame.
            if (_keyboardWanted) _keyboardCaptured = true;
        }
    }

    // No IL2CPP arguments or result are marshalled. NGUI keyboard navigation and text processing
    // pause only while our search holds focus, including its final input frame on close/hide/failure.
    private static bool KeyboardPrefix() => _installed is not { _stopped: false, _keyboardCaptured: true };

    public void Dispose()
    {
        if (_stopped) return;
        _stopped = true;
        _regions.Clear();
        _keyboardOwners.Clear();
        _keyboardWanted = _keyboardCaptured = false;
        if (ReferenceEquals(_installed, this)) _installed = null;
        _harmony?.UnpatchSelf();
        _harmony = null;
    }

    private sealed class Region : IToolInputRegion
    {
        private readonly ToolInputService _owner;
        private readonly string _key;
        private int _disposed;
        internal bool Disposed => Volatile.Read(ref _disposed) != 0;
        internal ToolInputRect[] Rectangles { get; private set; } = Array.Empty<ToolInputRect>();
        internal bool CaptureAll { get; private set; }

        internal Region(ToolInputService owner, string key) { _owner = owner; _key = key; }

        public void Update(IReadOnlyList<ToolInputRect> rectangles, bool captureAllPointer = false)
        {
            if (Disposed || _owner._stopped) return;
            if (Environment.CurrentManagedThreadId != _owner._threadId)
                throw new InvalidOperationException("Input updates require the main thread.");
            ArgumentNullException.ThrowIfNull(rectangles);
            if (rectangles.Count > 128) throw new ArgumentOutOfRangeException(nameof(rectangles));
            ToolInputRect[] accepted = rectangles.Where(r => r.IsValid).ToArray();
            if (_owner._updateLogCount < 6)
            {
                _owner._updateLogCount++;
                var first = rectangles.Count == 0 ? "none" : $"{rectangles[0].X:F0},{rectangles[0].Y:F0} {rectangles[0].Width:F0}x{rectangles[0].Height:F0} valid={rectangles[0].IsValid}";
                _owner._log($"[input-region {_owner._updateLogCount}] key={_key} offered={rectangles.Count} accepted={accepted.Length} first={first}");
            }
            Rectangles = accepted;
            CaptureAll = captureAllPointer;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // Off-thread disposal only marks a managed lease inactive. Never run native teardown here.
            if (Environment.CurrentManagedThreadId == _owner._threadId
                && _owner._regions.TryGetValue(_key, out Region? current) && ReferenceEquals(current, this))
                _owner._regions.Remove(_key);
        }
    }
}
