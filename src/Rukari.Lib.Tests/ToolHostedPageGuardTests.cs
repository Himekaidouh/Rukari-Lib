using Rukari.Lib.Tools;

namespace Rukari.Lib.Tests;

internal static class ToolHostedPageGuardTests
{
    public static void HeightGetterFailureIsIsolatedAndNeverRetriedEveryFrame()
    {
        var logs = new List<string>();
        var guard = new ToolHostedPageGuard(logs.Add);
        var registration = new object();
        var bad = new ProbePage { ThrowHeight = true };
        guard.Select(registration, bad, "bad-height");
        var size = guard.Measure();
        Check.Equal(ToolDrawerLayout.MinimumHostedHeight, size.Height);
        Check.Equal(0f, size.Width);
        for (int frame = 0; frame < 1000; frame++)
        {
            guard.Select(registration, bad, "bad-height");
            guard.Measure(); guard.Show(); guard.DrawFrame(Surface());
        }
        Check.True(guard.HasFailure, "A bad getter quarantines only its current showing.");
        Check.Equal(1, bad.HeightReads); Check.Equal(0, bad.WidthReads);
        Check.Equal(0, bad.Shows); Check.Equal(0, bad.Hides); Check.Equal(0, bad.Draws);
        Check.Equal(1, logs.Count);
        var healthy = new ProbePage();
        guard.Select(new object(), healthy, "healthy");
        Check.Equal((700f, 500f), guard.Measure());
        guard.Show();
        Check.True(guard.Draw(Surface()), "Another page still draws.");
        Check.Equal(1, healthy.Shows); Check.Equal(1, healthy.Draws);
    }

    public static void WidthGetterFailureUsesFiniteFallbackAndSkipsTheProvider()
    {
        var logs = new List<string>();
        var guard = new ToolHostedPageGuard(logs.Add);
        var bad = new ProbePage { ThrowWidth = true };
        guard.Select(new object(), bad, "bad-width");
        var size = guard.Measure();
        ToolDrawerLayout layout = ToolDrawerLayout.MeasureHosted(size.Height, size.Width);
        Check.Equal(ToolDrawerLayout.MinimumHostedHeight, layout.Height);
        Check.Equal(ToolDrawerLayout.Width, layout.PanelWidth);
        for (int frame = 0; frame < 100; frame++) { guard.Measure(); guard.Show(); guard.DrawFrame(Surface()); }
        Check.Equal(1, bad.HeightReads); Check.Equal(1, bad.WidthReads);
        Check.Equal(0, bad.Draws); Check.Equal(0, bad.Shows); Check.Equal(0, bad.Hides);
        Check.Equal(1, logs.Count);
        Check.True(logs[0].Contains("bad-width", StringComparison.Ordinal), "The diagnostic names its registration.");
        Check.True(logs[0].Contains("width", StringComparison.Ordinal), "The diagnostic names its failing boundary.");
    }

    public static void AVisiblePagesSizeFailureHidesItExactlyOnce()
    {
        var logs = new List<string>();
        var guard = new ToolHostedPageGuard(logs.Add);
        var page = new ProbePage();
        guard.Select(new object(), page, "visible");
        guard.Measure(); guard.Show(); guard.Draw(Surface());
        Check.Equal(1, page.Shows);
        page.ThrowHeight = true;
        guard.Measure();
        for (int frame = 0; frame < 100; frame++) { guard.Measure(); guard.Show(); guard.DrawFrame(Surface()); guard.Hide(); }
        Check.Equal(2, page.HeightReads); Check.Equal(1, page.WidthReads);
        Check.Equal(1, page.Shows); Check.Equal(1, page.Hides); Check.Equal(1, page.Draws);
        Check.True(guard.Current is null, "A failed visible provider no longer owns the panel.");
        Check.Equal(1, logs.Count);
    }

    public static void PartialShownFailureGetsOneHiddenCallbackAndNoDraw()
    {
        var logs = new List<string>();
        var guard = new ToolHostedPageGuard(logs.Add);
        var page = new ProbePage { ThrowShown = true };
        guard.Select(new object(), page, "partial-shown");
        guard.Measure(); guard.Show();
        for (int frame = 0; frame < 100; frame++) { guard.Show(); guard.Measure(); guard.DrawFrame(Surface()); }
        Check.Equal(1, page.Shows); Check.Equal(1, page.Hides); Check.Equal(0, page.Draws);
        Check.True(guard.Current is null, "Even partial initialization must release its lifecycle ownership.");
        Check.Equal(1, logs.Count);
    }

