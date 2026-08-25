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

    // Boot-time bring-up, rendered from the target's declared profile. It owns no target data: which FBs
    // a resource boots with, under which frozen ids and in which order, is device.yml's bootSequence
    // joined to each target's bootFbs, and TargetRegistry validates that join before a plan exists.
    public static class TargetBootstrap
    {
        // The resource's runtime bring-up, emitted before any component wire and in this order. It names
        // boot ROLES, so it is protocol rather than target data and does not vary per controller.
        public static readonly IReadOnlyList<(string Source, string Destination)> BringUpWires = new[]
        {
            ("START.COLD",          "FB1.INIT"),
            ("START.WARM",          "FB1.INIT"),
            ("START.ONLINECHANGE",  "FB1.OC_RETRIGGER"),
            ("FB2.FIRST_INIT",      "FB2.ACK_FIRST"),
        };

        public static IReadOnlyList<SystemFbSpec> For(PlcAssignment plc, LayoutCatalog layout) =>
            TargetRegistry.Of(plc).BootFbs.Select(b =>
            {
                var at = layout.BootFbs.FirstOrDefault(l => string.Equals(l.Name, b.LayoutKey, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException(
                        $"[Bootstrap] layout.yml declares no bootFb '{b.LayoutKey}', so its canvas position is unknown.");
                return new SystemFbSpec(b.Id, b.Role, b.Type, b.Namespace,
                    at.SysresX, at.SysresY, b.Parameters);
            }).ToList();
    }
}
