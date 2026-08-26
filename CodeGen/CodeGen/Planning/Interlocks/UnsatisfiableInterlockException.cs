using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeGen.Translation.Interlocks
{
    /// An authored safety rule that can never fire.
    ///
    /// VueOne writes one ConditionGroup as a CONJUNCTION, and the runtime evaluator reads ONE state per
    /// source, so a group naming the same component at two different stops demands that component be in
    /// two places at once. Every alternative built from that group is dead, and an actuator whose only
    /// alternative is dead moves freely while the model, the rule table and the HMI all say it is
    /// guarded.
    ///
    /// The compiler will not reinterpret it. Reading the AND as an OR invents a guard; dropping one term
    /// invents a different one. Both silently change what the plant is permitted to do. So the run stops
    /// and says exactly which model edit makes the twin mean what it appears to mean.
    public sealed class UnsatisfiableInterlockException : InvalidOperationException
    {
        public string Actuator { get; }
        public string State { get; }
        public string SourceComponent { get; }

        public UnsatisfiableInterlockException(
            string actuator, string state, string source,
            IReadOnlyList<(string Condition, int SettlesAt)> terms)
            : base(Describe(actuator, state, source, terms))
        {
            Actuator = actuator;
            State = state;
            SourceComponent = source;
        }

        static string Describe(string actuator, string state, string source,
                               IReadOnlyList<(string Condition, int SettlesAt)> terms) =>
            $"UNSATISFIABLE INTERLOCK — generation ABORTED before anything was written.{Environment.NewLine}" +
            $"  Actuator : {actuator}{Environment.NewLine}" +
            $"  State    : {state}{Environment.NewLine}" +
            $"  Guard    : " + string.Join(" AND ", terms.Select(t => $"{t.Condition} (settles at {t.SettlesAt})")) +
            Environment.NewLine +
            $"  Why      : these sit in ONE ConditionGroup, which VueOne writes as a conjunction, so they " +
            $"require '{source}' to be at {string.Join(" and ", terms.Select(t => t.SettlesAt).Distinct())} " +
            $"simultaneously. It can only ever report one. The rule would be emitted with a non-zero " +
            $"count and could never fire, leaving '{actuator}' guarded by nothing on this move while the " +
            $"model, the rule table and the panel all say it is guarded." + Environment.NewLine +
            $"  Fix      : in VueOne, open {actuator} → state '{state}' → its interlock, and put each " +
            $"'{source}' condition in its OWN ConditionGroup. Separate groups are ALTERNATIVES, which is " +
            "what a guard naming two ends of one axis means: block when the source is at either stop." +
            Environment.NewLine +
            "  Note     : the compiler will not reinterpret the AND as an OR, and will not drop a term " +
            "to make the guard fire — either would invent a safety rule the twin never stated.";
    }
}
