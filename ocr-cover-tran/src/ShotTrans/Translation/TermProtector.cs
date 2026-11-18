using System.Text.RegularExpressions;
using ShotTrans.Ocr;

namespace ShotTrans.Translation;

public sealed class ProtectedText
{
    public string Masked { get; init; } = "";
    public IReadOnlyList<string> Values { get; init; } = Array.Empty<string>();
    public bool HasTranslatableContent { get; init; }

    public string Restore(string? translated)
    {
        if (string.IsNullOrEmpty(translated)) return translated ?? "";
        var result = Regex.Replace(translated, @"\[\s*\[\s*(\d+)\s*\]\s*\]|\[\s*(\d+)\s*\]|[⟦【［]\s*(\d+)\s*[⟧】］]", m =>
        {
            string digits = m.Groups[1].Success ? m.Groups[1].Value
                : m.Groups[2].Success ? m.Groups[2].Value
                : m.Groups[3].Value;
            if (int.TryParse(digits, out int idx) && idx >= 0 && idx < Values.Count)
                return Values[idx];
            return m.Value;
        });
        return result.Trim();
    }
}

/// <summary>代码标识符 / URL / 路径 / 版本号 / 用户术语保护。</summary>
public sealed class TermProtector
{
    private static readonly Regex PlaceholderRegex = new(
        @"\[\s*\[\s*\d+\s*\]\s*\]|\[\s*\d+\s*\]|[⟦【［]\s*\d+\s*[⟧】］]",
        RegexOptions.Compiled);

    private static readonly Regex CodeRegex = new(
        string.Join("|", new[]
        {
            @"\b(?:https?|ftp)://[^\s<>""']+",                                    // URL
            @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b",                // email
            @"\b[A-Za-z]:\\[^\s<>""']+",                                          // Windows path
            @"(?<![\w])/(?:[A-Za-z0-9._-]+/)*[A-Za-z0-9._-]+",                    // unix path
            @"[A-Za-z_][A-Za-z0-9_.]*\s*\([^()\n]{0,80}\)",                       // call()
            @"[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+",               // dotted.name
            @"[A-Za-z][A-Za-z0-9]*_[A-Za-z0-9_]*",                                // snake_case
            @"[A-Za-z][A-Za-z0-9]*[0-9][A-Za-z0-9]*",                             // word2
            @"[A-Z][a-z]+[A-Z][A-Za-z0-9]*",                                      // PascalCase
            @"[a-z]+[A-Z][A-Za-z0-9]*",                                           // camelCase
            @"[A-Z]{2,}[A-Z0-9_]*",                                               // ACRONYM
            @"(?<![\w-])--?[A-Za-z][A-Za-z0-9-]*",                                // --flag
            @"%[A-Za-z_][A-Za-z0-9_]*%",                                          // %ENVVAR%
            @"%[-+0-9.#]*[sdifuxX]",                                              // printf
            @"\{[A-Za-z0-9_]+(?::[^}]+)?\}",                                      // {0} {name}
            @"\bv?\d+(?:\.\d+){2,}(?:[-+][A-Za-z0-9.]+)?\b"                       // version
        }),
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly List<(string Source, string Target, Regex? Regex)> _glossary = new();
    private readonly bool _protectCode;

    public TermProtector(string? glossary, bool protectCode)
    {
        _protectCode = protectCode;
        if (!string.IsNullOrWhiteSpace(glossary))
        {
            foreach (var raw in glossary.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                int eq = line.IndexOfAny(new[] { '=', '＝' });
                if (eq <= 0 || eq >= line.Length - 1) continue;
                var source = line[..eq].Trim();
                var target = line[(eq + 1)..].Trim();
                if (source.Length == 0 || target.Length == 0) continue;

                Regex? rx = null;
                if (source.Length > 0 && IsWordChar(source[0]) && IsWordChar(source[^1]))
                {
                    rx = new Regex(@"(?<![A-Za-z0-9_])" + Regex.Escape(source) + @"(?![A-Za-z0-9_])",
                        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                }
                _glossary.Add((source, target, rx));
            }
            _glossary.Sort((a, b) => b.Source.Length.CompareTo(a.Source.Length));
        }
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// 收紧代码标识符判定：OCR 常把 "to" 识别成 "tO"、"Control" 识别成 "ControI"，
    /// 这些不应被当成 camelCase 保护起来，否则翻译模型无法修正。
    /// </summary>
    private static bool ShouldProtect(string token)
    {
        if (token.Length == 0) return false;
        if (token.StartsWith("-")) return true;
        if (token.Any(c => c is '_' or '.' or '(' or ')' or '/' or '\\' or '%' or '{' or '}'
                                or '[' or ']' or '@' or ':' or '=' or '+' or '&' or '?' or '#'))
            return true;
        if (token.Any(char.IsDigit)) return true;

        bool allUpper = token.All(c => char.IsUpper(c) || c == '_');
        if (allUpper)
        {
            // 保护 API/HTTP；但 LICENSE/SETTINGS/CONTROL 等全大写常用词要允许翻译。
            return token.Length >= 3 && !OcrService.IsCommonWord(token);
        }

        int leadingUpper = 0;
        while (leadingUpper < token.Length && char.IsUpper(token[leadingUpper])) leadingUpper++;
        if (leadingUpper >= 2 && leadingUpper < token.Length) return true; // HTTPClient / IOStream

        // 只把“小写→大写→小写”的真实驼峰边界当作代码：
        // GetProcAddress、camelCase、iPhone、GitHub；放过 ControI / tO / HeIIo 这类 OCR 大小写错误。
        for (int i = 1; i < token.Length - 1; i++)
        {
            if (char.IsLower(token[i - 1]) && char.IsUpper(token[i]) && char.IsLower(token[i + 1]))
                return token.Length >= 4;
        }
        return false;
    }

    public ProtectedText Protect(string text, bool toEnglish = false)
    {
        if (string.IsNullOrEmpty(text))
            return new ProtectedText { Masked = text ?? "", HasTranslatableContent = false };

        var values = new List<string>();
        var working = text;

        string Take(string value)
        {
            int idx = values.Count;
            values.Add(value);
            return "[[" + idx + "]]";
        }

        foreach (var (source, target, rx) in _glossary)
        {
            working = rx != null
                ? rx.Replace(working, _ => Take(target))
                : working.Replace(source, Take(target), StringComparison.Ordinal);
        }

        if (_protectCode)
        {
            working = CodeRegex.Replace(working, m =>
            {
                if (values.Count >= 200 || !ShouldProtect(m.Value)) return m.Value;
                return Take(m.Value);
            });
        }

        var remainder = PlaceholderRegex.Replace(working, "");
        int latin = 0, cjk = 0;
        foreach (char c in remainder)
        {
            if (c >= 0x4E00 && c <= 0x9FFF) cjk++;
            else if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) latin++;
        }
        // 译成中文时外文才算可翻译；译成英文时中文才算可翻译。
        bool translatable = toEnglish ? (cjk > 0 && cjk >= latin) : (latin > 0 && latin >= cjk);

        return new ProtectedText
        {
            Masked = working,
            Values = values,
            HasTranslatableContent = translatable
        };
    }
}
