using System.Text;

namespace Rukari.Lib.Commands;

/// <summary>Game-free implementation of registration, whole-line extraction and isolated compilation callbacks.</summary>
public sealed class EmbeddedDirectiveService : IEmbeddedDirectiveService, IDisposable
{
    private const int MaximumRoutesPerRegistration = 32;
    private const int MaximumPublishedRoutes = 512;
    private readonly object _gate = new();
    private readonly Func<bool> _isReady;
    private readonly Func<bool> _isMainThread;
    private readonly Action<string>? _log;
    private readonly List<Registration> _registrations = new();
    private bool _stopped;
    private long _revision;

    /// <summary>Creates a registry. Readiness and thread probes must be managed and safe on their calling thread.</summary>
    public EmbeddedDirectiveService(Func<bool> isReady, Func<bool> isMainThread, Action<string>? log)
    {
        _isReady = isReady ?? throw new ArgumentNullException(nameof(isReady));
        _isMainThread = isMainThread ?? throw new ArgumentNullException(nameof(isMainThread));
        _log = log;
    }

    /// <inheritdoc/>
    public long Revision { get { lock (_gate) return _revision; } }

    /// <inheritdoc/>
    public ModResult<IDisposable> Register(string ownerId, IReadOnlyList<string> routes, Action<DirectiveCompilation> callback)
    {
        if (!_isMainThread())
            return ModResult<IDisposable>.Fail(ModErrorCode.WrongThread, "Directive registration requires the main thread.");
        if (string.IsNullOrWhiteSpace(ownerId) || ownerId.Length > 160 || ownerId.Any(char.IsControl)
            || routes is null || routes.Count == 0 || routes.Count > MaximumRoutesPerRegistration || callback is null)
            return Invalid("An owner, callback and between 1 and 32 bounded routes are required.");

        var specs = new List<Route>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (string candidate in routes)
        {
            if (!TryRoute(candidate, ownerId.Trim(), out Route route))
                return Invalid("Routes must be #namespace followed by up to seven semicolon-separated identifier tokens.");
            if (!unique.Add(route.Canonical))
                return ModResult<IDisposable>.Fail(ModErrorCode.Conflict, "A route is repeated in this registration.");
            specs.Add(route);
        }

        lock (_gate)
        {
            if (_stopped || !_isReady())
                return ModResult<IDisposable>.Fail(ModErrorCode.NotReady, "The directive service is not ready or has stopped.");
            if (_registrations.SelectMany(item => item.Routes).Any(route => unique.Contains(route.Canonical)))
                return ModResult<IDisposable>.Fail(ModErrorCode.Conflict, "A directive route already belongs to a live registration.");
            if (_registrations.Sum(item => item.Routes.Length) + specs.Count > MaximumPublishedRoutes)
                return ModResult<IDisposable>.Fail(ModErrorCode.Busy, "The directive registry is full.");
            var registration = new Registration(this, specs.ToArray(), callback);
            _registrations.Add(registration);
            _revision++;
            return ModResult<IDisposable>.Ok(registration);
        }
    }

    /// <inheritdoc/>
    public string Sanitize(string text) => CaptureSanitizer().Sanitize(text);

    /// <inheritdoc/>
    public IEmbeddedDirectiveSanitizer CaptureSanitizer()
    {
        lock (_gate)
            return new Sanitizer(_revision, _stopped || !_isReady()
                ? Array.Empty<Route>() : _registrations.SelectMany(item => item.Routes).ToArray());
    }

