using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ChatGpt
{
    public static class ChunkBuilder
    {
        public static List<(string CardType, string Content)> BuildChunks(DVSchema schema)
        {
            var result = new List<(string, string)>();
            if (schema?.sections == null) return result;

            // ИТЕРИРУЕМ по KeyValuePair, чтобы видеть ключ = SectionId
            var groupedByCard = schema.sections
                .Where(kv => kv.Value != null)
                .GroupBy(
                    kv => string.IsNullOrWhiteSpace(kv.Value.card_type_alias)
                        ? "UnknownCardType"
                        : kv.Value.card_type_alias.Trim(),
                    StringComparer.OrdinalIgnoreCase
                )
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var cardGroup in groupedByCard)
            {
                // GUID типа карточки один для всех её секций
                var cardTypeId = cardGroup.FirstOrDefault().Value?.card_type_id
                                 ?? "00000000-0000-0000-0000-000000000000";

                // Проходим по секциям ЭТОГО типа карточки
                foreach (var kv in cardGroup.OrderBy(kv => kv.Value.alias ?? "", StringComparer.OrdinalIgnoreCase))
                {
                    var sec = kv.Value;
                    var sectionId = kv.Key; // ключ словаря = SectionId из твоего JSON

                    var sectionAlias = string.IsNullOrWhiteSpace(sec.alias) ? "UnknownSection" : sec.alias.Trim();

                    // Собираем только поля этой секции
                    var cols = CollectSectionColumns(sec);
                    if (cols.Count == 0) continue;

                    var ordered = OrderColumns(cols);

                    // Формируем шапку в требуемом формате
                    var sb = new StringBuilder();
                    sb.AppendLine($"CardType: {cardGroup.Key}");
                    sb.AppendLine($"Section: {sectionAlias}");
                    sb.AppendLine($"CardTypeId: {cardTypeId}");
                    sb.AppendLine($"SectionId: {sectionId}");
                    sb.AppendLine($"Table: dvtable_{{{sectionId}}}"); // как просил: таблица = dvtable_{SectionId}
                    sb.AppendLine("Key: InstanceID");
                    sb.AppendLine("Columns (partial):");

                    foreach (var c in ordered)
                    {
                        var friendly = GuessFriendlyType(c.alias, c.type, c.max);
                        sb.AppendLine(string.IsNullOrEmpty(friendly)
                            ? $"  - {c.alias} (type={c.type}, max={c.max})"
                            : $"  - {c.alias} ({friendly})");
                    }

                    // Подсказка про State — только если в этой секции есть поле State
                    var hasState = cols.Any(c => c.alias.Equals("State", StringComparison.OrdinalIgnoreCase));
                    if (hasState)
                    {
                        sb.AppendLine("Tip:");
                        sb.AppendLine("  - Фильтровать по State без указания Section.");
                        sb.AppendLine($"  - Пример: SELECT InstanceID FROM dvtable_{{{cardTypeId}}} WHERE State = '00000000-0000-0000-0000-000000000000';");
                    }

                    var content = sb.ToString().TrimEnd();

                    // Ключ чанка — CardType::Section (удобно для фильтрации)
                    var key = $"{cardGroup.Key}::{sectionAlias}";
                    result.Add((key, content));
                }
            }

            return result;
        }

        public static void SaveChunksToFiles(DVSchema schema, string outputDir)
        {
            Directory.CreateDirectory(outputDir);
            var chunks = BuildChunks(schema);

            foreach (var (key, content) in chunks)
            {
                var fileName = $"{SanitizeFileName(key)}.txt";
                File.WriteAllText(Path.Combine(outputDir, fileName), content, Encoding.UTF8);
            }
        }

        // ---------- helpers ----------

        // Только поля текущей секции + гарантируем InstanceID
        private static List<Field> CollectSectionColumns(Section sec)
        {
            var list = new List<Field> { new Field { alias = "InstanceID", type = 7, max = 0 } };

            if (sec?.fields != null)
            {
                foreach (var f in sec.fields)
                {
                    if (f == null || string.IsNullOrWhiteSpace(f.alias)) continue;
                    if (!list.Any(x => x.alias.Equals(f.alias, StringComparison.OrdinalIgnoreCase)))
                        list.Add(f);
                }
            }
            return list;
        }

        // Приоритет: InstanceID, State, Name, RegNumber — затем алфавит
        private static IEnumerable<Field> OrderColumns(List<Field> cols)
        {
            var keyOrder = new[] { "InstanceID", "State", "Name", "RegNumber" };

            var head = cols
                .Where(c => keyOrder.Any(k => k.Equals(c.alias, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(c => Array.IndexOf(keyOrder, keyOrder.First(k => k.Equals(c.alias, StringComparison.OrdinalIgnoreCase))));

            var tail = cols
                .Where(c => !keyOrder.Any(k => k.Equals(c.alias, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(c => c.alias, StringComparer.OrdinalIgnoreCase);

            return head.Concat(tail);
        }

        // Аккуратный human‑friendly тип по коду DV
        private static string GuessFriendlyType(string alias, int type, int max)
        {
            var map = new Dictionary<int, string>
            {
                { 0, "INT" },
                { 1, "BIT" },
                { 2, "DATETIME" },
                { 5, "ENUM" },
                { 7, "GUID" },
                { 9, "NUMERIC" },
                { 10, "NVARCHAR" },
                { 12, "DECIMAL" },
                { 13, "REF" },
                { 14, "REF" },
                { 15, "XML" },
                { 16, "NVARCHAR" },
                { 20, "DECIMAL" }
            };

            if (map.TryGetValue(type, out var friendly))
                return friendly;

            if (!string.IsNullOrWhiteSpace(alias))
            {
                if (alias.Equals("InstanceID", StringComparison.OrdinalIgnoreCase) ||
                    alias.Equals("State", StringComparison.OrdinalIgnoreCase) ||
                    alias.EndsWith("Id", StringComparison.OrdinalIgnoreCase))
                    return "GUID";
            }

            if (max > 0) return "NVARCHAR";
            return null;
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) name = "Unknown";
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Trim();
        }
    }
}
