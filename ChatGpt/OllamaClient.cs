using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

public class OllamaClient
{
    private readonly HttpClient _http;
    private readonly string _chatModel;
    private readonly string _genModel;
    private readonly double _temperature;

    public OllamaClient(
        string baseUrl = "http://localhost:11434",
        string chatModel = "gemma3:4b",
        string genModel = "gemma3:4b",
        double temperature = 0.2,
        TimeSpan? timeout = null)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = timeout ?? TimeSpan.FromMinutes(2) };
        _chatModel = chatModel;
        _genModel = genModel;
        _temperature = temperature;
    }

    /// <summary>Проверка, что Ollama жив (и вообще отвечает)</summary>
    public async Task<bool> IsAliveAsync(CancellationToken ct = default(CancellationToken))
    {
        try
        {
            using (var resp = await _http.GetAsync("/api/tags", ct))
                return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>
    /// Универсальный вызов: собирает промпт (вопрос + контекст) и сначала пытается /api/generate, затем /api/chat.
    /// </summary>
    public async Task<string> AskAsync(
        string question,
        string context = "",
        string systemPrompt = "Ты помощник по SQL для схемы Docsvision. Отвечай кратко и по делу.",
        CancellationToken ct = default(CancellationToken))
    {
        if (string.IsNullOrWhiteSpace(question))
            return string.Empty;

        var prompt = string.IsNullOrWhiteSpace(context)
            ? question.Trim()
            : ("Вопрос: " + question.Trim() + "\nКонтекст:\n" + context.Trim());

        // 1) generate (3 ретрая)
        var text = await TryGenerateStreamAsync(prompt, ct);
        if (!string.IsNullOrWhiteSpace(text))
            return text.Trim();

        // 2) chat (3 ретрая)
        text = await TryChatStreamAsync(systemPrompt, prompt, ct);
        return (text ?? string.Empty).Trim();
    }

    // ---------- внутренности ----------

    private async Task<string> TryGenerateStreamAsync(string prompt, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try { return await GenerateStreamAsync(prompt, ct); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt < 3) { await Task.Delay(300 * attempt, ct); }
            catch (HttpRequestException) when (attempt < 3) { await Task.Delay(300 * attempt, ct); }
            catch (IOException) when (attempt < 3) { await Task.Delay(300 * attempt, ct); }
        }
        return string.Empty;
    }

    private async Task<string> TryChatStreamAsync(string systemPrompt, string prompt, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try { return await ChatStreamAsync(systemPrompt, prompt, ct); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt < 3) { await Task.Delay(300 * attempt, ct); }
            catch (HttpRequestException) when (attempt < 3) { await Task.Delay(300 * attempt, ct); }
            catch (IOException) when (attempt < 3) { await Task.Delay(300 * attempt, ct); }
        }
        return string.Empty;
    }

    private async Task<string> GenerateStreamAsync(string prompt, CancellationToken ct)
    {
        var payload = new
        {
            model = _genModel,          // подставь установленную модель (ollama pull llama3.1 и т.п.)
            prompt = prompt,
            stream = true,
            options = new { temperature = _temperature }
        };

        using (var req = new HttpRequestMessage(HttpMethod.Post, "/api/generate"))
        {
            req.Content = MakeJsonContent(payload);

            using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                return await ReadNdjsonStreamAsync(resp, preferResponseField: true, ct: ct);
            }
        }
    }

    private async Task<string> ChatStreamAsync(string systemPrompt, string prompt, CancellationToken ct)
    {
        var payload = new
        {
            model = _chatModel,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = prompt }
            },
            stream = true,
            options = new { temperature = _temperature }
        };

        using (var req = new HttpRequestMessage(HttpMethod.Post, "/api/chat"))
        {
            req.Content = MakeJsonContent(payload);

            using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                // Для /api/chat стандартно приходит message.content
                return await ReadNdjsonStreamAsync(resp, preferResponseField: false, ct: ct);
            }
        }
    }

    private static StringContent MakeJsonContent(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    /// <summary>
    /// Читает NDJSON-стрим (построчно). Поддерживает оба поля: "response" (generate) и "message.content" (chat).
    /// </summary>
    private static async Task<string> ReadNdjsonStreamAsync(HttpResponseMessage resp, bool preferResponseField, CancellationToken ct)
    {
        var sb = new StringBuilder();

        using (var stream = await resp.Content.ReadAsStreamAsync())
        using (var reader = new StreamReader(stream))
        {
            while (!reader.EndOfStream)
            {
                ct.ThrowIfCancellationRequested();

                var line = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) continue;

                using (var doc = JsonDocument.Parse(line))
                {
                    var root = doc.RootElement;

                    // Ошибка
                    JsonElement err;
                    if (root.TryGetProperty("error", out err))
                        throw new Exception("Ollama error: " + (err.GetString() ?? "unknown"));

                    // done?
                    JsonElement doneEl;
                    bool done = (root.TryGetProperty("done", out doneEl) && doneEl.ValueKind == JsonValueKind.True);

                    // контент
                    if (preferResponseField)
                    {
                        // generate → response
                        JsonElement respEl;
                        if (root.TryGetProperty("response", out respEl))
                        {
                            var chunk = respEl.GetString();
                            if (!string.IsNullOrEmpty(chunk)) sb.Append(chunk);
                        }
                        else
                        {
                            // иногда драйверы пишут message.content даже в generate
                            AppendMessageContentIfAny(root, sb);
                        }
                    }
                    else
                    {
                        // chat → message.content
                        if (!AppendMessageContentIfAny(root, sb))
                        {
                            // на всякий случай — вдруг пришёл response
                            JsonElement respEl;
                            if (root.TryGetProperty("response", out respEl))
                            {
                                var chunk = respEl.GetString();
                                if (!string.IsNullOrEmpty(chunk)) sb.Append(chunk);
                            }
                        }
                    }

                    if (done) break;
                }
            }
        }

        return sb.ToString();
    }

    private static bool AppendMessageContentIfAny(JsonElement root, StringBuilder sb)
    {
        JsonElement msg;
        if (root.TryGetProperty("message", out msg))
        {
            JsonElement content;
            if (msg.TryGetProperty("content", out content))
            {
                var chunk = content.GetString();
                if (!string.IsNullOrEmpty(chunk))
                {
                    sb.Append(chunk);
                    return true;
                }
            }
        }
        return false;
    }
}
