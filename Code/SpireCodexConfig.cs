using BaseLib.Config;
using Godot;

namespace SpireCodex;

public sealed class SpireCodexConfig : SimpleModConfig
{
    [ConfigSection("RunTracking")]
    public static bool UploadRuns { get; set; } = false;

    public static bool BackfillHistory { get; set; } = true;

    public static bool RecordReplays { get; set; } = true;

    public static bool UploadReplays { get; set; } = true;

    public static bool ShareLiveStatus { get; set; } = true;

    [ConfigSection("Overlay")]
    public static bool ShowDeckView { get; set; } = true;
    public static bool ShowCardRewardHints { get; set; } = true;
    public static bool ShowHoverTips { get; set; } = true;
    public static StatBracket Stats { get; set; } = StatBracket.All;
    public static bool ShowMapDanger { get; set; } = true;
    public static bool ShowDamageMeter { get; set; } = true;
    public static bool ShowUpcomingEvents { get; set; } = true;
    public static bool ShowPostRunCard { get; set; } = true;

    [ConfigSection("Hotkeys")]
    public static HotKey OverlayKey { get; set; } = HotKey.F5;

    public static ControllerToggle OverlayPad { get; set; } = ControllerToggle.StickClick;

    public static Key OverlayKeycode => KeyOf(OverlayKey);

    public static string BracketKey => Stats switch
    {
        StatBracket.A10 => "a10",
        StatBracket.A10_WR30 => "wr30",
        StatBracket.A10_WR50 => "wr50",
        StatBracket.A10_WR75 => "wr75",
        _ => "all",
    };

    private static Key KeyOf(HotKey k) => k switch
    {
        HotKey.F5 => Key.F5,
        HotKey.F6 => Key.F6,
        HotKey.F7 => Key.F7,
        HotKey.F8 => Key.F8,
        HotKey.F9 => Key.F9,
        HotKey.F10 => Key.F10,
        HotKey.F11 => Key.F11,
        HotKey.F12 => Key.F12,
        _ => Key.None,
    };
}

public enum HotKey { None, F5, F6, F7, F8, F9, F10, F11, F12 }

public enum StatBracket { All, A10, A10_WR30, A10_WR50, A10_WR75 }

public enum ControllerToggle { Off, StickClick }
