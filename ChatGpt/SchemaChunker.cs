using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ChatGpt
{
    public static class SchemaChunker
    {
        //public static List<string> CreateChunks(string pathToJson)
        //{
        //    var json = File.ReadAllText(pathToJson);
        //    var schema = JsonSerializer.Deserialize<DVSchema>(json);

        //    // Сохранить чанки в папку "chunks"
        //    ChunkBuilder.SaveChunksToFiles(schema, "chunks");

        //    // Или просто получить список строк
        //    var chunks = ChunkBuilder.BuildChunks(schema);

        //    //// Группируем секции по card_type_alias
        //    //var grouped = schema.sections.Values
        //    //    .GroupBy(s => s.card_type_alias ?? "UnknownCardType");

        //    //foreach (var group in grouped)
        //    //{
        //    //    var sb = new StringBuilder();
        //    //    sb.AppendLine($"CardType: {group.Key}");

        //    //    foreach (var section in group)
        //    //    {
        //    //        sb.AppendLine($"  Section: {section.alias}");

        //    //        foreach (var field in section.fields)
        //    //        {
        //    //            sb.AppendLine($"    - {field.alias} (type={field.type}, max={field.max})");
        //    //        }
        //    //    }

        //    //    chunks.Add(sb.ToString());
        //    //}

        //    return chunks;

        //}
    }
}
