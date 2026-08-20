using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeGen.Models
{
    // A VueOne guard as VueOne actually structures it. Control.xml nests
    // Sequence_Condition/Interlock_Condition -> ConditionValue -> ConditionGroup* -> Condition*, so a
    // guard is a sum of products: the groups are alternatives, the conditions inside one hold together.
    //
    // WHAT VUEONE SUPPLIES, measured across all four shipped twins: references, and those two nesting
    // levels. ConditionGroup@Operator is always empty and Condition@Operator carries only the kind
    // marker ("" on a Sequence_Condition, "-" on an Interlock_Condition), so the schema expresses NO
    // negation and no explicit operator. Nothing here invents one.
    public abstract record ConditionExpr
    {
        // Every leaf, in Control.xml document order. This is the lowering the recipe compiler and the
        // report graph consume; it is a projection of the tree, never a second copy of it.
        public IReadOnlyList<VueOneCondition> References()
        {
            var found = new List<VueOneCondition>();
            Collect(found);
            return found;
        }

        private protected abstract void Collect(List<VueOneCondition> into);

        public sealed record Ref(VueOneCondition Condition) : ConditionExpr
        {
            private protected override void Collect(List<VueOneCondition> into) => into.Add(Condition);
        }

        // Simultaneous: every operand holds. One ConditionGroup's conditions.
        public sealed record All(IReadOnlyList<ConditionExpr> Operands) : ConditionExpr
        {
            private protected override void Collect(List<VueOneCondition> into)
            {
                foreach (var o in Operands) o.Collect(into);
            }
        }

        // Alternative: any operand releases. The ConditionGroups of one ConditionValue.
        public sealed record Any(IReadOnlyList<ConditionExpr> Operands) : ConditionExpr
        {
            private protected override void Collect(List<VueOneCondition> into)
            {
                foreach (var o in Operands) o.Collect(into);
            }
        }

        // A single operand is that operand, so a plain one-condition guard is a bare Ref and callers
        // never have to unwrap a one-element node.
        public static ConditionExpr? Conjunction(IEnumerable<ConditionExpr> operands) =>
            Combine(operands, ops => new All(ops));

        public static ConditionExpr? Disjunction(IEnumerable<ConditionExpr> operands) =>
            Combine(operands, ops => new Any(ops));

        private static ConditionExpr? Combine(
            IEnumerable<ConditionExpr> operands, Func<IReadOnlyList<ConditionExpr>, ConditionExpr> node)
        {
            var ops = operands?.Where(o => o != null).ToList() ?? new List<ConditionExpr>();
            return ops.Count switch { 0 => null, 1 => ops[0], _ => node(ops) };
        }

        // A guard written as a bare condition list means "all of these": the shape a caller supplies
        // when it has no grouping to preserve.
        public static ConditionExpr? FromFlat(IEnumerable<VueOneCondition>? conditions) =>
            Conjunction((conditions ?? Enumerable.Empty<VueOneCondition>()).Select(c => new Ref(c)));

        // True when the guard offers alternatives, i.e. more than one ConditionGroup. Reported rather
        // than silently flattened, because a list of references cannot express a choice.
        public bool HasAlternatives => this switch
        {
            Any a => a.Operands.Count > 1 || a.Operands.Any(o => o.HasAlternatives),
            All l => l.Operands.Any(o => o.HasAlternatives),
            _ => false,
        };
    }
}
