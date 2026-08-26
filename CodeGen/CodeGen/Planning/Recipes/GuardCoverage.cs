using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeGen.Translation.Process.Recipes
{
    // What became of one guard leaf. A leaf is one <Condition> the twin wrote inside a transition's
    // guard, and every one of them must land in exactly one of these - or generation stops. A warning
    // is not one of the outcomes: a control semantic that reaches nothing is not a note, it is a defect.
    public enum GuardLeafOutcome
    {
        // Lowered into one or more WAIT rows: the step genuinely waits for it.
        Waited,

        // The same requirement is already standing in this recipe - an earlier row established it, or a
        // sibling term in the same guard says the same thing. Adding a second row would change nothing.
        AlreadyRequired,

        // This state COMMANDS the movement the leaf observes, so the command's own arrival WAIT is the
        // requirement. A second wait on the same arrival would be the same row written twice.
        ProvedByOwnedCommand,

        // The reference is compiled the way the DEPLOYMENT declares it should be. Used where the twin
        // states something the plant answers rather than the recipe - a producer's boot readiness, or a
        // carrier that stands for a phase - and only ever where a declaration authorises it.
        SatisfiedByDeclaration,

        // The state holding it is not reachable from its process's entry, so it never executes and its
        // guard is never evaluated.
        Unreachable,

        // The leaf names the very process being compiled: the recipe is already there.
        SelfReference,
    }

    // What makes one guard leaf that leaf and no other.
    //
    // Every part of this is an identity the twin ASSIGNS, never a display name: two conditions may
    // legitimately carry the same Name (the twin names a condition after the state it references, and
    // two components can have states of the same name), so a name-keyed record silently loses one of
    // them. Ordinal is the leaf's structural position in its transition's guard, in document order,
    // which is what separates a guard that names the same state twice.
    public readonly record struct GuardLeafId(
        string ProcessId,
        string StateId,
        string TransitionId,
        string ConditionComponentId,
        string ConditionStateId,
        int Ordinal);

    // One guard leaf and what the compiler did with it. Identity is typed and ID-based; the names are
    // carried alongside so a refusal can be read by a person.
    public sealed record GuardLeaf(
        GuardLeafId Id,
        string Process, string State, string Condition,
        GuardLeafOutcome Outcome, string Why)
    {
        public override string ToString() =>
            $"'{Process}' state '{State}' condition '{Condition}' (leaf #{Id.Ordinal + 1} of transition {Id.TransitionId})";
    }

    // Every guard leaf the twin declares, and what became of it. The compiler records as it lowers; the
    // plan then proves the two sides correspond one-to-one, so a leaf cannot be dropped by a path
    // nobody remembered, nor accounted for by a decision about some other leaf.
    public sealed class GuardCoverage
    {
        private readonly Dictionary<GuardLeafId, GuardLeaf> _byId = new();

        public IReadOnlyCollection<GuardLeaf> Leaves => _byId.Values;

        // A leaf may be decided once and then REFINED - an enclosing guard can subsume a term its own
        // sub-pass already lowered. What may never happen is two DIFFERENT leaves sharing an identity;
        // AssertCovers proves that, over identities the twin declared.
        public void Record(GuardLeaf leaf) => _byId[leaf.Id] = leaf;

        public GuardLeaf? Find(GuardLeafId id) => _byId.GetValueOrDefault(id);

        // The declared leaves and the decided leaves must be the SAME set - no leaf undecided, no
        // decision about a leaf the model never wrote, and no two leaves sharing one identity.
        public void AssertCovers(IReadOnlyCollection<GuardLeaf> declared)
        {
            var collisions = declared.GroupBy(d => d.Id).Where(g => g.Count() > 1).ToList();
            if (collisions.Count > 0)
                throw new InvalidOperationException(
                    $"[Semantics] {collisions.Count} guard leaf identity/identities are claimed by more " +
                    "than one declared condition, so one of them would be accounted for by the other's " +
                    "decision: " + string.Join("; ", collisions.Take(8).Select(g =>
                        $"{g.Count()}x {g.First()}")) +
                    ". Generation stops rather than proving coverage against a key that cannot tell " +
                    "two conditions apart.");

            var declaredIds = declared.Select(d => d.Id).ToHashSet();

            var missing = declared.Where(d => !_byId.ContainsKey(d.Id)).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    $"[Semantics] {missing.Count} guard leaf/leaves the model declares reached no compiler " +
                    "decision, so whatever they asked for is simply absent from the generated project: " +
                    string.Join("; ", missing.Take(12).Select(m => m.ToString())) +
                    (missing.Count > 12 ? $" (+{missing.Count - 12} more)" : string.Empty) +
                    ". Generation stops rather than shipping a plant that ignores part of its own model.");

            var phantom = _byId.Values.Where(r => !declaredIds.Contains(r.Id)).ToList();
            if (phantom.Count > 0)
                throw new InvalidOperationException(
                    $"[Semantics] {phantom.Count} compiler decision(s) name a guard leaf the model does not " +
                    "declare, so coverage would be proved against something that is not in the twin: " +
                    string.Join("; ", phantom.Take(12).Select(p => p.ToString())) +
                    (phantom.Count > 12 ? $" (+{phantom.Count - 12} more)" : string.Empty) + ".");
        }
    }
}
