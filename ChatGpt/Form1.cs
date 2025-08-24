using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ChatGpt
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
        }

        private void textBox2_TextChanged(object sender, EventArgs e)
        {

        }
        private readonly OllamaClient _client = new OllamaClient(
            baseUrl: "http://localhost:11434",
            chatModel: "gemma3:4b",
            genModel: "gemma3:4b"
        );

        private void button1_Click(object sender, EventArgs e)
        {

            GetTaskAsync();

        }

        private string LoadDvContextIfAny()
        {
            // Простейший заготовок: если положишь рядом JSON со схемой — подставим как контекст
            var schemaJsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dv_schema.json");
            if (File.Exists(schemaJsonPath))
            {
                try
                {
                    var json = File.ReadAllText(schemaJsonPath);
                    // Можно отфильтровать и сделать краткий контекст (алиасы/типы полей и т.д.)
                    return "Схема Docsvision:\n" + json;
                }
                catch { /* игнорируем ошибки чтения */ }
            }
            return string.Empty;
        }

        private static readonly string SchemaPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
    "dv_schema_with_full_alias_final.json"  // перенеси сюда файл, либо поменяй путь как тебе удобно
);
        private static readonly string ChromaUrl = "http://localhost:8000";
        private static readonly string OllamaUrl = "http://localhost:11434";
        private static readonly string CollectionName = "dv_schema";

        private static bool _schemaIndexed = false;
        private static string _collectionId = null;
        private readonly ChromaDirect _chroma = new ChromaDirect(ChromaUrl);
        private readonly OllamaClient _ollama = new OllamaClient(
            baseUrl: OllamaUrl,
            chatModel: "gemma3:4b",
            genModel: "gemma3:4b",
            temperature: 0.1
        );

        private async Task GetTaskAsync()
        {
            button1.Enabled = false;
            try
            {
                // 0) Проверки окружения
                if (!await _ollama.IsAliveAsync())
                {
                    textBox2.Text = "Ollama не отвечает. Запусти `ollama serve` и подтяни модель: `ollama pull llama3.1`.";
                    return;
                }

                // 1) Готовим коллекцию в Chroma (один раз)
                if (string.IsNullOrWhiteSpace(_collectionId))
                    _collectionId = await _chroma.EnsureCollectionAsync(CollectionName);

                // 2) Индексируем схему только один раз за запуск
                if (!_schemaIndexed)
                {
                    if (!File.Exists(SchemaPath))
                    {
                        textBox2.Text = "Не найден файл схемы: " + SchemaPath;
                        return;
                    }

                    var json = File.ReadAllText(SchemaPath, Encoding.UTF8);
                    var schema = JsonSerializer.Deserialize<DVSchema>(json);
                    if (schema == null)
                    {
                        textBox2.Text = "Не удалось разобрать dv_schema JSON.";
                        return;
                    }

                    // Генерим строгие чанки
                    var chunks = ChunkBuilder.BuildChunks(schema);

                    // Upsert в Chroma (idempotent по Id — используем стабильные doc_* номера)
                    await _chroma.UpsertAsync(
                        _collectionId,
                        chunks.Select((c, i) => (Id: $"doc_{i:D6}", Text: c.Content)).ToList()
                    );

                    _schemaIndexed = true; // помечаем, что индекс уже загружен
                }

                // 3) Семантический поиск по вопросу
                var userQuestion = (textBox1.Text ?? "").Trim();
                if (string.IsNullOrWhiteSpace(userQuestion))
                {
                    textBox2.Text = "Введите запрос.";
                    return;
                }


                var relevant = await _chroma.QueryAsync(
       _collectionId,
       textBox1.Text,
       n: 8   // бери 5–12, чтобы хватало связных секций
   );

                // Если фильтры слишком узкие — ослабим и повторим
                if (relevant.Count == 0)
                {
                    relevant = await _chroma.QueryAsync(_collectionId, userQuestion, n: 12);
                    if (relevant.Count == 0)
                        relevant = await _chroma.QueryAsync(_collectionId, userQuestion, n: 12);
                }

                // 4) Формируем компактный контекст из top-K (слишком много = хуже)
                var k = Math.Min(8, relevant.Count);   // 5–8 обычно оптимально
                var context = string.Join("\n---\n", relevant);

                // 5) Системный промпт: максимально жёсткие рамки
                var systemPrompt =
                    "Ты помощник по SQL для схемы Docsvision. " +
                    "Строго используй ТОЛЬКО таблицы/колонки из CONTEXT. " +
                    "Если требуемого поля нет в CONTEXT — явно напиши об этом и не выдумывай. " +
                    "Генерируй запросы для SQL Server.";

                // 6) Запрос к LLM
                var answer = await _ollama.AskAsync(userQuestion, context, systemPrompt);

                // 7) Вывод
                var sb = new StringBuilder();
                sb.AppendLine("Контекст (top " + k + "):");
                sb.AppendLine(context);
                sb.AppendLine();
                sb.AppendLine("Ответ:");
                sb.AppendLine(answer);

                textBox2.Text = sb.ToString();
            }
            catch (Exception ex)
            {
                textBox2.Text = "Ошибка: " + ex.Message;
            }
            finally
            {
                button1.Enabled = true;
            }
        }



    }
}
