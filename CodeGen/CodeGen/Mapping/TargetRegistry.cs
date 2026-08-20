using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Translation;

namespace CodeGen.Mapping
{
    // One supported deployment target, fully described. Everything the planner needs about a controller
    // lives on its descriptor, so planning selects a target by key instead of running a switch per
    // question. Backend RENDERERS stay typed C#: an EAE device cannot be named in YAML.
    public sealed record TargetDescriptor(
        PlcAssignment Plc,
        // EAE resource name. Load-bearing: the authored M580 .hcf symlinks read 'RES0.M580IO.*'.
        string ResourceName,
        // The sysdev Type. BX1 and the RevPi are both Soft_dPAC, so Type alone does not identify a device.
        string DeviceType,
        // Disambiguates two targets sharing a DeviceType; null when the Type is unique.
        string? DeviceName,
        string HcfTemplate,
        bool HostsFeedStation,
        bool DeviceLocalCanvas,
        // Receives components relocated off another target, so they must not be swept from its sysres.
        bool ReceivesRelocatedComponents,
        // Hands the cover detour out to another target; its ring closes across the seam, not locally.
        bool OpensCoverSeam,
        // Carries a chain another target commands, so it is open at both ends.
        bool CarriesDetouredChain)
    {
        // Every EAE device the Mapper emits lives in the same vendor namespace.
        public const string DeviceNamespace = "SE.DPAC";
    }

    public static class TargetRegistry
    {
        // The targets this codebase can actually GENERATE. Naming one in device.yml with no emitter here
        // would allocate a component to a device nothing can write, so the implemented set stays in C#.
        private static readonly PlcAssignment[] Implemented =
        {
            PlcAssignment.M262, PlcAssignment.M580, PlcAssignment.BX1, PlcAssignment.RevPi,
        };

        // Joined once per configuration load; the checks in Join catch either half being absent.
        private static readonly object _gate = new();
        private static IReadOnlyList<TargetDescriptor>? _targets;
        private static IReadOnlyList<Configuration.TargetIdentity>? _from;

        private static IReadOnlyList<TargetDescriptor> Targets
        {
            get
            {
                var declared = Configuration.DeviceConfig.Current.Targets;
                lock (_gate)
                {
                    if (_targets != null && ReferenceEquals(_from, declared)) return _targets;
                    _from = declared;
                    return _targets = Join(declared);
                }
            }
        }

        private static IReadOnlyList<TargetDescriptor> Join(
            IReadOnlyList<Configuration.TargetIdentity> declared)
        {
            var errors = new List<string>();
            foreach (var d in declared)
                if (!Implemented.Contains(d.Plc))
                    errors.Add($"device.yml declares target '{d.Plc}', which no backend implements");
            foreach (var plc in Implemented)
                if (declared.All(d => d.Plc != plc))
                    errors.Add($"backend '{plc}' has no device.yml targets entry, so it has no resource name");
            foreach (var g in declared.GroupBy(d => d.Plc).Where(g => g.Count() > 1))
                errors.Add($"device.yml declares target '{g.Key}' {g.Count()} times");
            foreach (var d in declared)
                if (string.IsNullOrWhiteSpace(d.ResourceName) || string.IsNullOrWhiteSpace(d.DeviceType))
                    errors.Add($"device.yml target '{d.Plc}' is missing a resourceName or deviceType");
            if (errors.Count > 0)
                throw new InvalidOperationException(
                    "device.yml targets do not match the supported backends:" + Environment.NewLine +
                    "  - " + string.Join(Environment.NewLine + "  - ", errors));

            return Implemented.Select(plc =>
            {
                var d = declared.First(i => i.Plc == plc);
                return new TargetDescriptor(
                    plc, d.ResourceName, d.DeviceType,
                    string.IsNullOrWhiteSpace(d.DeviceName) ? null : d.DeviceName,
                    d.HcfTemplate,
                    d.HostsFeedStation, d.DeviceLocalCanvas, d.ReceivesRelocatedComponents,
                    d.OpensCoverSeam, d.CarriesDetouredChain);
            }).ToList();
        }

        // Throws rather than returning a blank, which would emit a device with no resource name.
        public static TargetDescriptor Of(PlcAssignment plc) =>
            Targets.FirstOrDefault(t => t.Plc == plc)
            ?? throw new InvalidOperationException(
                $"[Target] '{plc}' is not a supported deployment target. Registered: " +
                string.Join(", ", Targets.Select(t => t.Plc)) +
                ". A new controller needs a backend emitter and a device.yml targets entry.");

        public static bool IsRegistered(PlcAssignment plc) => Targets.Any(t => t.Plc == plc);

        public static IReadOnlyList<TargetDescriptor> All => Targets;

        // The controller that runs the Feed station when nothing has relocated it.
        public static PlcAssignment FeedTarget =>
            Targets.First(t => t is { HostsFeedStation: true, ReceivesRelocatedComponents: false }).Plc;
    }
}
