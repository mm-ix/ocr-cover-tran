using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Text;
using ShotTrans.Interop;
using ShotTrans.Ocr;
using ShotTrans.Overlay;
using ShotTrans.Translation;

namespace ShotTrans;

/// <summary>
/// 无界面压力测试：反复执行 OCR → HTTP 翻译 → 覆盖层创建/渲染/销毁。
/// 用于在锁屏/CI 环境下验证连续触发 100 次不崩溃、无残留窗口、内存不持续增长。
/// </summary>
internal static class StressTest
{
    public static void Run(int cycles)
    {
        cycles = Math.Clamp(cycles, 1, 1000);
        var report = new StringBuilder();
        var outputDir = Path.Combine(ConfigStore.DataDirectory, "stress-output");
        Directory.CreateDirectory(outputDir);
        Log.Init(Path.Combine(ConfigStore.DataDirectory, "logs"));

        var cfg = ConfigStore.Load();
        var service = new TranslationService(cfg);
        int errors = 0;
        int ocrTotal = 0;
        int translatedTotal = 0;
        long memBefore = 0;
        long memAfter = 0;

        report.AppendLine("ShotTrans StressTest " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        report.AppendLine("cycles=" + cycles + " ocr=" + cfg.OcrLanguage + " translation=" + (cfg.IsTranslationConfigured ? cfg.TranslationBaseUrl : "(stub)"));

        using var proc = Process.GetCurrentProcess();
        var total = Stopwatch.StartNew();
        int warmup = Math.Min(5, cycles);
        int measuredCycles = 0;

        for (int i = 1; i <= cycles; i++)
        {
            try
            {
                using var image = MakeStressImage();
                var ocr = OcrService.RecognizeAsync(image, cfg.OcrLanguage).GetAwaiter().GetResult();
                ocrTotal += ocr.Lines.Count;
                var texts = ocr.Lines.Select(l => l.Text).ToList();
                if (texts.Count == 0) throw new InvalidOperationException("OCR 未识别到文字");

                IReadOnlyList<string> translations;
                if (cfg.IsTranslationConfigured)
                {
                    var result = service.TranslateBlocksAsync(texts, CancellationToken.None).GetAwaiter().GetResult();
                    translations = result.Texts;
                    translatedTotal += result.TranslatedCount;
                }
                else
                {
                    translations = texts.Select(t => "译文：" + t).ToList();
                }

                var overlayImage = MakeStressImage();
                var overlay = new ResultOverlay(overlayImage, ocr.Lines, new Rectangle(100, 100, 1400, 500), cfg);
                overlay.ShowBusyOriginal();
                overlay.ShowTranslated(translations, 0.1);
                Application.DoEvents();
                overlay.Close();
                Application.DoEvents();
            }
            catch (Exception ex)
            {
                errors++;
                report.AppendLine($"cycle {i} ERROR: {ex.GetType().Name}: {ex.Message}");
                if (errors <= 3) Log.Error("压力测试循环失败 " + i, ex);
            }

            if (i == warmup)
            {
                proc.Refresh();
                memBefore = proc.WorkingSet64;
                total.Restart();
            }
            if (i > warmup) measuredCycles++;

            if (i % 20 == 0) report.AppendLine($"progress {i}/{cycles}");
        }

        total.Stop();
        Application.DoEvents();
        Thread.Sleep(400);
        Application.DoEvents();
        proc.Refresh();
        memAfter = proc.WorkingSet64;

        int visible = CountVisibleWindows((uint)Environment.ProcessId);
        long growthMb = (memAfter - memBefore) / (1024 * 1024);
        double avgMs = measuredCycles > 0 ? total.Elapsed.TotalMilliseconds / measuredCycles : 0;

        report.AppendLine($"elapsed={total.Elapsed.TotalSeconds:0.0}s avg={avgMs:0}ms/cycle (after {warmup} warmup)");
        report.AppendLine($"memory before={memBefore / 1024 / 1024}MB after={memAfter / 1024 / 1024}MB growth={growthMb}MB");
        report.AppendLine($"visible windows left={visible}");
        report.AppendLine($"OCR lines total={ocrTotal} translated={translatedTotal} errors={errors}");
        bool pass = errors == 0 && visible == 0 && growthMb < 120;
        report.AppendLine("RESULT: " + (pass ? "PASS" : "FAIL"));

        var path = Path.Combine(outputDir, "stress-report.txt");
        try { File.WriteAllText(path, report.ToString(), new UTF8Encoding(false)); } catch { }
        try { Console.WriteLine(report.ToString()); } catch { }
        Environment.ExitCode = pass ? 0 : 1;
    }

    private static int CountVisibleWindows(uint pid)
    {
        int count = 0;
        NativeMethods.EnumWindows((h, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(h, out uint p);
            if (p == pid && NativeMethods.IsWindowVisible(h)) count++;
            return true;
        }, IntPtr.Zero);
        return count;
    }

    private static Bitmap MakeStressImage()
    {
        var bmp = new Bitmap(1400, 500, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(246, 247, 249));
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        using var titleFont = new Font("Arial", 40, FontStyle.Bold);
        using var bodyFont = new Font("Arial", 24, FontStyle.Regular);
        using var monoFont = new Font("Consolas", 22, FontStyle.Regular);
        using var dark = new SolidBrush(Color.FromArgb(23, 27, 34));
        using var white = new SolidBrush(Color.FromArgb(242, 245, 248));
        using var black = new SolidBrush(Color.FromArgb(20, 24, 30));
        using var light = new SolidBrush(Color.FromArgb(90, 102, 115));

        g.DrawString("System Settings", titleFont, black, 40, 30);
        g.DrawString("Control who and which devices are allowed to connect in your network.", bodyFont, black, 40, 110);
        g.DrawString("Click GetProcAddress to load kernel32.dll. Version 10.0.19041.", bodyFont, black, 40, 160);
        g.DrawString("The download failed: Access denied.", bodyFont, black, 40, 210);

        g.FillRectangle(dark, 40, 280, 1320, 90);
        g.DrawString("Dark background: Access denied. Check C:\\Windows\\System32\\drivers\\etc\\hosts", monoFont, white, 60, 305);

        g.DrawString("This page is a deterministic test target for the screenshot translation overlay.", bodyFont, light, 40, 400);
        g.DrawString("Extra line: Press Ctrl+C to copy, Space to compare, R to retry.", bodyFont, light, 40, 445);
        return bmp;
    }
}
