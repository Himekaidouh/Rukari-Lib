using Rukari.Lib.Tools;

namespace Rukari.Lib.Tests;

/// <summary>
/// A hosted page draws through the shared panel instead of describing a snapshot. These rules are what a mod
/// depends on while it ports an editor: which rectangle a control got, which control owns the current press, and
/// when a drag keeps consuming input. They are pure arithmetic, so they are pinned here without a game.
/// </summary>
internal static class ToolPanelBuilderTests
{
    private static readonly ToolInputRect Content = new(0f, 0f, 576f, 640f);

    public static void RowsAreReservedInOrderAndCellsSplitThemEvenly()
    {
        var builder = new ToolPanelBuilder(Content, ToolPanelPointer.None);

        ToolInputRect first = builder.Row(40f);
        Check.Equal(0f, first.Y, "The first row starts at the bottom of the content.");
        Check.Equal(Content.Width, first.Width, "A row spans the whole content width.");
        ToolInputRect second = builder.Row(50f);
        Check.Equal(40f, second.Y, "Each row is placed above the previous one, because Y grows upward.");

        builder.Space(10f);
        ToolInputRect third = builder.Row(20f);
        Check.Equal(100f, third.Y, "A space advances the cursor without drawing anything.");
        Check.Equal(120f, builder.UsedHeight, "The used height is what the page must fit inside.");

        // Three columns share the row and leave exactly the gaps between them.
        ToolInputRect middle = builder.Cell(third, 1, 3, 8f);
        float columnWidth = (third.Width - 16f) / 3f;
        Check.Equal(columnWidth, middle.Width, "Equal columns share the row's width.");
        Check.Equal(columnWidth + 8f, middle.X, "The second column starts after the first plus one gap.");
        ToolInputRect last = builder.Cell(third, 2, 3, 8f);
        Check.Equal(third.X + third.Width, last.X + last.Width, "The last column ends flush with the row.");
        foreach (int columns in new[] { 1, 2, 4 })
        {
            for (int i = 0; i < columns; i++)
            {
                ToolInputRect cell = builder.Cell(third, i, columns);
                Check.True(cell.X >= third.X - .01 && cell.X + cell.Width <= third.X + third.Width + .01,
                    "Every cell must stay inside its row.");
            }
        }
    }

    public static void HostedControlIdsMustBeUniqueWithinAFrame()
    {
        var builder = new ToolPanelBuilder(Content, ToolPanelPointer.None);
        builder.Button("apply", "应用", builder.Row(39f));
        // A duplicate id would create a control that can never receive input, so it must fail loudly.
        Check.Throws<InvalidOperationException>(() => builder.Button("apply", "又一个", builder.Row(39f)));
        // An empty id cannot be addressed by the page either.
        Check.Throws<InvalidOperationException>(() => builder.Text("  ", "空 id", builder.Row(20f)));
        Check.Equal(1, builder.Elements.Count, "A rejected control is not added to the frame.");
    }

    public static void OnlyTheControlUnderThePressCanBeClicked()
    {
        var layout = new ToolPanelBuilder(Content, ToolPanelPointer.None);
        ToolInputRect apply = layout.Row(39f);
        layout.Button("apply", "应用", apply);
        ToolInputRect cancel = layout.Row(39f);
        layout.Button("cancel", "取消", cancel);

        // The renderer resolves the press against the previous frame's layout, then hands it back to the page.
        string? pressed = ToolPanelBuilder.HitTest(layout.Elements, apply.X + 5f, apply.Y + 5f);
        Check.Equal("apply", pressed, "A press inside a button belongs to that button.");

        var click = new ToolPanelBuilder(Content, new ToolPanelPointer(apply.X + 5f, apply.Y + 5f, false, false, true, 0f, 0f), pressed);
        Check.True(click.Button("apply", "应用", apply), "A press and release on the same button is a click.");
        Check.True(!click.Button("cancel", "取消", cancel), "A button that was not pressed cannot be clicked.");
        Check.True(click.IsHovered("apply"), "The pointer is over the button it clicked.");

        var draggedAway = new ToolPanelBuilder(Content,
            new ToolPanelPointer(cancel.X + 5f, cancel.Y + 5f, false, false, true, 0f, 0f), "apply");
        Check.True(!draggedAway.Button("apply", "应用", apply),
            "Releasing over another control must not count as a click on the control the press started on.");
        Check.True(!draggedAway.Button("cancel", "取消", cancel), "…nor as a click on the control under the release.");
    }

