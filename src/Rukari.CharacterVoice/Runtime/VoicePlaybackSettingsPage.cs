using Rukari.CharacterVoice.Core;
using Rukari.Lib.Tools;

namespace Rukari.CharacterVoice.Runtime;

/// <summary>A managed page using the existing shared surface; no new native UI or frozen API members.</summary>
internal sealed class VoicePlaybackSettingsPage : IToolPanelContent, IToolPanelSizing, IToolPanelLifecycle
{
    internal const string PageId = "rukari.charactervoice.playback-settings";
    internal const string SliderId = "voice-delay.slider";
    internal const string VolumeSliderId = "voice-volume.slider";
    private readonly Func<float> _read;
    private readonly Action<float> _save;
    private readonly Func<float> _readVolume;
    private readonly Action<float> _saveVolume;
    private float _seconds;
    private bool _dragging;
    private bool _dirty;
    private float _volumePercent;
    private bool _volumeDragging;
    private bool _volumeDirty;
    private string _status = "松开滑条后自动保存；从下一次语音播放起生效。";

    internal VoicePlaybackSettingsPage(Func<float> read, Action<float> save)
        : this(read, save, () => VoicePlaybackVolumePolicy.DefaultPercent, _ => { }) { }
    internal VoicePlaybackSettingsPage(Func<float> read, Action<float> save,
        Func<float> readVolume, Action<float> saveVolume)
    {
        _read = read; _save = save; _readVolume = readVolume; _saveVolume = saveVolume;
    }
    public float PreferredHeight => 630f;
    public float PreferredWidth => 560f;
    public void OnShown()
    {
        _seconds = (float)VoicePlaybackDelayPolicy.Normalize(_read());
        _volumePercent = VoicePlaybackVolumePolicy.Normalize(_readVolume());
        _dragging = _dirty = false;
        _volumeDragging = _volumeDirty = false;
    }
    public void OnHidden() { CommitVolume(); Commit(); _dragging = _volumeDragging = false; }

