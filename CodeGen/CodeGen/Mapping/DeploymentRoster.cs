using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Translation;

namespace CodeGen.Mapping
{
    // Canonical row labels for the SMC rig syslay canvas; Y per row comes from the layout catalog.
    public enum LayoutRow
    {
        Boot,       // Bootstrap FBs (DPAC_FULLINIT / plcStart) — fixed coords, no column grid
        Floating,   // Top-of-canvas row — e.g. the MQTT connection
        Hmi,
        Station,    // Area / Station / Terminator
        Process,    // Processes AND sensors share this row
        Sensor,     // Alias for Process (same Y)
        Actuator,
    }

    // One component registration in SYSLAY (shared-canvas) coordinates.
    public sealed record ComponentEntry(
        string Name,
        PlcAssignment Plc,
        string Resource,
        int Column,
        LayoutRow Row,
        int X,
        int Y);

    // Which components this RUN deploys, where each one runs and where it is drawn — projected from
    // Config/layout.yml and the run's deployment profile. Per run, by value: nothing here is cached.
    public sealed class DeploymentRoster
    {
        private readonly Dictionary<string, ComponentEntry> _byName;

        public DeploymentProfile Profile { get; }

        public DeploymentRoster(DeploymentProfile profile)
        {
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
            var layout = profile.Layout;

            var revPiResource = ControllerMap.ResourceForPlc(PlcAssignment.RevPi);
            var rows = layout.BootFbs
                .Select(b => new ComponentEntry(b.Name, PlcAssignment.Unknown, string.Empty,
                    -1, LayoutRow.Boot, b.X, b.Y))
                .Concat(layout.Components.Select(e =>
                {
                    var band = layout.Band(e.Plc);
                    // A partial swap keeps the relocated component's canvas X/Y, so it still renders in the M262 band.
                    bool relocated = e.Plc == PlcAssignment.M262 && profile.RunsOnRevPi(e.Name);
                    return new ComponentEntry(e.Name,
                        relocated ? PlcAssignment.RevPi : e.Plc,
                        relocated ? revPiResource : ControllerMap.ResourceForPlc(e.Plc),
                        e.Column, Enum.Parse<LayoutRow>(e.Row, ignoreCase: true),
                        band.ColumnBaseX + e.Column * layout.Geometry.ColumnPitchX,
                        layout.RowY(e.Row));
                }));
            _byName = rows.ToDictionary(r => r.Name, r => r, StringComparer.Ordinal);

            // Both orderings come from the ONE declaration per component, so a name is written once.
            List<string> Ranked(Func<RosterEntry, bool> role) => layout.Components
                .Where(e => e.IdRank.HasValue && role(e))
                .OrderBy(e => e.IdRank!.Value)
                .Select(e => e.Name).ToList();
            IdOrderSensors = Ranked(e => IsRow(e, LayoutRow.Sensor) || IsRow(e, LayoutRow.Process));
            IdOrderActuators = Ranked(e => IsRow(e, LayoutRow.Actuator));
            CaSBusOrder = layout.Components
                .Where(e => e.CasBusRank.HasValue)
                .OrderBy(e => e.CasBusRank!.Value)
                .Select(e => e.Name).ToList();
        }

        private static bool IsRow(RosterEntry e, LayoutRow row) =>
            Enum.TryParse<LayoutRow>(e.Row, ignoreCase: true, out var r) && r == row;

        public IEnumerable<ComponentEntry> All => _byName.Values;

        public ComponentEntry? Get(string? name) =>
            string.IsNullOrEmpty(name) ? null : _byName.TryGetValue(name!, out var e) ? e : null;

        public bool Contains(string? name) => Get(name) != null;

        // TWO DIFFERENT ORDERS, both load-bearing, deliberately NOT merged. Id order assigns the state_table
        // index, the CAT actuator_id and every recipe Wait1Id; CaS-bus order chains the station adapter and
        // interleaves sensors with actuators. layout.yml documents what breaks if either is normalised.

        public IReadOnlyList<string> IdOrderSensors { get; }
        public IReadOnlyList<string> IdOrderActuators { get; }
        public IReadOnlyList<string> CaSBusOrder { get; }

