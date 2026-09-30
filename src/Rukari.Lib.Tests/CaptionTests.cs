using Rukari.Lib.Captions;
using Rukari.Lib.Tools;

namespace Rukari.Lib.Tests;

/// <summary>
/// The caption layer's arithmetic, pinned without a game: the typewriter, the quarter-second breathing that must
/// never exceed its limits, the fade, the placement that has to keep off a face, and the subtitle files a song
/// already comes with. Every number here is what the renderer will draw, so a screenshot is never the first
/// evidence that the motion went wrong.
/// </summary>
internal static class CaptionTests
{
    /// <summary>A plain LRC file, the enhanced one, and the offset tag the format defines.</summary>
    internal static void LrcImportKeepsTheSongsOwnBeats()
    {
        const string plain =
            "[ar:Mili]\n" +
            "[ti:In Hell We Live, Lament]\n" +
            "[offset:+250]\n" +
            "[00:12.35]在我年少迷茫之时\n" +
            "[00:16.00][00:20.00]第二句重复两次\n";
        var cues = SubtitleImport.FromLrc(plain);
        Check.Equal(3, cues.Count, "Two timestamps on one line are two captions, and metadata is not a caption.");
        Check.Near(12.10, cues[0].At, "The offset tag shifts every caption, so a file's own nudge is honoured.");
        Check.Equal("在我年少迷茫之时", cues[0].Text);
        Check.Near(15.75, cues[1].At, "A caption starts at its own timestamp.");
        Check.Near(19.75, cues[2].At, "A repeated line keeps both of its own times.");
        Check.Equal(8, CaptionTimeline.CharacterCount(cues[0]), "A CJK caption counts one character per glyph.");
        Check.Equal(7, CaptionTimeline.CharacterCount(cues[1]), "…and counts the next line's own glyphs.");
        Check.True(cues[0].CharacterTimes is null,
            "A plain LRC line carries no per-character times, so the layer spaces the characters itself.");

        const string enhanced = "[00:05.00]<00:05.00>在<00:05.40>我<00:05.80>年<00:06.20>少";
        var beat = SubtitleImport.FromLrc(enhanced);
        Check.Equal(1, beat.Count);
        Check.Equal("在我年少", beat[0].Text, "The per-character markers are timing, not text.");
        Check.True(beat[0].CharacterTimes is { Count: 4 }, "Every character kept its own beat.");
        Check.Near(0.0, beat[0].CharacterTimes![0], "The beats are relative to the caption's own start.");
        Check.Near(1.2, beat[0].CharacterTimes![3], "The last beat is 1.2s after the first.");

        // A marker count that does not match the text is not usable timing, and must not shift the text.
        var mismatched = SubtitleImport.FromLrc("[00:01.00]<00:01.00>甲<00:02.00>乙丙丁");
        Check.Equal("甲乙丙丁", mismatched[0].Text);
        Check.True(mismatched[0].CharacterTimes is null,
            "A file whose markers do not line up falls back to even spacing instead of guessing.");

        Check.True(SubtitleImport.LooksLikeSrt("1\n00:00:01,000 --> 00:00:02,000\nhi"), "SRT is recognised by its arrow.");
        Check.True(!SubtitleImport.LooksLikeSrt(plain), "An LRC file is not mistaken for SRT.");
    }

    internal static void SrtAndAssImportKeepTheirOwnTiming()
    {
        const string srt = "1\r\n00:00:01,000 --> 00:00:04,000\r\n第一行\r\n第二行\r\n\r\n2\r\n00:00:05,500 --> 00:00:08,000\r\n下一句\r\n";
        var cues = SubtitleImport.FromSrt(srt);
        Check.Equal(2, cues.Count);
        Check.Near(1.0, cues[0].At, "A subtitle's own start is used as written.");
        Check.True(cues[0].End.HasValue, "A subtitle states its own end, which replaces the caption's hold.");
        Check.Near(4.0, cues[0].End!.Value, "The end is the one the file wrote.");
        Check.Equal("第一行 第二行", cues[0].Text, "A wrapped subtitle is one caption, not two.");
        Check.Near(5.5, cues[1].At, "The second block keeps its own timing.");

        const string ass =
            "[Script Info]\n" +
            "Title: test\n" +
            "[Events]\n" +
            "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n" +
            "Dialogue: 0,0:00:02.00,0:00:05.00,Default,CH0335,0,0,0,,{\\i1}愿意为了他人的幸福{\\i0}与便利牺牲自身的人又有几个……\n";
        var assCues = SubtitleImport.FromAss(ass);
        Check.Equal(1, assCues.Count, "Only dialogue lines become captions.");
        Check.Near(2.0, assCues[0].At, "The start column is the caption's time.");
        Check.Equal("CH0335", assCues[0].Speaker, "The speaker column becomes the caption's style key.");
        Check.Equal("愿意为了他人的幸福与便利牺牲自身的人又有几个……", assCues[0].Text,
            "Inline styling blocks are removed; they are the file's look, not its words.");
    }

