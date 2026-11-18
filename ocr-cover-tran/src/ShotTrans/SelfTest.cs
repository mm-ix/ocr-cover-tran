using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using ShotTrans.Capture;
using ShotTrans.History;
using ShotTrans.Input;
using ShotTrans.Ocr;
using ShotTrans.Overlay;
using ShotTrans.Rendering;
using ShotTrans.Settings;
using ShotTrans.Translation;
using ShotTrans.Ui;
using ShotTrans.Util;

namespace ShotTrans;

/// <summary>无界面自检：OCR、渲染、术语保护、翻译、历史库、配置默认值。</summary>
internal static class SelfTest
{
    public static void Run()
    {
        var report = new StringBuilder();
        int failures = 0;
        int skipped = 0;

        void Line(string s = "")
        {
            report.AppendLine(s);
        }

        void Check(string name, bool ok, string detail = "")
        {
            if (ok) Line($"PASS  {name}{(detail.Length > 0 ? "  — " + detail : "")}");
            else { failures++; Line($"FAIL  {name}{(detail.Length > 0 ? "  — " + detail : "")}"); }
        }

        void Skip(string name, string reason)
        {
            skipped++;
            Line($"SKIP  {name}  — {reason}");
        }

        var outputDir = Path.Combine(ConfigStore.DataDirectory, "selftest-output");
        Directory.CreateDirectory(outputDir);
        Log.Init(Path.Combine(ConfigStore.DataDirectory, "logs"));
        var progressPath = Path.Combine(outputDir, "selftest-progress.txt");
        void Progress(string step)
        {
            try { File.AppendAllText(progressPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + step + Environment.NewLine); } catch { }
        }
        try { File.WriteAllText(progressPath, ""); } catch { }
        try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { }
        try { Application.EnableVisualStyles(); } catch { }
        try { Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException); } catch { }