        // Folds in every twin component the layout does not list. A layout row is an OVERRIDE that pins a
        // controller and a canvas cell for a component someone placed deliberately, not a permit: a component
        // the twin declares and the layout omits is placed with the process that drives it, in the next free
        // cell of that band's role row, walked in the twin's own declaration order.
        //
        // Placement is safety-relevant — the wrong controller means the wrong ring and the wrong recipe — so
        // it is resolved from the MODEL, and where the model does not decide it says so instead of choosing:
        // COMMAND (an actuator transition names Process/State) beats OBSERVATION (a process waits on a state),
        // and anything anchored nowhere falls to the profile's single declared default.
        internal static IReadOnlyDictionary<string, PlcAssignment> ResolvePlacement(
            Domain.Twin.TwinModel twin, Func<string, PlcAssignment> pinned, PlcAssignment defaultTarget)
        {
            var resolved = new Dictionary<string, PlcAssignment>(StringComparer.OrdinalIgnoreCase);
            var conflicts = new List<string>();

            // A process is anchored by the pinned components it commands; pinned to two controllers is a contradiction.
            var processTarget = new Dictionary<string, PlcAssignment>(StringComparer.OrdinalIgnoreCase);
            foreach (var process in twin.Processes)
            {
                var own = pinned(process.Name);
                if (own != PlcAssignment.Unknown) { processTarget[process.Name] = own; continue; }

                var anchors = twin.CommandedBy(process)
                    .Select(c => (Component: c.Name, Target: pinned(c.Name)))
                    .Where(a => a.Target != PlcAssignment.Unknown)
                    .ToList();
                var distinct = anchors.Select(a => a.Target).Distinct().ToList();
                if (distinct.Count > 1)
                {
                    conflicts.Add(
                        $"process '{process.Name}' commands components pinned to {distinct.Count} different " +
                        $"targets ({string.Join(", ", distinct)}): " +
                        string.Join(", ", anchors.Select(a => $"{a.Component}->{a.Target}")));
                    continue;
                }
                if (distinct.Count == 1) processTarget[process.Name] = distinct[0];
            }

            foreach (var kv in processTarget) resolved[kv.Key] = kv.Value;

            // A process that nothing anchors runs on the profile's declared default. Stated, not guessed.
            foreach (var process in twin.Processes)
                if (!resolved.ContainsKey(process.Name))
                    resolved[process.Name] = defaultTarget;

            foreach (var component in twin.Components.Where(c => !c.IsProcess))
            {
                if (pinned(component.Name) != PlcAssignment.Unknown)
                {
                    resolved[component.Name] = pinned(component.Name);
                    continue;
                }

                // Two commanders on different controllers must never resolve silently: the component would be
                // wired onto one ring and commanded from another.
                var commanders = twin.CommandingProcesses(component).Select(p => p.Name).ToList();
                var targets = commanders
                    .Select(n => resolved.TryGetValue(n, out var t) ? t : PlcAssignment.Unknown)
                    .Where(t => t != PlcAssignment.Unknown)
                    .Distinct().ToList();
                if (targets.Count > 1)
                {
                    conflicts.Add(
                        $"component '{component.Name}' is commanded by {commanders.Count} processes on " +
                        $"different targets ({string.Join(", ", targets)}): {string.Join(", ", commanders)}");
                    continue;
                }
                if (targets.Count == 1) { resolved[component.Name] = targets[0]; continue; }

                var observers = twin.ObservingProcesses(component)
                    .Select(p => resolved.TryGetValue(p.Name, out var t) ? t : PlcAssignment.Unknown)
                    .Where(t => t != PlcAssignment.Unknown)
                    .Distinct().ToList();
                // Observed from several controllers says nothing about where it RUNS, so it is not a conflict.
                resolved[component.Name] = observers.Count == 1 ? observers[0] : defaultTarget;
            }

            if (conflicts.Count > 0)
                throw new InvalidOperationException(
                    $"[Placement] {conflicts.Count} component(s) cannot be placed unambiguously: " +
                    string.Join("; ", conflicts) +
                    ". Pin the intended controller in Config/layout.yml, or correct the twin so one " +
                    "process commands each of them. Generation stops rather than choosing a controller.");
            return resolved;
        }

        public void PlaceUnlisted(Domain.Twin.TwinModel twin)
        {
            var layout = Profile.Layout;
            // Decided from the model's own command graph; ambiguity throws here, before any artefact exists.
            var ownership = ResolvePlacement(twin, Of, layout.DefaultTarget);

            var used = new HashSet<(PlcAssignment Plc, LayoutRow Row, int Column)>();
            foreach (var e in _byName.Values) used.Add((e.Plc, e.Row, e.Column));

            foreach (var c in twin.Components)
            {
                var name = c.Name.Trim();
                if (name.Length == 0 || _byName.ContainsKey(name)) continue;

                var plc = ownership.TryGetValue(name, out var t) ? t : PlcAssignment.Unknown;
                if (plc == PlcAssignment.Unknown)
                    throw new InvalidOperationException(
                        $"[Placement] '{name}' has no controller: nothing in the twin commands or observes " +
                        "it and the profile declares no default target. Pin it in Config/layout.yml.");

                var row = c.IsProcess ? LayoutRow.Process : c.IsSensor ? LayoutRow.Sensor : LayoutRow.Actuator;
                int column = 0;
                while (!used.Add((plc, row, column))) column++;

                var band = layout.Band(plc);
                _byName[name] = new ComponentEntry(name, plc, TargetRegistry.Of(plc).ResourceName,
                    column, row,
                    band.ColumnBaseX + column * layout.Geometry.ColumnPitchX,
                    layout.RowY(row.ToString()));
            }
        }

        private PlcAssignment Of(string name) =>
            _byName.TryGetValue(name, out var e) ? e.Plc : PlcAssignment.Unknown;
    }
}
