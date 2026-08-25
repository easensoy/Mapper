using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Models;

namespace CodeGen.Domain.Twin
{
    // The digital twin, resolved once at construction, so every later question is an index lookup.
    // Model facts only - slots, ids, CAT types and controllers belong to the plan.
    public sealed class TwinModel
    {
        private readonly Dictionary<string, TwinComponent> _byId;
        private readonly Dictionary<string, TwinComponent> _byName;

        public IReadOnlyList<TwinComponent> Components { get; }
        public IReadOnlyList<TwinComponent> Processes { get; }

        private TwinModel(IReadOnlyList<TwinComponent> components,
                          Dictionary<string, TwinComponent> byId,
                          Dictionary<string, TwinComponent> byName)
        {
            Components = components;
            _byId = byId;
            _byName = byName;
            Processes = components.Where(c => c.IsProcess).ToList();
        }

        public TwinComponent? ById(string? componentId) =>
            string.IsNullOrEmpty(componentId) ? null
                : _byId.TryGetValue(componentId.Trim(), out var c) ? c : null;

        // Processes that COMMAND this component: its own transition naming a Process/State is the model
        // stating that process state issues the driving command. The strong relationship, since a
        // component runs on the controller that commands it.
        public IReadOnlyList<TwinComponent> CommandingProcesses(TwinComponent component)
        {
            if (component is null || component.IsProcess) return Array.Empty<TwinComponent>();
            var found = new List<TwinComponent>();
            foreach (var st in component.States)
                foreach (var tr in st.Transitions)
                    foreach (var c in tr.Leaves)
                        if (c.Component.IsProcess && !found.Any(f => ReferenceEquals(f, c.Component)))
                            found.Add(c.Component);
            return found;
        }

        // Processes that merely OBSERVE this component. Weaker than a command, since a process routinely
        // watches hardware another controller drives, so it only places a component nothing commands.
        public IReadOnlyList<TwinComponent> ObservingProcesses(TwinComponent component)
        {
            if (component is null || component.IsProcess) return Array.Empty<TwinComponent>();
            var found = new List<TwinComponent>();
            foreach (var proc in Processes)
                foreach (var st in proc.States)
                    foreach (var tr in st.Transitions)
                        foreach (var c in tr.Leaves)
                            if (ReferenceEquals(c.Component, component) &&
                                !found.Any(f => ReferenceEquals(f, proc)))
                                found.Add(proc);
            return found;
        }

        // The inverse of CommandingProcesses, and what anchors a process to a controller when one is pinned.
        public IReadOnlyList<TwinComponent> CommandedBy(TwinComponent process) =>
            process is null || !process.IsProcess
                ? Array.Empty<TwinComponent>()
                : Components.Where(c => !c.IsProcess &&
                        CommandingProcesses(c).Any(p => ReferenceEquals(p, process)))
                    .ToList();

        public TwinComponent? ByName(string? name) =>
            string.IsNullOrWhiteSpace(name) ? null
                : _byName.TryGetValue(name.Trim(), out var c) ? c : null;

        // A reference that does not close is a model error, not an absence: the guard would otherwise
        // be dropped without a word.
        internal TwinRef? Resolve(VueOneCondition? condition, string site, List<string> problems)
        {
            if (condition == null) return null;
            var component = ById(condition.ComponentID);
            if (component == null)
            {
                problems.Add($"{site} references ComponentID '{condition.ComponentID}'" +
                             $"{Describe(condition)}, which Control.xml does not declare.");
                return null;
            }
            var state = component.StateById(condition.ID);
            if (state == null)
            {
                problems.Add($"{site} references StateID '{condition.ID}'{Describe(condition)} on " +
                             $"'{component.Name}', which declares no such state.");
                return null;
            }
            return new TwinRef(component, state);
        }

        private static string Describe(VueOneCondition c) =>
            string.IsNullOrWhiteSpace(c.Name) ? string.Empty : $" (\"{c.Name}\")";

