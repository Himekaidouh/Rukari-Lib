using Rukari.Lib.Settings;
using Rukari.Lib.Tools;

namespace Rukari.Lib.Tests;

internal static class ModSettingsTests
{
    internal static void RegistrationWorksWithoutAProjectAndKeepsIndependentSnapshots()
    {
        using var fixture = new Fixture();
        IReadOnlyList<ModSettingsPage> empty = fixture.Service.Pages;
        var first = new Page();
        using IDisposable lease = Check.Success(fixture.Service.RegisterPage("mod.voice", "人物配音支持", first));
        IReadOnlyList<ModSettingsPage> snapshot = fixture.Service.Pages;
        Check.Equal(0, empty.Count, "A previous snapshot must not acquire later registrations.");
        Check.Equal(1, snapshot.Count);
        Check.Same(first, snapshot[0].Content, "The page requires no editor, project or current dialogue.");
        Check.True(!fixture.Service.IsOpen, "Registering content does not open the global UI.");
        Check.Equal(0, first.Shown);
        Check.Success(fixture.Service.Open(null));
        Check.Equal("mod.voice", fixture.Service.SelectedPage!.OwnerId);
        lease.Dispose();
        Check.Equal(1, snapshot.Count, "Removing a registration does not mutate a captured snapshot.");
        Check.Equal(0, fixture.Service.Pages.Count);
        Check.True(fixture.Service.IsOpen, "An empty settings window can still show its empty state.");
        Check.True(fixture.Service.SelectedPage is null, "A removed page is not retained as the selected owner.");
    }

    internal static void DuplicateOwnersAndInvalidContentNeverReplaceExistingPages()
    {
        using var fixture = new Fixture();
        var first = new Page();
        using IDisposable lease = Check.Success(fixture.Service.RegisterPage("mod.voice", "Voice", first));
        Check.Failure(ModErrorCode.Conflict, fixture.Service.RegisterPage("mod.voice", "Duplicate", new Page()));
        Check.Failure(ModErrorCode.InvalidArgument, fixture.Service.RegisterPage("", "Empty", new Page()));
        Check.Failure(ModErrorCode.InvalidArgument, fixture.Service.RegisterPage("mod.other", "", new Page()));
        Check.Failure(ModErrorCode.InvalidArgument, fixture.Service.RegisterPage("mod.other", "Broken", null!));
        Check.Failure(ModErrorCode.InvalidArgument, fixture.Service.RegisterPage("mod\nother", "Broken", new Page()));
        Check.Equal(1, fixture.Service.Pages.Count);
        Check.Same(first, fixture.Service.Pages[0].Content, "Rejected registrations leave the first owner intact.");
        Check.Failure(ModErrorCode.NotFound, fixture.Service.Open("missing.mod"));
        Check.Equal(0, fixture.ShowCalls, "An unknown owner never opens the native host.");
    }

    internal static void OldLeasesCannotRemoveReplacementSettingsPages()
    {
        using var fixture = new Fixture();
        IDisposable oldLease = Check.Success(fixture.Service.RegisterPage("mod.voice", "Old", new Page()));
        oldLease.Dispose();
        var replacement = new Page();
        using IDisposable newLease = Check.Success(fixture.Service.RegisterPage("mod.voice", "New", replacement));
        oldLease.Dispose();
        Check.Equal(1, fixture.Service.Pages.Count);
        Check.Same(replacement, fixture.Service.Pages[0].Content, "Disposal is bound to a registration, not merely its owner ID.");
        Check.Success(fixture.Service.Open("mod.voice"));
        Check.Equal(1, replacement.Shown);
    }

    internal static void PageLifecyclePairsOnceAcrossSelectionClosingAndDisposal()
    {
        using var fixture = new Fixture();
        var first = new Page();
        var second = new Page();
        using IDisposable firstLease = Check.Success(fixture.Service.RegisterPage("mod.first", "First", first));
        using IDisposable secondLease = Check.Success(fixture.Service.RegisterPage("mod.second", "Second", second));
        Check.Success(fixture.Service.Open("mod.first"));
        Check.Success(fixture.Service.Open("mod.first"));
        Check.Success(fixture.Service.DrawSelected(Surface()));
        Check.Success(fixture.Service.DrawSelected(Surface()));
        Check.Equal(1, first.Shown, "Repeated opens and frames do not repeat OnShown.");
        Check.Equal(2, first.Drawn);
        Check.Success(fixture.Service.Open("mod.second"));
        Check.Equal(1, first.Hidden);
        Check.Equal(1, second.Shown);
        fixture.Service.Close();
        fixture.Service.Close();
        Check.Equal(1, second.Hidden, "Repeated close does not repeat cleanup.");
        Check.Success(fixture.Service.Open("mod.second"));
        secondLease.Dispose();
        Check.Equal(2, second.Hidden, "Removing the visible owner pairs its second showing.");
        Check.Same(first, fixture.Service.SelectedPage!.Content, "A remaining page becomes selected after removal.");
        Check.Equal(2, first.Shown);
        fixture.Service.Dispose();
        fixture.Service.Dispose();
        Check.Equal(2, first.Hidden, "Shutdown pairs the visible page once.");
        Check.Equal(0, fixture.Service.Pages.Count);
        Check.True(!fixture.Service.IsOpen, "Shutdown clears logical visibility.");
        Check.Failure(ModErrorCode.NotReady, fixture.Service.Open(null));
        Check.Failure(ModErrorCode.NotReady, fixture.Service.RegisterPage("mod.third", "Third", new Page()));
    }

