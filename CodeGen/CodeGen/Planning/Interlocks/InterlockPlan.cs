using System.Collections.Generic;
using System.Linq;

namespace CodeGen.Translation.Interlocks
{
    // Exactly the rules the twin states, in the order it states them. Nothing here is sized to a capacity:
    // the capacity is derived FROM the plans, so a rule can never be dropped to make one fit.
    //
    // Rows are a flattened sum of products. A row with TermCount >= 1 HEADS an alternative and the next
    // TermCount-1 rows are the rest of its terms, which must hold together; a continuation row carries 0.
    // The table blocks when any one alternative holds wholly. From/To repeat on every row of an
    // alternative, so a reader that only wants "which transition, blocked by what" still gets one line
    // per term.
    public sealed record InterlockPlan(
        IReadOnlyList<int> From, IReadOnlyList<int> To,
        IReadOnlyList<int> Src, IReadOnlyList<int> Blocked,
        IReadOnlyList<int> TermCount)
    {
        public int Count => From.Count;

        public static readonly InterlockPlan Empty = new(
            System.Array.Empty<int>(), System.Array.Empty<int>(),
            System.Array.Empty<int>(), System.Array.Empty<int>(), System.Array.Empty<int>());

        public IEnumerable<InterlockPlanner.Alternative> Alternatives()
        {
            for (int i = 0; i < Count; i++)
            {
                if (TermCount[i] < 1) continue;
                var terms = Enumerable.Range(i, TermCount[i])
                    .Select(t => new InterlockPlanner.Term(Src[t], Blocked[t])).ToList();
                yield return new InterlockPlanner.Alternative(From[i], To[i], terms);
            }
        }

        // Accumulates alternatives with dedup, as the planner and both post-filters need. It has no
        // capacity: the deployed arrays are sized to the plans, never the plans trimmed to the arrays.
        public sealed class Builder
        {
            private readonly List<InterlockPlanner.Alternative> _alternatives = new();

            public void Add(InterlockPlanner.Alternative alternative)
            {
                if (alternative.Terms.Count == 0) return;
                if (_alternatives.Any(a => Same(a, alternative))) return;
                _alternatives.Add(alternative);
            }

            private static bool Same(InterlockPlanner.Alternative a, InterlockPlanner.Alternative b) =>
                a.From == b.From && a.To == b.To && a.Terms.SequenceEqual(b.Terms);

            public InterlockPlan ToPlan()
            {
                var from = new List<int>();
                var to = new List<int>();
                var src = new List<int>();
                var blocked = new List<int>();
                var terms = new List<int>();
                foreach (var alternative in _alternatives)
                    for (int t = 0; t < alternative.Terms.Count; t++)
                    {
                        from.Add(alternative.From);
                        to.Add(alternative.To);
                        src.Add(alternative.Terms[t].Src);
                        blocked.Add(alternative.Terms[t].Blocked);
                        terms.Add(t == 0 ? alternative.Terms.Count : 0);
                    }
                return new InterlockPlan(from, to, src, blocked, terms);
            }
        }
    }
}
