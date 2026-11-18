using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace ShotTrans.Ocr;

public sealed record OcrLineBox(string Text, Rectangle Bounds, double Confidence = 0);

public sealed record OcrResultData(IReadOnlyList<OcrLineBox> Lines, int ImageWidth, int ImageHeight, double Scale);

public static class OcrService
{
    private static readonly HashSet<string> WarnedFallbacks = new(StringComparer.OrdinalIgnoreCase);

    // OCR 引擎创建（COM 对象 + 枚举已安装语言）较慢，按语言标签复用，避免每次截图都重建。
    private static readonly Dictionary<string, List<OcrEngine>> EngineCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object EngineCacheGate = new();

    public static void ClearEngineCache()
    {
        lock (EngineCacheGate) EngineCache.Clear();
    }

    // 常见英文/UI 词汇：仅当两个碎片各自不是词、拼起来是常见词时才合并，避免把 "API key" 之类误合并。
    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "work", "world", "word", "words", "wording", "write", "workbench", "workspace",
        "settings", "setting", "control", "network", "device", "devices", "connect", "connection",
        "click", "load", "version", "download", "failed", "fail", "access", "denied", "open",
        "background", "check", "hosts", "file", "files", "folder", "page", "pages", "test",
        "screenshot", "overlay", "extra", "line", "lines", "space", "compare", "copy", "retry",
        "select", "text", "box", "below", "translation", "system", "window", "button", "menu",
        "option", "options", "configuration", "config", "service", "services", "server", "client",
        "request", "response", "timeout", "address", "key", "keys", "value", "values", "error",
        "warning", "success", "account", "password", "email", "search", "help", "about", "close",
        "cancel", "save", "delete", "edit", "view", "list", "data", "model", "example", "demo",
        "sample", "privacy", "security", "location", "permission", "administrator", "user", "profile",
        "start", "stop", "restart", "build", "debug", "release", "install", "update", "image",
        "images", "document", "documents", "message", "messages", "notification", "history", "record",
        "records", "project", "session", "workspace", "tool", "editor", "console", "terminal",
        "command", "script", "function", "variable", "object", "class", "method", "field", "array",
        "string", "number", "boolean", "true", "false", "null", "type", "format", "encode", "decode",
        "token", "cache", "memory", "process", "thread", "async", "task", "queue", "event",
        "handler", "callback", "local", "remote", "host", "port", "database", "table", "query",
        "index", "hello", "shot", "trans", "cover", "language", "keyboard", "display", "screen",
        "allowed", "hello", "downloads", "installed", "running", "stopped", "loading", "loaded",
        "network", "traffic", "policy", "rules", "edit", "editor", "default", "custom", "machine",
        "machine", "devices", "access", "connection", "error", "errors", "warning", "warnings",
        "license", "licensed", "licensing", "to", "of", "in", "on", "at", "for", "and", "or",
        "is", "are", "was", "were", "be", "been", "this", "that", "these", "those", "with",
        "from", "into", "your", "you", "we", "they", "it", "as", "by", "not", "can", "will",
        "read", "dark", "light", "white", "black", "red", "blue", "green", "user", "users",
        "kernel32", "cmd", "hosts", "running", "ready", "loading", "done", "failed", "success"
    };

    internal static bool IsCommonWord(string token) => CommonWords.Contains(token);

    public static IReadOnlyList<Language> AvailableLanguages()
    {
        try { return OcrEngine.AvailableRecognizerLanguages.ToList(); }
        catch { return Array.Empty<Language>(); }
    }

    /// <summary>按配置的语言标签创建引擎；auto / 空值偏好 en-US（若已安装），再回退系统语言。</summary>
    public static OcrEngine? CreateEngine(string? languageTag)
    {
        var candidates = CreateEngineCandidates(languageTag);
        return candidates.FirstOrDefault();
    }

    public static List<OcrEngine> CreateEngineCandidates(string? languageTag)
    {
        var key = string.IsNullOrWhiteSpace(languageTag) ? "auto" : languageTag.Trim();
        lock (EngineCacheGate)
        {
            if (EngineCache.TryGetValue(key, out var cached) && cached.Count > 0)
                return cached;
        }

        var built = BuildEngineCandidates(languageTag);
        if (built.Count > 0)
        {
            lock (EngineCacheGate) EngineCache[key] = built;
        }
        return built;
    }

    private static List<OcrEngine> BuildEngineCandidates(string? languageTag)
    {
        var engines = new List<OcrEngine>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(OcrEngine? engine)
        {
            if (engine == null) return;
            var tag = engine.RecognizerLanguage.LanguageTag;
            if (seen.Add(tag)) engines.Add(engine);
        }

        try
        {
            bool auto = string.IsNullOrWhiteSpace(languageTag) ||
                        languageTag.Equals("auto", StringComparison.OrdinalIgnoreCase);

            if (!auto)
            {
                Add(OcrEngine.TryCreateFromLanguage(new Language(languageTag)));
            }

            if (auto)
            {
                // 本工具主要翻译外语界面，auto 时优先英文 OCR（若已安装）；中文界面可在设置中指定 zh-Hans。
                var english = OcrEngine.AvailableRecognizerLanguages
                    .FirstOrDefault(l => l.LanguageTag.Equals("en-US", StringComparison.OrdinalIgnoreCase))
                    ?? OcrEngine.AvailableRecognizerLanguages
                        .FirstOrDefault(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
                if (english != null) Add(OcrEngine.TryCreateFromLanguage(english));
            }

            Add(OcrEngine.TryCreateFromUserProfileLanguages());
            foreach (var lang in new[] { "en-US", "zh-Hans-CN", "zh-Hans" })
                Add(OcrEngine.TryCreateFromLanguage(new Language(lang)));
        }
        catch (Exception ex)
        {
            Log.Warn("创建 OCR 引擎失败：" + ex.Message);
        }

        if (!string.IsNullOrWhiteSpace(languageTag) &&
            !languageTag.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
            engines.Count > 0 &&
            !engines[0].RecognizerLanguage.LanguageTag.Equals(languageTag, StringComparison.OrdinalIgnoreCase) &&
            WarnedFallbacks.Add(languageTag))
        {
            Log.Warn($"OCR 语言 {languageTag} 未安装，回退到 {engines[0].RecognizerLanguage.LanguageTag}");
        }
        return engines;
    }

    public static Task<OcrResultData> RecognizeAsync(Bitmap bitmap, string? languageTag, CancellationToken ct = default)
    {
        return Task.Run(() => Recognize(bitmap, languageTag, ct), ct);
    }

    private static OcrResultData Recognize(Bitmap bitmap, string? languageTag, CancellationToken ct)
    {
        var engines = CreateEngineCandidates(languageTag);
        if (engines.Count == 0)
            throw new InvalidOperationException("系统 OCR 引擎不可用，请安装 Windows 语言包。");

        int maxDim = (int)OcrEngine.MaxImageDimension;
        double scale = 1.0;
        Bitmap work = bitmap;
        bool ownsWork = false;
        try
        {
            if (bitmap.Width > maxDim || bitmap.Height > maxDim)
            {
                scale = Math.Min((double)maxDim / bitmap.Width, (double)maxDim / bitmap.Height);
                int w = Math.Max(1, (int)Math.Floor(bitmap.Width * scale));
                int h = Math.Max(1, (int)Math.Floor(bitmap.Height * scale));
                work = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using var g = Graphics.FromImage(work);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(bitmap, 0, 0, w, h);
                ownsWork = true;
            }

            ct.ThrowIfCancellationRequested();
            OcrResult? result = null;
            OcrEngine? usedEngine = null;
            Exception? lastError = null;
            foreach (var engine in engines)
            {
                try
                {
                    using var softwareBitmap = ToSoftwareBitmap(work);
                    var r = engine.RecognizeAsync(softwareBitmap).AsTask(ct).GetAwaiter().GetResult();
                    result = r;
                    if (r.Lines.Count > 0)
                    {
                        usedEngine = engine;
                        break;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    lastError = ex;
                    Log.Warn($"OCR 引擎 {engine.RecognizerLanguage.LanguageTag} 识别失败：" + ex.Message);
                }
            }
            ct.ThrowIfCancellationRequested();
            if (result == null)
                throw new InvalidOperationException("OCR 识别失败：" + (lastError?.Message ?? "未知错误"), lastError);

            if (usedEngine != null && engines.Count > 1 && !ReferenceEquals(usedEngine, engines[0]) &&
                Environment.GetEnvironmentVariable("SHOTRANS_DEBUG_OCR") == "1")
            {
                Log.Info($"OCR 首选引擎未识别到文字，回退到 {usedEngine.RecognizerLanguage.LanguageTag}");
            }

            var lines = new List<OcrLineBox>();
            foreach (var line in result.Lines)
            {
                double left = double.MaxValue, top = double.MaxValue, right = 0, bottom = 0;
                bool any = false;
                foreach (var word in line.Words)
                {
                    var r = word.BoundingRect;
                    if (r.Width <= 0 || r.Height <= 0) continue;
                    left = Math.Min(left, r.X);
                    top = Math.Min(top, r.Y);
                    right = Math.Max(right, r.X + r.Width);
                    bottom = Math.Max(bottom, r.Y + r.Height);
                    any = true;
                }
                if (!any) continue;
                var text = RebuildLineText(line);
                if (text.Length == 0) continue;

                if (scale != 1.0)
                {
                    left /= scale; right /= scale; top /= scale; bottom /= scale;
                }
                var rect = Rectangle.FromLTRB(
                    Math.Max(0, (int)Math.Floor(left)),
                    Math.Max(0, (int)Math.Floor(top)),
                    Math.Min(bitmap.Width, (int)Math.Ceiling(right)),
                    Math.Min(bitmap.Height, (int)Math.Ceiling(bottom)));
                if (rect.Width < 2 || rect.Height < 2) continue;
                lines.Add(new OcrLineBox(text, rect));
            }

            return new OcrResultData(SortReadingOrder(lines), bitmap.Width, bitmap.Height, scale);
        }
        finally
        {
            if (ownsWork) work.Dispose();
        }
    }

    public static List<OcrLineBox> SortReadingOrder(IEnumerable<OcrLineBox> lines)
    {
        // 同一行容差 6px，再按 X 排序；块之间按 Y。
        return lines
            .OrderBy(l => l.Bounds.Top / 6)
            .ThenBy(l => l.Bounds.Top)
            .ThenBy(l => l.Bounds.Left)
            .ToList();
    }

    /// <summary>
    /// Windows OCR 偶尔把英文单词拆成多个 word（如 "WO" + "rk"），并用空格拼成 "WO rk"。
    /// 按 word 的字符间距重建行文本：间距明显小于行高时合并，并修正拆词导致的错误大小写。
    /// </summary>
    private static string RebuildLineText(OcrLine line)
    {
        var words = line.Words;
        if (words.Count == 0)
            return RepairOcrText(RepairSplitWords(NormalizeSpaces(line.Text ?? "")));

        var sb = new StringBuilder();
        string? previousWord = null;
        double previousRight = 0;
        double previousHeight = 0;
        bool debug = Environment.GetEnvironmentVariable("SHOTRANS_DEBUG_OCR") == "1";

        foreach (var word in words)
        {
            var r = word.BoundingRect;
            string text = word.Text ?? "";
            if (text.Length == 0) continue;

            if (sb.Length > 0)
            {
                double gap = r.X - previousRight;
                double height = Math.Max(1.0, Math.Max(previousHeight, r.Height));
                bool join = gap <= Math.Max(1.5, height * 0.18);
                if (debug)
                    Log.Info($"OCR word '{previousWord}' + '{text}' gap={gap:0.0} h={height:0.0} join={join}");

                if (join)
                {
                    if (previousWord != null &&
                        previousWord.Length is >= 2 and <= 4 &&
                        previousWord.All(char.IsUpper) &&
                        text.Length > 0 && char.IsLower(text[0]) &&
                        sb.Length >= previousWord.Length)
                    {
                        // "WO" + "rk" -> "Wo" + "rk"
                        sb.Length -= previousWord.Length;
                        sb.Append(char.ToUpperInvariant(previousWord[0]))
                          .Append(previousWord[1..].ToLowerInvariant());
                    }
                    sb.Append(text);
                }
                else
                {
                    sb.Append(' ').Append(text);
                }
            }
            else
            {
                sb.Append(text);
            }

            previousWord = text;
            previousRight = r.X + r.Width;
            previousHeight = r.Height;
        }

        return RepairOcrText(RepairSplitWords(NormalizeSpaces(sb.ToString())));
    }

    /// <summary>把 "WO rk" / "Wor ld" 这类被空格拆开的常见词合并回 "work"/"world"。</summary>
    private static string RepairSplitWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return text;

        var result = new List<string>(parts.Length);
        for (int i = 0; i < parts.Length; i++)
        {
            if (i + 1 < parts.Length && TryMergeFragments(parts[i], parts[i + 1], out string merged))
            {
                if (result.Count == 0 ||
                    result[^1].EndsWith('.') || result[^1].EndsWith('!') ||
                    result[^1].EndsWith('?') || result[^1].EndsWith(':'))
                {
                    merged = char.ToUpperInvariant(merged[0]) + merged[1..];
                }
                result.Add(merged);
                i++;
            }
            else
            {
                result.Add(parts[i]);
            }
        }
        return string.Join(" ", result);
    }

    private static bool TryMergeFragments(string a, string b, out string merged)
    {
        merged = "";
        if (a.Length is < 2 or > 4 || !a.All(char.IsUpper)) return false;
        if (b.Length is < 1 or > 5 || !b.All(char.IsLetter)) return false;

        string candidate = (a + b).ToLowerInvariant();
        if (!CommonWords.Contains(candidate)) return false;
        // 两个碎片本身都是词时，宁可保留空格（例如 "WORK shop" / "THE end" / "API key"）。
        if (CommonWords.Contains(a.ToLowerInvariant()) || CommonWords.Contains(b.ToLowerInvariant())) return false;

        merged = candidate;
        return true;
    }

    private static readonly Regex OcrTokenRegex = new(
        @"(?<![A-Za-z0-9])([A-Za-z0-9°º˚©®™|!·•]{2,})(?![A-Za-z0-9])",
        RegexOptions.Compiled);

    // 中文 OCR 模型常见字符混淆：度数符号常代替 i/o，I/l/1、0/O 混淆。
    private static readonly Dictionary<char, char[]> ConfusableMap = new()
    {
        ['°'] = new[] { 'i', 'o' }, ['º'] = new[] { 'i', 'o' }, ['˚'] = new[] { 'i', 'o' },
        ['©'] = new[] { 'c' }, ['®'] = new[] { 'r' }, ['™'] = new[] { 't' },
        ['|'] = new[] { 'l', 'i' }, ['!'] = new[] { 'l', 'i' }, ['·'] = new[] { 'i' }, ['•'] = new[] { 'i' },
        ['1'] = new[] { 'l', 'i' }, ['0'] = new[] { 'o' }, ['5'] = new[] { 's' },
        ['I'] = new[] { 'l' }
    };

    /// <summary>
    /// 用常见词词典修正 OCR 字符级误识别，例如 L°CENSE → LICENSE、HeIIo → Hello、
    /// ControI → Control、tO → to、W0RD → WORD、kerne132 → kernel32。
    /// 只接受词典中的结果，避免把代码标识符改坏。
    /// </summary>
    internal static string RepairOcrText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return OcrTokenRegex.Replace(text, m =>
        {
            var original = m.Value;
            var repaired = RepairToken(original);
            if (Environment.GetEnvironmentVariable("SHOTRANS_DEBUG_OCR") == "1" && repaired != original)
                Log.Info($"OCR 字符修正：'{original}' -> '{repaired}'");
            return repaired;
        });
    }

    private static string RepairToken(string token)
    {
        if (CommonWords.Contains(token))
        {
            bool allUpper = token.All(c => !char.IsLetter(c) || char.IsUpper(c));
            bool titleCase = token.Length > 0 && char.IsUpper(token[0]) &&
                             token.Skip(1).All(c => !char.IsLetter(c) || char.IsLower(c));
            if (allUpper || titleCase) return token; // 大小写正常的词典词
        }

        // 1) 只修正大小写异常：tO -> to, SettIngs -> Settings
        if (token.All(char.IsLetter) &&
            token.Any(char.IsUpper) &&
            !token.All(char.IsUpper) &&
            token.Skip(1).Any(char.IsUpper) &&
            CommonWords.Contains(token.ToLowerInvariant()))
        {
            return FormatLikeOriginal(token, token.ToLowerInvariant());
        }

        // 2) 尝试字符混淆替换，只接受词典中的词
        string? best = null;
        int budget = 64;

        void Recurse(string current, int index)
        {
            if (best != null || budget <= 0) return;
            while (index < current.Length && !ConfusableMap.ContainsKey(current[index])) index++;
            if (index >= current.Length)
            {
                budget--;
                if (CommonWords.Contains(current)) best = current;
                return;
            }

            foreach (char replacement in ConfusableMap[current[index]])
            {
                var next = current[..index] + replacement + current[(index + 1)..];
                Recurse(next, index + 1);
                if (best != null) return;
            }
        }

        Recurse(token, 0);
        return best == null ? token : FormatLikeOriginal(token, best);
    }

    private static string FormatLikeOriginal(string original, string corrected)
    {
        var latin = original.Where(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')).ToList();
        bool allUpper = latin.Count > 0 && latin.All(char.IsUpper);
        if (allUpper) return corrected.ToUpperInvariant();
        if (original.Length > 0 && char.IsUpper(original[0]))
            return char.ToUpperInvariant(corrected[0]) + corrected[1..].ToLowerInvariant();
        return corrected.ToLowerInvariant();
    }

    private static string NormalizeSpaces(string text)
    {
        text = (text ?? "").Replace(' ', ' ').Trim();
        return System.Text.RegularExpressions.Regex.Replace(text, @"[ \t]{2,}", " ");
    }

    private static SoftwareBitmap ToSoftwareBitmap(Bitmap bitmap)
    {
        using var bgra = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bgra))
        {
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.DrawImage(bitmap, 0, 0, bitmap.Width, bitmap.Height);
        }

        var rect = new Rectangle(0, 0, bgra.Width, bgra.Height);
        var data = bgra.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            int expected = bgra.Width * 4;
            var bytes = new byte[stride * bgra.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);

            byte[] source = bytes;
            if (stride != expected)
            {
                source = new byte[expected * bgra.Height];
                for (int y = 0; y < bgra.Height; y++)
                    System.Buffer.BlockCopy(bytes, y * stride, source, y * expected, expected);
            }

            var writer = new DataWriter();
            writer.WriteBytes(source);
            var buffer = writer.DetachBuffer();
            return SoftwareBitmap.CreateCopyFromBuffer(buffer, BitmapPixelFormat.Bgra8, bgra.Width, bgra.Height, BitmapAlphaMode.Ignore);
        }
        finally
        {
            bgra.UnlockBits(data);
        }
    }
}
