using System.Drawing;

namespace ShotTrans.Ui;

/// <summary>弹窗与提示条使用的配色。设置界面保持系统默认外观，不在此处处理。</summary>
internal sealed class UiPalette
{
    public bool Dark { get; init; }

    public Color CardBack { get; init; }
    public Color CardBorder { get; init; }
    public Color TitleAccent { get; init; }
    public Color ErrorAccent { get; init; }
    public Color BodyText { get; init; }
    public Color ErrorText { get; init; }
    public Color FooterText { get; init; }
    public Color Divider { get; init; }
    public Color ExplainText { get; init; }
    public Color ScrollTrack { get; init; }
    public Color ScrollThumb { get; init; }
    public Color SelectionFill { get; init; }

    public Color ToastBack { get; init; }
    public Color ToastBorder { get; init; }
    public Color ToastText { get; init; }

    public static UiPalette For(bool dark) => dark ? DarkPalette : LightPalette;

    private static UiPalette DarkPalette => new()
    {
        Dark = true,
        CardBack = Color.FromArgb(246, 20, 24, 31),
        CardBorder = Color.FromArgb(52, 120, 170, 255),
        TitleAccent = Color.FromArgb(255, 110, 190, 255),
        ErrorAccent = Color.FromArgb(255, 255, 110, 110),
        BodyText = Color.FromArgb(250, 240, 244, 250),
        ErrorText = Color.FromArgb(255, 255, 214, 214),
        FooterText = Color.FromArgb(190, 160, 172, 190),
        Divider = Color.FromArgb(40, 150, 170, 210),
        ExplainText = Color.FromArgb(220, 210, 220, 232),
        ScrollTrack = Color.FromArgb(55, 150, 170, 210),
        ScrollThumb = Color.FromArgb(170, 150, 185, 225),
        SelectionFill = Color.FromArgb(95, 80, 150, 235),
        ToastBack = Color.FromArgb(238, 20, 24, 31),
        ToastBorder = Color.FromArgb(45, 120, 170, 255),
        ToastText = Color.FromArgb(250, 240, 244, 250)
    };

    private static UiPalette LightPalette => new()
    {
        Dark = false,
        CardBack = Color.FromArgb(247, 248, 251),
        CardBorder = Color.FromArgb(60, 120, 140, 170),
        TitleAccent = Color.FromArgb(255, 32, 108, 190),
        ErrorAccent = Color.FromArgb(230, 190, 40, 40),
        BodyText = Color.FromArgb(255, 28, 32, 40),
        ErrorText = Color.FromArgb(255, 165, 30, 30),
        FooterText = Color.FromArgb(190, 110, 122, 140),
        Divider = Color.FromArgb(45, 140, 152, 172),
        ExplainText = Color.FromArgb(230, 55, 62, 74),
        ScrollTrack = Color.FromArgb(55, 140, 150, 170),
        ScrollThumb = Color.FromArgb(170, 120, 140, 172),
        SelectionFill = Color.FromArgb(90, 70, 140, 235),
        ToastBack = Color.FromArgb(247, 248, 250, 253),
        ToastBorder = Color.FromArgb(80, 120, 140, 170),
        ToastText = Color.FromArgb(255, 28, 32, 40)
    };
}

internal static class Theme
{
    /// <summary>根据配置项（system/light/dark）解析当前是否为深色。</summary>
    public static bool IsDark(string? mode)
    {
        var m = (mode ?? "").Trim().ToLowerInvariant();
        return m switch
        {
            "dark" => true,
            "light" => false,
            _ => IsSystemDark()
        };
    }

    public static UiPalette Palette(string? mode) => UiPalette.For(IsDark(mode));

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v) return v == 0;
        }
        catch
        {
        }
        return false;
    }
}
