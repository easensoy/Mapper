using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Translation;

namespace CodeGen.Mapping
{
    // One system FB a resource boots with, fully specified. The sysres mirror renders it verbatim.
    public sealed record SystemFbSpec(
        string Id, string Name, string Type, string Namespace, int X, int Y,
        IReadOnlyList<(string Name, string Value)> Parameters);

    // Which system FBs each controller's resource carries, and under which identity. EAE identifies an FB by its
    // ID, so a deployed resource's ids are FROZEN: regenerating with a different one makes EAE treat it as a new
    // instance. The Feed controller carries no I/O broker — its .hcf publishes symlinks straight to consumer FBs.
    public static class TargetBootstrap
    {
        private static readonly (string Name, string Value)[] NoParameters = Array.Empty<(string, string)>();

        // Declared in emission order: the sysres appends them as listed.
        private static readonly (string Name, string Type, string Namespace, (string, string)[] Parameters)[] Shape =
        {
            ("FB1", "DPAC_FULLINIT", "SE.DPAC",   NoParameters),
            ("FB2", "plcStart",      "SE.AppBase", new[] { ("Prio", "10"), ("Delay", "T#1000ms") }),
        };

        private static readonly Dictionary<PlcAssignment, string[]> FrozenIds = new()
        {
            [PlcAssignment.M262] = new[] { "593A8F4FDEA0A668", "3DB1FB0F578E5F1E" },
            [PlcAssignment.M580] = new[] { "66C40EEF3F39D969", "ACED009B79DFCE69" },
            [PlcAssignment.BX1]  = new[] { "0FE5E1B2C3D4A5B6", "1A2B3C4D5E6F7081" },
        };

        // The resource's runtime bring-up, emitted before any component wire and in this order.
        public static readonly IReadOnlyList<(string Source, string Destination)> BringUpWires = new[]
        {
            ("START.COLD",          "FB1.INIT"),
            ("START.WARM",          "FB1.INIT"),
            ("START.ONLINECHANGE",  "FB1.OC_RETRIGGER"),
            ("FB2.FIRST_INIT",      "FB2.ACK_FIRST"),
        };

        public static IReadOnlyList<SystemFbSpec> For(PlcAssignment plc, LayoutCatalog layout)
        {
            var resource = ControllerMap.ResourceForPlc(plc);
            return Shape.Select((s, i) =>
            {
                var at = layout.BootFbs.FirstOrDefault(b => string.Equals(b.Name, s.Name, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException(
                        $"[Bootstrap] layout.yml declares no bootFb '{s.Name}', so its canvas position is unknown.");
                var id = FrozenIds.TryGetValue(plc, out var frozen)
                    ? frozen[i]
                    : FBIdGenerator.GenerateFBId($"{resource}.{s.Name}");
                return new SystemFbSpec(id, s.Name, s.Type, s.Namespace,
                    at.SysresX, at.SysresY, s.Parameters);
            }).ToList();
        }
    }
}
