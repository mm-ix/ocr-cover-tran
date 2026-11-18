using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShotTrans.Translation;

public sealed class TranslationException : Exception
{
    public TranslationException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed record TranslationBatchResult(IReadOnlyList<string> Texts, int TranslatedCount, TimeSpan Elapsed);
public sealed record TranslationTestResult(bool Success, string Message, TimeSpan Elapsed);

public sealed class TranslationService
{
    // 延迟到首次请求时再创建，避免启动阶段初始化连接池/网络栈。
    private static readonly Lazy<HttpClient> HttpLazy = new(CreateHttp);
    private static HttpClient Http => HttpLazy.Value;

    private static HttpClient CreateHttp() => new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private const string SystemPromptToChinese =
        "You are a professional translation engine.\n" +
        "The user sends a JSON array of strings. Translate every element into Simplified Chinese.\n" +
        "Rules:\n" +
        "1. Output ONLY a valid JSON array of strings with exactly the same length and order. " +
        "Do not output Markdown fences, explanations, or object keys.\n" +
        "2. Placeholders like [[0]], [[1]] must be preserved exactly; never translate, remove, or reorder them.\n" +
        "3. Keep code identifiers, function names, variable names, command flags, file paths, URLs, " +
        "version numbers, numbers and punctuation unchanged.\n" +
        "4. If an element is already Chinese, return it unchanged.\n" +
        "5. Keep line breaks inside each element.\n" +
        "6. Return exactly one output element per input element; never merge, skip, or drop elements, even short ones.\n" +
        "7. The input may come from OCR and contain broken word spacing or case errors (for example \"WO rk\" means \"work\"). " +
        "Silently repair obvious OCR mistakes in normal prose before translating, but never alter code identifiers, " +
        "function names, file paths, URLs, version numbers, numbers, or placeholders.";

    private const string SystemPromptToEnglish =
        "You are a professional translation engine.\n" +
        "The user sends a JSON array of strings. Translate every element into natural English.\n" +
        "Rules:\n" +
        "1. Output ONLY a valid JSON array of strings with exactly the same length and order. " +
        "Do not output Markdown fences, explanations, or object keys.\n" +
        "2. Placeholders like [[0]], [[1]] must be preserved exactly; never translate, remove, or reorder them.\n" +
        "3. Keep code identifiers, function names, variable names, command flags, file paths, URLs, " +
        "version numbers, numbers and punctuation unchanged.\n" +
        "4. If an element is already English, return it unchanged.\n" +
        "5. Keep line breaks inside each element.\n" +
        "6. Return exactly one output element per input element; never merge, skip, or drop elements, even short ones.";

    private const string ExplainSystemPrompt =
        "你是一个简洁的词典与百科助手。用户会给你一个词语、短语或一小段文字（可能是外文，也可能是中文）。\n" +
        "请用简体中文给出关于它的简短介绍/解释：\n" +
        "1. 先用一句话说明它是什么或是什么意思；如有常见用法、背景或易混点，再补充 1-2 句。\n" +
        "2. 如果输入是一个单词或术语，重点解释含义和常见用法；如果是一句话，简要概括其意思或语境。\n" +
        "3. 总长度控制在约 120 字以内，最多不超过 3 句话。\n" +
        "4. 只输出解释正文，不要标题、编号、Markdown，也不要重复原文。\n" +
        "5. 如果输入是无意义乱码或无法理解，直接输出“（无可用解释）”。";

    private readonly AppConfig _config;
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly object _cacheLock = new();

    public TranslationService(AppConfig config)
    {
        _config = config;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_config.TranslationBaseUrl) && !string.IsNullOrWhiteSpace(_config.TranslationModel);

    public void ClearCache()
    {
        lock (_cacheLock) _cache.Clear();
    }

    public async Task<TranslationBatchResult> TranslateBlocksAsync(IReadOnlyList<string> texts, CancellationToken ct, bool toEnglish = false)
    {
        var sw = Stopwatch.StartNew();
        RequireConfigured();

        var protector = new TermProtector(_config.Glossary, _config.ProtectCodeIdentifiers);
        var results = new string[texts.Count];
        var protectedTexts = new ProtectedText?[texts.Count];
        var uniqueMasked = new List<string>();
        var seenMasked = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < texts.Count; i++)
        {
            var raw = texts[i] ?? "";
            if (string.IsNullOrWhiteSpace(raw))
            {
                results[i] = raw;
                continue;
            }
            if (TryGetCache(raw, toEnglish, out var cached))
            {
                results[i] = cached;
                continue;
            }

            var p = protector.Protect(raw, toEnglish);
            protectedTexts[i] = p;
            if (!p.HasTranslatableContent)
            {
                results[i] = raw;
                continue;
            }
            if (seenMasked.Add(p.Masked))
                uniqueMasked.Add(p.Masked);
        }

