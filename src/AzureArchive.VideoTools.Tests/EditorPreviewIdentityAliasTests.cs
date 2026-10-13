using AzureArchive.VideoTools.Core.Commands;

namespace AzureArchive.VideoTools.Tests;

/// <summary>
/// The preview lease stores its capture under the nested continuous boundary
/// identity, while the engine reports the outer wrapper's final official text.
/// These fixtures pin the alias table that bridges the two.
/// </summary>
internal static class EditorPreviewIdentityAliasTests
{
    public static void EngineReportedWrapperIdentityResolvesToTheNestedCaptureKey()
    {
        CompiledScriptIdentity captured = CommandIdentity.CompiledScript("正文");
        CompiledScriptIdentity reported = CommandIdentity.CompiledScript("#voice;intro.ogg\r\n正文");
        var aliases = new EditorPreviewIdentityAliasMap();
        AssertEx.True(aliases.Add(reported, captured.Sha256));
        AssertEx.Equal(1, aliases.Count);
        AssertEx.Equal(captured, aliases.Resolve(reported, new[] { captured }));

        // The direct key needs no edge, and an edge never invents a candidate the
        // generation does not hold.
        AssertEx.True(aliases.Resolve(captured, new[] { captured }) == null);
        AssertEx.True(aliases.Resolve(reported, new[] { CommandIdentity.CompiledScript("别的") }) == null);

        // Repeating the same edge adds nothing.
        AssertEx.False(aliases.Add(reported, captured.Sha256));
        AssertEx.Equal(1, aliases.Count);
    }

    public static void SelfEmptyAndSupersededEdgesCannotChangeResolution()
    {
        CompiledScriptIdentity identity = CommandIdentity.CompiledScript("正文");
        CompiledScriptIdentity first = CommandIdentity.CompiledScript("旧");
        CompiledScriptIdentity second = CommandIdentity.CompiledScript("新");
        var aliases = new EditorPreviewIdentityAliasMap();

        AssertEx.False(aliases.Add(identity, identity.Sha256));
        AssertEx.False(aliases.Add(identity, string.Empty));
        AssertEx.Equal(0, aliases.Count);

        AssertEx.True(aliases.Add(identity, first.Sha256));
        AssertEx.True(aliases.Add(identity, second.Sha256));
        AssertEx.Equal(1, aliases.Count);
        // A later compile of the same text is the one a later window belongs to.
        AssertEx.Equal(second, aliases.Resolve(identity, new[] { first, second }));
    }

    public static void CyclicEdgesStayBoundedAndDeepChainsStopAtTheLimit()
    {
        CompiledScriptIdentity a = CommandIdentity.CompiledScript("A");
        CompiledScriptIdentity b = CommandIdentity.CompiledScript("B");
        CompiledScriptIdentity target = CommandIdentity.CompiledScript("T");
        var cyclic = new EditorPreviewIdentityAliasMap();
        AssertEx.True(cyclic.Add(a, b.Sha256));
        AssertEx.True(cyclic.Add(b, a.Sha256));
        AssertEx.True(cyclic.Resolve(a, new[] { target }) == null);

        var chain = new EditorPreviewIdentityAliasMap();
        for (int index = 0; index < EditorPreviewIdentityAliasMap.MaximumDepth - 1; index++)
        {
            AssertEx.True(chain.Add(
                CommandIdentity.CompiledScript($"step-{index}"),
                CommandIdentity.CompiledScript($"step-{index + 1}").Sha256));
        }

        AssertEx.True(chain.Add(
            CommandIdentity.CompiledScript($"step-{EditorPreviewIdentityAliasMap.MaximumDepth - 1}"),
            target.Sha256));
        AssertEx.Equal(
            target,
            chain.Resolve(CommandIdentity.CompiledScript("step-0"), new[] { target }));
        AssertEx.True(chain.Add(
            CommandIdentity.CompiledScript("step-far"),
            CommandIdentity.CompiledScript("step-0").Sha256));
        // One edge beyond the bound is not followed: an unresolved identity stays
        // unresolved instead of matching an unrelated candidate.
        AssertEx.True(chain.Resolve(CommandIdentity.CompiledScript("step-far"), new[] { target }) == null);
    }
}
