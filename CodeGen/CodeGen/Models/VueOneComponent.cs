using System.Collections.Generic;

namespace CodeGen.Models
{
    public class VueOneComponent
    {
        public string ComponentID { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public List<VueOneState> States { get; set; } = new();
    }

    public class VueOneState
    {
        public string StateID { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int StateNumber { get; set; }
        public bool InitialState { get; set; }
        public int Time { get; set; }
        public double Position { get; set; }
        public int Counter { get; set; }
        public bool StaticState { get; set; }
    }
}