    public static void DrawFailureDiscardsPartialControlsDragFocusAndTyping()
    {
        var logs = new List<string>();
        var guard = new ToolHostedPageGuard(logs.Add);
        var page = new ProbePage
        {
            Drawing = surface =>
            {
                surface.Area("drag", surface.Row(40f));
                ((IToolPanelSurfaceText)surface).TextField("field", "draft", surface.Row(30f), true);
                throw new InvalidOperationException("draw failed after claiming input");
            }
        };
        guard.Select(new object(), page, "partial-draw"); guard.Measure(); guard.Show();
        var offered = new ToolPanelBuilder(new ToolInputRect(0, 0, 500, 600),
            new ToolPanelPointer(5, 5, true, false, false, 4, 7), "drag", new ToolInputRect(10, 20, 250, 300),
            new ToolPanelKeyboard("stale text", true, true, true, true, true, true, true, true, true, "旧组合"));
        ToolPanelBuilder frame = guard.DrawFrame(offered);
        Check.True(!ReferenceEquals(offered, frame), "An incomplete provider frame must never be published.");
        Check.Equal(0, frame.Elements.Count); Check.Equal<string?>(null, frame.FocusedField);
        Check.True(!frame.IsDragging, "The failed page cannot keep pointer capture.");
        Check.Equal(ToolPanelPointer.None, frame.Pointer);
        Check.Equal(string.Empty, frame.TypedText); Check.Equal(string.Empty, frame.Composition);
        Check.True(!frame.Backspace && !frame.Submitted && !frame.Cancelled && !frame.Delete && !frame.MoveLeft
            && !frame.MoveRight && !frame.MoveHome && !frame.MoveEnd && !frame.SelectAll, "Keyboard state is dropped with the partial frame.");
        Check.Equal(offered.BoundsInPixels, frame.BoundsInPixels);
        Check.Equal(1, page.Shows); Check.Equal(1, page.Hides);
        for (int repeat = 0; repeat < 100; repeat++) guard.DrawFrame(Surface());
        Check.Equal(1, page.Draws); Check.Equal(1, logs.Count);
    }

    public static void HiddenCallbackFailureCannotPreventAnotherPageFromShowing()
    {
        var logs = new List<string>();
        var guard = new ToolHostedPageGuard(logs.Add);
        var old = new ProbePage { ThrowHidden = true };
        var next = new ProbePage();
        guard.Select(new object(), old, "bad-hide"); guard.Measure(); guard.Show();
        guard.Select(new object(), next, "next"); guard.Measure(); guard.Show();
        Check.Equal(1, old.Shows); Check.Equal(1, old.Hides);
        Check.Equal(1, next.Shows); Check.True(!guard.HasFailure, "An old hide failure does not quarantine its successor.");
        Check.Same(next, guard.Current, "The successor owns the visible panel.");
        Check.True(guard.Draw(Surface()), "The next page remains usable.");
        for (int repeat = 0; repeat < 100; repeat++) guard.Hide();
        Check.Equal(1, next.Hides); Check.Equal(1, old.Hides); Check.Equal(1, logs.Count);
    }

    public static void ExplicitCloseAndReturnPermitOneRetryOfTheSameRegistration()
    {
        var logs = new List<string>();
        var guard = new ToolHostedPageGuard(logs.Add);
        var registration = new object();
        var page = new ProbePage { ThrowDraw = true };
        guard.Select(registration, page, "retry"); guard.Measure(); guard.Show(); guard.DrawFrame(Surface());
        Check.Equal(1, page.Draws); Check.True(guard.HasFailure, "The first showing failed.");
        guard.Select(null, null, string.Empty);
        page.ThrowDraw = false;
        guard.Select(registration, page, "retry"); guard.Measure(); guard.Show();
        Check.True(guard.Draw(Surface()), "Closing and reopening permits a recovered provider to retry.");
        Check.Equal(2, page.Shows); Check.Equal(2, page.Draws); Check.Equal(1, page.Hides);
        Check.True(!guard.HasFailure, "No old failure leaks into the new showing.");
        guard.Select(null, null, string.Empty); guard.Hide();
        Check.Equal(2, page.Hides); Check.Equal(1, logs.Count);
    }

    public static void SharedContentAndReusedPageIdsStillUseRegistrationIdentity()
    {
        var guard = new ToolHostedPageGuard(_ => { });
        var content = new ProbePage();
        var first = new object(); var second = new object();
        guard.Select(first, content, "same-id"); guard.Measure(); guard.Show();
        for (int frame = 0; frame < 10; frame++) { guard.Select(first, content, "same-id"); guard.Show(); }
        Check.Equal(1, content.Shows); Check.Equal(0, content.Hides);
        guard.Select(second, content, "same-id"); guard.Measure(); guard.Show();
        Check.Equal(2, content.Shows); Check.Equal(1, content.Hides);
        content.ThrowDraw = true; guard.Draw(Surface());
        content.ThrowDraw = false;
        guard.Select(new object(), content, "same-id"); guard.Measure(); guard.Show();
        Check.True(guard.Draw(Surface()), "A replacement registration is distinct even with the same name and provider object.");
        Check.Equal(3, content.Shows); Check.Equal(2, content.Hides);
    }