    /// <summary>The typewriter: one character at a time, all of them before the hold is over, none after the end.</summary>
    internal static void TheTypewriterRevealsOneCharacterAtATime()
    {
        var rules = CaptionMotionRules.Default;
        var cue = new CaptionCue { Text = "在我年少", At = 0 };
        const int count = 4;
        double duration = CaptionTimeline.Duration(cue, rules, count);
        Check.True(duration > 2 && duration < 3,
            $"A four character caption lives about two and a half seconds; got {duration:F2}.");

        Check.Equal(0, Visible(cue, rules, count, 0), "Nothing is readable on the first frame.");
        int previous = 0;
        for (double t = 0; t <= duration; t += .02)
        {
            int visible = Visible(cue, rules, count, t);
            Check.True(visible >= previous,
                $"Characters never disappear mid-line; at {t:F2}s there were {visible} after {previous}.");
            previous = visible;
        }
        Check.Equal(count, Visible(cue, rules, count, .4), "Every character has appeared well before the hold ends.");

        (CaptionLineState line, CaptionCharacterState[] characters) = CaptionTimeline.Evaluate(cue, rules, count, .4);
        Check.Equal(4, characters.Length);
        foreach (var character in characters)
        {
            Check.True(character.Alpha > .9, $"A settled character is fully opaque; got {character.Alpha:F2}.");
            // Settled means grown to its own size; the couple of percent of breathing sits on top of that.
            Check.True(character.Scale >= .95 && character.Scale <= 1.03,
                $"A settled character has grown to its own size; got {character.Scale:F3}.");
        }
        Check.Equal(1.0, line.Alpha, "The line is fully visible during its hold.");

        // The slow fade holds the line bright and drops it at the very end, which is the point of that curve.
        (CaptionLineState early, _) = CaptionTimeline.Evaluate(cue, rules, count, duration - rules.LineFadeSeconds + .1);
        (CaptionLineState late, _) = CaptionTimeline.Evaluate(cue, rules, count, duration - .01);
        Check.True(early.Alpha > .75, $"The slow fade has barely started a tenth of the way in; got {early.Alpha:F2}.");
        Check.True(late.Alpha < .15, $"…and it is nearly gone at the end; got {late.Alpha:F2}.");
        Check.Equal(0.0, CaptionTimeline.Evaluate(cue, rules, count, duration + .1).Line.Alpha,
            "A caption that is over draws nothing at all.");

        // With per-character times the characters follow the song instead of the layer's own spacing.
        var sung = cue with { CharacterTimes = new[] { 0.0, 1.0, 2.0, 3.0 } };
        Check.Near(3.0, CaptionTimeline.AppearsAt(sung, rules, 3), "A character appears on its own beat.");
        Check.Equal(1, Visible(sung, rules, count, .5),
            "Half a second in, only the first character of the line has arrived.");
        Check.Equal(2, Visible(sung, rules, count, 1.2), "…and the second arrives on its own beat.");
    }