    /// <summary>
    /// Adapter entry point. Extracts against one route snapshot, then invokes each still-live registration once,
    /// including registrations with no matching lines. Callback and diagnostic failures never block other mods.
    /// Returns input unchanged after shutdown or before readiness. Calls may originate on a compiler worker.
    /// </summary>
    public string Process(string text, DirectiveCompilationBoundary boundary, long compilationId, bool isAuthoritative)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!Enum.IsDefined(typeof(DirectiveCompilationBoundary), boundary))
            throw new ArgumentOutOfRangeException(nameof(boundary));
        Registration[] registrations;
        lock (_gate)
        {
            if (_stopped || !_isReady()) return text;
            registrations = _registrations.ToArray();
        }

        Route[] routes = registrations.SelectMany(item => item.Routes).ToArray();
        var removed = new List<RemovedDirective>();
        var assignments = new List<Route>();
        string officialText = Extract(text, routes, removed, assignments);
        IReadOnlyList<RemovedDirective> all = removed.AsReadOnly();
        foreach (Registration registration in registrations)
        {
            lock (_gate)
                if (_stopped || registration.Removed || !_isReady()) continue;
            var owned = new List<RemovedDirective>();
            for (int index = 0; index < removed.Count; index++)
                if (Array.IndexOf(registration.Routes, assignments[index]) >= 0) owned.Add(removed[index]);
            try
            {
                registration.Callback(new DirectiveCompilation(text, officialText, all, owned.AsReadOnly(),
                    boundary, compilationId, isAuthoritative));
            }
            catch (Exception exception)
            {
                try { _log?.Invoke($"Directive callback failed for {registration.Routes[0].OwnerId}: {exception}"); }
                catch { /* A logger must not interrupt another mod or the official compiler. */ }
            }
        }
        return officialText;
    }

    /// <summary>Stops dispatch and clears the registry. Previously captured sanitizers remain immutable.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            foreach (Registration registration in _registrations) registration.Removed = true;
            _registrations.Clear();
            _revision++;
        }
    }

    private void Remove(Registration registration)
    {
        lock (_gate)
        {
            if (registration.Removed) return;
            registration.Removed = true;
            if (_registrations.Remove(registration)) _revision++;
        }
    }

    private static ModResult<IDisposable> Invalid(string text) => ModResult<IDisposable>.Fail(ModErrorCode.InvalidArgument, text);

    private static bool TryRoute(string? candidate, string ownerId, out Route route)
    {
        route = null!;
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 160
            || candidate.Any(character => char.IsControl(character) && character != '\t')) return false;
        string[] tokens = candidate.Split(';').Select(token => token.Trim().ToLowerInvariant()).ToArray();
        if (tokens.Length > 8 || tokens[0].Length < 2 || tokens[0][0] != '#') return false;
        for (int index = 0; index < tokens.Length; index++)
        {
            string identifier = index == 0 ? tokens[index][1..] : tokens[index];
            if (identifier.Length == 0 || identifier.Length > 64 || !AsciiLetterOrDigit(identifier[0])
                || identifier.Any(character => !AsciiLetterOrDigit(character) && character != '.' && character != '_' && character != '-'))
                return false;
        }
        route = new Route(string.Join(";", tokens), ownerId, tokens);
        return true;
    }

    private static bool AsciiLetterOrDigit(char character)
        => character >= 'a' && character <= 'z' || character >= '0' && character <= '9';

    private static string Extract(string text, Route[] routes, List<RemovedDirective>? removed = null,
        List<Route>? assignments = null, HashSet<string>? retainedOwners = null)
    {
        if (text.Length == 0 || routes.Length == 0) return text;
        StringBuilder? output = null;
        int cursor = 0, lineNumber = 1;
        while (cursor < text.Length)
        {
            int start = cursor;
            while (cursor < text.Length && text[cursor] != '\r' && text[cursor] != '\n') cursor++;
            int lineEnd = cursor;
            if (cursor < text.Length && text[cursor++] == '\r' && cursor < text.Length && text[cursor] == '\n') cursor++;
            ReadOnlySpan<char> line = text.AsSpan(start, lineEnd - start);
            Route? match = null;
            foreach (Route route in routes)
                if ((match is null || route.Tokens.Length > match.Tokens.Length) && Matches(line, route.Tokens)) match = route;
            if (match is not null && (retainedOwners is null || !retainedOwners.Contains(match.OwnerId)))
            {
                if (output is null) { output = new StringBuilder(text.Length); output.Append(text, 0, start); }
                removed?.Add(new RemovedDirective(text.Substring(start, lineEnd - start), lineNumber, match.OwnerId, match.Canonical));
                assignments?.Add(match);
            }
            else output?.Append(text, start, cursor - start);
            lineNumber++;
        }
        return output?.ToString() ?? text;
    }

    private static bool Matches(ReadOnlySpan<char> line, string[] tokens)
    {
        for (int index = 0; index < tokens.Length; index++)
        {
            int delimiter = line.IndexOf(';');
            ReadOnlySpan<char> token = (delimiter < 0 ? line : line[..delimiter]).Trim();
            if (!token.Equals(tokens[index].AsSpan(), StringComparison.OrdinalIgnoreCase)) return false;
            if (index == tokens.Length - 1) return true;
            if (delimiter < 0) return false;
            line = line[(delimiter + 1)..];
        }
        return false;
    }

    // Immutable metadata only: a sanitizer snapshot never retains callbacks or plugin registrations.
    private sealed record Route(string Canonical, string OwnerId, string[] Tokens);

    private sealed class Sanitizer : IEmbeddedDirectiveSanitizer
    {
        private readonly Route[] _routes;
        public Sanitizer(long revision, Route[] routes) => (Revision, _routes) = (revision, routes);
        public long Revision { get; }
        public string Sanitize(string text) { ArgumentNullException.ThrowIfNull(text); return Extract(text, _routes); }
        public string SanitizeExceptOwners(string text, IReadOnlyList<string> ownerIds)
        {
            ArgumentNullException.ThrowIfNull(text);
            ArgumentNullException.ThrowIfNull(ownerIds);
            var retainedOwners = new HashSet<string>(StringComparer.Ordinal);
            foreach (string ownerId in ownerIds)
            {
                if (string.IsNullOrWhiteSpace(ownerId))
                    throw new ArgumentException("Retained owner IDs must be nonempty registration IDs.", nameof(ownerIds));
                retainedOwners.Add(ownerId.Trim());
            }
            return Extract(text, _routes, retainedOwners: retainedOwners);
        }
    }

    private sealed class Registration : IDisposable
    {
        private readonly EmbeddedDirectiveService _owner;
        public Registration(EmbeddedDirectiveService owner, Route[] routes, Action<DirectiveCompilation> callback)
            => (_owner, Routes, Callback) = (owner, routes, callback);
        public Route[] Routes { get; }
        public Action<DirectiveCompilation> Callback { get; }
        public bool Removed { get; set; }
        public void Dispose() => _owner.Remove(this);
    }
}
