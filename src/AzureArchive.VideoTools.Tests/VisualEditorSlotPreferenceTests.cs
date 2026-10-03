using AzureArchive.VideoTools.Core.VisualEditor;

namespace AzureArchive.VideoTools.Tests;

internal static class VisualEditorSlotPreferenceTests
{
    public static void AReadGapPreservesOnlyTheSameLinesUiTarget()
    {
        var preference = new VisualEditorSlotPreference();
        preference.Remember("inspector:node:script:7", 5);
        AssertEx.Equal(0, preference.Restore(null));
        AssertEx.Equal(0, preference.Restore(string.Empty));
        // A recovered document may have a completely new edit token. The preference does
        // not accept or return that token: only the freshly supplied line identity is used.
        AssertEx.Equal(5, preference.Restore("inspector:node:script:7"));
        AssertEx.Equal(5, preference.Restore("inspector:node:script:7"));
    }

    public static void AnotherLineDropsThePreviousTargetIncludingOnReturn()
    {
        var preference = new VisualEditorSlotPreference();
        preference.Remember("inspector:node:script:7", 4);
        AssertEx.Equal(0, preference.Restore("inspector:node:other-script:8"));
        AssertEx.Equal(0, preference.Restore("inspector:node:script:7"));
        preference.Remember("inspector:node:script:7", 3);
        AssertEx.Equal(0, preference.Restore("other-inspector:node:script:7"));
    }

    public static void InvalidTargetsCannotReplaceAValidPreference()
    {
        var preference = new VisualEditorSlotPreference();
        preference.Remember("line", 2);
        preference.Remember(null, 5);
        preference.Remember(string.Empty, 5);
        preference.Remember("line", 0);
        preference.Remember("line", 6);
        AssertEx.Equal(2, preference.Restore("line"));
    }
}