    /// <summary>
    /// Every motion stays inside its limit even when a hand-written sheet asks for far more: this is the "random but
    /// regular" rule, and it is what keeps a caption readable.
    /// </summary>
    internal static void CaptionMotionNeverLeavesItsBounds()
    {
        var wild = new CaptionMotionRules
        {
            LineTiltDegrees = 90,
            CharacterTiltDegrees = 90,
            CharacterOffsetPixels = 90,
            CharacterScaleFraction = 9,
            LineDriftFraction = 9,
            JitterPeriodSeconds = .25
        };
        var cue = new CaptionCue { Text = "在我年少迷茫之时，愿它不被忘记", At = 0, Emphasis = true };
        int count = CaptionTimeline.CharacterCount(cue);
        double duration = CaptionTimeline.Duration(cue, wild, count);
        for (double t = 0; t <= duration; t += .017)
        {
            (CaptionLineState line, CaptionCharacterState[] characters) = CaptionTimeline.Evaluate(cue, wild, count, t);
            Check.True(Math.Abs(line.TiltDegrees) <= CaptionMotionRules.MaximumLineTiltDegrees + .001,
                $"A line may lean at most {CaptionMotionRules.MaximumLineTiltDegrees} degrees; got {line.TiltDegrees:F2} at {t:F2}s.");
            Check.True(Math.Abs(line.OffsetX) <= CaptionMotionRules.MaximumLineDriftFraction + .001
                && Math.Abs(line.OffsetY) <= CaptionMotionRules.MaximumLineDriftFraction + .001,
                "A line may only drift a small fraction of the screen.");
            foreach (var character in characters)
            {
                Check.True(Math.Abs(character.RotationDegrees) <= CaptionMotionRules.MaximumTotalTiltDegrees + .001,
                    $"Line lean plus character lean stays under {CaptionMotionRules.MaximumTotalTiltDegrees} degrees; got {character.RotationDegrees:F2}.");
                Check.True(Math.Abs(character.OffsetX) <= CaptionMotionRules.MaximumCharacterOffsetPixels + .001
                    && Math.Abs(character.OffsetY) <= CaptionMotionRules.MaximumCharacterOffsetPixels + .001,
                    $"A character may wander at most {CaptionMotionRules.MaximumCharacterOffsetPixels} pixels.");
                Check.True(character.Scale >= .8 && character.Scale <= 1.03,
                    $"A character may only breathe a couple of percent; got {character.Scale:F3}.");
                Check.True(character.Emphasised, "An emphasised line marks its characters.");
            }
        }
        Check.Equal(CaptionMotionRules.MaximumLineTiltDegrees, CaptionMotionRules.Default.Bounded().LineTiltDegrees,
            "The shipped rules sit exactly at the limit, so the effect is as strong as it may be.");
        Check.Equal(CaptionMotionRules.MaximumCharacterOffsetPixels, wild.Bounded().CharacterOffsetPixels,
            "A wild sheet is clamped to the limit rather than refused.");
    }

    /// <summary>Deterministic so a frame can be reproduced, smooth so nothing snaps at a period boundary.</summary>
    internal static void CaptionMotionIsDeterministicAndSmooth()
    {
        var rules = CaptionMotionRules.Default;
        var cue = new CaptionCue { Text = "在我年少迷茫之时", At = 0 };
        int count = CaptionTimeline.CharacterCount(cue);
        double duration = CaptionTimeline.Duration(cue, rules, count);

        (_, CaptionCharacterState[] first) = CaptionTimeline.Evaluate(cue, rules, count, 1.234);
        (_, CaptionCharacterState[] again) = CaptionTimeline.Evaluate(cue, rules, count, 1.234);
        for (int i = 0; i < count; i++)
        {
            Check.Equal(first[i].OffsetX, again[i].OffsetX, "The same instant always draws the same character.");
            Check.Equal(first[i].RotationDegrees, again[i].RotationDegrees, "…including its lean.");
        }
        Check.True(!CaptionTimeline.Evaluate(cue, rules, count, 1.234).Characters
                .SequenceEqual(CaptionTimeline.Evaluate(cue with { Text = "另一句" }, rules, count, 1.234).Characters),
            "Two different lines do not move in lockstep, or the whole run would look mechanical.");

        // Sixty frames a second: a character may never jump, because a jump reads as a glitch rather than as life.
        // A quarter-second period spread over a 2.0 span moves at most about 0.24px per frame, so this bound
        // catches a snap without failing on the motion itself.
        (_, CaptionCharacterState[] previous) = CaptionTimeline.Evaluate(cue, rules, count, .6);
        for (double t = .6 + 1.0 / 60; t <= duration; t += 1.0 / 60)
        {
            (_, CaptionCharacterState[] current) = CaptionTimeline.Evaluate(cue, rules, count, t);
            for (int i = 0; i < count; i++)
            {
                Check.True(Math.Abs(current[i].OffsetX - previous[i].OffsetX) < .35,
                    $"A character moves smoothly between frames; it moved {Math.Abs(current[i].OffsetX - previous[i].OffsetX):F3}px at {t:F2}s.");
                Check.True(Math.Abs(current[i].RotationDegrees - previous[i].RotationDegrees) < .6,
                    "A character leans smoothly between frames.");
            }
            previous = current;
        }
    }

