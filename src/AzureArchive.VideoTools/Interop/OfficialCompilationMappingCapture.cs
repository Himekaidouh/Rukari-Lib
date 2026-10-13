using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using AzureArchive.Automation;
using AzureArchive.VideoTools.Core.Commands;
using AzureArchive.VideoTools.Core.Projects;
using AzureArchive.VideoTools.Core.Playback;
using AzureArchive.VideoTools.Core.Voices;
using AzureArchive.VideoTools.Formats.Aap;
using AzureArchive.VideoTools.Formats.Aas;
using Google.FlatBuffers;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Studio.Scripts;

namespace AzureArchive.VideoTools.Interop;

/// <summary>Leaf values observed at an actual official script export boundary.</summary>
internal sealed record OfficialCompiledSceneRecord(
    SceneKey Scene,
    int PlaybackRecordIndex,
    string SourceAdditionalPrompt,
    int ObservedTableOffset);

internal sealed record OfficialCompilationMappingCaptureReceipt(
    ProjectSnapshot Project,
    PlaybackArchiveSnapshot Playback,
    IReadOnlyList<OfficialCompiledSceneRecord> Records);

/// <summary>
/// Observes an official compilation and its successful safe-save. A source scene is accepted only
/// when its exact Script wrapper occurs in BuildVertices and ParseScript adds exactly one table.
/// Table offsets are resolved through the output FlatBuffer vector, never inferred from call order.
/// All wrappers stay inside synchronous callbacks; the scope retains only managed copies and pointers.
/// </summary>
internal static class OfficialCompilationMappingCapture
{
    private const string HarmonyId = "rukari.moreeffects.official-compilation-mapping";
    // An empty dialogue card that contains only a registered AAVT directive can
    // be treated as a no-op by the official compiler.  Keep the author-visible
    // dialogue empty, but give the official ScriptKr one recognised 1 ms
    // wait so it emits a stable AAS row.  The marker is added to the managed
    // ScriptKr result only; authoring text and additionalPrompt stay unchanged.
    private const string EmptyDialogueAnchor = "#wait;1";
    private const int MaximumScenes = 100000;
    private const int MaximumOutputBytes = 64 * 1024 * 1024;
    private static Harmony? _harmony;
    private static Action<OfficialCompilationMappingCaptureReceipt>? _accept;
    private static int _mainThread;
    [ThreadStatic] private static RootCapture? _root;
    [ThreadStatic] private static ByteCapture? _bytes;

