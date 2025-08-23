using System;
using System.Collections.Generic;

namespace ChatGpt
{
    public class DVSchema
    {
        public Dictionary<string, Section> sections { get; set; }
    }

    public class Section
    {
        public string alias { get; set; }
        public string card_type_id { get; set; }
        public string card_type_alias { get; set; }
        public List<Field> fields { get; set; }
    }

    public class Field
    {
        public string field_id { get; set; }
        public string alias { get; set; }
        public string section_alias { get; set; }
        public int type { get; set; }
        public int max { get; set; }
        public bool is_dynamic { get; set; }
        public bool is_extended { get; set; }
        public bool is_new { get; set; }
    }

}