    /// <summary>The placement keeps a caption off a face and off the dialogue window, and always fully on screen.</summary>
    internal static void CaptionPlacementAvoidsWhatTheSceneNeeds()
    {
        // A character standing low in the middle, and the official dialogue window along the bottom.
        var forbidden = new[]
        {
            new ToolInputRect(0, 0, 1920, 200),
            new ToolInputRect(600, 200, 720, 480)
        };
        var request = new CaptionPlacementRequest
        {
            ScreenWidth = 1920,
            ScreenHeight = 1080,
            TextWidth = 600,
            TextHeight = 80,
            Forbidden = forbidden,
            Seed = CaptionTimeline.SeedOf("在我年少迷茫之时")
        };
        CaptionPlacement placement = CaptionPlacementPolicy.Place(request);
        Check.True(placement.Rect.IsValid, "A caption on a normal screen is always placed.");
        Check.Equal(0, placement.BandIndex, "With nothing in the way, the caption takes its preferred band.");
        Check.Equal(0f, placement.OverlapFraction, "…and covers nothing the scene needs.");
        Check.True(placement.Rect.Y > 200, "The caption is above the dialogue window, never over it.");
        Check.True(!Overlaps(placement.Rect, forbidden[1]), "The caption does not cover the character.");
        Check.True(placement.Rect.X >= 1920 * .035 - .01
            && placement.Rect.X + placement.Rect.Width <= 1920 * .965 + .01,
            "The caption keeps the screen's own margin instead of touching an edge.");

        // A long line is scaled down until it fits: "it must show itself completely" is a placement rule, not a
        // hope. This caption would be four screens wide at its own size.
        CaptionPlacement tooWide = CaptionPlacementPolicy.Place(request with { TextWidth = 8000, TextHeight = 80 });
        Check.True(tooWide.FontScale < .3, $"An enormous caption is scaled down; got {tooWide.FontScale:F2}.");
        Check.True(tooWide.Rect.Width <= 1920 * .93 + .01, "…and the rectangle it gets still fits inside the screen.");
        Check.True(tooWide.Rect.X >= 0 && tooWide.Rect.X + tooWide.Rect.Width <= 1920 + .01,
            "A scaled caption never runs off the screen.");

        // Nothing is free: the caption is dimmed rather than moved onto a face, and it is never dropped.
        CaptionPlacement blocked = CaptionPlacementPolicy.Place(request with
        {
            Forbidden = new[] { new ToolInputRect(0, 0, 1920, 1080) }
        });
        Check.True(blocked.Rect.IsValid, "A caption is drawn even when the whole screen is busy.");
        Check.True(blocked.OverlapFraction > .5, "…and it reports how much it had to cover.");
        Check.True(CaptionPlacementPolicy.AlphaFor(blocked) < .3,
            "A caption that could not avoid what is under it is drawn faint, not hidden.");
        Check.Equal(1f, CaptionPlacementPolicy.AlphaFor(placement), "A caption with a free band is drawn at full strength.");

        // An author's pin replaces the search, and is still kept inside the screen.
        CaptionPlacement pinned = CaptionPlacementPolicy.Place(request with { AnchorX = .5, AnchorY = .5 });
        Check.True(pinned.Pinned, "A pinned caption says so, so the editor can show it as pinned.");
        Check.True(Math.Abs((pinned.Rect.Y + pinned.Rect.Height / 2) - 540) < 1, "A pinned caption sits where it was pinned.");
        CaptionPlacement offScreen = CaptionPlacementPolicy.Place(request with { AnchorX = .99, AnchorY = .99 });
        Check.True(offScreen.Rect.X + offScreen.Rect.Width <= 1920 + .01
            && offScreen.Rect.Y + offScreen.Rect.Height <= 1080 + .01,
            "A pin that would hang off the screen is pulled back inside it.");
    }