        public static TwinModel Build(IReadOnlyList<VueOneComponent> components)
        {
            if (components == null) throw new ArgumentNullException(nameof(components));

            var problems = new List<string>();
            var byId = new Dictionary<string, TwinComponent>(StringComparer.OrdinalIgnoreCase);
            var byName = new Dictionary<string, TwinComponent>(StringComparer.OrdinalIgnoreCase);
            var built = new List<TwinComponent>(components.Count);

            foreach (var source in components)
            {
                var component = TwinComponent.Build(source, problems);
                built.Add(component);

                var id = (source.ComponentID ?? string.Empty).Trim();
                if (id.Length > 0 && !byId.TryAdd(id, component))
                    problems.Add($"two components share ComponentID '{id}': " +
                                 $"'{byId[id].Name}' and '{component.Name}'.");

                var name = (source.Name ?? string.Empty).Trim();
                if (name.Length > 0 && !byName.TryAdd(name, component))
                    problems.Add($"two components share the name '{name}'; a reference to it is ambiguous.");
            }

            var model = new TwinModel(built, byId, byName);

            // Resolved against the finished index, so a condition may name any component in any order.
            foreach (var component in built)
                component.ResolveReferences(model, problems);

            if (problems.Count > 0)
                throw new InvalidOperationException(
                    $"[Twin] Control.xml is not a valid model ({problems.Count} problem(s)):" +
                    Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ",
                        problems.OrderBy(p => p, StringComparer.Ordinal)));

