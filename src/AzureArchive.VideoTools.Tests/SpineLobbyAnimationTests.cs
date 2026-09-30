using AzureArchive.VideoTools.Core.Spines;

namespace AzureArchive.VideoTools.Tests;

internal static class SpineLobbyAnimationTests
{
    public static void OrdinaryCharactersKeepNativePlayback()
    {
        string[] names = { "Idle_01", "Talk_01_M", "Talk_01_A", "Smile" };
        AssertEx.False(SpineLobbyAnimationPolicy.IsLobbyCatalog(names));
        AssertEx.True(SpineLobbyAnimationPolicy.BuildPlan(names, "Talk_01_M", true) is null);
        AssertEx.False(SpineLobbyAnimationPolicy.IsLobbyCatalog(new[] { "Start_Idle_01" }));
        AssertEx.True(SpineLobbyAnimationPolicy.BuildPlan(new[] { "Start_Idle_01" }, "Start_Idle_01", true) is null);
        AssertEx.True(SpineLobbyAnimationPolicy.IsLobbyCatalog(Catalog()));
    }

    public static void MainAndAttachmentSelectionsResolveToSamePlan()
    {
        string[] names = Catalog("Talk_01_M", "Talk_01_A");
        SpineLobbyAnimationPlan main = AssertEx.NotNull(
            SpineLobbyAnimationPolicy.BuildPlan(names, "Talk_01_M", true));
        SpineLobbyAnimationPlan attachment = AssertEx.NotNull(
            SpineLobbyAnimationPolicy.BuildPlan(names, "Talk_01_A", true));

        AssertEx.Equal(main, attachment);
        AssertEx.Equal("Talk_01_M", main.PrimaryAnimationName);
        AssertEx.Equal("Talk_01_A", main.CompanionAnimationName);
        AssertEx.False(main.Loop);
        AssertEx.True(main.ReturnToIdle);
        AssertEx.Equal(0.2f, main.FadeSeconds);
    }

    public static void MissingCompanionsAreNeverInvented()
    {
        string[] names = Catalog("Talk_02_M", "Talk_03_A", "PinchEnd_01_M", "LookEnd_01_A");
        foreach (string selected in names.Skip(2))
        {
            SpineLobbyAnimationPlan plan = AssertEx.NotNull(
                SpineLobbyAnimationPolicy.BuildPlan(names, selected, true));
            AssertEx.Equal(selected, plan.PrimaryAnimationName);
            AssertEx.True(plan.CompanionAnimationName is null);
            bool isTalk = selected.StartsWith("Talk_", StringComparison.Ordinal);
            AssertEx.Equal(!isTalk, plan.Loop);
            AssertEx.Equal(isTalk, plan.ReturnToIdle);
        }
    }

    public static void EntranceAndNumberedTalksPlayOnce()
    {
        string[] names = Catalog("Talk_1_M", "Talk_123_A");
        foreach (bool requestedLoop in new[] { false, true })
        {
            SpineLobbyAnimationPlan entrance = AssertEx.NotNull(
                SpineLobbyAnimationPolicy.BuildPlan(names, "Start_Idle_01", requestedLoop));
            AssertEx.False(entrance.Loop);
            AssertEx.True(entrance.ReturnToIdle);
            AssertEx.Equal(1f, entrance.FadeSeconds);

            foreach (string selected in new[] { "Talk_1_M", "Talk_123_A" })
            {
                SpineLobbyAnimationPlan talk = AssertEx.NotNull(
                    SpineLobbyAnimationPolicy.BuildPlan(names, selected, requestedLoop));
                AssertEx.False(talk.Loop);
                AssertEx.True(talk.ReturnToIdle);
                AssertEx.Equal(0.2f, talk.FadeSeconds);
            }
        }
    }

    public static void IdleAndOtherAnimationsKeepRequestedLoop()
    {
        string[] names = Catalog(
            "Idle_01_R", "Idle_02_M", "Idle_02_A", "Dummy", "Pat_01_M", "Pat_01_A", "Eye_Close_01");
        foreach (bool requestedLoop in new[] { false, true })
        {
            foreach (string selected in names.Where(name => name != "Start_Idle_01"))
            {
                SpineLobbyAnimationPlan plan = AssertEx.NotNull(
                    SpineLobbyAnimationPolicy.BuildPlan(names, selected, requestedLoop));
                AssertEx.Equal(requestedLoop, plan.Loop);
                AssertEx.False(plan.ReturnToIdle);
                AssertEx.Equal(0f, plan.FadeSeconds);
            }

            SpineLobbyAnimationPlan pat = AssertEx.NotNull(
                SpineLobbyAnimationPolicy.BuildPlan(names, "Pat_01_A", requestedLoop));
            AssertEx.Equal("Pat_01_M", pat.PrimaryAnimationName);
            AssertEx.Equal("Pat_01_A", pat.CompanionAnimationName);
        }
    }

    public static void MissingOrDifferentlyCasedAnimationsAreNotHandled()
    {
        string[] names = Catalog("Talk_01_M");
        foreach (string missing in new[] { "00", "Talk_01_A", "talk_01_m", "", " Idle_01" })
        {
            AssertEx.True(SpineLobbyAnimationPolicy.BuildPlan(names, missing, true) is null);
        }

        var insensitiveNames = new HashSet<string>(
            new[] { "idle_01", "Start_Idle_01" }, StringComparer.OrdinalIgnoreCase);
        AssertEx.False(SpineLobbyAnimationPolicy.IsLobbyCatalog(insensitiveNames));
        AssertEx.True(SpineLobbyAnimationPolicy.BuildPlan(insensitiveNames, "idle_01", true) is null);
    }

    public static void PairingRequiresExactSuffixAndExistingName()
    {
        string[] names = Catalog("PoseA", "PoseM", "Pose_m", "Pose_a", "LookEnd_01_M", "LookEnd_01_A");
        foreach (string selected in new[] { "PoseA", "PoseM", "Pose_m", "Pose_a" })
        {
            SpineLobbyAnimationPlan plan = AssertEx.NotNull(
                SpineLobbyAnimationPolicy.BuildPlan(names, selected, true));
            AssertEx.Equal(selected, plan.PrimaryAnimationName);
            AssertEx.True(plan.CompanionAnimationName is null);
        }

        SpineLobbyAnimationPlan end = AssertEx.NotNull(
            SpineLobbyAnimationPolicy.BuildPlan(names, "LookEnd_01_A", true));
        AssertEx.Equal("LookEnd_01_M", end.PrimaryAnimationName);
        AssertEx.Equal("LookEnd_01_A", end.CompanionAnimationName);
        AssertEx.True(end.Loop);
        AssertEx.False(end.ReturnToIdle);
    }

    public static void TalkNamesWithoutNumericIdentifierKeepRequestedLoop()
    {
        string[] names = Catalog("Talk__M", "Talk_01_End_M", "Talk_X_M", "Talk_01", "Start_Idle_02");
        foreach (string selected in names.Skip(2))
        {
            SpineLobbyAnimationPlan plan = AssertEx.NotNull(
                SpineLobbyAnimationPolicy.BuildPlan(names, selected, true));
            AssertEx.True(plan.Loop);
            AssertEx.False(plan.ReturnToIdle);
        }
    }

    private static string[] Catalog(params string[] additionalNames) =>
        new[] { "Idle_01", "Start_Idle_01" }.Concat(additionalNames).ToArray();
}