    public static void StyledButtonsKeepTheLegacyButtonAndRespectInputState()
    {
        var layout = new ToolPanelBuilder(Content, ToolPanelPointer.None);
        IToolPanelSurface legacy = layout;
        IToolPanelSurfaceStyledButtons styled = layout;
        ToolInputRect normal = layout.Row(39f);
        ToolInputRect primary = layout.Row(39f);
        ToolInputRect undo = layout.Row(39f);
        ToolInputRect selected = layout.Row(39f);
        ToolInputRect disabled = layout.Row(39f);

        legacy.Button("normal", "普通", normal);
        styled.StyledButton("primary", "确认", primary, ToolSurfaceStyle.Primary, true, false);
        styled.StyledButton("undo", "撤销", undo, ToolSurfaceStyle.Undo, true, false);
        styled.StyledButton("selected", "选中", selected, ToolSurfaceStyle.Selected, true, false);
        styled.StyledButton("disabled", "不可用", disabled, ToolSurfaceStyle.Disabled, true, false);

        Check.Equal(ToolSurfaceStyle.Normal, layout.Find("normal")!.Style,
            "Existing pages retain the original neutral button without opting into the new interface.");
        Check.Equal(ToolSurfaceStyle.Primary, layout.Find("primary")!.Style);
        Check.Equal(ToolSurfaceStyle.Undo, layout.Find("undo")!.Style);
        Check.Equal(ToolSurfaceStyle.Selected, layout.Find("selected")!.Style);
        Check.True(!layout.Find("disabled")!.Enabled,
            "The Disabled role must suppress input even when the caller passes enabled=true.");
        Check.True(layout.HitTest(disabled.X + 5f, disabled.Y + 5f) == null,
            "A disabled styled button must not capture a press from the previous-frame layout.");

        string? pressed = layout.HitTest(primary.X + 5f, primary.Y + 5f);
        var released = new ToolPanelBuilder(Content,
            new ToolPanelPointer(primary.X + 5f, primary.Y + 5f, false, false, true, 0f, 0f), pressed);
        var releasedStyled = (IToolPanelSurfaceStyledButtons)released;
        Check.True(releasedStyled.StyledButton("primary", "确认", primary, ToolSurfaceStyle.Primary, true, false),
            "Styled buttons use the same press-and-release rule as the original button.");
        Check.True(!releasedStyled.StyledButton("disabled", "不可用", disabled,
            ToolSurfaceStyle.Disabled, true, false), "A disabled styled button cannot report a click.");
    }

    public static void APressOutsideEveryControlBelongsToNobody()
    {
        var layout = new ToolPanelBuilder(Content, ToolPanelPointer.None);
        ToolInputRect label = layout.Row(24f);
        layout.Text("hint", "提示文字", label);
        ToolInputRect button = layout.Row(39f);
        layout.Button("apply", "应用", button);

        Check.True(layout.HitTest(label.X + 4f, label.Y + 4f) == null,
            "Text is not interactive, so a press on it must not start a drag or a click.");
        Check.Equal("apply", layout.HitTest(button.X + 4f, button.Y + 4f));

        var nowhere = new ToolPanelBuilder(Content,
            new ToolPanelPointer(Content.Width - 1f, Content.Height - 1f, false, false, true, 0f, 0f), null);
        Check.True(!nowhere.Button("apply", "应用", button), "Without a pressed control nothing can be clicked.");
        Check.True(!nowhere.IsHeld("apply"), "Nothing is held without a pressed control.");

        var disabled = new ToolPanelBuilder(Content,
            new ToolPanelPointer(button.X + 4f, button.Y + 4f, true, true, false, 0f, 0f), "apply");
        Check.True(!disabled.IsHovered("apply"), "A disabled button cannot be hovered…");
        Check.True(!disabled.Button("apply", "应用", button, enabled: false), "…and cannot be clicked.");
        Check.True(ToolPanelBuilder.HitTest(disabled.Elements, button.X + 4f, button.Y + 4f) == null,
            "A disabled button must not swallow a press that belongs to the page.");
    }

    public static void DragOnlyContinuesWhileThePressStaysOnTheArea()
    {
        var layout = new ToolPanelBuilder(Content, ToolPanelPointer.None);
        ToolInputRect stage = layout.Row(300f);
        layout.Area("stage", stage);
        string? pressed = ToolPanelBuilder.HitTest(layout.Elements, stage.X + 20f, stage.Y + 20f);
        Check.Equal("stage", pressed, "A press inside the area belongs to it.");

        var dragging = new ToolPanelBuilder(Content,
            new ToolPanelPointer(stage.X - 60f, stage.Y + 40f, true, false, false, -12f, 6f), "stage");
        Check.True(dragging.Area("stage", stage), "The area reports that it is being held.");
        Check.Equal(-12f, dragging.DragX("stage"), "A drag reports the pointer movement even outside the rectangle.");
        Check.Equal(6f, dragging.DragY("stage"));
        Check.True(dragging.IsDragging, "The renderer must keep consuming input for the whole drag.");

        var buttons = new ToolPanelBuilder(Content,
            new ToolPanelPointer(stage.X + 20f, stage.Y + 20f, true, false, false, -12f, 6f), "apply");
        ToolInputRect apply = buttons.Row(39f);
        buttons.Button("apply", "应用", apply);
        Check.True(!buttons.IsDragging, "Holding a button is not a drag: it must not capture the whole screen.");

        var released = new ToolPanelBuilder(Content,
            new ToolPanelPointer(stage.X + 20f, stage.Y + 20f, false, false, false, 0f, 0f), "stage");
        Check.True(!released.Area("stage", stage), "A released press stops driving the area.");
        Check.Equal(0f, released.DragX("stage"), "A released drag reports no movement.");
    }