    /// <summary>
    /// A pure CG has no dialogue window, so the caption may use far more of the frame — which is exactly when the
    /// layer is worth having. It must still keep off whatever the picture puts in the middle.
    /// </summary>
    internal static void ACinematicSceneGivesTheCaptionMoreRoom()
    {
        // A tall figure filling the upper middle: every dialogue-mode band runs into it.
        var forbidden = new[] { new ToolInputRect(0, 700, 1920, 380) };
        var request = new CaptionPlacementRequest
        {
            ScreenWidth = 1920,
            ScreenHeight = 1080,
            TextWidth = 600,
            TextHeight = 80,
            Forbidden = forbidden,
            Seed = 1234
        };
        CaptionPlacement dialogue = CaptionPlacementPolicy.Place(request);
        CaptionPlacement cinematic = CaptionPlacementPolicy.Place(request with { Cinematic = true });
        Check.True(cinematic.OverlapFraction < dialogue.OverlapFraction - .1f,
            $"The cinematic bands find the free part of the frame; dialogue {dialogue.OverlapFraction:F2} vs cinematic {cinematic.OverlapFraction:F2}.");
        Check.Equal(0f, cinematic.OverlapFraction, "…and in this scene they find a band that covers nothing at all.");
        Check.True(cinematic.Rect.Y < dialogue.Rect.Y,
            "The cinematic caption is the lower one, because the figure owns the top of this frame.");
        Check.True(cinematic.Rect.Y >= 0 && cinematic.Rect.Y + cinematic.Rect.Height <= 1080 + .01,
            "It is still fully on screen.");
        Check.True(CaptionPlacementPolicy.FontScaleForScene(true) > 1f,
            "A cinematic scene draws the caption larger, since nothing competes with it.");
        Check.Equal(1f, CaptionPlacementPolicy.FontScaleForScene(false));
    }

    /// <summary>The three style levels, the file round trip, and how a caption is found at a moment in time.</summary>
    internal static void TheSheetLayersStylesAndFindsItsOwnCaptions()
    {
        var sheet = new CaptionSheet
        {
            Mode = CaptionSheet.ModeLoop,
            Track = "bgm_song",
            Default = new CaptionStyle { FontSize = 40, Color = "#FFFFFF", OutlineColor = "#000000" },
            Speakers = new Dictionary<string, CaptionStyle>(StringComparer.OrdinalIgnoreCase)
            {
                ["CH0335"] = new() { FontSize = 64, Color = "#FFD24A" },
                ["lyric"] = new() { Color = "#7DD3FC" }
            },
            Lines =
            {
                new CaptionCue { Text = "在我年少迷茫之时", At = 12.35, Speaker = "lyric" },
                new CaptionCue { Text = "愿意为了他人的幸福……", At = 30.0, Speaker = "CH0335" },
                new CaptionCue { Text = "这一句自己定了大小", At = 40.0, Speaker = "CH0335", Style = new CaptionStyle { FontSize = 72 } },
                new CaptionCue { Text = "挂在对话行上的", Line = "line-42" }
            }
        };
        var lyrics = sheet.StyleFor(sheet.Lines[0]);
        Check.Equal(40, lyrics.FontSize, "The sheet's own size is the base every caption starts from.");
        Check.Equal("#7DD3FC", lyrics.Color.ToHex()[..7], "A speaker's colour wins over the sheet default.");

        var boss = sheet.StyleFor(sheet.Lines[1]);
        Check.Equal(64, boss.FontSize, "A character may have its own size, which is how a boss shouts.");
        Check.Equal("#FFD24A", boss.Color.ToHex()[..7], "…and its own colour, which is how a character is recognised.");

        var oneLine = sheet.StyleFor(sheet.Lines[2]);
        Check.Equal(72, oneLine.FontSize, "A single line may override its own speaker.");
        Check.Equal("#FFD24A", oneLine.Color.ToHex()[..7], "…without losing what it inherited.");
        Check.Equal(3f, oneLine.OutlineWidth, "An unset field falls back to the resolved default.");

        var rules = CaptionMotionRules.Default;
        Check.Equal("在我年少迷茫之时", sheet.CueAt(12.4, rules)?.Text, "A caption is found while it is on screen.");
        Check.True(sheet.CueAt(5.0, rules) is null, "…and nothing is shown before the first one starts.");
        Check.True(sheet.CueAt(200, rules) is null, "…and nothing long after the last one.");
        Check.Equal("line-42", sheet.CueForLine("line-42")?.Line, "A dialogue-bound caption is found by its line id.");
        Check.True(sheet.CueForLine("nothing") is null, "A line with no caption reports none.");
        Check.True(sheet.IsTrackDriven, "A loop sheet follows the track.");
        Check.True(!(sheet with { Mode = CaptionSheet.ModeScript }).IsTrackDriven, "A script sheet follows the dialogue.");

        // The file the editor writes must read back exactly, and a broken file must not take the layer down.
        CaptionSheet reloaded = CaptionSheet.Load(sheet.ToJson());
        Check.Equal(sheet.Lines.Count, reloaded.Lines.Count);
        Check.Equal(sheet.Lines[1].Text, reloaded.Lines[1].Text, "Text survives the round trip, CJK included.");
        Check.Equal("#FFD24A", reloaded.StyleFor(reloaded.Lines[1]).Color.ToHex()[..7], "Styles survive too.");
        Check.Equal(64, reloaded.StyleFor(reloaded.Lines[1]).FontSize);
        Check.True(!CaptionSheet.TryLoadFile(Path.Combine(Path.GetTempPath(), "rukari-no-such-sheet.json"),
            out _, out string error), "A missing sheet is reported, not thrown.");
        Check.True(error.Length != 0, "The reason is handed back so the log can say it.");
    }

