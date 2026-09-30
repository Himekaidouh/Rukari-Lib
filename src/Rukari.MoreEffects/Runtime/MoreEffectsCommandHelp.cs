using Rukari.Lib;
using Rukari.Lib.Commands;

namespace AzureArchive.VideoTools.Runtime;

/// <summary>
/// Publishes this mod's directives into the editor's own 「指令格式」 panel, so a user can see them and insert
/// them with one click. Only rows are published: the panel, its layout and its behaviour stay the editor's own.
/// </summary>
internal static class MoreEffectsCommandHelp
{
    private static readonly List<IDisposable> Leases = new();

    internal static void Install()
    {
        var help = ModServices.Current?.GetService<ICommandHelpService>();
        if (help?.Success != true || help.Value == null) return;
        Add(help.Value, "角色", "#aavt;char;<槽位>;set;x=;y=;rotation=;flipX=;duration=;easing=",
            "把角色直接摆到指定位置；槽位 1-5 对应场上的角色。");
        Add(help.Value, "角色", "#aavt;char;<槽位>;move;dx=;dy=;drotation=;duration=;easing=",
            "在当前基础上移动或旋转角色，可带缓动。");
        Add(help.Value, "角色", "#aavt;charPending;<槽位>;set;x=;y=;rotation=;flipX=",
            "先把角色摆好但不出现，留到下一句再入场。");
        Add(help.Value, "角色", "#aavt;fx;<槽位>;sway;amplitude=12;frequency=3;cycles=3;direction=right",
            "相对当前姿态左右摆动；frequency 为每秒次数，cycles=0 持续到下一句。");
        Add(help.Value, "角色", "#aavt;fx;<槽位>;spin;frequency=1;cycles=2;direction=right",
            "绕 Y 轴原地转身；frequency 为每秒圈数，可选 direction=left，cycles=0 持续到下一句。");
        Add(help.Value, "角色", "#aavt;fx;<槽位>;headbutt;back=20;forward=45;duration=650;direction=right",
            "先后仰、快速前倾，再回到原位；duration 使用毫秒。");
        Add(help.Value, "角色", "#aavt;fx;<槽位>;squash;amplitude=0.15;frequency=2;cycles=3;direction=right",
            "纵向伸长时横向收窄，纵向压缩时横向变宽；保留原大小与翻转。");
        Add(help.Value, "角色", "#aavt;fx;<槽位>;spin;frequency=1;cycles=2;bezier=0.42,0,0.58,1",
            "四种预设均可附加 bezier=x1,y1,x2,y2，四个值为 0–1；省略时保留默认动作。循环每轮套用，头槌分段套用。");
        Add(help.Value, "镜头", "#aavt;camera;set;x=;y=;zoom=;duration=;easing=",
            "把镜头移到指定位置或缩放到指定倍率。");
        Add(help.Value, "镜头", "#aavt;camera;reset;duration=;easing=",
            "镜头回到默认位置。");
        Add(help.Value, "对白", "#aavt;continue",
            "本句接着上一句继续打字，形成连续对白；该场景需要跟在有台词的场景之后。");
    }

    internal static void Stop()
    {
        foreach (IDisposable lease in Leases) lease.Dispose();
        Leases.Clear();
    }

    private static void Add(ICommandHelpService help, string section, string syntax, string description)
    {
        var result = help.Register(section, syntax, description);
        if (result.Success)
        {
            Leases.Add(result.Value);
            return;
        }

        Plugin.Logger.LogWarning($"指令帮助登记失败（{syntax}）：{result.Error?.Message}");
    }
}