            return model;
        }
    }

    public sealed record TwinRef(TwinComponent Component, TwinState State);

    public sealed class TwinComponent
    {
        private readonly Dictionary<string, TwinState> _byStateId;
        private readonly Dictionary<string, TwinState> _byStateName;

        public VueOneComponent Source { get; }
        public string Id { get; }
        public string Name { get; }
        public string Type { get; }
        public IReadOnlyList<TwinState> States { get; }

        public bool IsProcess => string.Equals(Type, "Process", StringComparison.OrdinalIgnoreCase);
        public bool IsSensor => string.Equals(Type, "Sensor", StringComparison.OrdinalIgnoreCase);
        // Robots included: which CAT renders one is a generation decision, not a model fact.
        public bool IsActuator => !IsProcess && !IsSensor;

        private TwinComponent(VueOneComponent source, IReadOnlyList<TwinState> states,
                              Dictionary<string, TwinState> byStateId,
                              Dictionary<string, TwinState> byStateName)
        {
            Source = source;
            Id = (source.ComponentID ?? string.Empty).Trim();
            Name = (source.Name ?? string.Empty).Trim();
            Type = (source.Type ?? string.Empty).Trim();
            States = states;
            _byStateId = byStateId;
            _byStateName = byStateName;
        }

        public TwinState? StateById(string? stateId) =>
            string.IsNullOrEmpty(stateId) ? null
                : _byStateId.TryGetValue(stateId.Trim(), out var s) ? s : null;

        // Fallback for a condition naming its state rather than its id; first declaration wins.
        public TwinState? StateByName(string? name) =>
            string.IsNullOrWhiteSpace(name) ? null
                : _byStateName.TryGetValue(name.Trim(), out var s) ? s : null;

        internal static TwinComponent Build(VueOneComponent source, List<string> problems)
        {
            var byStateId = new Dictionary<string, TwinState>(StringComparer.OrdinalIgnoreCase);
            var byStateName = new Dictionary<string, TwinState>(StringComparer.OrdinalIgnoreCase);
            var states = new List<TwinState>(source.States.Count);

            foreach (var s in source.States)
            {
                var state = new TwinState(s);
                states.Add(state);

                var id = (s.StateID ?? string.Empty).Trim();
                if (id.Length > 0 && !byStateId.TryAdd(id, state))
                    problems.Add($"'{source.Name}' declares StateID '{id}' twice.");

                // A repeated state NAME is legal and occurs in the shipped twins; only the ID is identity.
                var name = (s.Name ?? string.Empty).Trim();
                if (name.Length > 0) byStateName.TryAdd(name, state);
            }

            return new TwinComponent(source, states, byStateId, byStateName);
        }

        internal void ResolveReferences(TwinModel model, List<string> problems)
        {
            foreach (var state in States)
            {
                foreach (var t in state.Transitions)
                {
                    t.Bind(this, StateById(t.Source.DestinationStateID));
                    if (t.Destination == null && !string.IsNullOrEmpty(t.Source.DestinationStateID))
                        problems.Add($"'{Name}' transition '{t.Source.TransitionID}' leaves state " +
                                     $"'{state.Name}' for StateID '{t.Source.DestinationStateID}', " +
                                     "which the component does not declare.");
                    t.BindConditions(model, $"'{Name}' state '{state.Name}' transition guard", problems);
                }
                state.BindInterlocks(model, $"'{Name}' state '{state.Name}' interlock", problems);
            }
        }
    }

    public sealed class TwinState
    {
        public VueOneState Source { get; }
        public string Id { get; }
        public string Name { get; }
        public int Number { get; }
        public IReadOnlyList<TwinTransition> Transitions { get; }

        // Resolved interlock guards; a dangling one is dropped, but the resolver reports it.
        public IReadOnlyList<TwinRef> Interlocks { get; private set; } = Array.Empty<TwinRef>();

        public ConditionExpr? InterlockGuard => Source.InterlockGuard;

        private Dictionary<VueOneCondition, TwinRef> _resolvedInterlocks = new();

        // The resolved target of one leaf of InterlockGuard.
        public TwinRef? ResolvedInterlock(VueOneCondition leaf) =>
            leaf != null && _resolvedInterlocks.TryGetValue(leaf, out var r) ? r : null;

        internal TwinState(VueOneState source)
        {
            Source = source;
            Id = (source.StateID ?? string.Empty).Trim();
            Name = (source.Name ?? string.Empty).Trim();
            Number = source.StateNumber;
            Transitions = source.Transitions.Select(t => new TwinTransition(t)).ToList();
        }

        internal void BindInterlocks(TwinModel model, string site, List<string> problems)
        {
            var bound = new List<TwinRef>();
            _resolvedInterlocks = new Dictionary<VueOneCondition, TwinRef>();
            foreach (var leaf in Source.InterlockConditions)
            {
                var resolved = model.Resolve(leaf, site, problems);
                if (resolved == null) continue;
                bound.Add(resolved);
                _resolvedInterlocks[leaf] = resolved;
            }
            Interlocks = bound;
        }
    }

    public sealed class TwinTransition
    {
        public VueOneTransition Source { get; }
        public TwinComponent? Owner { get; private set; }
        public TwinState? Destination { get; private set; }

        // Resolved leaves in guard order. This answers EXISTENCE questions ("does any leaf name X"),
        // where grouping cannot matter. A question about sequence or alternatives walks Guard.
        public IReadOnlyList<TwinRef> Leaves { get; private set; } = Array.Empty<TwinRef>();

        private Dictionary<VueOneCondition, TwinRef> _resolved = new();

        // The resolved target of one leaf of Guard.
        public TwinRef? Resolved(VueOneCondition leaf) =>
            leaf != null && _resolved.TryGetValue(leaf, out var r) ? r : null;

        // The guard as VueOne structured it. Conditions is its leaves in the same order, so a caller
        // that only needs the references never has to walk the tree.
        public ConditionExpr? Guard => Source.Guard;

        // The guard offers a choice the flat reference list cannot express.
        public bool HasAlternatives => Source.Guard?.HasAlternatives == true;

        internal TwinTransition(VueOneTransition source)
        {
            Source = source;
        }

        internal void Bind(TwinComponent owner, TwinState? destination)
        {
            Owner = owner;
            Destination = destination;
        }

        internal void BindConditions(TwinModel model, string site, List<string> problems)
        {
            var leaves = new List<TwinRef>();
            var index = new Dictionary<VueOneCondition, TwinRef>();
            foreach (var c in Source.Guard?.References() ?? (IReadOnlyList<VueOneCondition>)Array.Empty<VueOneCondition>())
            {
                var r = model.Resolve(c, site, problems);
                if (r == null) continue;
                leaves.Add(r);
                index[c] = r;
            }
            Leaves = leaves;
            _resolved = index;
        }
    }
}
