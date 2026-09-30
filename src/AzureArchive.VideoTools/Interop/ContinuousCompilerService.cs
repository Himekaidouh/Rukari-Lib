using System;
using System.Reflection;
using AzureArchive.VideoTools.Api;
using AzureArchive.VideoTools.Core;
using Studio.Scripts;

namespace AzureArchive.VideoTools.Interop;

internal sealed class ContinuousCompilerService : IContinuousCompilerService
{
    private readonly RuntimeCapabilityService _capabilities;
    private readonly EditorSceneService _editor;

    public ContinuousCompilerService(RuntimeCapabilityService capabilities, EditorSceneService editor)
    {
        _capabilities = capabilities;
        _editor = editor;
    }

    public ApiResult<ContinuousCompileProof> CompileSelectedCopy()
    {
        try
        {
            ApiResult<SceneSnapshot> snapshotResult = _editor.GetSelectedScene();
            if (!snapshotResult.Success || snapshotResult.Value == null)
            {
                return ApiResult<ContinuousCompileProof>.Fail(snapshotResult.Error);
            }

            if (!_editor.TryGetInternalSelection(out EditorSceneService.InternalSceneSelection selection, out string error))
            {
                return ApiResult<ContinuousCompileProof>.Fail(error);
            }

            if (selection.Previous == null)
            {
                return ApiResult<ContinuousCompileProof>.Fail("Select scene index 1 or later; scene 0 has no previous scene.");
            }

            Script standaloneCopy = new(selection.Current, false);
            Script previousCopy = new(selection.Previous, false);
            Script continuousCopy = new(selection.Current, false);

            string standalone = InvokeString(standaloneCopy, "CompileScriptStandalone");
            bool compatible = continuousCopy.IsCompatibleWith(previousCopy);
            continuousCopy.MatchWithPrev(previousCopy);
            bool matchSetPrevious = InteropObjectGuard.IsAlive(InteropMemberAccess.Get<Script>(continuousCopy, "prev"));
            bool matchSetContinuous = InteropMemberAccess.Get<bool>(continuousCopy, "continuous");
            string continuous = continuousCopy.CompileScriptContinuous();

            _capabilities.Verified("Compiler.ScriptCopy", "Script copy constructor completed on isolated objects");
            _capabilities.Verified("Compiler.MatchWithPrev", $"completed; compatible={compatible}");
            _capabilities.Verified("Compiler.Continuous", "CompileScriptContinuous returned normally");

            return ApiResult<ContinuousCompileProof>.Ok(new ContinuousCompileProof(
                snapshotResult.Value.Address,
                compatible,
                matchSetPrevious,
                matchSetContinuous,
                InteropMemberAccess.Get<string>(selection.Previous, "text") ?? string.Empty,
                InteropMemberAccess.Get<string>(selection.Current, "text") ?? string.Empty,
                standalone,
                continuous));
        }
        catch (Exception ex)
        {
            string detail = PatchGuard.Describe(ex);
            _capabilities.Degraded("Compiler.Continuous", detail);
            return ApiResult<ContinuousCompileProof>.Fail(detail);
        }
    }

    private static string InvokeString(Script target, string methodName)
    {
        MethodInfo? method = typeof(Script).GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (method == null)
        {
            throw new MissingMethodException(typeof(Script).FullName, methodName);
        }

        return method.Invoke(target, null) as string ?? string.Empty;
    }
}
