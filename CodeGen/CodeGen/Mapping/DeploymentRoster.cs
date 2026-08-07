using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Translation;

namespace CodeGen.Mapping
{
    // Canonical row labels for the SMC rig syslay canvas; Y is per-row (see LayoutGrid.RowY).
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
        private readonly IReadOnlyDictionary<string, ComponentEntry> _byName;

        public DeploymentProfile Profile { get; }

        public DeploymentRoster(DeploymentProfile profile)
        {
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
            var layout = LayoutCatalog.Current;

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
            IdOrderActuators = HandoffPlanner.DischargeActive
                ? layout.IdOrder.Actuators.Concat(layout.IdOrder.RobotTail).ToList()
                : layout.IdOrder.Actuators;
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
    }
}
