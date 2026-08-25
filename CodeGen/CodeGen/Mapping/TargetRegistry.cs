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
        bool CarriesDetouredChain,
        // The system FBs this resource boots with, in emission order, each already joined to its shape.
        IReadOnlyList<BootFbSpec> BootFbs)
    {
        // Every EAE device the Mapper emits lives in the same vendor namespace.
        public const string DeviceNamespace = "SE.DPAC";
    }

    // One boot FB, fully specified: what it is (role, type, namespace, parameters, order) joined to who
    // it is on this target (the frozen EAE id) and where it is drawn (the layout key).
    public sealed record BootFbSpec(
        string Role, string Id, string Type, string Namespace, string LayoutKey,
        IReadOnlyList<(string Name, string Value)> Parameters);

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
            var sequence = Configuration.DeviceConfig.Current.BootSequence;
            errors.AddRange(BootProfileErrors(declared, sequence));
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
                    d.OpensCoverSeam, d.CarriesDetouredChain,
                    BootProfile(d, sequence));
            }).ToList();
        }

        // The boot sequence is protocol and the ids are identity: a target answers the sequence role for
        // role, so the two are joined here once and every emitter reads the result.
        private static IReadOnlyList<BootFbSpec> BootProfile(
            Configuration.TargetIdentity target, IReadOnlyList<Configuration.BootFbDeclaration> sequence) =>
            sequence.Select(shape => new BootFbSpec(
                shape.Role,
                target.BootFbs.First(b => RoleEquals(b.Role, shape.Role)).Id,
                shape.Type, shape.Namespace, shape.LayoutKey,
                shape.Parameters.Select(p => (p.Name, p.Value)).ToList())).ToList();

        private static bool RoleEquals(string a, string b) =>
            string.Equals(a, b, StringComparison.Ordinal);

        // A boot id is an EAE identity: a missing, malformed or repeated one is a resource EAE cannot
        // load, so it is refused here - before the plan, and therefore before anything is written.
        internal static IEnumerable<string> BootProfileErrors(
            IReadOnlyList<Configuration.TargetIdentity> declared,
            IReadOnlyList<Configuration.BootFbDeclaration> sequence)
        {
            if (sequence.Count == 0)
            {
                yield return "device.yml declares no bootSequence, so no resource knows what to boot with";
                yield break;
            }
            foreach (var shape in sequence)
                if (string.IsNullOrWhiteSpace(shape.Role) || string.IsNullOrWhiteSpace(shape.Type) ||
                    string.IsNullOrWhiteSpace(shape.Namespace) || string.IsNullOrWhiteSpace(shape.LayoutKey))
                    yield return $"device.yml bootSequence role '{shape.Role}' is missing a " +
                                 "type, namespace or layoutKey";
            foreach (var g in sequence.GroupBy(s => s.Role, StringComparer.Ordinal).Where(g => g.Count() > 1))
                yield return $"device.yml bootSequence declares role '{g.Key}' {g.Count()} times";

            var seen = new Dictionary<string, PlcAssignment>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in declared)
            {
                if (d.BootFbs.Count != sequence.Count)
                {
                    yield return $"device.yml target '{d.Plc}' declares {d.BootFbs.Count} bootFbs but the " +
                                 $"bootSequence has {sequence.Count} role(s)";
                    continue;
                }
                foreach (var shape in sequence)
                {
                    var matches = d.BootFbs.Where(b => RoleEquals(b.Role, shape.Role)).ToList();
                    if (matches.Count != 1)
                    {
                        yield return $"device.yml target '{d.Plc}' declares boot role '{shape.Role}' " +
                                     $"{matches.Count} times; it needs exactly one";
                        continue;
                    }
                    var id = matches[0].Id ?? string.Empty;
                    if (!BootId.IsMatch(id))
                    {
                        yield return $"device.yml target '{d.Plc}' boot role '{shape.Role}' has id '{id}', " +
                                     "which is not a 16-character upper-case hex EAE id";
                        continue;
                    }
                    if (seen.TryGetValue(id, out var owner))
                        yield return $"device.yml boot id '{id}' is declared on both '{owner}' and " +
                                     $"'{d.Plc}'; EAE loads a duplicate instance id as one FB";
                    else seen[id] = d.Plc;
                }
            }
        }

        private static readonly System.Text.RegularExpressions.Regex BootId =
            new("^[0-9A-F]{16}$", System.Text.RegularExpressions.RegexOptions.Compiled);

        // Throws rather than returning a blank, which would emit a device with no resource name.
        public static TargetDescriptor Of(PlcAssignment plc) =>
            Targets.FirstOrDefault(t => t.Plc == plc)
            ?? throw new InvalidOperationException(
                $"[Target] '{plc}' is not a supported deployment target. Registered: " +
                string.Join(", ", Targets.Select(t => t.Plc)) +
                ". A new controller needs a backend emitter and a device.yml targets entry.");

        public static bool IsRegistered(PlcAssignment plc) => Targets.Any(t => t.Plc == plc);

        public static IReadOnlyList<TargetDescriptor> All => Targets;

        // The backends that can actually GENERATE a target, in the order a run drives them: a device
        // whose System folder another one creates has to come after it. Registering a target here is
        // what makes it implemented; device.yml supplies what it IS.
        private static readonly CodeGen.Devices.ITargetBackend[] Implementations =
        {
            new CodeGen.Devices.M262.M262Backend(),
            new CodeGen.Devices.RevPi.RevPiBackend(),
            new CodeGen.Devices.M580.M580Backend(),
            new CodeGen.Devices.BX1.Bx1Backend(),
        };

        public static IReadOnlyList<CodeGen.Devices.ITargetBackend> Backends => Implementations;

        // The controller that runs the Feed station when nothing has relocated it.
        public static PlcAssignment FeedTarget =>
            Targets.First(t => t is { HostsFeedStation: true, ReceivesRelocatedComponents: false }).Plc;
    }
}
