using System;
using System.Collections.Generic;
using System.Linq;
using AzureArchive.VideoTools.Api;

namespace AzureArchive.VideoTools.Core;

internal sealed class RuntimeCapabilityService : ICapabilityService
{
    private readonly object _lock = new();
    private readonly Dictionary<string, CapabilitySnapshot> _items = new(StringComparer.Ordinal);

    public CapabilitySnapshot Get(string id)
    {
        lock (_lock)
        {
            return _items.TryGetValue(id, out CapabilitySnapshot? value)
                ? value
                : new CapabilitySnapshot(id, CapabilityStatus.Unavailable, "not registered");
        }
    }

    public IReadOnlyList<CapabilitySnapshot> GetAll()
    {
        lock (_lock)
        {
            return _items.Values.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
        }
    }

    public void Bound(string id, string detail)
    {
        Set(id, CapabilityStatus.Bound, detail);
    }

    public void Verified(string id, string detail)
    {
        Set(id, CapabilityStatus.RuntimeVerified, detail);
    }

    public void Unavailable(string id, string detail)
    {
        Set(id, CapabilityStatus.Unavailable, detail);
    }

    public void Degraded(string id, string detail)
    {
        Set(id, CapabilityStatus.Degraded, detail);
    }

    public void Blacklisted(string id, string detail)
    {
        Set(id, CapabilityStatus.Blacklisted, detail);
    }

    private void Set(string id, CapabilityStatus status, string detail)
    {
        lock (_lock)
        {
            _items[id] = new CapabilitySnapshot(id, status, detail);
        }
    }
}