    /// <summary>
    /// The area a caption may use: the editor keeps its own preview pane on the right, so a caption spreads across
    /// what is left instead of covering the picture it accompanies. While a line is being edited it is previewed in
    /// a strip along the top, because its real position belongs to the scene.
    /// </summary>
    internal static void AUsableAreaKeepsCaptionsOffTheEditorsPreviewPane()
    {
        var request = new CaptionPlacementRequest
        {
            ScreenWidth = 2560,
            ScreenHeight = 1494,
            TextWidth = 700,
            TextHeight = 90,
            UsableFraction = .62f,
            Seed = 99
        };
        CaptionPlacement placed = CaptionPlacementPolicy.Place(request);
        Check.True(placed.Rect.X + placed.Rect.Width <= 2560 * .62f + .01f,
            $"A caption stays inside the area it was given; got {placed.Rect}.");
        Check.True(placed.Rect.X >= 2560 * .035f - .01f, "…and keeps the screen's own margin on the left.");

        CaptionPlacement tooWide = CaptionPlacementPolicy.Place(request with { TextWidth = 4000 });
        Check.True(tooWide.FontScale < .5,
            $"A caption too wide for the usable area is scaled down rather than pushed into the preview pane; got {tooWide.FontScale:F2}.");
        Check.True(tooWide.Rect.X + tooWide.Rect.Width <= 2560 * .62f + .01f,
            "…and still ends inside the area.");

        // The editor's own preview pane is the right-hand part of the screen. A caption being edited is drawn there
        // instead of over the pane: same rules, same size, so what the author sees is what the scene will show.
        var inPane = request with { UsableStart = .62f, UsableFraction = .38f };
        CaptionPlacement preview = CaptionPlacementPolicy.Place(inPane);
        Check.True(preview.Rect.X >= 2560 * .62f - .01f,
            $"A preview stays inside the preview pane; it started at {preview.Rect.X:F0}.");
        Check.True(preview.Rect.X + preview.Rect.Width <= 2560 + .01f, "…and inside the window.");
        Check.Equal(1f, preview.FontScale, "A preview is drawn at the size the caption will really use.");

        CaptionPlacement pinned = CaptionPlacementPolicy.Place(request with { AnchorX = .5, AnchorY = .5 });
        Check.True(pinned.Rect.X + pinned.Rect.Width / 2 <= 2560 * .62f + .01f,
            "A hand-pinned caption is placed inside the usable area too, so a pin cannot cover the preview pane.");
    }

    /// <summary>
    /// Reading a font's own name, which is the only way to use a font the game does not ship: the platform's font
    /// APIs take a family name, not a path. The parser is checked against a real localisation font when one is on
    /// this machine, and against rubbish always, because a caption layer must survive a file that is not a font.
    /// </summary>
    internal static void TtfNamesAreReadFromAFontFileOrReportedAsBroken()
    {
        Check.True(!TtfNameReader.TryRead(new byte[] { 1, 2, 3 }, out _, out string shortError),
            "Three bytes are not a font.");
        Check.True(shortError.Length != 0, "…and the reason is reported so the log can say it.");

        var notAFont = new byte[64];
        notAFont[0] = 0x89;
        notAFont[1] = 0x50;
        notAFont[2] = 0x4E;
        notAFont[3] = 0x47;
        Check.True(!TtfNameReader.TryRead(notAFont, out _, out string signatureError),
            "A PNG is not mistaken for a font.");
        Check.True(signatureError.Contains("signature", StringComparison.OrdinalIgnoreCase),
            $"The reason names the signature check; got '{signatureError}'.");

        // A TrueType header with no name table must be refused rather than returned as an empty name.
        var headerOnly = new byte[28];
        headerOnly[1] = 1;
        Check.True(!TtfNameReader.TryRead(headerOnly, out _, out string tableError),
            "A font with no name table has no name to report.");
        Check.True(tableError.Length != 0, "…and says so.");

        const string limbus =
            @"E:\SteamLibrary\steamapps\common\Limbus Company\LimbusCompany_Data\Lang\LLC_zh-CN\Font\Context\ChineseFont.ttf";
        if (File.Exists(limbus))
        {
            Check.True(TtfNameReader.TryReadFile(limbus, out TtfNames names, out string error), $"A real font reads: {error}");
            Check.True(names.Preferred.Length != 0, "A real font reports a family name to hand to the platform.");
            Check.True(names.FamilyEnglish.Length != 0, "…and an English one to fall back to.");
            Check.True(names.PostScriptName.Length != 0, "…and the name a font tool would call it.");
        }
    }

