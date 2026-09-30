using System;
using AzureArchive.VideoTools.Api;
using AzureArchive.VideoTools.Core;

namespace AzureArchive.VideoTools.Interop;

internal sealed class ManagedSceneIdentityService : IEditorSceneIdentityService
{
    private readonly object _lock = new();
    private readonly RuntimeCapabilityService _capabilities;
    private EditorSceneIdentitySnapshot? _current;
    private long _observationSequence;

    public ManagedSceneIdentityService(RuntimeCapabilityService capabilities)
    {
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _capabilities.Bound(
            "Editor.ManagedSceneIdentity",
            "managed immutable snapshot service initialized; no IL2CPP object is retained");
    }

    public EditorSceneIdentitySnapshot? Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    internal EditorSceneIdentitySnapshot Publish(
        int selectionRequestId,
        string compiledScriptSha256,
        int compiledScriptLength,
        int compiledScriptLineCount)
    {
        if (compiledScriptSha256.Length != 64)
        {
            throw new ArgumentException(
                "A complete SHA-256 digest is required.",
                nameof(compiledScriptSha256));
        }

        EditorSceneIdentitySnapshot snapshot;
        lock (_lock)
        {
            snapshot = new EditorSceneIdentitySnapshot(
                ++_observationSequence,
                selectionRequestId,
                compiledScriptSha256,
                compiledScriptLength,
                compiledScriptLineCount);
            _current = snapshot;
        }

        _capabilities.Verified(
            "Editor.ManagedSceneIdentity",
            "full compiled-script SHA-256 published from managed BepInEx log data");
        return snapshot;
    }
}