    internal static void PageAndLoggerFailuresDoNotBreakOtherPagesOrPreventRetry()
    {
        using var fixture = new Fixture { ThrowFromLog = true };
        var broken = new Page { ThrowOnShow = true, ThrowOnHide = true };
        var good = new Page();
        using IDisposable brokenLease = Check.Success(fixture.Service.RegisterPage("mod.broken", "Broken", broken));
        using IDisposable goodLease = Check.Success(fixture.Service.RegisterPage("mod.good", "Good", good));
        Check.Failure(ModErrorCode.ProviderFailed, fixture.Service.Open("mod.broken"));
        Check.True(fixture.Service.IsOpen, "The window survives with an error message for the broken page.");
        Check.True(fixture.Service.LastPageError is not null, "The renderer can show a useful error state.");
        Check.Equal(1, broken.Shown);
        Check.Equal(1, broken.Hidden, "A partially shown page still receives one cleanup attempt.");
        Check.Failure(ModErrorCode.ProviderFailed, fixture.Service.DrawSelected(Surface()));
        Check.Equal(0, broken.Drawn, "The failed page is not called again every frame.");
        Check.Success(fixture.Service.Open("mod.good"));
        Check.Success(fixture.Service.DrawSelected(Surface()));
        Check.Equal(1, good.Drawn);
        Check.True(fixture.Service.LastPageError is null, "Another page starts without the previous owner's error.");
        broken.ThrowOnShow = false;
        broken.ThrowOnHide = false;
        Check.Success(fixture.Service.Open("mod.broken"));
        Check.Success(fixture.Service.DrawSelected(Surface()));
        Check.Equal(2, broken.Shown, "Switching back lets a repaired page try again.");
        Check.Equal(1, broken.Drawn);
    }

    internal static void DrawFailuresReleaseThePageAndDoNotRepeatEveryFrame()
    {
        using var fixture = new Fixture();
        var page = new Page { ThrowOnDraw = true };
        using IDisposable lease = Check.Success(fixture.Service.RegisterPage("mod.broken", "Broken", page));
        Check.Success(fixture.Service.Open(null));
        Check.Failure(ModErrorCode.ProviderFailed, fixture.Service.DrawSelected(Surface()));
        Check.Failure(ModErrorCode.ProviderFailed, fixture.Service.DrawSelected(Surface()));
        Check.Equal(1, page.Drawn);
        Check.Equal(1, page.Hidden);
        fixture.Service.Close();
        Check.Equal(1, page.Hidden, "Closing a failed page does not clean it up twice.");
        page.ThrowOnDraw = false;
        Check.Success(fixture.Service.Open(null));
        Check.Success(fixture.Service.DrawSelected(Surface()));
        Check.Equal(2, page.Shown);
        Check.Equal(2, page.Drawn);
    }

    internal static void AnInvisibleOrFailingNativeHostNeverClaimsSuccessfulOpening()
    {
        using var fixture = new Fixture { AcceptShow = false };
        var page = new Page();
        using IDisposable lease = Check.Success(fixture.Service.RegisterPage("mod.test", "Test", page));
        Check.Failure(ModErrorCode.NotReady, fixture.Service.Open(null));
        Check.True(!fixture.Service.IsOpen, "A rejected native show must not become a logical success.");
        Check.Equal(0, page.Shown);
        fixture.ThrowFromShow = true;
        Check.Failure(ModErrorCode.ProviderFailed, fixture.Service.Open(null));
        Check.Equal(0, page.Shown);
        fixture.ThrowFromShow = false;
        fixture.AcceptShow = true;
        Check.Success(fixture.Service.Open(null));
        Check.Equal(1, page.Shown);
        fixture.AcceptShow = false;
        Check.Failure(ModErrorCode.NotReady, fixture.Service.Open(null));
        Check.Equal(1, page.Hidden, "Losing the native host also ends the previous page's lifetime.");
        Check.True(!fixture.Service.IsOpen, "Visibility must not remain stale after host failure.");
        Check.True(fixture.HideCalls > 0, "Partial native windows are offered cleanup after failed show.");
    }