        var translatedByMasked = new Dictionary<string, string>(StringComparer.Ordinal);
        if (uniqueMasked.Count > 0)
        {
            var translated = await CallChatBatchWithRecoveryAsync(uniqueMasked, ct, toEnglish).ConfigureAwait(false);
            if (translated.Count != uniqueMasked.Count)
                throw new TranslationException($"翻译返回条数不一致（期望 {uniqueMasked.Count}，实际 {translated.Count}），请按 R 重试");

            for (int i = 0; i < uniqueMasked.Count; i++)
                translatedByMasked[uniqueMasked[i]] = translated[i];
        }

        for (int i = 0; i < texts.Count; i++)
        {
            if (results[i] != null) continue;
            var p = protectedTexts[i];
            if (p == null || !p.HasTranslatableContent)
            {
                results[i] = texts[i] ?? "";
                continue;
            }

            string raw = texts[i] ?? "";
            string final = translatedByMasked.TryGetValue(p.Masked, out var maskedTranslation)
                ? p.Restore(maskedTranslation)
                : raw;
            if (string.IsNullOrWhiteSpace(final)) final = raw;
            results[i] = final;
            SetCache(raw, final, toEnglish);
        }

        int translatedCount = 0;
        for (int i = 0; i < texts.Count; i++)
        {
            if (results[i] != null && !string.Equals(results[i], texts[i], StringComparison.Ordinal))
                translatedCount++;
        }

        sw.Stop();
        return new TranslationBatchResult(results, translatedCount, sw.Elapsed);
    }

    public async Task<string> TranslateTextAsync(string text, CancellationToken ct, bool toEnglish = false)
    {
        var result = await TranslateBlocksAsync(new[] { text }, ct, toEnglish).ConfigureAwait(false);
        return result.Texts.Count > 0 ? result.Texts[0] : text;
    }

    /// <summary>针对选中的原文生成一段简短的中文词义/介绍。</summary>
    public async Task<string> ExplainAsync(string text, CancellationToken ct)
    {
        RequireConfigured();
        var content = await SendChatAsync(ExplainSystemPrompt, text ?? "", ct).ConfigureAwait(false);
        return (content ?? "").Trim();
    }

