using System;
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

        private void button1_Click(object sender, EventArgs e)
        {

            GetTaskAsync();

        }

        private async Task GetTaskAsync()
        {
            var json = File.ReadAllText("C:\\Users\\Daniil\\Downloads\\dv_schema_with_full_alias_final.json");
            var schema = JsonSerializer.Deserialize<DVSchema>(json);

            var chunks = ChunkBuilder.BuildChunks(schema);

            var chroma = new ChromaDirect();
            var collectionId = await chroma.EnsureCollectionAsync("dv_schema");

            // Загружаем schema в Chroma
            await chroma.UpsertAsync(collectionId, chunks.Select((c, i) => ($"doc_{i}", c.Content)).ToList());

            // Делаем запрос
            var relevant = await chroma.QueryAsync(collectionId, textBox1.Text, 3);
            var context = string.Join("\n---\n", relevant);

            var ollama = new OllamaClient();
            var answer = await ollama.AskAsync(textBox1.Text, context);

            textBox2.Text = "\nОтвет:\n" + answer;
        }
    }
}
