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
        int Y,
        string ProcessOwner);

    // Which components this RUN deploys, where each one runs and where it is drawn — projected from
    // Config/layout.yml and the run's deployment profile.
    //
    // Per run, by value. The roster used to be a static keyed on a routing-mode token, which meant a second
    // generation in the same process could read the first one's partition, and two generations could not run
    // at once at all. Nothing here is shared or cached across runs.
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
                    -1, LayoutRow.Boot, b.X, b.Y, string.Empty))
                .Concat(layout.Components.Select(e =>
                {
                    var band = layout.Band(e.Plc);
                    // The partial swap relocates the named Feed components onto the RevPi resource, keeping
                    // their canvas X/Y so the Feed station still renders in the M262 band.
                    bool relocated = e.Plc == PlcAssignment.M262 && profile.RunsOnRevPi(e.Name);
                    return new ComponentEntry(e.Name,
                        relocated ? PlcAssignment.RevPi : e.Plc,
                        relocated ? revPiResource : ControllerMap.ResourceForPlc(e.Plc),
                        e.Column, Enum.Parse<LayoutRow>(e.Row, ignoreCase: true),
                        band.ColumnBaseX + e.Column * layout.Geometry.ColumnPitchX,
                        layout.RowY(e.Row),
                        e.Owner ?? string.Empty);
                }));
            _byName = rows.ToDictionary(r => r.Name, r => r, StringComparer.Ordinal);

            IdOrderSensors = layout.IdOrder.Sensors;
            // The discharge tail's reservations are always listed; a name the twin does not declare
            // simply resolves to nothing, so presence in the MODEL decides whether the tail exists.
            IdOrderActuators = layout.IdOrder.Actuators.Concat(layout.IdOrder.RobotTail).ToList();
            CaSBusOrder = layout.CasBusOrder;
        }

        public IEnumerable<ComponentEntry> All => _byName.Values;

        public ComponentEntry? Get(string? name) =>
            string.IsNullOrEmpty(name) ? null : _byName.TryGetValue(name!, out var e) ? e : null;

        public bool Contains(string? name) => Get(name) != null;

        // ── Ordered projections ──────────────────────────────────────────────────
        //
        // TWO DIFFERENT ORDERS, both load-bearing, deliberately NOT merged. Id order assigns the state_table
        // index, the CAT actuator_id and every recipe Wait1Id; CaS-bus order chains the station adapter and
        // interleaves sensors with actuators. layout.yml documents what breaks if either is normalised.

        public IReadOnlyList<string> IdOrderSensors { get; }
        public IReadOnlyList<string> IdOrderActuators { get; }
        public IReadOnlyList<string> CaSBusOrder { get; }

        // Fold in every twin component the layout does not list. A layout row is an OVERRIDE -- it pins a
        // controller and a canvas cell for a component whose placement someone chose deliberately -- not a
        // permit. A component the twin declares and the layout omits is placed with the process that drives
        // it, in the next free cell of that band's role row, so adding an ordinary sensor, actuator or
        // process to the model is a model edit and nothing else.
        //
        // Deterministic by construction: the owner fixes the band, the role fixes the row, and the column is
        // the first one that band/row has not already used, walked in the twin's own declaration order.
        public void PlaceUnlisted(Domain.Twin.TwinModel twin)
        {
            var layout = Profile.Layout;
            var used = new HashSet<(PlcAssignment Plc, LayoutRow Row, int Column)>();
            foreach (var e in _byName.Values) used.Add((e.Plc, e.Row, e.Column));

            foreach (var c in twin.Components)
            {
                var name = c.Name.Trim();
                if (name.Length == 0 || _byName.ContainsKey(name)) continue;

                // A component runs where its driver runs; a process that drives nothing yet defaults to the
                // Feed controller, which is the only one guaranteed to exist.
                var owner = c.IsProcess ? c : twin.OwningProcess(c);
                var plc = owner == null || ReferenceEquals(owner, c)
                    ? PlcAssignment.Unknown
                    : Of(owner.Name.Trim());
                if (plc == PlcAssignment.Unknown)
                    plc = c.IsProcess ? BusiestProcessTarget() : ControllerMap.FeedController;

                var row = c.IsProcess ? LayoutRow.Process : c.IsSensor ? LayoutRow.Sensor : LayoutRow.Actuator;
                int column = 0;
                while (!used.Add((plc, row, column))) column++;

                var band = layout.Band(plc);
                _byName[name] = new ComponentEntry(name, plc, ControllerMap.ResourceForPlc(plc),
                    column, row,
                    band.ColumnBaseX + column * layout.Geometry.ColumnPitchX,
                    layout.RowY(row.ToString()),
                    owner?.Name?.Trim() ?? string.Empty);
            }
        }

        // Where an unplaced PROCESS goes: the target already running the most, ties broken by registry
        // order. A process is emitted and ring-threaded per controller, and the target that already runs
        // several is the one whose emitter is written for N of them.
        private PlcAssignment BusiestProcessTarget()
        {
            var counts = _byName.Values
                .Where(e => e.Row == LayoutRow.Process)
                .GroupBy(e => e.Plc)
                .ToDictionary(g => g.Key, g => g.Count());
            return TargetRegistry.All
                .OrderByDescending(t => counts.TryGetValue(t.Plc, out int n) ? n : 0)
                .Select(t => t.Plc)
                .First();
        }

        private PlcAssignment Of(string name) =>
            _byName.TryGetValue(name, out var e) ? e.Plc : PlcAssignment.Unknown;
    }
}
