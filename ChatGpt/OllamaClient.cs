using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

public class OllamaClient
{
    private readonly HttpClient _http;

    public OllamaClient(string baseUrl = "http://localhost:11434")
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async Task<string> AskAsync(string question, string context = "")
    {
        // Сформируем единый промпт
        var prompt = string.IsNullOrWhiteSpace(context)
            ? question
            : $"Вопрос: {question}\nКонтекст:\n{context}";

        // 1) Попытка через /api/generate (response-стрим)
        var text = await GenerateStreamAsync(prompt);
        if (!string.IsNullOrWhiteSpace(text))
            return text.Trim();

        // 2) Фолбэк через /api/chat (message.content-стрим)
        text = await ChatStreamAsync(prompt);
        return text.Trim();
    }

    private async Task<string> GenerateStreamAsync(string prompt)
    {
        var payload = new
        {
            model = "gpt-oss:20b", // подставь свою модель
            prompt = prompt,
            stream = true,
            options = new { temperature = 0.2 }
        };

        var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json"
        );

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/generate")
        {
            Content = content
        };

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        var sb = new StringBuilder();

        using var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;

            using var doc = JsonDocument.Parse(line);

            // Ошибка от Ollama
            if (doc.RootElement.TryGetProperty("error", out var err))
                throw new Exception($"Ollama error: {err.GetString()}");

            // Основной путь для /api/generate
            if (doc.RootElement.TryGetProperty("response", out var respChunk))
            {
                var chunk = respChunk.GetString();
                if (!string.IsNullOrEmpty(chunk))
                    sb.Append(chunk);
            }
            // На случай если модель всё же стримит через message.content
            else if (doc.RootElement.TryGetProperty("message", out var msg) &&
                     msg.TryGetProperty("content", out var contentNode))
            {
                var chunk = contentNode.GetString();
                if (!string.IsNullOrEmpty(chunk))
                    sb.Append(chunk);
            }

            if (doc.RootElement.TryGetProperty("done", out var done) && done.GetBoolean())
                break;
        }

        return sb.ToString();
    }

    private async Task<string> ChatStreamAsync(string prompt)
    {
        var payload = new
        {
            model = "gpt-oss:20b", // подставь свою модель
            messages = new object[]
            {
                new { role = "system", content = "Ты помощник по SQL для схемы Docsvision. Отвечай кратко и по делу." },
                new { role = "user", content = prompt }
            },
            stream = true,
            options = new { temperature = 0.2 }
        };

        var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json"
        );

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = content
        };

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        var sb = new StringBuilder();

        using var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;

            using var doc = JsonDocument.Parse(line);

            // Ошибка от Ollama
            if (doc.RootElement.TryGetProperty("error", out var err))
                throw new Exception($"Ollama error: {err.GetString()}");

            if (doc.RootElement.TryGetProperty("message", out var msg) &&
                msg.TryGetProperty("content", out var contentNode))
            {
                var chunk = contentNode.GetString();
                if (!string.IsNullOrEmpty(chunk))
                    sb.Append(chunk);
            }
            else if (doc.RootElement.TryGetProperty("response", out var respChunk))
            {
                // На всякий случай — если модель отдаёт 'response' даже в /api/chat
                var chunk = respChunk.GetString();
                if (!string.IsNullOrEmpty(chunk))
                    sb.Append(chunk);
            }

            if (doc.RootElement.TryGetProperty("done", out var done) && done.GetBoolean())
                break;
        }

        return sb.ToString();
    }
}