        Line("ShotTrans SelfTest  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        Line("data dir: " + ConfigStore.DataDirectory);
        Line();

        // 1. 快捷键
        try
        {
            Check("热键解析 Ctrl+Alt+X", Hotkey.TryParse("Ctrl+Alt+X", out var hk, out var err)
                  && hk.Modifiers == 0x3 && hk.VirtualKey == 'X', err);
            Check("热键解析 F8", Hotkey.TryParse("F8", out var f8, out _) && f8.VirtualKey == 0x77);
            Check("拒绝无修饰键 X", !Hotkey.TryParse("X", out _, out _));
            Check("拒绝硬编码 Esc", !Hotkey.TryParse("Ctrl+Esc", out _, out _));
        }
        catch (Exception ex) { Check("热键解析", false, ex.Message); }

        // 2. 默认配置
        try
        {
            var c = new AppConfig();
            Check("历史记录默认关闭", !c.HistoryEnabled);
            Check("调试日志默认关闭", !c.EnableDebugLog);
            Check("默认截图热键 Ctrl+Alt+X", c.ScreenshotHotkey == "Ctrl+Alt+X");
            Check("默认划词热键 Ctrl+Alt+Z", c.SelectionHotkey == "Ctrl+Alt+Z");
        }
        catch (Exception ex) { Check("默认配置", false, ex.Message); }

        // 3. 术语保护
        try
        {
            var protector = new TermProtector("GetProcAddress=获取函数地址", protectCode: true);
            var p = protector.Protect("Call GetProcAddress to load kernel32.dll version 10.0.19041 from https://example.com/a?b=1 --verbose.");
            var restored = p.Restore(p.Masked); // 模拟“原样返回”的翻译
            Check("术语保护：有可翻译内容", p.HasTranslatableContent);
            Check("术语保护：替换占位符", p.Masked.Contains("[[") && !p.Masked.Contains("GetProcAddress"));
            Check("术语保护：还原术语", restored.Contains("获取函数地址"), restored);
            Check("术语保护：还原 dll", restored.Contains("kernel32.dll"));
            Check("术语保护：还原版本号", restored.Contains("10.0.19041"));
            Check("术语保护：还原 URL", restored.Contains("https://example.com/a?b=1"));
            Check("术语保护：还原参数", restored.Contains("--verbose"));

            var zh = new TermProtector("", true).Protect("你好，世界。");
            Check("中文不送翻译", !zh.HasTranslatableContent);

            var falsePositives = new TermProtector("", true).Protect("tO ControI HeIIo API GetProcAddress work");
            Check("代码保护：放过 OCR 误识别 tO", falsePositives.Masked.Contains("tO"), falsePositives.Masked);
            Check("代码保护：放过 OCR 误识别 ControI", falsePositives.Masked.Contains("ControI"), falsePositives.Masked);
            Check("代码保护：放过 OCR 误识别 HeIIo", falsePositives.Masked.Contains("HeIIo"), falsePositives.Masked);
            Check("代码保护：保护 API", !falsePositives.Masked.Contains("API"), falsePositives.Masked);
            Check("代码保护：保护 GetProcAddress", !falsePositives.Masked.Contains("GetProcAddress"), falsePositives.Masked);

            var repaired = OcrService.RepairOcrText("L°CENSE C°NTROL HeIIo ControI tO W0RD kerne132");
            Line("      OCR 字符修正：" + repaired);
            Check("OCR 字符修正：L°CENSE → LICENSE", repaired.Contains("LICENSE"), repaired);
            Check("OCR 字符修正：C°NTROL → CONTROL", repaired.Contains("CONTROL"), repaired);
            Check("OCR 字符修正：HeIIo → Hello", repaired.Contains("Hello"), repaired);
            Check("OCR 字符修正：ControI → Control", repaired.Contains("Control"), repaired);
            Check("OCR 字符修正：tO → to", repaired.Contains(" to "), repaired);
            Check("OCR 字符修正：W0RD → WORD", repaired.Contains("WORD"), repaired);
            Check("OCR 字符修正：kerne132 → kernel32", repaired.Contains("kernel32"), repaired);

            var allCaps = new TermProtector("", true).Protect("LICENSE SETTINGS API");
            Check("代码保护：全大写常用词 LICENSE 可翻译", allCaps.Masked.Contains("LICENSE"), allCaps.Masked);
            Check("代码保护：全大写常用词 SETTINGS 可翻译", allCaps.Masked.Contains("SETTINGS"), allCaps.Masked);
            Check("代码保护：全大写缩写 API 仍保护", !allCaps.Masked.Contains("API"), allCaps.Masked);
        }
        catch (Exception ex) { Check("术语保护", false, ex.ToString()); }

        // 4. 接口地址
        try
        {
            Check("补全 /v1/chat/completions",
                TranslationService.BuildEndpoint("https://api.deepseek.com") == "https://api.deepseek.com/v1/chat/completions");
            Check("保留完整地址",
                TranslationService.BuildEndpoint("https://host/v1/chat/completions") == "https://host/v1/chat/completions");
        }
        catch (Exception ex) { Check("接口地址", false, ex.Message); }

        // 5. 渲染器
        try
        {
            using var shot = MakeSampleScreenshot();
            var blocks = new List<OcrLineBox>
            {
                new("Hello world", new Rectangle(20, 20, 300, 40)),
                new("Dark background", new Rectangle(20, 80, 300, 40)),
                new("Small box with a long translation", new Rectangle(20, 140, 120, 24))
            };
            var cfg = new AppConfig();
            var sw = Stopwatch.StartNew();
            using var renderer = new OverlayRenderer(shot, blocks, cfg);
            var translated = new[] { "你好，世界", "深色背景文字", "这是一个比较长的译文需要缩小字号并截断" };
            using var layout = renderer.BuildLayout(blocks, translated, center: false, allowedBottom: shot.Height);
            var buildMs = sw.Elapsed.TotalMilliseconds;

            using var canvas = new Bitmap(shot.Width, shot.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(canvas))
            {
                g.Clear(Color.Transparent);
                renderer.DrawBlocks(g, layout);
                renderer.DrawStatusBar(g, canvas.Width, "已翻译 3 块 · 0.4s   Space 看原文 / Esc 关闭", error: false);
            }
            sw.Stop();
            var target = Path.Combine(outputDir, "render-sample.png");
            canvas.Save(target, ImageFormat.Png);

            Check("渲染：3 块均排版", layout.Blocks.Count == 3, $"blocks={layout.Blocks.Count}");
            Check("渲染：浅色底用深色字", layout.Blocks[0].Foreground.GetBrightness() < 0.5f,
                layout.Blocks[0].Foreground.ToString());
            Check("渲染：深色底用浅色字", layout.Blocks[1].Foreground.GetBrightness() > 0.5f,
                layout.Blocks[1].Foreground.ToString());
            var smallBlock = layout.Blocks[2];
            Check("渲染：小框字号不超过上限", smallBlock.Font.SizeInPoints <= cfg.FontMaxSize, smallBlock.Font.SizeInPoints.ToString("0.0"));
            Check("渲染：窄框长译文保持可读字号",
                smallBlock.Font.Unit == GraphicsUnit.Pixel && smallBlock.Font.Size >= 12,
                $"{smallBlock.Font.Size:0.#}{smallBlock.Font.Unit}");

            bool overlap = false;
            for (int a = 0; a < layout.Blocks.Count && !overlap; a++)
                for (int b = a + 1; b < layout.Blocks.Count; b++)
                    if (layout.Blocks[a].Rect.IntersectsWith(layout.Blocks[b].Rect)) { overlap = true; break; }
            Check("渲染：译文块互不重叠", !overlap,
                string.Join(" ", layout.Blocks.Select(x => x.Rect.ToString())));
            Check("渲染：耗时 < 500ms", sw.ElapsedMilliseconds < 500, $"{sw.ElapsedMilliseconds}ms (layout {buildMs:0.0}ms)");
            Line("      渲染样例：" + target);
        }
        catch (Exception ex) { Check("渲染器", false, ex.Message); }

        // 5b. 150% DPI：可读字号放大、卡片扩展且不重叠
        try
        {
            using var shot = MakeSampleScreenshot();
            var blocks = new List<OcrLineBox>
            {
                new("Small box with a long translation", new Rectangle(20, 20, 120, 24)),
                new("Next line close below", new Rectangle(20, 46, 120, 24))
            };
            var cfg = new AppConfig();
            using var renderer = new OverlayRenderer(shot, blocks, cfg, dpiScale: 1.5f);
            using var layout = renderer.BuildLayout(blocks,
                new[] { "这是一个比较长的译文需要自动扩展", "下一行也需要避让" },
                center: false, allowedBottom: shot.Height + 160);

            bool overlap = layout.Blocks.Count > 1 &&
                           layout.Blocks[0].Rect.IntersectsWith(layout.Blocks[1].Rect);
            Check("渲染：150% DPI 长译文保持 18px 且不重叠",
                !overlap && layout.Blocks[0].Font.Unit == GraphicsUnit.Pixel && layout.Blocks[0].Font.Size >= 18,
                string.Join(" ", layout.Blocks.Select(x => $"{x.Rect}/{x.Font.Size:0.#}px")));
        }
        catch (Exception ex) { Check("渲染：150% DPI", false, ex.Message); }

        // 6. OCR
        try
        {
            var engine = OcrService.CreateEngine("en-US");
            Line("      OCR 引擎：" + (engine?.RecognizerLanguage.LanguageTag ?? "不可用"));
            Line("      可用 OCR 语言：" + string.Join(", ", OcrService.AvailableLanguages().Select(l => l.LanguageTag)));

            using var ocrImage = MakeOcrTestImage("Hello World 123");
            ocrImage.Save(Path.Combine(outputDir, "ocr-input.png"), ImageFormat.Png);
            var sw = Stopwatch.StartNew();
            var ocr = OcrService.RecognizeAsync(ocrImage, "en-US").GetAwaiter().GetResult();
            sw.Stop();
            var text = string.Join(" ", ocr.Lines.Select(l => l.Text));
            Check("OCR：识别出文字", ocr.Lines.Count > 0, $"{ocr.Lines.Count} 行，{sw.ElapsedMilliseconds}ms");
            bool hasEnglishPack = OcrService.AvailableLanguages().Any(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
            if (hasEnglishPack)
                Check("OCR：内容包含 Hello", text.Contains("Hello", StringComparison.OrdinalIgnoreCase), text);
            else
            {
                // 本机只有中文 OCR 模型，英文小写辨识度有限（HeIIO / He110），做等价字符归一化后校验。
                var normalized = text.ToLowerInvariant().Replace("1", "l").Replace("i", "l").Replace("0", "o");
                Check("OCR：英文可用（未安装 en-US，使用 zh-Hans-CN 归一化校验）", normalized.Contains("hello"), text);
            }
            Check("OCR：边界框有效", ocr.Lines.All(l => l.Bounds.Width > 0 && l.Bounds.Height > 0));
            Check("OCR：耗时 < 1500ms", sw.ElapsedMilliseconds < 1500, $"{sw.ElapsedMilliseconds}ms");

            using var upscaled = new Bitmap(ocrImage.Width * 2, ocrImage.Height * 2, PixelFormat.Format32bppArgb);
            using (var ug = Graphics.FromImage(upscaled))
            {
                ug.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                ug.DrawImage(ocrImage, 0, 0, upscaled.Width, upscaled.Height);
            }
            var sw2 = Stopwatch.StartNew();
            var ocr2 = OcrService.RecognizeAsync(upscaled, "en-US").GetAwaiter().GetResult();
            sw2.Stop();
            var text2 = string.Join(" ", ocr2.Lines.Select(l => l.Text));
            Line($"      2x 放大识别（{sw2.ElapsedMilliseconds}ms）：{text2}");

            using var wordImage = MakeWordTestImage("work work WO rk work Write workbench");
            var wordOcr = OcrService.RecognizeAsync(wordImage, "en-US").GetAwaiter().GetResult();
            var wordText = string.Join(" ", wordOcr.Lines.Select(l => l.Text));
            Line("      拆词合并识别：" + wordText);
            Check("OCR：拆开的英文单词被合并", System.Text.RegularExpressions.Regex.IsMatch(wordText, @"\bwork\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase), wordText);
            Check("OCR：不再出现 WO rk 空格拆词", !wordText.Contains("WO rk", StringComparison.Ordinal), wordText);
        }
        catch (Exception ex) { Check("OCR", false, ex.Message); }

        // 7. 历史记录
        try
        {
            var dbPath = Path.Combine(outputDir, "history-test.db");
            try { File.Delete(dbPath); } catch { }
            var store = new HistoryStore(dbPath);
            for (int i = 0; i < 205; i++)
                store.Add("screenshot", null, "原始文本 " + i, "译文 " + i);
            var list = store.List(300);
            Check("历史：最多 200 条", list.Count == 200, $"count={list.Count}");
            Check("历史：最新在前", list.Count > 0 && list[0].Original.EndsWith("204"));
            if (list.Count > 0)
            {
                store.Delete(list[0].Id);
                Check("历史：删除一条", store.List().Count == 199);
            }
            store.Clear();
            Check("历史：清空", store.List().Count == 0);
        }
        catch (Exception ex) { Check("历史记录", false, ex.Message); }

        // 8. UI 冒烟（裁剪版重点验证 WinForms / 分层窗口 / 全局钩子）
        try
        {
            Progress("config json round-trip");
            var cfg = new AppConfig();
            var json = JsonSerializer.Serialize(cfg, AppJsonContext.Default.AppConfig);
            var roundTrip = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppConfig);
            Check("配置：源生成 JSON 往返", roundTrip != null && roundTrip.ScreenshotHotkey == cfg.ScreenshotHotkey);

            Progress("settings form ctor");
            try
            {
                using var settings = new SettingsForm(cfg, _ => null);
                Progress("settings form create handle");
                settings.CreateControl();
                settings.Show();
                Application.DoEvents();
                settings.Hide();
                Check("UI：设置窗口可创建", settings.Controls.Count > 0);
            }
            catch (Exception ex) { Check("UI：设置窗口可创建", false, ex.ToString()); }

            Progress("history form ctor");
            try
            {
                using var historyForm = new HistoryForm(new HistoryStore(Path.Combine(outputDir, "ui-history.db")), cfg);
                Progress("history form create handle");
                historyForm.CreateControl();
                historyForm.Show();
                Application.DoEvents();
                historyForm.Hide();
                Check("UI：历史窗口可创建", historyForm.Controls.Count > 0);
            }
            catch (Exception ex) { Check("UI：历史窗口可创建", false, ex.ToString()); }

            Progress("toast ctor");
            try
            {
                using var toast = new ToastWindow("UI smoke test", ToastKind.Info, new Rectangle(100, 100, 1, 1), 800);
                Check("UI：提示条可创建", toast.Handle != IntPtr.Zero);
            }
            catch (Exception ex) { Check("UI：提示条可创建", false, ex.ToString()); }

            Progress("popup ctor");
            try
            {
                using var popup = new SelectionTranslatePopup(new Point(300, 300));
                popup.ShowLoading("Hello world");
                popup.ShowResult("你好，世界");
                Check("UI：划词弹窗可创建", popup.Handle != IntPtr.Zero);
                popup.Close();
            }
            catch (Exception ex) { Check("UI：划词弹窗可创建", false, ex.ToString()); }

            Progress("hotkey manager");
            try
            {
                var hotkeyConfig = new AppConfig
                {
                    ScreenshotHotkey = "Ctrl+Alt+Shift+F12",
                    SelectionHotkey = "Ctrl+Alt+Shift+F11"
                };
                using var manager = new HotkeyManager();
                bool ok = manager.TryRegisterInitial(hotkeyConfig, out string hkError);
                Check("UI：全局热键可注册", ok, hkError);
            }
            catch (Exception ex) { Check("UI：全局热键可注册", false, ex.ToString()); }

            Progress("result overlay");
            try
            {
                using var shot = MakeSampleScreenshot();
                var blocks = new List<OcrLineBox>
                {
                    new("Hello world", new Rectangle(20, 20, 300, 40)),
                    new("Dark background", new Rectangle(20, 80, 300, 40))
                };
                var overlay = new ResultOverlay(shot, blocks, new Rectangle(100, 100, 360, 200), cfg);
                overlay.ShowBusyOriginal();
                overlay.ShowTranslated(new[] { "你好，世界", "深色背景" }, 1.2);
                Check("UI：结果覆盖层可创建", overlay.Handle != IntPtr.Zero);
                overlay.Close();
            }
            catch (Exception ex) { Check("UI：结果覆盖层可创建", false, ex.ToString()); }
            Progress("ui smoke done");
        }
        catch (Exception ex) { Check("UI 冒烟", false, ex.ToString()); }

        // 9. 剪贴板（Win32 直接读写，避免 OLE 卡死）
        try
        {
            string? previous = ClipboardService.TryGetText();
            bool setOk = ClipboardService.TrySetText("SHOTRANS-CLIP-TEST", 800);
            string? read = ClipboardService.TryGetText();
            if (previous != null) ClipboardService.TrySetText(previous, 800);
            Check("剪贴板：直接读写", setOk && read == "SHOTRANS-CLIP-TEST", $"set={setOk}, read={read}");
        }
        catch (Exception ex) { Check("剪贴板", false, ex.Message); }

        // 10. 调试日志开关：关闭时不创建/不写入；开启时才写入
        try
        {
            var tempLog = Path.Combine(outputDir, "log-toggle-test");
            try { if (Directory.Exists(tempLog)) Directory.Delete(tempLog, true); } catch { }
            Log.Configure(tempLog, false);
            Log.Info("LOG-OFF-MARKER");
            bool noDir = !Directory.Exists(tempLog);
            Log.Configure(tempLog, true);
            Log.Info("LOG-ON-MARKER");
            var logFile = Path.Combine(tempLog, "shotrans.log");
            bool onWrote = File.Exists(logFile) && File.ReadAllText(logFile).Contains("LOG-ON-MARKER");
            Check("调试日志：关闭不创建、开启才写入", noDir && onWrote, $"noDir={noDir}, onWrote={onWrote}");
            Log.Configure(Path.Combine(ConfigStore.DataDirectory, "logs"), true);
        }
        catch (Exception ex) { Check("调试日志开关", false, ex.Message); }

        // 11. 开机自启快捷方式（创建后立即删除；若已存在则跳过以免覆盖用户配置）
        try
        {
            if (StartupManager.IsEnabled())
            {
                Skip("开机自启", "已存在 ShotTrans.lnk，跳过以免覆盖");
            }
            else
            {
                StartupManager.Enable();
                bool created = StartupManager.IsEnabled();
                StartupManager.Disable();
                bool deleted = !StartupManager.IsEnabled();
                Check("开机自启：创建并删除快捷方式", created && deleted, $"created={created}, deleted={deleted}");
            }
        }
        catch (Exception ex) { Check("开机自启", false, ex.Message); }

        // 12. 翻译接口（需要环境变量提供 key，避免把 key 写进配置文件）
        var testKey = Environment.GetEnvironmentVariable("SHOTRANS_TEST_KEY");
        if (string.IsNullOrWhiteSpace(testKey))
        {
            Skip("翻译接口", "未设置 SHOTRANS_TEST_KEY");
        }
        else
        {
            try
            {
                var cfg = new AppConfig
                {
                    TranslationBaseUrl = Environment.GetEnvironmentVariable("SHOTRANS_TEST_URL")
                                         ?? "https://tokenhub.tencentmaas.com/v1/chat/completions",
                    TranslationApiKey = testKey,
                    TranslationModel = Environment.GetEnvironmentVariable("SHOTRANS_TEST_MODEL") ?? "hy-mt2-lite",
                    TranslationTimeoutSeconds = 30
                };
                var service = new TranslationService(cfg);
                var sw = Stopwatch.StartNew();
                var result = service.TranslateBlocksAsync(new[]
                {
                    "Hello, world",
                    "Click GetProcAddress to continue.",
                    "The download failed: Access denied.",
                    "Open C:\\Windows\\System32\\cmd.exe and retry."
                }, CancellationToken.None).GetAwaiter().GetResult();
                sw.Stop();
                Line("      译文：" + string.Join(" | ", result.Texts));
                Check("翻译：返回四条", result.Texts.Count == 4);
                Check("翻译：第一条含中文", ContainsCjk(result.Texts[0]), result.Texts[0]);
                Check("翻译：保留 GetProcAddress", result.Texts[1].Contains("GetProcAddress", StringComparison.Ordinal), result.Texts[1]);
                Check("翻译：保留 Windows 路径", result.Texts[3].Contains("cmd.exe", StringComparison.OrdinalIgnoreCase), result.Texts[3]);
                Check("翻译：耗时 < 20s", sw.Elapsed.TotalSeconds < 20, $"{sw.Elapsed.TotalSeconds:0.0}s");
            }
            catch (Exception ex) { Check("翻译接口", false, ex.Message); }
        }

        Line();
        Line($"结果：{failures} 失败，{skipped} 跳过");
        var reportPath = Path.Combine(ConfigStore.DataDirectory, "selftest-report.txt");
        try { File.WriteAllText(reportPath, report.ToString(), Encoding.UTF8); } catch { }
        try { Console.WriteLine(report.ToString()); } catch { }
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }

    private static bool ContainsCjk(string s) => s.Any(c => c >= 0x4E00 && c <= 0x9FFF);

    private static Bitmap MakeSampleScreenshot()
    {
        var bmp = new Bitmap(360, 200, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(245, 246, 248));
        using (var dark = new SolidBrush(Color.FromArgb(28, 32, 40)))
            g.FillRectangle(dark, 10, 70, 330, 60);
        using var textBrush = new SolidBrush(Color.Black);
        using var font = new Font("Segoe UI", 16);
        g.DrawString("Hello world", font, textBrush, 20, 22);
        using var white = new SolidBrush(Color.White);
        g.DrawString("Dark background", font, white, 20, 82);
        g.DrawString("Small box", font, textBrush, 20, 142);
        return bmp;
    }

    private static Bitmap MakeOcrTestImage(string text)
    {
        var bmp = new Bitmap(1200, 240, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Arial", 40, FontStyle.Regular);
        using var brush = new SolidBrush(Color.Black);
        g.DrawString(text, font, brush, 40, 80);
        return bmp;
    }

    private static Bitmap MakeWordTestImage(string text)
    {
        var bmp = new Bitmap(1800, 280, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Arial", 48, FontStyle.Regular);
        using var brush = new SolidBrush(Color.Black);
        g.DrawString(text, font, brush, 40, 100);
        return bmp;
    }
}
