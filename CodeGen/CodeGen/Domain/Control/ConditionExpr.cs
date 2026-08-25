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

        // The guard as a SUM OF PRODUCTS: alternatives, each a list of leaves that hold together. An
        // interlock table is exactly that shape - it blocks when ANY alternative is wholly satisfied -
        // so this is the form the interlock planner compiles against. Exhaustive over the three node
        // kinds, so a fourth cannot be added without the compiler pointing here.
        //
        // Distributing All over Any is what makes it canonical and is also the only place it can grow:
        // (A|B) AND (C|D) is four products. A guard that expands past the cap is REFUSED rather than
        // truncated, because a dropped product is a lifted interlock - silent, and wrong on a rig.
        public IReadOnlyList<IReadOnlyList<Ref>> SumOfProducts(int maxProducts = 64)
        {
            var products = Distribute(this, maxProducts);
            if (products.Count > maxProducts) throw TooMany(products.Count, maxProducts);
            return products;
        }

        private static List<IReadOnlyList<Ref>> Distribute(ConditionExpr e, int cap) => e switch
        {
            Ref r => new List<IReadOnlyList<Ref>> { new[] { r } },
            Any any => any.Operands.SelectMany(o => Distribute(o, cap)).ToList(),
            All all => all.Operands.Aggregate(
                new List<IReadOnlyList<Ref>> { Array.Empty<Ref>() },
                (acc, operand) => Cross(acc, Distribute(operand, cap), cap)),
            _ => throw new InvalidOperationException(
                $"[Guard] {e.GetType().Name} is a guard node this compiler has no reading for."),
        };

        // Document order on both axes: the left operand's alternatives vary slowest, and within one
        // product the leaves stay in the order the twin wrote them.
        private static List<IReadOnlyList<Ref>> Cross(
            List<IReadOnlyList<Ref>> left, List<IReadOnlyList<Ref>> right, int cap)
        {
            if ((long)left.Count * right.Count > cap) throw TooMany(left.Count * right.Count, cap);
            var crossed = new List<IReadOnlyList<Ref>>(left.Count * right.Count);
            foreach (var l in left)
                foreach (var r in right)
                    crossed.Add(l.Concat(r).ToList());
            return crossed;
        }

        private static InvalidOperationException TooMany(long got, int cap) =>
            new($"[Guard] this guard expands to {got} alternatives, more than the {cap} a plan may " +
                "carry. Nothing here can drop one without changing what the model asks for.");

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
