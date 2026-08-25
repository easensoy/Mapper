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
        // The declared bring-up, in declaration order - which is emission order. TargetRegistry has
        // already proved every endpoint names a role some target boots with.
        public static IEnumerable<(string Source, string Destination)> BringUp =>
            DeviceConfig.Current.BringUp.Select(w => (w.From, w.To));

        // Every declared boot role. A boot FB is emitted under its role name, so this is also the set of
        // instance names a resource boots with, which is what tells a component apart from a boot FB.
        public static IReadOnlySet<string> BootRoles =>
            DeviceConfig.Current.BootSequence.Select(b => b.Role).ToHashSet(StringComparer.Ordinal);

        // The role whose INITO heads a resource's init chain: the first FB the boot sequence declares.
        public static string InitRole =>
            DeviceConfig.Current.BootSequence.FirstOrDefault()?.Role
            ?? throw new InvalidOperationException(
                "[Bootstrap] device.yml declares no bootSequence, so no resource has an FB to init from.");

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
