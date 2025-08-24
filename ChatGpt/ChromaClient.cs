using System.Text.Json;
using System.Text;
using System.Net.Http;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;
using System.Windows.Forms;

public class ChromaDirect
{
    private readonly HttpClient _http;

    public ChromaDirect(string baseUrl = "http://localhost:8000")
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async Task<string> EnsureCollectionAsync(string name)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Collection name is null/empty.", nameof(name));

            var response = await _http.GetAsync("/api/v1/collections");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using (var doc = JsonDocument.Parse(json))
            {
                foreach (var c in doc.RootElement.EnumerateArray())
                {
                    if (string.Equals(c.GetProperty("name").GetString(), name, StringComparison.Ordinal))
                        return c.GetProperty("id").GetString();
                }
            }

            var payload = new { name = name };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var createResponse = await _http.PostAsync("/api/v1/collections", content);
            createResponse.EnsureSuccessStatusCode();

            var createdJson = await createResponse.Content.ReadAsStringAsync();
            using var createdDoc = JsonDocument.Parse(createdJson);
            return createdDoc.RootElement.GetProperty("id").GetString();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString());
            return null;
        }
    }

    /// <summary>
    /// Upsert: рассчитываем эмбеддинги в Ollama и отправляем в Chroma.
    /// </summary>
    public async Task UpsertAsync(string collectionId, List<(string Id, string Text)> documents)
    {
        if (string.IsNullOrWhiteSpace(collectionId))
            throw new ArgumentException("collectionId is null/empty", nameof(collectionId));

        var clean = (documents ?? new List<(string Id, string Text)>())
            .Where(d => !string.IsNullOrWhiteSpace(d.Id) && !string.IsNullOrWhiteSpace(d.Text))
            .Select(d => (Id: d.Id.Trim(), Text: d.Text.Trim()))
            .ToList();

        if (clean.Count == 0)
            throw new InvalidOperationException("No valid (Id, Text) pairs to upsert.");

        var embeddings = await GetEmbeddingsAsync(clean.Select(d => d.Text));
        if (embeddings == null || embeddings.Count != clean.Count || embeddings.Any(e => e == null || e.Length == 0))
            throw new Exception("Embeddings are invalid or misaligned with documents.");

        var payloadObj = new
        {
            ids = clean.Select(d => d.Id).ToArray(),
            documents = clean.Select(d => d.Text).ToArray(),
            embeddings = embeddings.ToArray(),
            metadatas = clean.Select(_ => new Dictionary<string, object>()).ToArray()
        };

        var debugJson = JsonSerializer.Serialize(payloadObj, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine("Payload to Chroma:\n" + debugJson);

        var content = new StringContent(debugJson, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync("/api/v1/collections/" + collectionId + "/upsert", content);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception("Upsert failed: " + response.StatusCode + "\n" + error);
        }
    }

    /// <summary>
    /// Возвращает эмбеддинги из Ollama (/api/embeddings) по одному запросу на текст.
    /// </summary>
    public async Task<List<float[]>> GetEmbeddingsAsync(IEnumerable<string> texts)
    {
        try
        {
            var results = new List<float[]>();
            using (var http = new HttpClient { BaseAddress = new Uri("http://localhost:11434") })
            {
                foreach (var raw in texts ?? Enumerable.Empty<string>())
                {
                    var text = (raw ?? string.Empty).Trim();
                    if (string.IsNullOrEmpty(text))
                        throw new ArgumentException("Embedding input contains an empty string.");

                    // Ollama embeddings: { model, prompt }
                    var payload = new { model = "nomic-embed-text", prompt = text };
                    var reqJson = JsonSerializer.Serialize(payload);
                    using (var content = new StringContent(reqJson, Encoding.UTF8, "application/json"))
                    using (var resp = await http.PostAsync("/api/embeddings", content))
                    {
                        if (!resp.IsSuccessStatusCode)
                        {
                            var err = await resp.Content.ReadAsStringAsync();
                            throw new Exception("Ollama embeddings failed: " + resp.StatusCode + " " + err);
                        }

                        var json = await resp.Content.ReadAsStringAsync();
                        using (var doc = JsonDocument.Parse(json))
                        {
                            if (!doc.RootElement.TryGetProperty("embedding", out var emb))
                                throw new Exception("Ollama did not return 'embedding'");

                            var vec = new List<float>();
                            foreach (var v in emb.EnumerateArray())
                                vec.Add((float)v.GetDouble());

                            if (vec.Count == 0)
                                throw new Exception("Received empty embedding vector.");

                            results.Add(vec.ToArray());
                        }
                    }
                }
            }

            if (results.Count == 0)
                throw new Exception("No embeddings were produced.");

            return results;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString());
            return new List<float[]>();
        }
    }

    /// <summary>
    /// Семантический поиск с доп. фильтром по содержимому документа (where_document).
    /// </summary>
    public async Task<List<string>> QueryAsync(
        string collectionId,
        string query,
        int n = 8,
        string mustContain1 = null,
        string mustContain2 = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(collectionId))
                throw new ArgumentException("collectionId is null/empty", nameof(collectionId));
            if (string.IsNullOrWhiteSpace(query))
                throw new ArgumentException("query must be a non-empty string", nameof(query));

            var queryEmbedding = await GetEmbeddingsAsync(new[] { query.Trim() });
            if (queryEmbedding == null || queryEmbedding.Count != 1 || queryEmbedding[0] == null || queryEmbedding[0].Length == 0)
                throw new InvalidOperationException("Failed to get a valid embedding for the query.");

            object whereDocument = null;
            var clauses = new List<Dictionary<string, object>>();
            if (!string.IsNullOrWhiteSpace(mustContain1))
                clauses.Add(new Dictionary<string, object> { { "$contains", mustContain1 } });
            if (!string.IsNullOrWhiteSpace(mustContain2))
                clauses.Add(new Dictionary<string, object> { { "$contains", mustContain2 } });

            if (clauses.Count == 1) whereDocument = clauses[0];
            else if (clauses.Count > 1) whereDocument = new Dictionary<string, object> { { "$and", clauses } };

            var payload = new Dictionary<string, object>
            {
                { "query_embeddings", queryEmbedding },
                { "n_results", Math.Max(3, n) },
                { "include", new[] { "documents", "distances" } }
            };
            if (whereDocument != null) payload["where_document"] = whereDocument;

            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync("/api/v1/collections/" + collectionId + "/query", content);
            var jsonStr = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                throw new Exception("Query failed: " + resp.StatusCode + "\n" + jsonStr);

            var docs = new List<string>();
            var dists = new List<double>();
            using (var doc = JsonDocument.Parse(jsonStr))
            {
                if (doc.RootElement.TryGetProperty("documents", out var documents))
                    foreach (var arr in documents.EnumerateArray())
                        foreach (var d in arr.EnumerateArray())
                            docs.Add(d.GetString() ?? string.Empty);

                if (doc.RootElement.TryGetProperty("distances", out var distances))
                    foreach (var arr in distances.EnumerateArray())
                        foreach (var d in arr.EnumerateArray())
                            dists.Add(d.GetDouble());
            }

            // Лёгкий реранк: ближе = лучше (distance меньше), +бонус за совпадение ключевых строк
            var scored = docs.Select((txt, i) =>
            {
                double baseScore = (i < dists.Count) ? -dists[i] : 0.0;
                double bonus = 0.0;
                if (!string.IsNullOrWhiteSpace(mustContain1) && txt.IndexOf(mustContain1, StringComparison.OrdinalIgnoreCase) >= 0) bonus += 1.0;
                if (!string.IsNullOrWhiteSpace(mustContain2) && txt.IndexOf(mustContain2, StringComparison.OrdinalIgnoreCase) >= 0) bonus += 1.0;
                return new { txt, score = baseScore + bonus };
            })
            .OrderByDescending(x => x.score)
            .Take(Math.Max(3, n))
            .Select(x => x.txt)
            .ToList();

            if (scored.Count == 0 && docs.Count > 0)
                return docs.Take(Math.Max(3, n)).ToList();

            return scored;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString());
            return new List<string>();
        }
    }
}
