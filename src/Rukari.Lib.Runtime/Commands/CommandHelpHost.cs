using Rukari.Lib.Commands;

namespace Rukari.Lib.Runtime.Commands;

/// <summary>
/// Owns the shared command-help registry and the one patch that publishes its rows into the editor's own
/// 「指令格式」 panel. Mods publish rows; nothing here edits an official row, layout or behaviour.
/// </summary>
public static class CommandHelpHost
{
    private static CommandHelpService? _service;
    private static IDisposable? _registration;

    /// <summary>Registers the service and installs the publishing patch, on the game main thread.</summary>
    public static void Initialize(IModRuntime runtime, bool enabled, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(log);
        if (_service is not null) return;
        if (!runtime.IsMainThread) throw new InvalidOperationException("Command help initialization requires the game main thread.");
        if (!enabled)
        {
            // Off by default: the panel builds its rows with expectations only its own entries satisfy, and
            // publishing means writing into its entry field. Until that shape is understood, the editor's
            // command-format panel stays exactly as shipped.
            log("Command help disabled by configuration; the editor's own command-format panel is untouched.");
            return;
        }
        var service = new CommandHelpService(log);
        if (!CommandHelpPatch.Install(service.Snapshot, log))
        {
            service.Dispose();
            log("Command help disabled: the editor's command-format panel was not found; other lib services remain active.");
            return;
        }
        var registered = runtime.RegisterService<ICommandHelpService>("rukari.lib.runtime", service,
            new CapabilityInfo(CommandHelpCapabilities.CommandHelp, "rukari.lib.runtime", "0.2.0", CapabilityLevel.Experimental,
                "Adds a mod's own directives to the editor's command-format help; additive rows only, official rows and layout untouched."));
        if (!registered.Success)
        {
            CommandHelpPatch.Uninstall();
            service.Dispose();
            log(registered.Error!.Message);
            return;
        }
        _service = service;
        _registration = registered.Value;
        log("Command help service registered; enabled mods publish their own directives into the editor's command-format panel.");
    }

    /// <summary>Removes every published row and the patch, on the game main thread.</summary>
    public static void Shutdown()
    {
        _registration?.Dispose(); _registration = null;
        CommandHelpPatch.Uninstall();
        _service?.Dispose(); _service = null;
    }
}

internal sealed class CommandHelpService : ICommandHelpService, IDisposable
{
    private readonly Action<string> _log;
    private readonly List<CommandHelpEntry> _rows = new();
    private readonly HashSet<string> _syntaxes = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    internal CommandHelpService(Action<string> log) { _log = log; }

    /// <summary>A copy of the published rows, safe to read from the panel's own call.</summary>
    internal IReadOnlyList<CommandHelpEntry> Snapshot()
    {
        lock (_rows) return _rows.ToArray();
    }

    public ModResult<IDisposable> Register(string section, string syntax, string description)
    {
        lock (_rows)
        {
            if (_disposed) return ModResult<IDisposable>.Fail(ModErrorCode.NotReady, "指令帮助服务已停止。");
            if (_rows.Count >= CommandHelpPolicy.MaximumPublishedRows)
                return ModResult<IDisposable>.Fail(ModErrorCode.Busy, "指令帮助条目已达上限。");
            if (!CommandHelpPolicy.TryNormalize(section, syntax, description, out CommandHelpEntry entry, out string error))
                return ModResult<IDisposable>.Fail(ModErrorCode.InvalidArgument, error);
            if (!_syntaxes.Add(entry.Syntax))
                return ModResult<IDisposable>.Fail(ModErrorCode.Conflict, "该指令语法已发布：" + entry.Syntax);
            _rows.Add(entry);
            _log("指令帮助已登记：" + entry.Section + " · " + entry.Syntax);
            return ModResult<IDisposable>.Ok(new Lease(this, entry));
        }
    }

    public void Dispose()
    {
        lock (_rows) { _disposed = true; _rows.Clear(); _syntaxes.Clear(); }
    }

    private void Remove(CommandHelpEntry entry)
    {
        lock (_rows)
        {
            if (_rows.Remove(entry)) _syntaxes.Remove(entry.Syntax);
        }
    }

    private sealed class Lease : IDisposable
    {
        private CommandHelpService? _owner;
        private readonly CommandHelpEntry _entry;

        internal Lease(CommandHelpService owner, CommandHelpEntry entry) { _owner = owner; _entry = entry; }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Remove(_entry);
    }
}
