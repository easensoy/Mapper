using System.Collections.Generic;

namespace CodeGen.Models
{
    public class VueOneComponent
    {
        public string ComponentID { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        // VueOne <VcID> hardware marker; used ONLY to narrowly identify the real UR3e task arm
        // (TemplateMap.IsRobotTaskArm). Empty when the XML omits it.
        public string VcID { get; set; } = string.Empty;
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

        // State-level <Interlock_Condition> (NOT the transition Sequence_Condition): "block this
        // state's transition while <ComponentID> is in state <ID>".
        public ConditionExpr? InterlockGuard { get; set; }

        // The guard's leaves in document order. Assigning a bare list means "all of these".
        public IReadOnlyList<VueOneCondition> InterlockConditions
        {
            get => InterlockGuard?.References() ?? System.Array.Empty<VueOneCondition>();
            set => InterlockGuard = ConditionExpr.FromFlat(value);
        }
    }

    public class VueOneTransition
    {
        public string TransitionID { get; set; } = string.Empty;
        public string OriginStateID { get; set; } = string.Empty;
        public string DestinationStateID { get; set; } = string.Empty;
        public int Priority { get; set; }
        public ConditionExpr? Guard { get; set; }

        // The guard's leaves in document order. Assigning a bare list means "all of these".
        public IReadOnlyList<VueOneCondition> Conditions
        {
            get => Guard?.References() ?? System.Array.Empty<VueOneCondition>();
            set => Guard = ConditionExpr.FromFlat(value);
        }

        // VueOne transition <Type>: SINGLE (default), PARALLEL, or ALTERNATIVE. A state with both
        // PARALLEL and ALTERNATIVE outgoing transitions is a branched (13-state swivel) actuator.
        public string TransitionType { get; set; } = "SINGLE";
    }

    public class VueOneCondition
    {
        public string ID { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string ComponentID { get; set; } = string.Empty;
        public string Operator { get; set; } = string.Empty;
    }
}
