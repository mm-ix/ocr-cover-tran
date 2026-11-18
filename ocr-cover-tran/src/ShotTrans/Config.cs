using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShotTrans;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(ChatRequest))]
internal partial class AppJsonContext : JsonSerializerContext
{
}

public sealed class ChatMessage
{
    public string role { get; set; } = "";
    public string content { get; set; } = "";
}

public sealed class ChatRequest
{
    public string model { get; set; } = "";
    public ChatMessage[] messages { get; set; } = Array.Empty<ChatMessage>();
    public double temperature { get; set; }
    public bool stream { get; set; }
}

public sealed class AppConfig
{
    // 快捷键
    public string ScreenshotHotkey { get; set; } = "Ctrl+Alt+X";
    public string SelectionHotkey { get; set; } = "Ctrl+Alt+Z";

    // OCR
    public string OcrLanguage { get; set; } = "auto";

    // 覆盖层
    public int FontMinSize { get; set; } = 8;
    public int FontMaxSize { get; set; } = 24;
    public double BackgroundOpacity { get; set; } = 0.92;
    public bool AlignCenter { get; set; } = false;
    public int StatusBarFadeSeconds { get; set; } = 3;
    public string FontFamily { get; set; } = "Microsoft YaHei UI";

    // 翻译
    public string TranslationBaseUrl { get; set; } = "";
    public string TranslationApiKey { get; set; } = "";
    public string TranslationModel { get; set; } = "";
    public double TranslationTemperature { get; set; } = 0.2;
    public int TranslationTimeoutSeconds { get; set; } = 20;

    // 划词翻译：在译文下方显示选中词的简短词义/介绍
    public bool SelectionExplainEnabled { get; set; } = true;

    // 划词翻译中英互译：选中中文时译成英文；关闭时中文只显示原文
    public bool SelectionBidirectionalEnabled { get; set; } = false;

    // 界面主题：弹窗颜色，"system" 跟随系统 / "light" 明亮 / "dark" 黑暗
    public string UiTheme { get; set; } = "system";

    // 术语
    public bool ProtectCodeIdentifiers { get; set; } = true;
    public string Glossary { get; set; } = "";

    // 其他
    public bool HistoryEnabled { get; set; } = false;
    public bool AutoStart { get; set; } = false;
    public bool EnableDebugLog { get; set; } = false;
    public bool FirstRunDone { get; set; } = false;

    [JsonIgnore]
    public bool IsTranslationConfigured =>
        !string.IsNullOrWhiteSpace(TranslationBaseUrl) && !string.IsNullOrWhiteSpace(TranslationModel);

    public AppConfig Clone() => (AppConfig)MemberwiseClone();

    public void CopyFrom(AppConfig other)
    {
        ScreenshotHotkey = other.ScreenshotHotkey;
        SelectionHotkey = other.SelectionHotkey;
        OcrLanguage = other.OcrLanguage;
        FontMinSize = other.FontMinSize;
        FontMaxSize = other.FontMaxSize;
        BackgroundOpacity = other.BackgroundOpacity;
        AlignCenter = other.AlignCenter;
        StatusBarFadeSeconds = other.StatusBarFadeSeconds;
        FontFamily = other.FontFamily;
        TranslationBaseUrl = other.TranslationBaseUrl;
        TranslationApiKey = other.TranslationApiKey;
        TranslationModel = other.TranslationModel;
        TranslationTemperature = other.TranslationTemperature;
        TranslationTimeoutSeconds = other.TranslationTimeoutSeconds;
        SelectionExplainEnabled = other.SelectionExplainEnabled;
        SelectionBidirectionalEnabled = other.SelectionBidirectionalEnabled;
        UiTheme = other.UiTheme;
        ProtectCodeIdentifiers = other.ProtectCodeIdentifiers;
        Glossary = other.Glossary;
        HistoryEnabled = other.HistoryEnabled;
        AutoStart = other.AutoStart;
        EnableDebugLog = other.EnableDebugLog;
        FirstRunDone = other.FirstRunDone;
    }
}

public static class ConfigStore
{
    public static string DataDirectory { get; } = DetermineDataDirectory();
    public static string ConfigPath => Path.Combine(DataDirectory, "config.json");

    private static string DetermineDataDirectory()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        try
        {
            Directory.CreateDirectory(baseDir);
            var probe = Path.Combine(baseDir, ".write-test-" + Environment.ProcessId);
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return baseDir;
        }
        catch
        {
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ShotTrans");
            Directory.CreateDirectory(appData);
            return appData;
        }
    }

    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                var fresh = new AppConfig();
                Save(fresh);
                return fresh;
            }

            var json = File.ReadAllText(ConfigPath);
            var cfg = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppConfig) ?? new AppConfig();
            Normalize(cfg);
            return cfg;
        }
        catch (Exception ex)
        {
            Log.Error("读取配置失败，使用默认配置", ex);
            return new AppConfig();
        }
    }

    public static void Save(AppConfig config)
    {
        Normalize(config);
        Directory.CreateDirectory(DataDirectory);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, AppJsonContext.Default.AppConfig));
        if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
        File.Move(tmp, ConfigPath);
    }

    private static void Normalize(AppConfig c)
    {
        c.FontMinSize = Math.Clamp(c.FontMinSize, 6, 48);
        c.FontMaxSize = Math.Clamp(c.FontMaxSize, c.FontMinSize, 64);
        c.BackgroundOpacity = Math.Clamp(c.BackgroundOpacity, 0.3, 1.0);
        c.TranslationTimeoutSeconds = Math.Clamp(c.TranslationTimeoutSeconds, 3, 120);
        c.StatusBarFadeSeconds = Math.Clamp(c.StatusBarFadeSeconds, 0, 30);
        c.TranslationTemperature = Math.Clamp(c.TranslationTemperature, 0, 2);
        c.ScreenshotHotkey = string.IsNullOrWhiteSpace(c.ScreenshotHotkey) ? "Ctrl+Alt+X" : c.ScreenshotHotkey;
        c.SelectionHotkey = string.IsNullOrWhiteSpace(c.SelectionHotkey) ? "Ctrl+Alt+Z" : c.SelectionHotkey;
        c.OcrLanguage = string.IsNullOrWhiteSpace(c.OcrLanguage) ? "auto" : c.OcrLanguage;
        c.FontFamily = string.IsNullOrWhiteSpace(c.FontFamily) ? "Microsoft YaHei UI" : c.FontFamily;
        var theme = (c.UiTheme ?? "").Trim().ToLowerInvariant();
        c.UiTheme = theme is "light" or "dark" ? theme : "system";
    }
}