    internal static void WorkerOperationsCannotTouchWindowOrPageCallbacks()
    {
        using var fixture = new Fixture();
        var page = new Page();
        using IDisposable lease = Check.Success(fixture.Service.RegisterPage("mod.test", "Test", page));
        Check.Failure(ModErrorCode.WrongThread, Check.OnWorker(() => fixture.Service.Open(null)));
        Check.Failure(ModErrorCode.WrongThread, Check.OnWorker(() => fixture.Service.RegisterPage("worker.mod", "Worker", new Page())));
        Check.Failure(ModErrorCode.WrongThread, Check.OnWorker(() => fixture.Service.DrawSelected(Surface())));
        Check.OnWorker(() => { Check.Throws<InvalidOperationException>(() => lease.Dispose()); return true; });
        Check.OnWorker(() => { Check.Throws<InvalidOperationException>(() => fixture.Service.Close()); return true; });
        Check.OnWorker(() => { Check.Throws<InvalidOperationException>(() => fixture.Service.Dispose()); return true; });
        Check.Equal(0, fixture.ShowCalls);
        Check.Equal(0, fixture.HideCalls);
        Check.Equal(0, page.Shown);
        Check.Equal(1, fixture.Service.Pages.Count, "A rejected worker disposal retains the lease for later main-thread cleanup.");
        lease.Dispose();
        Check.Equal(0, fixture.Service.Pages.Count);
    }

    internal static void ReentrantPageCallbacksCannotCorruptSelectionOrRegistration()
    {
        using var fixture = new Fixture();
        var page = new Page();
        using IDisposable lease = Check.Success(fixture.Service.RegisterPage("mod.test", "Test", page));
        page.OnShow = () =>
        {
            Check.Failure(ModErrorCode.Busy, fixture.Service.Open(null));
            Check.Failure(ModErrorCode.Busy, fixture.Service.RegisterPage("mod.nested", "Nested", new Page()));
            Check.Throws<InvalidOperationException>(() => fixture.Service.Close());
            Check.Throws<InvalidOperationException>(() => lease.Dispose());
        };
        Check.Success(fixture.Service.Open(null));
        Check.Equal(1, page.Shown);
        Check.Equal(0, page.Hidden);
        Check.Equal(1, fixture.Service.Pages.Count);
        Check.Success(fixture.Service.DrawSelected(Surface()));
        Check.Equal(1, page.Drawn);
    }

    private static ToolPanelBuilder Surface() => new(new ToolInputRect(0, 0, 600, 500), ToolPanelPointer.None);

    private sealed class Fixture : IDisposable
    {
        private readonly int _mainThread = Environment.CurrentManagedThreadId;
        internal Fixture()
        {
            Service = new ModSettingsService(() => Environment.CurrentManagedThreadId == _mainThread,
                () =>
                {
                    ShowCalls++;
                    if (ThrowFromShow) throw new InvalidOperationException("Host failure");
                    return ModResult<bool>.Ok(AcceptShow);
                }, () => HideCalls++, (_, _) =>
                {
                    if (ThrowFromLog) throw new InvalidOperationException("Logger failure");
                });
        }

        internal ModSettingsService Service { get; }
        internal bool AcceptShow { get; set; } = true;
        internal bool ThrowFromShow { get; set; }
        internal bool ThrowFromLog { get; set; }
        internal int ShowCalls { get; private set; }
        internal int HideCalls { get; private set; }
        public void Dispose() => Service.Dispose();
    }

    private sealed class Page : IToolPanelContent, IToolPanelLifecycle
    {
        public float PreferredHeight => 500;
        internal int Shown { get; private set; }
        internal int Hidden { get; private set; }
        internal int Drawn { get; private set; }
        internal bool ThrowOnShow { get; set; }
        internal bool ThrowOnHide { get; set; }
        internal bool ThrowOnDraw { get; set; }
        internal Action? OnShow { get; set; }

        public void Draw(IToolPanelSurface surface)
        {
            Drawn++;
            if (ThrowOnDraw) throw new InvalidOperationException("Draw failure");
            surface.Text("settings.label", "Settings", surface.Row(30));
        }

        public void OnShown()
        {
            Shown++;
            OnShow?.Invoke();
            if (ThrowOnShow) throw new InvalidOperationException("Show failure");
        }

        public void OnHidden()
        {
            Hidden++;
            if (ThrowOnHide) throw new InvalidOperationException("Hide failure");
        }
    }
}
