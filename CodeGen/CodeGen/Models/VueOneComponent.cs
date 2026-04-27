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
        public string NameTag { get; set; } = "Name";
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

        public List<VueOneTransition> Transitions { get; set; } = new();
    }

    public class VueOneTransition
    {
        public string TransitionID { get; set; } = string.Empty;
        public string OriginStateID { get; set; } = string.Empty;
        public string DestinationStateID { get; set; } = string.Empty;
        public int Priority { get; set; }
        public List<VueOneCondition> Conditions { get; set; } = new();
    }

    public class VueOneCondition
    {
        public string ID { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string ComponentID { get; set; } = string.Empty;
        public string Operator { get; set; } = string.Empty;
    }
}