    public static void EverySurfaceStyleResolvesToADrawableSurface()
    {
        foreach (ToolSurfaceStyle style in Enum.GetValues<ToolSurfaceStyle>())
        {
            string key = ToolSurfaceStyles.ThemeKey(style);
            Check.True(!string.IsNullOrWhiteSpace(key), $"Style {style} must resolve to a palette key.");
            Check.True(!key.Contains(' '), "A palette key must not contain spaces.");
        }
        // The roles a page is told to use must not silently collapse onto the default surface.
        Check.Equal(ToolPalette.Disabled, ToolPalette.ForKey(ToolSurfaceStyles.ThemeKey(ToolSurfaceStyle.Disabled)));
        Check.Equal(ToolPalette.Selected, ToolPalette.ForKey(ToolSurfaceStyles.ThemeKey(ToolSurfaceStyle.Selected)));
        Check.Equal(ToolPalette.Primary, ToolPalette.ForKey(ToolSurfaceStyles.ThemeKey(ToolSurfaceStyle.Primary)));
        Check.Equal(ToolPalette.Undo, ToolPalette.ForKey(ToolSurfaceStyles.ThemeKey(ToolSurfaceStyle.Undo)));
        Check.Equal(ToolPalette.Panel, ToolPalette.ForKey(ToolSurfaceStyles.ThemeKey(ToolSurfaceStyle.Panel)));
        Check.Equal(ToolPalette.Header, ToolPalette.ForKey(ToolSurfaceStyles.ThemeKey(ToolSurfaceStyle.Header)));
        Check.Equal(ToolPalette.Normal, ToolPalette.ForKey(ToolSurfaceStyles.ThemeKey(ToolSurfaceStyle.Normal)));
        Check.Equal(ToolPalette.Normal, ToolPalette.ForKey(ToolSurfaceStyles.ThemeKey(ToolSurfaceStyle.Solid)));
    }

    public static void HostedPageSeesItsPixelRectangle()
    {
        var pixels = new ToolInputRect(120f, 340f, 467f, 976f);
        var builder = new ToolPanelBuilder(Content, ToolPanelPointer.None, null, pixels);
        Check.Equal(pixels, builder.BoundsInPixels,
            "A page is told the pixel rectangle its content really occupies, so it can place its own canvas there.");
        Check.Equal(Content, builder.Content, "The page still draws in its own content coordinates.");
        Check.Equal(Content.Width, builder.Width, "Width is the content width, not the pixel width.");
        Check.Equal(Content.Height, builder.Height, "Height is the content height, not the pixel height.");

        // A surface with no separate pixel rectangle reports its content rectangle rather than a zero-sized one, so
        // a headless or test page still gets a usable answer.
        var headless = new ToolPanelBuilder(Content, ToolPanelPointer.None);
        Check.Equal(Content, headless.BoundsInPixels, "Without a pixel rectangle the content rectangle is the answer.");
        Check.True(headless.BoundsInPixels.IsValid, "The fallback rectangle must still be usable.");
    }

    public static void HostedSheetAndTextMeasurementAreBounded()
    {
        Check.Equal(ToolDrawerLayout.MinimumHostedHeight,
            ToolDrawerLayout.MeasureHosted(1f).Height, "A wrong tiny height must not collapse the sheet.");
        Check.Equal(ToolDrawerLayout.MaximumHostedHeight,
            ToolDrawerLayout.MeasureHosted(99999f).Height, "A wrong huge height must not overflow the sheet.");
        Check.Equal(ToolDrawerLayout.MinimumHostedHeight,
            ToolDrawerLayout.MeasureHosted(float.NaN).Height, "A non-finite height must fall back to the minimum.");
        Check.Equal(720f, ToolDrawerLayout.MeasureHosted(720f).Height, "A sane height is used as given.");
        var hosted = ToolDrawerLayout.MeasureHosted(720f);
        Check.True(!hosted.Pagination && hosted.VisibleRows == 0 && hosted.ActionRows == 0,
            "A hosted sheet has no snapshot sections.");

        // A hosted page sizes its own rectangles, so the measurement has to count CJK runes as wide ones.
        string ascii = new('W', 40);
        string cjk = new('字', 40);
        Check.True(ToolDrawerLayout.MeasureTextHeight(cjk, 18, 200) >= ToolDrawerLayout.MeasureTextHeight(ascii, 18, 200),
            "Full-width text must never measure narrower than Latin text.");
        Check.Equal(ToolDrawerLayout.LineHeight(18), ToolDrawerLayout.MeasureTextHeight("一行", 18, 400),
            "One line of text is one line high.");
        Check.Equal(0f, ToolDrawerLayout.MeasureTextHeight("", 18, 400), "Empty text takes no space.");
    }
}
