using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Models;

namespace CodeGen.Domain.Twin
{
    // The digital twin, resolved once. Control.xml names components and states by loose id strings;
    // every stage used to rescan the component list for them, which is how two stages came to answer
    // the same question differently. Resolution happens here, at construction, and every later question
    // is an index lookup. Model facts only - slots, ids, CAT types and controllers belong to the plan.
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

        // Trimmed, case-insensitive - the lookup every caller open-coded before.
        public TwinComponent? ById(string? componentId) =>
            string.IsNullOrEmpty(componentId) ? null
                : _byId.TryGetValue(componentId.Trim(), out var c) ? c : null;

        // First declaration wins where a name repeats, as FirstOrDefault gave before.
        // The process that DRIVES a component, read off the model the same way the recipe compiler reads
        // command ownership: an actuator transition whose condition names a Process/State is that process
        // stating it issues the command. A sensor has no owner in that sense, so it falls back to the
        // process that observes it. Null when nothing in the model refers to it.
        public TwinComponent? OwningProcess(TwinComponent component)
        {
            if (component is null || component.IsProcess) return null;
            // An actuator names its driver on its own transitions.
            foreach (var st in component.States)
                foreach (var tr in st.Transitions)
                    foreach (var c in tr.Conditions)
                        if (c.Component.IsProcess) return c.Component;
            // A sensor is named BY the process that waits on it.
            foreach (var proc in Processes)
                foreach (var st in proc.States)
                    foreach (var tr in st.Transitions)
                        foreach (var c in tr.Conditions)
                            if (ReferenceEquals(c.Component, component)) return proc;
            return null;
        }

        public TwinComponent? ByName(string? name) =>
            string.IsNullOrWhiteSpace(name) ? null
                : _byName.TryGetValue(name.Trim(), out var c) ? c : null;

        // The component and state a condition points at. A reference that does not close is a model
        // error, not an absence: the guard would otherwise be dropped without a word.
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
                // First wins, as the scans this replaces did; reported because a later reference is ambiguous.
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

    // A resolved pointer into the twin: the component a reference names and the state within it.
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

        internal TwinState(VueOneState source)
        {
            Source = source;
            Id = (source.StateID ?? string.Empty).Trim();
            Name = (source.Name ?? string.Empty).Trim();
            Number = source.StateNumber;
            Transitions = source.Transitions.Select(t => new TwinTransition(t)).ToList();
        }

        internal void BindInterlocks(TwinModel model, string site, List<string> problems) =>
            Interlocks = Source.InterlockConditions
                .Select(c => model.Resolve(c, site, problems))
                .Where(r => r != null).Select(r => r!).ToList();
    }

    public sealed class TwinTransition
    {
        public VueOneTransition Source { get; }
        public TwinComponent? Owner { get; private set; }
        public TwinState? Destination { get; private set; }

        public IReadOnlyList<TwinRef> Conditions { get; private set; } = Array.Empty<TwinRef>();

        internal TwinTransition(VueOneTransition source)
        {
            Source = source;
        }

        internal void Bind(TwinComponent owner, TwinState? destination)
        {
            Owner = owner;
            Destination = destination;
        }

        internal void BindConditions(TwinModel model, string site, List<string> problems) =>
            Conditions = Source.Conditions
                .Select(c => model.Resolve(c, site, problems))
                .Where(r => r != null).Select(r => r!).ToList();
    }
}