    public async Task<TranslationTestResult> TestAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            RequireConfigured();
            var texts = await CallChatBatchAsync(new List<string> { "Hello, world" }, ct).ConfigureAwait(false);
            sw.Stop();
            return new TranslationTestResult(true, $"连接成功，模型返回：{texts.FirstOrDefault()}", sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new TranslationTestResult(false, ex.Message, sw.Elapsed);
        }
    }

    private void RequireConfigured()
    {
        if (!IsConfigured)
            throw new TranslationException("尚未配置翻译接口，请打开设置填写 base_url / api_key / model");
    }

    /// <summary>
    /// 模型偶尔会合并/丢弃少量元素。批量条数不一致时按 4 条一块重试，仍不一致再逐条翻译，
    /// 保证结果与原文块一一对应。
    /// </summary>
    private async Task<List<string>> CallChatBatchWithRecoveryAsync(List<string> items, CancellationToken ct, bool toEnglish)
    {
        var first = await CallChatBatchAsync(items, ct, toEnglish).ConfigureAwait(false);
        if (first.Count == items.Count) return first;

        Log.Warn($"批量翻译返回 {first.Count}/{items.Count} 条，改用分块/逐条重试");
        if (items.Count == 1)
        {
            if (first.Count == 1) return first;
            throw new TranslationException("翻译结果条数不一致，请按 R 重试");
        }

        var chunks = new List<List<string>>();
        for (int i = 0; i < items.Count; i += 4)
            chunks.Add(items.GetRange(i, Math.Min(4, items.Count - i)));

        var chunkTasks = chunks.Select(chunk => TranslateChunkAsync(chunk, ct, toEnglish)).ToList();
        var results = await Task.WhenAll(chunkTasks).ConfigureAwait(false);
        return results.SelectMany(r => r).ToList();
    }

    private async Task<List<string>> TranslateChunkAsync(List<string> chunk, CancellationToken ct, bool toEnglish)
    {
        try
        {
            var result = await CallChatBatchAsync(chunk, ct, toEnglish).ConfigureAwait(false);
            if (result.Count == chunk.Count) return result;
            Log.Warn($"分块翻译返回 {result.Count}/{chunk.Count} 条，改为逐条翻译");
        }
        catch (TranslationException ex)
        {
            Log.Warn("分块翻译失败，改为逐条：" + ex.Message);
        }

        var singleTasks = chunk.Select(async text =>
        {
            var one = await CallChatBatchAsync(new List<string> { text }, ct, toEnglish).ConfigureAwait(false);
            return one.Count > 0 ? one[0] : "";
        });
        return (await Task.WhenAll(singleTasks).ConfigureAwait(false)).ToList();
    }

    private async Task<List<string>> CallChatBatchAsync(IReadOnlyList<string> items, CancellationToken ct, bool toEnglish = false)
    {
        var userContent = JsonSerializer.Serialize(items.ToList(), AppJsonContext.Default.ListString);
        var systemPrompt = toEnglish ? SystemPromptToEnglish : SystemPromptToChinese;
        var content = await SendChatAsync(systemPrompt, userContent, ct).ConfigureAwait(false);
        return ParseTranslatedArray(content, items.Count);
    }

    private async Task<string> SendChatAsync(string systemPrompt, string userContent, CancellationToken ct)
    {
        var endpoint = BuildEndpoint(_config.TranslationBaseUrl);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(_config.TranslationTimeoutSeconds, 3, 120));

        var payload = new ChatRequest
        {
            model = _config.TranslationModel.Trim(),
            messages = new[]
            {
                new ChatMessage { role = "system", content = systemPrompt },
                new ChatMessage { role = "user", content = userContent }
            },
            temperature = _config.TranslationTemperature,
            stream = false
        };

        string body;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            var key = _config.TranslationApiKey?.Trim();
            if (!string.IsNullOrEmpty(key))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload, AppJsonContext.Default.ChatRequest),
                Encoding.UTF8, "application/json");

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var snippet = body.Length > 300 ? body[..300] : body;
                throw new TranslationException($"翻译接口返回 HTTP {(int)response.StatusCode}：{Sanitize(snippet)}");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TranslationException($"翻译超时（{timeout.TotalSeconds:0} 秒），请检查网络或接口地址");
        }
        catch (HttpRequestException ex)
        {
            throw new TranslationException("无法连接翻译接口：" + ex.Message, ex);
        }

        var content = ExtractContent(body);
        if (Environment.GetEnvironmentVariable("SHOTRANS_DEBUG_TRANSLATION") == "1")
        {
            var debug = content.Length > 2000 ? content[..2000] : content;
            Log.Info("接口原始返回：" + debug);
        }
        return content;
    }

    public static string BuildEndpoint(string baseUrl)
    {
        var url = (baseUrl ?? "").Trim();
        if (url.Length == 0) throw new TranslationException("翻译接口地址为空");
        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;

        if (url.Contains("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return url;

        url = url.TrimEnd('/');
        if (url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            return url + "/chat/completions";
        return url + "/v1/chat/completions";
    }

    private static string ExtractContent(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                throw new TranslationException("接口响应缺少 choices 字段");

            var first = choices[0];
            if (first.TryGetProperty("message", out var message))
            {
                if (message.TryGetProperty("content", out var contentEl))
                {
                    if (contentEl.ValueKind == JsonValueKind.String)
                        return contentEl.GetString() ?? "";
                    if (contentEl.ValueKind == JsonValueKind.Array)
                    {
                        var sb = new StringBuilder();
                        foreach (var part in contentEl.EnumerateArray())
                            if (part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                                sb.Append(t.GetString());
                        return sb.ToString();
                    }
                }
            }
            if (first.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                return textEl.GetString() ?? "";

            throw new TranslationException("接口响应中没有可用的译文内容");
        }
        catch (JsonException ex)
        {
            throw new TranslationException("翻译接口返回的不是合法 JSON", ex);
        }
    }

    private static List<string> ParseTranslatedArray(string content, int expected)
    {
        var cleaned = (content ?? "").Trim().TrimStart('﻿');
        cleaned = Regex.Replace(cleaned, @"^```(?:json)?\s*", "", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s*```$", "");

        // 1) 标准 JSON 数组（修复模型常见的非法反斜杠转义）
        int start = cleaned.IndexOf('[');
        int end = cleaned.LastIndexOf(']');
        if (start >= 0 && end > start)
        {
            var candidate = FixJsonEscapes(cleaned[start..(end + 1)]);
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var list = ReadStringArray(doc.RootElement);
                    if (list.Count > 0) return list;
                }
            }
            catch (JsonException)
            {
                // 继续尝试宽松解析
            }
        }

        // 2) 有些模型会返回对象：先从对象中找数组，再按属性顺序取字符串值
        int objStart = cleaned.IndexOf('{');
        int objEnd = cleaned.LastIndexOf('}');
        if (objStart >= 0 && objEnd > objStart)
        {
            try
            {
                using var doc = JsonDocument.Parse(FixJsonEscapes(cleaned[objStart..(objEnd + 1)]));
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Array)
                        {
                            var nested = ReadStringArray(prop.Value);
                            if (nested.Count == expected) return nested;
                        }
                    }
                    var values = doc.RootElement.EnumerateObject()
                        .Where(p => p.Value.ValueKind == JsonValueKind.String)
                        .Select(p => p.Value.GetString() ?? "")
                        .ToList();
                    if (values.Count == expected) return values;
                }
            }
            catch (JsonException)
            {
            }
        }

        // 3) 宽松提取 JSON 字符串（容忍非法转义、未转义换行）
        var extracted = ExtractQuotedStrings(cleaned);
        if (extracted.Count == expected) return extracted;

        // 4) 回退：逐行解析（去除前缀编号）
        var lines = cleaned
            .Split('\n')
            .Select(l => Regex.Replace(l.Trim(), @"^\s*\d+\s*[\.\)、:：]\s*", ""))
            .Where(l => l.Length > 0)
            .ToList();
        if (lines.Count == expected) return lines;

        throw new TranslationException("无法解析翻译结果（模型未按要求返回 JSON 数组），请按 R 重试");
    }

    private static List<string> ReadStringArray(JsonElement array)
    {
        var list = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString() ?? "");
            else if (item.ValueKind == JsonValueKind.Object &&
                     item.TryGetProperty("translation", out var tr) && tr.ValueKind == JsonValueKind.String)
                list.Add(tr.GetString() ?? "");
            else
                list.Add(item.ToString());
        }
        return list;
    }

    /// <summary>把字符串中未转义的反斜杠（如 Windows 路径）变成 \\，并把裸换行变成 \n。</summary>
    private static string FixJsonEscapes(string json)
    {
        var sb = new StringBuilder(json.Length + 32);
        bool inString = false;
        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];
            if (!inString)
            {
                if (c == '"') inString = true;
                sb.Append(c);
                continue;
            }

            if (c == '\\')
            {
                if (i + 1 < json.Length)
                {
                    char n = json[i + 1];
                    if (n is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't')
                    {
                        sb.Append(c).Append(n);
                        i++;
                        continue;
                    }
                    if (n == 'u' && i + 5 < json.Length &&
                        IsHex(json[i + 2]) && IsHex(json[i + 3]) && IsHex(json[i + 4]) && IsHex(json[i + 5]))
                    {
                        sb.Append(json, i, 6);
                        i += 5;
                        continue;
                    }
                }
                sb.Append("\\\\");
                continue;
            }

            if (c == '"')
            {
                inString = false;
                sb.Append(c);
                continue;
            }
            if (c is '\r' or '\n')
            {
                sb.Append("\\n");
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static List<string> ExtractQuotedStrings(string text)
    {
        var result = new List<string>();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] != '"') { i++; continue; }
            i++;
            var sb = new StringBuilder();
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '\\' && i + 1 < text.Length)
                {
                    char n = text[i + 1];
                    switch (n)
                    {
                        case '"': sb.Append('"'); i += 2; continue;
                        case '\\': sb.Append('\\'); i += 2; continue;
                        case '/': sb.Append('/'); i += 2; continue;
                        case 'b': sb.Append('\b'); i += 2; continue;
                        case 'f': sb.Append('\f'); i += 2; continue;
                        case 'n': sb.Append('\n'); i += 2; continue;
                        case 'r': sb.Append('\r'); i += 2; continue;
                        case 't': sb.Append('\t'); i += 2; continue;
                        case 'u' when i + 5 < text.Length &&
                                       IsHex(text[i + 2]) && IsHex(text[i + 3]) &&
                                       IsHex(text[i + 4]) && IsHex(text[i + 5]):
                            sb.Append((char)Convert.ToInt32(text.Substring(i + 2, 4), 16));
                            i += 6;
                            continue;
                        default:
                            sb.Append('\\').Append(n);
                            i += 2;
                            continue;
                    }
                }
                if (c == '"')
                {
                    i++;
                    break;
                }
                sb.Append(c);
                i++;
            }
            result.Add(sb.ToString());
        }
        return result;
    }

    private static string Sanitize(string s)
    {
        // 避免把用户 key 等信息写进界面/日志
        s = Regex.Replace(s, @"sk-[A-Za-z0-9_\-]{8,}", "sk-***");
        return s.Replace("\r", " ").Replace("\n", " ").Trim();
    }

    private bool TryGetCache(string raw, bool toEnglish, out string value)
    {
        lock (_cacheLock)
        {
            return _cache.TryGetValue(CacheKey(raw, toEnglish), out value!);
        }
    }

    private void SetCache(string raw, string value, bool toEnglish)
    {
        lock (_cacheLock)
        {
            if (_cache.Count > 500) _cache.Clear();
            _cache[CacheKey(raw, toEnglish)] = value;
        }
    }

    private string CacheKey(string raw, bool toEnglish) => (_config.TranslationModel ?? "") + (toEnglish ? "en" : "zh") + raw;
}
