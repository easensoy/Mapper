using System.Collections.Generic;

namespace VueOneMapper.Models
{
    /// <summary>
    /// Represents a VueOne component extracted from Control.xml
    /// </summary>
    public class VueOneComponent
    {
        public string ComponentID { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Type { get; set; }
        public List<VueOneState> States { get; set; } = new List<VueOneState>();
    }

    /// <summary>
    /// Represents a state within a VueOne component
    /// </summary>
    public class VueOneState
    {
        public string StateID { get; set; }
        public string Name { get; set; }
        public int StateNumber { get; set; }
        public bool InitialState { get; set; }
        public int Time { get; set; }
        public double Position { get; set; }
        public int Counter { get; set; }
        public bool StaticState { get; set; }
    }
}