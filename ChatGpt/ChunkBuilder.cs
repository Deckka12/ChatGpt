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

            if (schema?.sections == null || schema.sections.Values == null)
                return result;

            var groupedByCard = schema.sections.Values
                .Where(s => s != null) // защитимся от null-секций
                .GroupBy(s => string.IsNullOrWhiteSpace(s.card_type_alias) ? "UnknownCardType" : s.card_type_alias.Trim())
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var cardGroup in groupedByCard)
            {
                var first = cardGroup.FirstOrDefault();
                var cardTypeId = string.IsNullOrWhiteSpace(first?.card_type_id)
                    ? "00000000-0000-0000-0000-000000000000"
                    : first.card_type_id;

                var tableName = $"dvtable_{{{cardTypeId}}}";
                var columns = CollectColumns(cardGroup);
                var ordered = OrderColumns(columns);

                var sb = new StringBuilder();
                sb.AppendLine($"CardType: {cardGroup.Key}");
                sb.AppendLine($"Table: {tableName}");
                sb.AppendLine("Key: InstanceID");
                sb.AppendLine("Columns (partial):");

                foreach (var c in ordered)
                {
                    var friendlyType = GuessFriendlyType(c.alias, c.type, c.max);
                    if (string.IsNullOrEmpty(friendlyType))
                        sb.AppendLine($"  - {Safe(c.alias)} (type={c.type}, max={c.max})");
                    else
                        sb.AppendLine($"  - {Safe(c.alias)} ({friendlyType})");
                }

                sb.AppendLine("Tip:");
                sb.AppendLine("  - Фильтровать по State без указания Section.");
                sb.AppendLine($"  - Пример: SELECT InstanceID FROM {tableName} WHERE State = '00000000-0000-0000-0000-000000000000';");

                var content = sb.ToString();
                if (!string.IsNullOrWhiteSpace(content))
                    result.Add((cardGroup.Key, content.Trim()));
            }

            return result;
        }

        public static void SaveChunksToFiles(DVSchema schema, string outputDir)
        {
            Directory.CreateDirectory(outputDir);

            var chunks = BuildChunks(schema)
                .Where(c => !string.IsNullOrWhiteSpace(c.CardType) && !string.IsNullOrWhiteSpace(c.Content))
                .ToList();

            foreach (var (cardType, content) in chunks)
            {
                var fileName = $"{SanitizeFileName(cardType)}.txt";
                File.WriteAllText(Path.Combine(outputDir, fileName), content, Encoding.UTF8);
            }
        }

        private static List<Field> CollectColumns(IGrouping<string, Section> cardGroup)
        {
            var dict = new Dictionary<string, Field>(StringComparer.OrdinalIgnoreCase)
            {
                // гарантируем наличие InstanceID
                ["InstanceID"] = new Field { alias = "InstanceID", type = 7, max = 0 }
            };

            foreach (var sec in cardGroup)
            {
                if (sec?.fields == null) continue;

                foreach (var f in sec.fields.Where(f => f != null))
                {
                    if (string.IsNullOrWhiteSpace(f.alias)) continue;
                    var alias = f.alias.Trim();

                    if (!dict.ContainsKey(alias))
                        dict[alias] = f;
                }
            }

            return dict.Values.ToList();
        }

        private static IEnumerable<Field> OrderColumns(List<Field> cols)
        {
            var keyOrder = new[] { "InstanceID", "State", "Name", "RegNumber" };

            var head = cols
                .Where(c => keyOrder.Any(k => k.Equals(c.alias, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(c => Array.IndexOf(keyOrder, keyOrder.First(k => k.Equals(c.alias, StringComparison.OrdinalIgnoreCase))));

            var tail = cols
                .Where(c => !keyOrder.Any(k => k.Equals(c.alias, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(c => c.alias ?? string.Empty, StringComparer.OrdinalIgnoreCase);

            return head.Concat(tail);
        }

        private static string GuessFriendlyType(string alias, int type, int max)
        {
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

        private static string Safe(string s) => s?.Trim() ?? string.Empty;

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) name = "Unknown";
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Trim();
        }
    }

}