    public static void ValidDynamicSizesAndLegacyTwoMemberPagesRemainSupported()
    {
        var guard = new ToolHostedPageGuard(_ => { });
        var page = new ProbePage();
        guard.Select(new object(), page, "dynamic");
        Check.Equal((700f, 500f), guard.Measure()); guard.Show();
        page.Height = 900f; page.Width = 1100f;
        Check.Equal((900f, 1100f), guard.Measure());
        page.Height = float.NaN; page.Width = float.PositiveInfinity;
        var size = guard.Measure();
        ToolDrawerLayout layout = ToolDrawerLayout.MeasureHosted(size.Height, size.Width);
        Check.Equal(ToolDrawerLayout.MinimumHostedHeight, layout.Height); Check.Equal(ToolDrawerLayout.Width, layout.PanelWidth);
        Check.True(!guard.HasFailure, "A documented no-op dimension is not a provider exception.");
        var minimal = new MinimalPage();
        guard.Select(new object(), minimal, "legacy");
        Check.Equal((400f, 0f), guard.Measure()); guard.Show();
        Check.True(guard.Draw(Surface()), "The frozen two-member content contract is sufficient.");
    }

    public static void FailureDiagnosticsAreBoundedAndCannotBreakTheGuard()
    {
        var logs = new List<string>();
        var guard = new ToolHostedPageGuard(logs.Add);
        var page = new ProbePage { ThrowHeight = true, ErrorText = new string('x', 100000) + "\r\nnoise" };
        guard.Select(new object(), page, "bounded");
        for (int frame = 0; frame < 1000; frame++) guard.Measure();
        Check.Equal(1, logs.Count);
        Check.True(logs[0].Length < 850 && !logs[0].Contains('\r') && !logs[0].Contains('\n'), "Provider diagnostics have bounded single-line detail.");
        var throwingLog = new ToolHostedPageGuard(_ => throw new InvalidOperationException("logging failed"));
        throwingLog.Select(new object(), new ProbePage { ThrowWidth = true }, "log-failure");
        throwingLog.Measure(); throwingLog.Show();
        Check.True(throwingLog.HasFailure, "Diagnostic failures cannot escape the page boundary.");
    }

    public static void CallbackNavigationDiscardsTheOldFrameWithoutQuarantiningTheNewPage()
    {
        var logs = new List<string>();
        var guard = new ToolHostedPageGuard(logs.Add);
        var next = new ProbePage();
        var nextRegistration = new object();
        var old = new ProbePage
        {
            Drawing = surface =>
            {
                surface.Button("old", "old", surface.Row(30));
                guard.Select(nextRegistration, next, "next");
                throw new InvalidOperationException("old draw failed after navigation");
            }
        };
        guard.Select(new object(), old, "old"); guard.Measure(); guard.Show();
        ToolPanelBuilder frame = guard.DrawFrame(Surface());
        Check.Equal(0, frame.Elements.Count); Check.Equal(1, old.Hides);
        Check.True(!guard.HasFailure, "A departing provider cannot mark the new registration failed.");
        guard.Measure(); guard.Show();
        Check.True(guard.Draw(Surface()), "The newly selected page is still usable.");
        Check.Equal(1, next.Shows); Check.Equal(1, logs.Count);
        Check.True(logs[0].Contains("'old'", StringComparison.Ordinal), "The old failure retains its original attribution.");
    }

    public static void FailedShownNavigationStillPairsTheDepartingLifecycle()
    {
        var guard = new ToolHostedPageGuard(_ => { });
        var next = new ProbePage();
        var old = new ProbePage
        {
            Showing = () =>
            {
                guard.Select(new object(), next, "next");
                throw new InvalidOperationException("old show failed after navigation");
            }
        };
        guard.Select(new object(), old, "old"); guard.Measure(); guard.Show();
        Check.Equal(1, old.Shows); Check.Equal(1, old.Hides);
        Check.True(!guard.HasFailure, "The new selected provider is unaffected.");
        guard.Measure(); guard.Show(); guard.Draw(Surface());
        Check.Equal(1, next.Shows); Check.Equal(1, next.Draws);
    }

