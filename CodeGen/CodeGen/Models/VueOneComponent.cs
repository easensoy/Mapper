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

        /// <summary>
        /// State-level &lt;Interlock_Condition&gt; entries (VueOne stores
        /// actuator interlocks here, NOT in the transition Sequence_Condition).
        /// Each is a "block this state's transition while &lt;ComponentID&gt;
        /// is in state &lt;ID&gt;" guard, translated to InterlockManager
        /// Rule* arrays by SystemInjector.BuildInterlockRules.
        /// </summary>
        public List<VueOneCondition> InterlockConditions { get; set; } = new();
    }

    public class VueOneTransition
    {
        public string TransitionID { get; set; } = string.Empty;
        public string OriginStateID { get; set; } = string.Empty;
        public string DestinationStateID { get; set; } = string.Empty;
        public int Priority { get; set; }
        public List<VueOneCondition> Conditions { get; set; } = new();

        /// <summary>
        /// VueOne's &lt;Type&gt; on a transition: SINGLE (default), PARALLEL,
        /// or ALTERNATIVE. A resting state with both PARALLEL and ALTERNATIVE
        /// outgoing transitions is a branched actuator (e.g. Bearing_PnP's
        /// 7+6 swivel — PARALLEL → Assembly branch, ALTERNATIVE → Disassembly
        /// branch). Used by validators to assign Seven_State_Actuator_CAT.fbt
        /// to 13-state branched actuators.
        /// </summary>
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