    /// <summary>
    /// The editing rules of one caption line. They live in a plain object so that "typing into the game to see what
    /// the caret does" is never the only way to find out, and so a Chinese line pasted from elsewhere cannot break
    /// it: every one of these cases is a real way a caption gets written.
    /// </summary>
    internal static void TheCaptionLineEditorKeepsItsCaretAndItsText()
    {
        var editor = new CaptionTextEditor();
        editor.Set("在我年少");
        Check.Equal(4, editor.Caret, "Setting a line puts the caret at its end.");
        Check.Equal("在我年少｜", editor.Display(), "The caret is drawn where the next character will land.");

        editor.MoveCaret(-2);
        editor.Insert("之");
        Check.Equal("在我之年少", editor.Text, "Typing inserts at the caret, not at the end.");
        Check.Equal(3, editor.Caret, "…and the caret follows what was typed.");
        editor.Home();
        editor.Insert("『");
        Check.Equal("『在我之年少", editor.Text);
        editor.End();
        editor.Insert("』");
        Check.Equal("『在我之年少』", editor.Text);

        // Backspace and delete are different keys, and both are used while writing.
        editor.Home();
        editor.Delete();
        Check.Equal("在我之年少』", editor.Text, "Delete removes the character after the caret.");
        editor.End();
        editor.Backspace();
        Check.Equal("在我之年少", editor.Text, "Backspace removes the character before the caret.");

        // Select-all then typing replaces the line, which is how a line is rewritten rather than patched.
        editor.SelectAllText();
        Check.True(editor.SelectAll, "Ctrl+A selects the line.");
        Check.Equal("【在我之年少】", editor.Display(), "A selection is shown as the whole line being replaced.");
        editor.Insert("新的一句");
        Check.Equal("新的一句", editor.Text);
        Check.True(!editor.SelectAll, "Typing clears the selection.");

        // A pasted paragraph is one caption line: a newline in a caption is a line nobody can render.
        editor.SelectAllText();
        editor.Insert("第一行\r\n第二行");
        Check.Equal("第一行  第二行", editor.Text, "A pasted break becomes a space, not a second line.");
        Check.True(!editor.Text.Contains('\n'), "A caption never contains a line break.");

        // What the input method is still composing is drawn but never stored.
        editor.Clear();
        editor.Insert("愿");
        string composing = editor.Display("意为了他人的");
        Check.Equal("愿｜意为了他人的", composing, "A composing run is shown at the caret, which stays before it.");
        Check.Equal("愿", editor.Text, "…and is not part of the text until it is committed.");
        editor.Insert("意");
        Check.Equal("愿意", editor.Text, "A committed run arrives as ordinary text.");

        // A surrogate pair is one character: half of an emoji must never be left behind.
        editor.Clear();
        editor.Insert("😀");
        Check.Equal("😀", editor.Text);
        editor.Backspace();
        Check.Equal("", editor.Text, "Backspace removes a whole surrogate pair.");
        editor.Insert("a\u0001b");
        Check.Equal("ab", editor.Text, "Control characters are dropped rather than drawn.");
    }

    private static int Visible(CaptionCue cue, CaptionMotionRules rules, int count, double seconds)
    {
        (_, CaptionCharacterState[] characters) = CaptionTimeline.Evaluate(cue, rules, count, seconds);
        return characters.Count(c => c.Alpha > .01f);
    }

    private static bool Overlaps(ToolInputRect a, ToolInputRect b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
}