    public static void SizeGetterNavigationStopsFurtherCallbacksAndKeepsNewPageUsable()
    {
        var guard = new ToolHostedPageGuard(_ => { });
        var next = new ProbePage();
        var old = new ProbePage { ThrowWidth = true };
        old.ReadingHeight = () => guard.Select(new object(), next, "next");
        guard.Select(new object(), old, "old");
        Check.Equal((ToolDrawerLayout.MinimumHostedHeight, 0f), guard.Measure());
        Check.Equal(1, old.HeightReads); Check.Equal(0, old.WidthReads);
        Check.True(!guard.HasFailure, "A departing getter cannot keep calling the old provider or fault the successor.");
        guard.Measure(); guard.Show();
        Check.True(guard.Draw(Surface()), "The successor still works after size-time navigation.");
        Check.Equal(1, next.Shows);
    }

    public static void CloseAndReopenInsideDrawCannotReuseTheOldFrameOrFailure()
    {
        var logs = new List<string>();
        var guard = new ToolHostedPageGuard(logs.Add);
        var registration = new object();
        var page = new ProbePage();
        page.Drawing = surface =>
        {
            surface.Button("old", "old", surface.Row(30));
            guard.Select(null, null, string.Empty);
            page.Drawing = null;
            guard.Select(registration, page, "same");
            throw new InvalidOperationException("the previous showing failed after reopening");
        };
        guard.Select(registration, page, "same"); guard.Measure(); guard.Show();
        ToolPanelBuilder frame = guard.DrawFrame(Surface());
        Check.Equal(0, frame.Elements.Count); Check.Equal(1, page.Hides);
        Check.True(!guard.HasFailure, "Matching registration and provider references do not make an old showing current again.");
        guard.Measure(); guard.Show();
        Check.True(guard.Draw(Surface()), "The new showing may retry independently.");
        Check.Equal(2, page.Shows); Check.Equal(2, page.Draws); Check.Equal(1, logs.Count);
    }

    public static void HiddenNavigationKeepsTheLatestSelectionAndItsOwnDiagnostics()
    {
        var logs = new List<string>();
        var guard = new ToolHostedPageGuard(logs.Add);
        var old = new ProbePage { ThrowHidden = true };
        var superseded = new ProbePage();
        var latest = new ProbePage { ThrowHidden = true };
        old.Hiding = () => guard.Select(new object(), latest, "latest");
        guard.Select(new object(), old, "old"); guard.Measure(); guard.Show();
        guard.Select(new object(), superseded, "superseded"); guard.Measure(); guard.Show();
        Check.Same(latest, guard.Current, "Navigation from the departing callback supersedes the older selection request.");
        Check.Equal(1, old.Shows); Check.Equal(1, old.Hides);
        Check.Equal(0, superseded.Shows); Check.Equal(1, latest.Shows);
        Check.True(!guard.HasFailure, "The old hidden failure does not quarantine the latest selection.");
        guard.Hide(); guard.Hide();
        Check.Equal(1, latest.Hides); Check.Equal(2, logs.Count);
        Check.True(logs[0].Contains("'old'", StringComparison.Ordinal)
            && logs[1].Contains("'latest'", StringComparison.Ordinal), "Each paired hide retains its own diagnostic attribution.");
    }

    private static ToolPanelBuilder Surface() => new(new ToolInputRect(0, 0, 500, 600), ToolPanelPointer.None);

    private sealed class MinimalPage : IToolPanelContent
    {
        public float PreferredHeight => 400f;
        public void Draw(IToolPanelSurface surface) => surface.Text("minimal", "minimal", surface.Row(30));
    }

    private sealed class ProbePage : IToolPanelContent, IToolPanelSizing, IToolPanelLifecycle
    {
        internal float Height = 700f;
        internal float Width = 500f;
        internal bool ThrowHeight;
        internal bool ThrowWidth;
        internal bool ThrowShown;
        internal bool ThrowHidden;
        internal bool ThrowDraw;
        internal string ErrorText = "provider failure";
        internal int HeightReads;
        internal int WidthReads;
        internal int Shows;
        internal int Hides;
        internal int Draws;
        internal Action<IToolPanelSurface>? Drawing;
        internal Action? Showing;
        internal Action? ReadingHeight;
        internal Action? Hiding;

        public float PreferredHeight
        {
            get { HeightReads++; ReadingHeight?.Invoke(); if (ThrowHeight) throw new InvalidOperationException(ErrorText); return Height; }
        }
        public float PreferredWidth
        {
            get { WidthReads++; if (ThrowWidth) throw new InvalidOperationException(ErrorText); return Width; }
        }
        public void OnShown()
        {
            Shows++; Showing?.Invoke(); if (ThrowShown) throw new InvalidOperationException(ErrorText);
        }
        public void OnHidden() { Hides++; Hiding?.Invoke(); if (ThrowHidden) throw new InvalidOperationException(ErrorText); }
        public void Draw(IToolPanelSurface surface)
        {
            Draws++; Drawing?.Invoke(surface);
            if (ThrowDraw) throw new InvalidOperationException(ErrorText);
            surface.Text("ready", "ready", surface.Row(30));
        }
    }
}
