using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ChatGpt
{
    public static class ChunkBuilder
    {
        /// <summary>
        /// Строит строго структурированные чанки по схеме.
        /// Формат каждого чанка:
        /// TABLE: dvtable_{SectionId}
        /// CARD_TYPE: {card_type_alias}
        /// CARD_TYPE_ID: {card_type_id}
        /// SECTION: {section_alias}
        /// SECTION_ID: {section_id}
        /// PRIMARY_KEY: InstanceID GUID
        /// COLUMNS:
        /// - Name TYPE [длина]
        /// QUERIES:
        /// - ...
        /// </summary>
        public static List<(string CardType, string Content)> BuildChunks(DVSchema schema)
        {
            var result = new List<(string, string)>();
            if (schema == null || schema.sections == null || schema.sections.Count == 0)
                return result;

            // группируем секции по типу карточки
            var groupedByCard = schema.sections
                .Where(kv => kv.Value != null)
                .GroupBy(
                    kv => SafeStr(kv.Value.card_type_alias, "UnknownCardType"),
                    StringComparer.OrdinalIgnoreCase
                )
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var card in groupedByCard)
            {
                var first = card.First().Value;
                var cardTypeAlias = card.Key;
                var cardTypeId = SafeStr(first.card_type_id, "00000000-0000-0000-0000-000000000000");

                foreach (var kv in card.OrderBy(kv => SafeStr(kv.Value.alias, ""), StringComparer.OrdinalIgnoreCase))
                {
                    var sectionId = kv.Key; // это именно SectionId из JSON
                    var sec = kv.Value;
                    var sectionAlias = SafeStr(sec.alias, "UnknownSection");

                    // Собираем поля только из этой секции + гарантируем InstanceID
                    var fields = CollectSectionColumns(sec);
                    if (fields.Count == 0) continue;

                    var ordered = OrderColumns(fields);

                    var sb = new StringBuilder();
                    sb.AppendLine("TABLE: dvtable_{" + sectionId + "}");
                    sb.AppendLine("CARD_TYPE: " + cardTypeAlias);
                    sb.AppendLine("CARD_TYPE_ID: " + cardTypeId);
                    sb.AppendLine("SECTION: " + sectionAlias);
                    sb.AppendLine("SECTION_ID: " + sectionId);
                    sb.AppendLine("PRIMARY_KEY: InstanceID GUID");
                    sb.AppendLine("COLUMNS:");

                    foreach (var f in ordered)
                    {
                        sb.AppendLine(FormatColumnLine(f));
                    }

                    // Примеры запросов — только по реально существующим полям в этой секции
                    sb.AppendLine("QUERIES:");
                    sb.AppendLine("- GetById: SELECT * FROM dvtable_{" + sectionId + "} WHERE InstanceID = @id;");

                    if (HasField(fields, "State"))
                        sb.AppendLine("- GetByState: SELECT InstanceID FROM dvtable_{" + sectionId + "} WHERE State = @state;");

                    if (HasField(fields, "Name"))
                        sb.AppendLine("- FindByName: SELECT TOP 50 InstanceID, Name FROM dvtable_{" + sectionId + "} WHERE Name LIKE @name;");

                    if (HasField(fields, "Created"))
                        sb.AppendLine("- RecentCreated: SELECT TOP 100 * FROM dvtable_{" + sectionId + "} WHERE Created >= @fromDate ORDER BY Created DESC;");

                    // Итоговый чанк
                    var content = sb.ToString().TrimEnd();

                    // Ключ чанка — CardType::Section (как и раньше удобно для фильтров)
                    var chunkKey = cardTypeAlias + "::" + sectionAlias;
                    result.Add((chunkKey, content));
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
                var fileName = SanitizeFileName(key) + ".txt";
                File.WriteAllText(Path.Combine(outputDir, fileName), content, Encoding.UTF8);
            }
        }

        // ----------------- helpers -----------------

        private static string SafeStr(string s, string fallback)
        {
            return string.IsNullOrWhiteSpace(s) ? fallback : s.Trim();
        }

        private static bool HasField(List<Field> fields, string alias)
        {
            return fields.Any(f => string.Equals(f.alias, alias, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Только поля текущей секции + InstanceID.</summary>
        private static List<Field> CollectSectionColumns(Section sec)
        {
            var list = new List<Field>
            {
                new Field { alias = "InstanceID", type = 7, max = 0 } // GUID
            };

            if (sec != null && sec.fields != null)
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

        /// <summary>Приоритет: ключевые поля впереди, затем по алфавиту.</summary>
        private static IEnumerable<Field> OrderColumns(List<Field> cols)
        {
            var keyOrder = new[] { "InstanceID", "State", "Name", "RegNumber", "Created", "CreatedBy" };

            var head = cols
                .Where(c => keyOrder.Any(k => k.Equals(c.alias, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(c => Array.IndexOf(keyOrder, keyOrder.First(k => k.Equals(c.alias, StringComparison.OrdinalIgnoreCase))));

            var tail = cols
                .Where(c => !keyOrder.Any(k => k.Equals(c.alias, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(c => c.alias, StringComparer.OrdinalIgnoreCase);

            return head.Concat(tail);
        }

        /// <summary>
        /// Человеко-читаемый тип с длиной/точностью. NVARCHAR(max) → NVARCHAR(MAX).
        /// Коды типов взяты из твоей схемы.
        /// </summary>
        private static string FormatType(string alias, int type, int max)
        {
            // маппинг по твоему проекту
            // 0-INT, 1-BIT, 2-DATETIME, 5-ENUM, 7-GUID, 9-NUMERIC, 10-NVARCHAR, 12-DECIMAL, 13/14-REF, 15-XML, 16-NVARCHAR, 20-DECIMAL
            switch (type)
            {
                case 0: return "INT";
                case 1: return "BIT";
                case 2: return "DATETIME";
                case 5: return "ENUM";
                case 7: return "GUID";
                case 9: return "NUMERIC";
                case 10: // NVARCHAR с длиной
                case 16:
                    return max > 0 ? "NVARCHAR(" + max + ")" : "NVARCHAR(MAX)";
                case 12:
                case 20:
                    return "DECIMAL(38,10)";
                case 13:
                case 14:
                    return "REF"; // внешняя ссылка/идентификатор
                case 15:
                    return "XML";
                default:
                    // эвристика: поля с именем ...Id и т.п. → GUID
                    if (!string.IsNullOrEmpty(alias))
                    {
                        if (alias.Equals("InstanceID", StringComparison.OrdinalIgnoreCase) ||
                            alias.Equals("State", StringComparison.OrdinalIgnoreCase) ||
                            alias.EndsWith("Id", StringComparison.OrdinalIgnoreCase))
                            return "GUID";
                    }
                    // по умолчанию NVARCHAR
                    return max > 0 ? "NVARCHAR(" + max + ")" : "NVARCHAR(MAX)";
            }
        }

        /// <summary>
        /// Форматирует строку для колонки. Для REF добавляет цель из references
        /// и, при необходимости, хинт по JOIN (для RefStaff.* → JOIN <Section>.RowID).
        /// </summary>
        private static string FormatColumnLine(Field f)
        {
            var typed = FormatType(f.alias, f.type, f.max);

            if ((f.type == 13 || f.type == 14) && f.references != null)
            {
                var target = BuildReferenceTarget(f.references);
                if (!string.IsNullOrWhiteSpace(target))
                {
                    var joinHint = BuildJoinHint(f.references) ?? string.Empty;
                    return "- " + f.alias + " " + typed + " \u2192 " + target + joinHint; // →
                }
            }

            return "- " + f.alias + " " + typed;
        }

        /// <summary>
        /// Строит читаемую цель для ссылки: сначала references.target,
        /// затем card_type_alias.section_alias, затем section_alias.
        /// </summary>
        private static string BuildReferenceTarget(Reference r)
        {
            if (r == null) return null;
            if (!string.IsNullOrWhiteSpace(r.target)) return r.target.Trim();

            var card = (r.card_type_alias ?? "").Trim();
            var sec = (r.section_alias ?? "").Trim();

            if (!string.IsNullOrEmpty(card) && !string.IsNullOrEmpty(sec))
                return card + "." + sec;

            if (!string.IsNullOrEmpty(sec))
                return sec;

            return null;
        }

        /// <summary>
        /// Возвращает JOIN‑хинт для известных справочников.
        /// Сейчас: для любых ссылок на RefStaff.* → (JOIN <SectionAlias>.RowID)
        /// </summary>
        private static string BuildJoinHint(Reference r)
        {
            if (r == null) return null;

            string card = (r.card_type_alias ?? "").Trim();
            string sec = (r.section_alias ?? "").Trim();
            string tgt = (r.target ?? "").Trim();

            bool isRefStaff =
                card.Equals("RefStaff", StringComparison.OrdinalIgnoreCase) ||
                tgt.StartsWith("RefStaff.", StringComparison.OrdinalIgnoreCase);

            if (isRefStaff)
            {
                // Имя секции: берём section_alias, иначе из target после точки
                string joinSection =
                    !string.IsNullOrEmpty(sec)
                        ? sec
                        : (tgt.Contains(".") ? tgt.Substring(tgt.IndexOf('.') + 1) : "Employees");

                return $" (JOIN {joinSection}.RowID)";
            }

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
