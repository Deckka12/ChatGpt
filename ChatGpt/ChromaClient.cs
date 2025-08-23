

using System.Text.Json;
using System.Text;
using System.Net.Http;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;
using OpenAI.Managers;
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
            var response = await _http.GetAsync("/api/v1/collections");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            foreach (var c in doc.RootElement.EnumerateArray())
            {
                if (c.GetProperty("name").GetString() == name)
                    return c.GetProperty("id").GetString();
            }

            var payload = new { name = name };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var createResponse = await _http.PostAsync("/api/v1/collections", content);
            createResponse.EnsureSuccessStatusCode();

            var createdJson = await createResponse.Content.ReadAsStringAsync();
            using var createdDoc = JsonDocument.Parse(createdJson);
            return createdDoc.RootElement.GetProperty("id").GetString();
        }
        catch  (Exception ex)
        {
            MessageBox.Show(ex.ToString());
            return null;
        }
        
    }

    public async Task UpsertAsync(string collectionId, List<(string Id, string Text)> documents)
    {
        var embeddings = await GetEmbeddingsAsync(documents.Select(d => d.Text));

        if (embeddings == null || embeddings.Count == 0 || embeddings.Any(e => e == null || e.Length == 0))
            throw new Exception("Embeddings пустые! Проверь Ollama embeddings endpoint.");

        var payload = new
        {
            ids = documents.Select(d => d.Id).ToArray(),
            documents = documents.Select(d => d.Text).ToArray(),
            embeddings = embeddings.ToArray(),
            metadatas = documents.Select(_ => new Dictionary<string, object>()).ToArray()
        };

        var debugJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine("Payload to Chroma:\n" + debugJson);

        var content = new StringContent(debugJson, Encoding.UTF8, "application/json");

        var response = await _http.PostAsync($"/api/v1/collections/{collectionId}/upsert", content);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception($"Upsert failed: {response.StatusCode}\n{error}");
        }
    }


    public async Task<List<float[]>> GetEmbeddingsAsync(IEnumerable<string> texts)
    {
        try
        {
            using (var http = new HttpClient())
            {
                http.BaseAddress = new Uri("http://localhost:11434");

                var results = new List<float[]>();

                foreach (var text in texts)
                {
                    var payload = new
                    {
                        model = "nomic-embed-text:latest",
                        input = text
                    };

                    var content = new StringContent(
                        JsonSerializer.Serialize(payload),
                        Encoding.UTF8,
                        "application/json");

                    var resp = await http.PostAsync("/api/embed", content);

                    if (!resp.IsSuccessStatusCode)
                    {
                        var err = await resp.Content.ReadAsStringAsync();
                        throw new Exception($"Ollama embeddings failed: {resp.StatusCode} {err}");
                    }

                    var json = await resp.Content.ReadAsStringAsync();
                    using (var doc = JsonDocument.Parse(json))
                    {
                        if (doc.RootElement.TryGetProperty("embeddings", out var embArray))
                        {
                            foreach (var emb in embArray.EnumerateArray())
                            {
                                var embedding = emb.EnumerateArray()
                                                   .Select(e => e.GetSingle())
                                                   .ToArray();
                                results.Add(embedding);
                            }
                        }
                        else
                        {
                            throw new Exception("Ollama did not return embeddings array");
                        }
                    }
                }

                return results;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString());
            return new List<float[]>();
        }
    }



    public async Task<List<string>> QueryAsync(string collectionId, string query, int n = 3)
    {
        try
        {
            // Получаем эмбеддинг для текста запроса
            var queryEmbedding = await GetEmbeddingsAsync(new[] { query });

            // Оборачиваем в список списков
            var payload = new
            {
                query_embeddings = queryEmbedding,  // например: [ [0.123, 0.456, ...] ]
                n_results = n
            };

            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var resp = await _http.PostAsync($"/api/v1/collections/{collectionId}/query", content);
            var jsonStr = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"Query failed: {resp.StatusCode}\n{jsonStr}");

            using var doc = JsonDocument.Parse(jsonStr);

            var result = new List<string>();
            if (doc.RootElement.TryGetProperty("documents", out var documents))
            {
                foreach (var docArr in documents.EnumerateArray())
                {
                    foreach (var d in docArr.EnumerateArray())
                    {
                        result.Add(d.GetString());
                    }
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString());
            return new List<string>();
        }
    }


}