    internal static bool Install(Action<OfficialCompilationMappingCaptureReceipt> accept)
    {
        if (_harmony != null) return true;
        _accept = accept ?? throw new ArgumentNullException(nameof(accept));
        _mainThread = Environment.CurrentManagedThreadId;
        var harmony = new Harmony(HarmonyId);
        try
        {
            HookRoot(harmony, Require(typeof(StudioCommon), "Compile", Type.EmptyTypes));
            HookRoot(harmony, Require(typeof(StudioCommon), "Compile", new[] { typeof(string) }));
            HookRoot(harmony, Require(typeof(AuthoringCompiler), "Compile", new[]
                { typeof(ProjectData), typeof(string), typeof(string), typeof(string) }));
            HookRoot(harmony, Require(typeof(AuthoringCompiler), "CompileToSaves", new[]
                { typeof(ProjectData), typeof(string), typeof(string), typeof(string) }));
            MethodInfo scriptKrGetter = AccessTools.PropertyGetter(typeof(Script), nameof(Script.ScriptKr))
                ?? throw new MissingMethodException(typeof(Script).FullName, nameof(Script.ScriptKr));
            harmony.Patch(
                scriptKrGetter,
                postfix: new HarmonyMethod(typeof(OfficialCompilationMappingCapture), nameof(ScriptKrPostfix))
                {
                    priority = Priority.First
                });
            MethodInfo[] byteMethods = typeof(AuthoringCompiler).GetMethods(BindingFlags.Public | BindingFlags.Static)
                         .Where(method => method.Name == "CompileBytes"
                             && method.ReturnType == typeof(Il2CppStructArray<byte>)
                             && method.GetParameters().Length is 1 or 2
                             && method.GetParameters()[0].ParameterType == typeof(ProjectData)).ToArray();
            if (byteMethods.Length == 0) throw new MissingMethodException(typeof(AuthoringCompiler).FullName, "CompileBytes");
            foreach (MethodInfo method in byteMethods)
            {
                harmony.Patch(method,
                    prefix: Callback(nameof(BytesPrefix)), postfix: Callback(nameof(BytesPostfix)),
                    finalizer: Callback(nameof(BytesFinalizer)));
            }
            harmony.Patch(Require(typeof(AuthoringCompiler), "BuildVertices", new[] { typeof(ProjectData) }),
                postfix: Callback(nameof(VerticesPostfix)));
            Type[] parseArguments = { typeof(Script), typeof(Il2CppSystem.Collections.Generic.List<Offset<GenericScenarioExcel>>),
                typeof(FlatBufferBuilder), typeof(long) };
            foreach (string methodName in new[] { "ParseScript", "ParseFunctionScript" })
                harmony.Patch(Require(typeof(StudioCommon), methodName, parseArguments),
                    prefix: Callback(nameof(ParsePrefix)), postfix: Callback(nameof(ParsePostfix)),
                    finalizer: Callback(nameof(ParseFinalizer)));
            harmony.Patch(Require(typeof(Utils.Util), "PromoteSafeSave", new[] { typeof(string), typeof(string) }),
                prefix: Callback(nameof(PromotionPrefix)), postfix: Callback(nameof(PromotionPostfix)));
            _harmony = harmony;
            Log("installed; output offsets and publication hashes are required; native acceptance pending");
            return true;
        }
        catch (Exception ex)
        {
            try { harmony.UnpatchSelf(); } catch { }
            _accept = null;
            Log("unavailable: " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    internal static void Stop()
    {
        _accept = null;
        Harmony? harmony = _harmony;
        _harmony = null;
        try { harmony?.UnpatchSelf(); } catch (Exception ex) { Log("cleanup: " + ex.Message); }
        _root = null;
        _bytes = null;
    }

    private static void HookRoot(Harmony harmony, MethodInfo method) => harmony.Patch(method,
        prefix: Callback(nameof(RootPrefix)), finalizer: Callback(nameof(RootFinalizer)));

    private static HarmonyMethod Callback(string name) => new(typeof(OfficialCompilationMappingCapture), name);

    private static MethodInfo Require(Type type, string name, Type[] arguments) =>
        type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance,
            null, arguments, null) ?? throw new MissingMethodException(type.FullName, name);

    private static void RootPrefix(out RootScope? __state)
    {
        __state = null;
        if (Environment.CurrentManagedThreadId != _mainThread) return;
        try
        {
            if (_root != null) { __state = new RootScope(_root, false); return; }
            AuthoringEditorSession? session = AuthoringEditorSession.Current;
            if (!InteropObjectGuard.IsAlive(session)) { Log("no saved editor session; proof omitted"); return; }
            string file = session!.FilePath ?? string.Empty;
            string source = session.SourcePath ?? string.Empty;
            string path = ExistingRootedFile(file) ?? ExistingRootedFile(source) ?? string.Empty;
            if (path.Length == 0) { Log("no rooted saved source path; proof omitted"); return; }
            var compatible = OfficialAapCompatibilityProjection.Resolve(path);
            if (!compatible.Success || compatible.Value == null)
            { Log("saved source projection unavailable; proof omitted: " + compatible.Error); return; }
            path = compatible.Value.ProjectPath;
            string? other = ExistingRootedFile(source);
            if (other != null)
            {
                var otherProjection = OfficialAapCompatibilityProjection.Resolve(other);
                if (!otherProjection.Success || otherProjection.Value == null)
                { Log("editor source projection unavailable; proof omitted: " + otherProjection.Error); return; }
                if (!path.Equals(otherProjection.Value.ProjectPath, StringComparison.OrdinalIgnoreCase))
                { Log("editor source/file paths disagree; proof omitted"); return; }
            }
            var read = new AapProjectReader().Read(path);
            if (!read.Success || read.Value == null) { Log("saved source unreadable; proof omitted: " + read.Error); return; }
            _root = new RootCapture(read.Value);
            __state = new RootScope(_root, true);
        }
        catch (Exception ex) { Log("begin failed; proof omitted: " + ex.Message); }
    }

    private static void RootFinalizer(RootScope? __state, Exception? __exception)
    {
        if (__state == null) return;
        RootCapture root = __state.Root;
        if (__exception != null) root.Fail("official compile threw " + __exception.GetType().Name);
        if (!__state.Owner) return;
        try
        {
            if (!root.Failed && root.Pending.Count == 0)
                Log(root.SawParse ? "no verified publication; proof omitted" : "no observed script emissions; proof omitted");
            foreach (PendingPublication pending in root.Pending)
            {
                if (root.Failed) break;
                var project = new AapProjectReader().Read(root.Project.Source.FullPath);
                var playback = new AasScenarioReader().Read(pending.Destination);
                if (!project.Success || project.Value == null || !playback.Success || playback.Value == null
                    || project.Value.Source.RevisionSha256 != root.Project.Source.RevisionSha256
                    || playback.Value.Source.RevisionSha256 != pending.Bytes.Hash)
                { root.Fail("source or published archive changed before compile completed"); break; }
                IReadOnlyList<OfficialCompiledSceneRecord>? records = ResolveRecords(pending.Bytes, playback.Value);
                if (records == null) { root.Fail("observed table offsets do not identify unique output records"); break; }
                _accept?.Invoke(new(project.Value, playback.Value, records));
            }
            if (root.Failed) Log("proof omitted: " + root.Failure);
        }
        catch (Exception ex) { Log("completion failed; proof omitted: " + ex.Message); }
        finally { if (ReferenceEquals(_root, root)) _root = null; _bytes = null; }
    }

    private static string? ExistingRootedFile(string path) =>
        path.Length > 0 && Path.IsPathRooted(path) && File.Exists(path) ? Path.GetFullPath(path) : null;

    private static void BytesPrefix(out ByteCapture? __state)
    {
        __state = null;
        if (_root == null || _root.Failed || Environment.CurrentManagedThreadId != _mainThread) return;
        __state = new ByteCapture(_root, _bytes);
        _bytes = __state;
    }

    private static void VerticesPostfix(Il2CppSystem.Collections.Generic.List<AuthoringCompiler.Vertex> __result)
    {
        ByteCapture? capture = _bytes;
        if (capture == null || capture.Root.Failed) return;
        try
        {
            if (!InteropObjectGuard.IsAlive(__result) || __result.Count > MaximumScenes)
                throw new InvalidDataException("compiler vertex collection unavailable or oversized");
            var nodes = capture.Root.Project.ScriptNodes.ToDictionary(node => node.NodeGuid, StringComparer.OrdinalIgnoreCase);
            for (int vertexIndex = 0; vertexIndex < __result.Count; vertexIndex++)
            {
                AuthoringCompiler.Vertex vertex = __result[vertexIndex];
                if (!InteropObjectGuard.IsAlive(vertex)) throw new InvalidDataException("dead compiler vertex");
                NodeData? node = vertex.Node;
                if (!InteropObjectGuard.IsAlive(node)) throw new InvalidDataException("dead compiler source node");
                ScriptNodeData? scriptNode = node!.TryCast<ScriptNodeData>();
                if (!InteropObjectGuard.IsAlive(scriptNode)) continue;
                string guid = node.Guid.ToString();
                if (!nodes.TryGetValue(guid, out StoryNodeSnapshot? savedNode))
                    throw new InvalidDataException("compiler source node absent from saved revision");
                var scripts = vertex.Scripts;
                if (!InteropObjectGuard.IsAlive(scripts) || scripts.Count > MaximumScenes)
                    throw new InvalidDataException(
                        $"compiler vertex scripts unavailable or oversized for node {guid}");
                if (savedNode.Scenes.Count == 0)
                {
                    // 2026-10-13: a saved Script node with no scenes has nothing to
                    // verify, but the official compiler still emits a vertex for it (a
                    // node the author dragged out and left empty, or a node whose rows
                    // do not reach the exported record set). Failing the whole capture
                    // here discarded EVERY ObservedCompilerRecordMatch proof in the
                    // archive — the field log shows "vertex capture: compiler source
                    // scene count differs from saved revision" — which pushed every row
                    // back onto the weaker anchored-node-sequence heuristic. Skip only
                    // the node with nothing to check; see the count guard below, which
                    // still fails hard for every other node.
                    continue;
                }

                if (scripts.Count != savedNode.Scenes.Count)
                    throw new InvalidDataException(
                        "compiler source scene count differs from saved revision for node "
                        + $"{guid}: compiler={scripts.Count}; saved={savedNode.Scenes.Count}");
                for (int index = 0; index < scripts.Count; index++)
                {
                    Script script = scripts[index];
                    if (!InteropObjectGuard.IsAlive(script)) throw new InvalidDataException("dead compiler source script");
                    SceneSnapshot saved = savedNode.Scenes[index];
                    string prompt = script.additionalPrompt ?? string.Empty;
                    if (saved.Key.SceneIndex != index || saved.DialogueText != (script.text ?? string.Empty)
                        || saved.AdditionalPrompt != prompt)
                        throw new InvalidDataException("compiler source content differs from saved revision");
                    long pointer = script.Pointer.ToInt64();
                    var leaf = new SourceLeaf(saved.Key, prompt);
                    if (capture.Sources.TryGetValue(pointer, out SourceLeaf? existing) && existing != leaf)
                        throw new InvalidDataException("one compiler script carries multiple source addresses");
                    capture.Sources[pointer] = leaf;
                    if (capture.Sources.Count > MaximumScenes) throw new InvalidDataException("compiler source collection oversized");
                }
            }
        }
        catch (Exception ex) { capture.Root.Fail("vertex capture: " + ex.Message); }
    }

    private static void ParsePrefix(Script script, Il2CppSystem.Collections.Generic.List<Offset<GenericScenarioExcel>> excels,
        FlatBufferBuilder fb, out ParseCapture? __state)
    {
        __state = null;
        ByteCapture? capture = _bytes;
        if (capture == null || capture.Root.Failed) return;
        capture.Root.SawParse = true;
        try
        {
            if (!InteropObjectGuard.IsAlive(script) || !InteropObjectGuard.IsAlive(excels) || !InteropObjectGuard.IsAlive(fb))
                throw new InvalidDataException("export boundary has an unavailable object");
            if (!capture.Sources.TryGetValue(script.Pointer.ToInt64(), out SourceLeaf? source)) return;
            if (excels.Count < 0 || excels.Count >= MaximumScenes) throw new InvalidDataException("export list oversized");
            __state = new(capture, source, excels.Pointer.ToInt64(), fb.Pointer.ToInt64(), excels.Count,
                null, null);
        }
        catch (Exception ex) { capture.Root.Fail("export prefix: " + ex.Message); }
    }

    private static void ParsePostfix(Il2CppSystem.Collections.Generic.List<Offset<GenericScenarioExcel>> excels,
        FlatBufferBuilder fb, ParseCapture? __state)
    {
        if (__state == null || __state.Bytes.Root.Failed) return;
        try
        {
            if (!InteropObjectGuard.IsAlive(excels) || !InteropObjectGuard.IsAlive(fb)
                || excels.Pointer.ToInt64() != __state.ListPointer || fb.Pointer.ToInt64() != __state.BuilderPointer
                || excels.Count != __state.BeforeCount + 1)
                throw new InvalidDataException("source export did not append exactly one table to the same output list");
            int offset = excels[__state.BeforeCount].Value;
            if (offset <= 0 || offset > MaximumOutputBytes) throw new InvalidDataException("invalid emitted table offset");
            if (__state.Bytes.BuilderPointer != 0 && __state.Bytes.BuilderPointer != __state.BuilderPointer)
                throw new InvalidDataException("one compile uses multiple observed builders");
            __state.Bytes.BuilderPointer = __state.BuilderPointer;
            __state.Bytes.Emissions.Add(new(__state.Source, offset));
        }
        catch (Exception ex) { __state.Bytes.Root.Fail("export postfix: " + ex.Message); }
    }

    private static void ParseFinalizer(ParseCapture? __state, Exception? __exception)
    {
        if (__state == null) return;
        RestoreEmptyDialogueAnchor(__state);
        if (__exception != null) __state.Bytes.Root.Fail("source export threw");
    }

    private static void ScriptKrPostfix(Script __instance, ref string __result)
    {
        try
        {
            string sourcePrompt = __instance.additionalPrompt ?? string.Empty;
            if (!NeedsEmptyDialogueAnchor(__instance, sourcePrompt)
                || ContainsOfficialWait(__result))
            {
                return;
            }

            __result = AppendEmptyDialogueAnchor(__result ?? string.Empty);
            Log("empty-dialogue anchor applied at ScriptKr; official wait=1ms");
        }
        catch (Exception ex)
        {
            // The anchor is an optional compatibility aid. Never block the
            // official getter when the wrapper or property is unavailable.
            Log("empty-dialogue ScriptKr anchor unavailable; "
                + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static bool NeedsEmptyDialogueAnchor(Script script, string sourcePrompt)
    {
        string dialogue = script.text ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(dialogue)) return false;

        // Only the namespaced AAVT route is eligible here.  A legacy #char
        // line may belong to the official parser or another mod and must never
        // receive an injected official anchor.
        EmbeddedAavtExtraction extracted = new EmbeddedAavtDirectiveExtractor()
            .Extract(sourcePrompt, acceptLegacyCharAlias: false);
        VoiceDirectiveParseResult voice = VoiceDirectivePolicy.Parse(sourcePrompt);
        bool ownsAavtRow = extracted.Commands.Count > 0
            || extracted.HasContinueDirective
            || voice.HasDirective;
        return extracted.Errors.Count == 0
            && voice.Errors.Count == 0
            && ownsAavtRow
            && string.IsNullOrWhiteSpace(extracted.SanitizedText)
            && !ContainsStandaloneLine(sourcePrompt, EmptyDialogueAnchor);
    }

    private static string AppendEmptyDialogueAnchor(string source)
    {
        if (source.Length == 0) return EmptyDialogueAnchor;
        string newline = source.Contains("\r\n", StringComparison.Ordinal)
            ? "\r\n" : source.Contains('\n') ? "\n" : "\r\n";
        return source.EndsWith("\r", StringComparison.Ordinal)
            || source.EndsWith("\n", StringComparison.Ordinal)
            ? source + EmptyDialogueAnchor
            : source + newline + EmptyDialogueAnchor;
    }

    private static bool ContainsOfficialWait(string source)
    {
        using var reader = new StringReader(source ?? string.Empty);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.TrimStart().StartsWith("#wait;", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool ContainsStandaloneLine(string source, string expected)
    {
        using var reader = new StringReader(source);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.Equals(line.Trim(), expected, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static void RestoreEmptyDialogueAnchor(ParseCapture state)
    {
        if (state.AnchoredScript == null || state.OriginalAdditionalPrompt == null) return;
        try
        {
            InteropMemberAccess.Set(state.AnchoredScript, "additionalPrompt", state.OriginalAdditionalPrompt);
        }
        catch (Exception ex)
        {
            // The official parser has already consumed the temporary value;
            // report the restoration failure without turning a successful
            // export into a false mapping proof.
            Log("empty-dialogue anchor restore failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static void BytesPostfix(Il2CppStructArray<byte> __result, ByteCapture? __state)
    {
        if (__state == null || __state.Root.Failed) return;
        try
        {
            if (!InteropObjectGuard.IsAlive(__result) || __result.Length <= 0 || __result.Length > MaximumOutputBytes)
                throw new InvalidDataException("official compiled output unavailable or oversized");
            byte[] managed = new byte[__result.Length];
            for (int index = 0; index < managed.Length; index++) managed[index] = __result[index];
            string hash = Convert.ToHexString(SHA256.HashData(managed));
            // The one-argument overload can delegate to the two-argument overload. Retain only
            // the child capture whose returned bytes are exactly the same as the outer bytes.
            if (__state.Emissions.Count == 0 && __state.Root.Output?.Hash == hash) return;
            if (__state.Emissions.Count == 0) { __state.Root.Output = null; return; }
            var tableIndex = AasOutputTableIndex.Read(managed, MaximumScenes);
            if (!tableIndex.Success || tableIndex.Value == null) throw new InvalidDataException(tableIndex.Error);
            IReadOnlyDictionary<int, int> offsets = tableIndex.Value;
            if (__state.Emissions.Any(emission => !offsets.ContainsKey(emission.TableOffset)))
                throw new InvalidDataException("observed table is absent from the actual output vector");
            __state.Root.Output = new(hash, offsets, __state.Emissions.ToArray());
        }
        catch (Exception ex) { __state.Root.Fail("output capture: " + ex.Message); }
    }

    private static void BytesFinalizer(ByteCapture? __state, Exception? __exception)
    {
        if (__state == null) return;
        if (__exception != null) __state.Root.Fail("official CompileBytes threw");
        if (ReferenceEquals(_bytes, __state)) _bytes = __state.Parent;
    }

    private static void PromotionPrefix(string tempPath, string dest, out PendingPublication? __state)
    {
        __state = null;
        RootCapture? root = _root;
        OutputCapture? output = root?.Output;
        if (root == null || root.Failed || output == null || !Path.IsPathRooted(dest)
            || !Path.GetExtension(dest).Equals(".aas", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            if (!Path.GetFileNameWithoutExtension(dest).Equals(
                    Path.GetFileNameWithoutExtension(root.Project.Source.FullPath), StringComparison.OrdinalIgnoreCase))
                return;
            if (Digest(tempPath) != output.Hash) return;
            __state = new(Path.GetFullPath(dest), output);
        }
        catch (Exception ex) { root.Fail("publication prefix: " + ex.Message); }
    }

    private static void PromotionPostfix(PendingPublication? __state)
    {
        if (__state == null || _root == null || _root.Failed) return;
        try
        {
            if (Digest(__state.Destination) != __state.Bytes.Hash)
                throw new InvalidDataException("safe-save destination does not match the captured official output");
            _root.Pending.Add(__state);
        }
        catch (Exception ex) { _root.Fail("publication postfix: " + ex.Message); }
    }

    private static IReadOnlyList<OfficialCompiledSceneRecord>? ResolveRecords(OutputCapture output, PlaybackArchiveSnapshot playback)
    {
        if (output.Offsets.Count != playback.Records.Count) return null;
        var seenRows = new HashSet<int>();
        var seenScenes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var records = new List<OfficialCompiledSceneRecord>();
        foreach (Emission emission in output.Emissions)
        {
            if (!output.Offsets.TryGetValue(emission.TableOffset, out int index) || !seenRows.Add(index)
                || !seenScenes.Add(emission.Source.Scene.NodeGuid + ":" + emission.Source.Scene.SceneIndex)) return null;
            records.Add(new(emission.Source.Scene, index, emission.Source.Prompt, emission.TableOffset));
        }
        return records.Count == 0 ? null : Array.AsReadOnly(records.OrderBy(record => record.PlaybackRecordIndex).ToArray());
    }

    private static string Digest(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var digest = SHA256.Create();
        return Convert.ToHexString(digest.ComputeHash(stream));
    }
    private static void Log(string message) { try { Plugin.Logger.LogInfo("compile-mapping-capture: " + message); } catch { } }

    private sealed class RootCapture
    {
        internal RootCapture(ProjectSnapshot project) => Project = project;
        internal readonly ProjectSnapshot Project;
        internal readonly List<PendingPublication> Pending = new();
        internal OutputCapture? Output;
        internal bool SawParse;
        internal bool Failed;
        internal string Failure = string.Empty;
        internal void Fail(string reason) { if (!Failed) Failure = reason; Failed = true; }
    }
    private sealed record RootScope(RootCapture Root, bool Owner);
    private sealed class ByteCapture
    {
        internal ByteCapture(RootCapture root, ByteCapture? parent) { Root = root; Parent = parent; }
        internal readonly RootCapture Root;
        internal readonly ByteCapture? Parent;
        internal readonly Dictionary<long, SourceLeaf> Sources = new();
        internal readonly List<Emission> Emissions = new();
        internal long BuilderPointer;
    }
    private sealed record SourceLeaf(SceneKey Scene, string Prompt);
    private sealed record Emission(SourceLeaf Source, int TableOffset);
    private sealed record ParseCapture(
        ByteCapture Bytes,
        SourceLeaf Source,
        long ListPointer,
        long BuilderPointer,
        int BeforeCount,
        Script? AnchoredScript,
        string? OriginalAdditionalPrompt);
    private sealed record OutputCapture(string Hash, IReadOnlyDictionary<int, int> Offsets, Emission[] Emissions);
    private sealed record PendingPublication(string Destination, OutputCapture Bytes);
}