    public void Draw(IToolPanelSurface surface)
    {
        float top = surface.Height;
        ToolInputRect Band(float height) { top -= height; return new(0, top, surface.Width, height); }
        surface.Text("voice-volume.title", $"配音音量：{_volumePercent:0}%", Band(34f), size: 20);
        surface.Status("voice-volume.scope", "所有由本 Mod 导入并绑定的语音共用。", Band(28f));
        ToolInputRect volumeSlider = Band(48f);
        float volumeLeft = volumeSlider.X + 12f;
        float volumeWidth = Math.Max(1f, volumeSlider.Width - 24f);
        float volumeFill = volumeWidth * _volumePercent / VoicePlaybackVolumePolicy.MaximumPercent;
        Paint(surface, "voice-volume.track", new(volumeLeft, volumeSlider.Y + 22f, volumeWidth, 4f), ToolPalette.ScrollTrack.Background);
        if (volumeFill > 0) Paint(surface, "voice-volume.fill", new(volumeLeft, volumeSlider.Y + 22f, volumeFill, 4f), ToolPalette.ScrollThumb.Background);
        Paint(surface, "voice-volume.thumb", new(volumeLeft + volumeFill - 9f, volumeSlider.Y + 15f, 18f, 18f), ToolPalette.ScrollThumb.Background);
        bool volumeHeld = surface.Area(VolumeSliderId, volumeSlider);
        if (volumeHeld || (_volumeDragging && surface.Pointer.Released))
        {
            float next = VoicePlaybackVolumePolicy.FromPointer(surface.Pointer.X, volumeLeft, volumeWidth);
            _volumeDirty |= next != _volumePercent;
            _volumePercent = next;
        }
        if (_volumeDragging && !volumeHeld) CommitVolume();
        _volumeDragging = volumeHeld;
        ToolInputRect volumeRange = Band(24f);
        surface.Status("voice-volume.min", "0%", new(volumeLeft - 8, volumeRange.Y, 48, volumeRange.Height));
        surface.Status("voice-volume.mid", "50%", new(volumeLeft + volumeWidth / 2 - 16, volumeRange.Y, 48, volumeRange.Height));
        surface.Status("voice-volume.max", "100%", new(volumeLeft + volumeWidth - 44, volumeRange.Y, 56, volumeRange.Height));
        ToolInputRect volumeButtons = Band(38f);
        if (surface.Button("voice-volume.less", "− 5%", surface.Cell(volumeButtons, 0, 3), _volumePercent > 0)) SetVolume(_volumePercent - 5);
        if (surface.Button("voice-volume.default", "恢复 50%", surface.Cell(volumeButtons, 1, 3), _volumePercent != VoicePlaybackVolumePolicy.DefaultPercent)) SetVolume(VoicePlaybackVolumePolicy.DefaultPercent);
        if (surface.Button("voice-volume.more", "+ 5%", surface.Cell(volumeButtons, 2, 3), _volumePercent < VoicePlaybackVolumePolicy.MaximumPercent)) SetVolume(_volumePercent + 5);
        surface.Status("voice-volume.note", "50% 保持原音量，100% 最多增强至 2 倍；0% 静音。\n实际响度仍受游戏音量设置与音频本身影响。\n首次调整先检查音源，从后续一段配音起生效。", Band(70f));
        Band(18f);
        surface.Text("voice-delay.title", $"语音结束后额外等待：{_seconds:0.0} 秒", Band(34f), size: 20);
        surface.Status("voice-delay.scope", "所有由本 Mod 绑定的语音共用，默认 0 秒。", Band(28f));
        ToolInputRect slider = Band(48f);
        float left = slider.X + 12f;
        float width = Math.Max(1f, slider.Width - 24f);
        float fill = width * _seconds / VoicePlaybackDelayPolicy.MaximumSeconds;
        Paint(surface, "voice-delay.track", new(left, slider.Y + 22f, width, 4f), ToolPalette.ScrollTrack.Background);
        if (fill > 0) Paint(surface, "voice-delay.fill", new(left, slider.Y + 22f, fill, 4f), ToolPalette.ScrollThumb.Background);
        Paint(surface, "voice-delay.thumb", new(left + fill - 9f, slider.Y + 15f, 18f, 18f), ToolPalette.ScrollThumb.Background);
        // The area is last so the decorative swatches cannot steal its press or release.
        bool held = surface.Area(SliderId, slider);
        if (held || (_dragging && surface.Pointer.Released))
        {
            float next = VoicePlaybackDelayPolicy.FromPointer(surface.Pointer.X, left, width);
            _dirty |= next != _seconds;
            _seconds = next;
        }
        if (_dragging && !held) Commit();
        _dragging = held;
        ToolInputRect range = Band(24f);
        surface.Status("voice-delay.min", "0 秒", new(left - 8, range.Y, 48, range.Height));
        surface.Status("voice-delay.mid", "5 秒", new(left + width / 2 - 16, range.Y, 48, range.Height));
        surface.Status("voice-delay.max", "10 秒", new(left + width - 36, range.Y, 48, range.Height));
        ToolInputRect buttons = Band(38f);
        if (surface.Button("voice-delay.less", "− 0.1 秒", surface.Cell(buttons, 0, 3), _seconds > 0)) Set(_seconds - 0.1f);
        if (surface.Button("voice-delay.zero", "恢复 0 秒", surface.Cell(buttons, 1, 3), _seconds > 0)) Set(0);
        if (surface.Button("voice-delay.more", "+ 0.1 秒", surface.Cell(buttons, 2, 3), _seconds < VoicePlaybackDelayPolicy.MaximumSeconds)) Set(_seconds + 0.1f);
        Band(10f);
        surface.Status("voice-delay.note", "音频末尾的静音也算播放时间；已有留白可设为 0。\n游戏原有的 AUTO 等待仍由游戏设置控制。", Band(48f));
        surface.Status("voice-delay.saved", _status, Band(32f));
    }

    private static void Paint(IToolPanelSurface surface, string id, ToolInputRect bounds, ToolColor colour)
    {
        if (surface is IToolPanelSurfaceColours colours)
            colours.Swatch(id, $"#{(int)(colour.R * 255):X2}{(int)(colour.G * 255):X2}{(int)(colour.B * 255):X2}", bounds);
        else surface.Plate(id, bounds, ToolSurfaceStyle.Selected);
    }

    private void Set(float value)
    {
        _seconds = (float)VoicePlaybackDelayPolicy.Normalize(value);
        _dirty = true;
        Commit();
    }

    private void SetVolume(float value)
    {
        _volumePercent = VoicePlaybackVolumePolicy.Normalize(value);
        _volumeDirty = true;
        CommitVolume();
    }

    private void CommitVolume()
    {
        if (!_volumeDirty) return;
        try
        {
            _saveVolume(_volumePercent);
            _volumeDirty = false;
            _status = $"已保存配音音量 {_volumePercent:0}%；从下一次语音播放起生效。";
        }
        catch (Exception)
        {
            _status = "设置保存失败，请检查配置文件是否被占用。";
        }
    }

    private void Commit()
    {
        if (!_dirty) return;
        try
        {
            _save(_seconds);
            _dirty = false;
            _status = $"已保存 {_seconds:0.0} 秒；从下一次语音播放起生效。";
        }
        catch (Exception)
        {
            _status = "设置保存失败，请检查配置文件是否被占用。";
        }
    }
}
