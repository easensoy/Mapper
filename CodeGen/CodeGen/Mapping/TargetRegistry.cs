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
        // The IO broker FB this target hosts, or null. Ownership of an emitted FB that is not a plant
        // component: without it the mirror has no way to say which resource such an FB belongs on.
        string? IoBroker,
        // The EAE simulation binding's deploy and archive service ports for this device.
        int SimulationDeployPort,
        int SimulationArchivePort,

        // The hardware modules this device carries, in bus order.
        IReadOnlyList<Configuration.HardwareModule> HardwareModules,

        // The EtherNet/IP coupler type this target's scanner instantiates, and the HwConfiguration
        // model folders that carry it. Empty on a target whose IO is not EtherNet/IP.
        string EtherNetIpDeviceType,
        IReadOnlyList<string> HwConfigModelFolders,
        // The system FBs this resource boots with, in emission order, each already joined to its shape.
        IReadOnlyList<BootFbSpec> BootFbs)
    {
        // Every EAE device the Mapper emits lives in the same vendor namespace.
        public const string DeviceNamespace = "SE.DPAC";
    }

    // Which target OWNS a station's ring. A target that merely RECEIVES components relocated off
    // another one is not that ring's host: the components moved, their station did not. Three passes
    // used to spell this out for themselves - a capability, a planner and a frame owner - so they could
    // disagree about where a relocated component's reports circulate.
    public static class RingHost
    {
        public static bool Owns(TargetDescriptor t) => !t.ReceivesRelocatedComponents;

        // The target hosting the same station as this one, without receiving anything onto it.
        public static PlcAssignment Of(TargetDescriptor t) =>
            Owns(t) ? t.Plc
                : TargetRegistry.All.FirstOrDefault(
                      o => o.HostsFeedStation == t.HostsFeedStation && Owns(o))?.Plc
                  ?? t.Plc;
    }

    // One boot FB, fully specified: what it is (role, type, namespace, parameters, order) joined to who
    // it is on this target (the frozen EAE id) and where it is drawn (the layout key).
    public sealed record BootFbSpec(
        string Role, string Id, string Type, string Namespace, string LayoutKey,
        IReadOnlyList<(string Name, string Value)> Parameters);

    public static class TargetRegistry
    {
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
            // Backend-vs-declaration agreement is checked in UseBackends: the registry is not allowed to
            // know a concrete backend, so it cannot ask that question while it is loading the declaration.
            foreach (var g in declared.GroupBy(d => d.Plc).Where(g => g.Count() > 1))
                errors.Add($"device.yml declares target '{g.Key}' {g.Count()} times");
            foreach (var d in declared)
                if (string.IsNullOrWhiteSpace(d.ResourceName) || string.IsNullOrWhiteSpace(d.DeviceType))
                    errors.Add($"device.yml target '{d.Plc}' is missing a resourceName or deviceType");
            // An FB has ONE owner. Two targets claiming one broker would mirror it onto both resources,
            // and EAE rejects the deploy for a duplicated instance rather than picking one.
            foreach (var g in declared
                         .Where(d => !string.IsNullOrWhiteSpace(d.IoBroker))
                         .GroupBy(d => d.IoBroker!.Trim(), StringComparer.Ordinal)
                         .Where(g => g.Count() > 1))
                errors.Add($"ioBroker '{g.Key}' is claimed by {g.Count()} targets " +
                           $"({string.Join(", ", g.Select(d => d.Plc))}); an emitted FB has one owner");

            // A device's own two services cannot share a port, and no two devices may claim one port
            // for the same role - either would bind one service and silently drop the other.
            foreach (var d in declared)
            {
                if (d.SimulationDeployPort <= 0 || d.SimulationArchivePort <= 0)
                    errors.Add($"target '{d.Plc}' declares no simulationDeployPort/simulationArchivePort");
                else if (d.SimulationDeployPort == d.SimulationArchivePort)
                    errors.Add($"target '{d.Plc}' claims port {d.SimulationDeployPort} for BOTH its " +
                               "deploy and archive service");
            }
            foreach (var (role, port) in new[] { ("deploy", 0), ("archive", 1) })
                foreach (var g in declared
                             .Select(d => (d.Plc, Port: port == 0 ? d.SimulationDeployPort : d.SimulationArchivePort))
                             .Where(x => x.Port > 0)
                             .GroupBy(x => x.Port).Where(g => g.Count() > 1))
                    errors.Add($"port {g.Key} is claimed as the {role} service by " +
                               $"{string.Join(" and ", g.Select(x => x.Plc))}");

            var sequence = Configuration.DeviceConfig.Current.BootSequence;
            errors.AddRange(BootProfileErrors(declared, sequence));
            errors.AddRange(BringUpErrors(Configuration.DeviceConfig.Current.BringUp, sequence));
            if (errors.Count > 0)
                throw new InvalidOperationException(
                    "device.yml targets do not match the supported backends:" + Environment.NewLine +
                    "  - " + string.Join(Environment.NewLine + "  - ", errors));

            // In DECLARATION order: every target is both declared and implemented by now, and the order a
            // descriptor list is walked in reaches artefacts, so it is the declaration that fixes it.
            return declared.Select(d =>
            {
                return new TargetDescriptor(
                    d.Plc, d.ResourceName, d.DeviceType,
                    string.IsNullOrWhiteSpace(d.DeviceName) ? null : d.DeviceName,
                    d.HcfTemplate,
                    d.HostsFeedStation, d.DeviceLocalCanvas, d.ReceivesRelocatedComponents,
                    d.OpensCoverSeam, d.CarriesDetouredChain,
                    string.IsNullOrWhiteSpace(d.IoBroker) ? null : d.IoBroker!.Trim(),
                    d.SimulationDeployPort, d.SimulationArchivePort,
                    d.HardwareModules,
                    d.EtherNetIpDeviceType,
                    d.HwConfigModelFolders,
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

        // A bring-up wire names boot ROLES, so a role no target boots with is a connection to an FB the
        // resource never emits - which EAE rejects on import. It is refused here, before a plan exists.
        internal static IEnumerable<string> BringUpErrors(
            IReadOnlyList<Configuration.BringUpWire> wires,
            IReadOnlyList<Configuration.BootFbDeclaration> sequence)
        {
            if (wires.Count == 0)
            {
                yield return "device.yml declares no bringUp, so no resource is started at all";
                yield break;
            }
            var typeOfRole = sequence.ToDictionary(s => s.Role, s => s.Type, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var w in wires)
            {
                foreach (var (endpoint, side) in new[] { (w.From, "from"), (w.To, "to") })
                {
                    var role = Configuration.BringUpWire.RoleOf(endpoint);
                    var port = Configuration.BringUpWire.PortOf(endpoint);
                    if (role == null || port == null)
                    {
                        yield return $"device.yml bringUp {side} '{endpoint}' is not a '<role>.<PORT>' endpoint";
                        continue;
                    }
                    if (role == Configuration.BringUpWire.ResourceEntry)
                    {
                        // The resource's own entry events. A misspelling here wires a start nothing raises.
                        if (!ResourceEntryEvents.Contains(port))
                            yield return $"device.yml bringUp {side} '{endpoint}' names entry event '{port}', " +
                                         "which a resource does not raise; it raises " +
                                         string.Join(", ", ResourceEntryEvents);
                        continue;
                    }
                    if (!typeOfRole.TryGetValue(role, out var type))
                    {
                        yield return $"device.yml bringUp {side} '{endpoint}' names boot role '{role}', which " +
                                     "no bootSequence role and not the resource entry declares";
                        continue;
                    }
                    // Checked against the type's own contract wherever the Mapper OWNS that type. A boot
                    // FB from the vendor library has no authored interface in this repo to check against,
                    // so its port is taken as declared until such a type is one we ship.
                    var contract = TemplateManifest.Find(type);
                    if (contract is { Ports.Count: > 0 } && !contract.Ports.Contains(port, StringComparer.Ordinal))
                        yield return $"device.yml bringUp {side} '{endpoint}' names port '{port}', which " +
                                     $"'{type}' does not declare";
                }
                if (!seen.Add($"{w.From}->{w.To}"))
                    yield return $"device.yml bringUp declares '{w.From} -> {w.To}' more than once";
            }
        }

        // What a resource itself raises, which is EAE's contract rather than anything the Mapper emits.
        private static readonly IReadOnlySet<string> ResourceEntryEvents =
            new HashSet<string>(StringComparer.Ordinal) { "COLD", "WARM", "ONLINECHANGE" };

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

        // Handed in by the composition root, which is the one place that may know a concrete backend.
        // The registry answers what a target IS from device.yml and never constructs one.
        private static IReadOnlyList<CodeGen.Devices.ITargetBackend> _backends =
            Array.Empty<CodeGen.Devices.ITargetBackend>();

        public static IReadOnlyList<CodeGen.Devices.ITargetBackend> Backends => _backends;

        // Called once, before anything is planned. A target is IMPLEMENTED because a backend claims it
        // and DECLARED because device.yml has a row for it; the two must agree exactly, or a run would
        // either emit a device with no resource name or silently skip one the deployment expects.
        public static void UseBackends(IReadOnlyList<CodeGen.Devices.ITargetBackend> backends)
        {
            if (backends is null || backends.Count == 0)
                throw new ArgumentException("no target backends were registered, so no device can be emitted",
                    nameof(backends));

            var errors = new List<string>();
            foreach (var g in backends.GroupBy(b => b.Target).Where(g => g.Count() > 1))
                errors.Add($"two backends both claim target '{g.Key}', so which one emits it is undecided");
            var implemented = backends.Select(b => b.Target).ToList();
            var declared = Configuration.DeviceConfig.Current.Targets;
            foreach (var d in declared)
                if (!implemented.Contains(d.Plc))
                    errors.Add($"device.yml declares target '{d.Plc}', which no backend implements");
            foreach (var plc in implemented)
                if (declared.All(d => d.Plc != plc))
                    errors.Add($"backend '{plc}' has no device.yml targets entry, so it has no resource name");
            if (errors.Count > 0)
                throw new InvalidOperationException(
                    "Target registration is inconsistent:" + Environment.NewLine +
                    "  - " + string.Join(Environment.NewLine + "  - ", errors));

            _backends = backends;
        }

        // The controller that runs the Feed station when nothing has relocated it.
        public static PlcAssignment FeedTarget =>
            Targets.First(t => t is { HostsFeedStation: true, ReceivesRelocatedComponents: false }).Plc;
    }
}
