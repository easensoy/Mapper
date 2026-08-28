using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
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
        // The EAE identities this device is emitted with - sysdev, resource, equipment, runtime and the
        // sub-equipment a wire attaches to. On the DESCRIPTOR because they belong to the target, not to
        // the emitter: read through a static keyed on a controller name, a second target of the same
        // kind could only ever be emitted with the first one's ids.
        Configuration.DeviceIdentity Identity,
        bool DeviceLocalCanvas,
        // The target this one stands in for, or null. A stand-in takes over components moved off that
        // target: it shares that target's report ring rather than owning one, its sysres is never swept
        // of what was moved onto it, and it is emitted only when this run actually moves something.
        Translation.PlcAssignment? StandsInFor,
        // The target that commands this one's chain, or null. Its components splice onto that target's
        // ring, so the chain is open at BOTH ends here. The other end - the seam the commander opens -
        // is DERIVED from this, so the two ends cannot be declared inconsistently.
        Translation.PlcAssignment? ChainCommandedBy,
        // The IO broker FB this target hosts, or null. Ownership of an emitted FB that is not a plant
        // component: without it the mirror has no way to say which resource such an FB belongs on.
        string? IoBroker,
        // The EAE simulation binding's deploy and archive service ports for this device.
        int SimulationDeployPort,
        int SimulationArchivePort,

        // The hardware modules this device carries, in bus order.
        IReadOnlyList<HardwareModule> HardwareModules,

        // The EtherNet/IP coupler type this target's scanner instantiates, and the HwConfiguration
        // model folders that carry it. Empty on a target whose IO is not EtherNet/IP.
        string EtherNetIpDeviceType,
        IReadOnlyList<string> HwConfigModelFolders,
        // The system FBs this resource boots with, in emission order, each already joined to its shape.
        IReadOnlyList<BootFbSpec> BootFbs)
    {
        // Every EAE device the Mapper emits lives in the same vendor namespace.
        public const string DeviceNamespace = Artefacts.EaeAbi.DeviceNamespace;
    }

    // One boot FB, fully specified: what it is (role, type, namespace, parameters, order) joined to who
    // it is on this target (the frozen EAE id) and where it is drawn (the layout key).
    public sealed record BootFbSpec(
        string Role, string Id, string Type, string Namespace, string LayoutKey,
        IReadOnlyList<(string Name, string Value)> Parameters);

    // One system FB a resource boots with, fully specified. The sysres mirror renders it verbatim.
    public sealed record SystemFbSpec(
        string Id, string Name, string Type, string Namespace, int X, int Y,
        IReadOnlyList<(string Name, string Value)> Parameters);

    /// EVERY QUESTION ABOUT A DEPLOYMENT TARGET, ANSWERED FROM ONE RUN'S DECLARATIONS.
    ///
    /// device.yml declares the targets, the boot sequence and the bring-up wiring; this joins the three
    /// and validates the join ONCE, when the run's configuration snapshot is built - so an invalid
    /// device.yml stops the run before a plan exists, and therefore before anything is written.
    ///
    /// It was a static class with a lock and a first-touch cache. That cache keyed on the declaration
    /// list's REFERENCE, so a second profile whose targets happened to be a different list object got a
    /// rebuild and one that did not, did not: whether a run saw its own declarations depended on object
    /// identity. Per-run construction removes the question.
    public sealed class TargetIndex
    {
        readonly IReadOnlyList<TargetDescriptor> _targets;

        /// The declared bring-up, in declaration order - which is emission order.
        public IReadOnlyList<(string Source, string Destination)> BringUp { get; }

        /// Every declared boot role. A boot FB is emitted under its role name, so this is also the set of
        /// instance names a resource boots with, which is what tells a component apart from a boot FB.
        public IReadOnlySet<string> BootRoles { get; }

        /// The role whose INITO heads a resource's init chain: the first FB the boot sequence declares.
        public string InitRole { get; }

        public TargetIndex(DeviceConfig devices, TemplateIndex templates)
        {
            if (devices is null) throw new ArgumentNullException(nameof(devices));
            if (templates is null) throw new ArgumentNullException(nameof(templates));

            _targets = Join(devices, templates);
            BringUp = devices.BringUp.Select(w => (w.From, w.To)).ToList();
            BootRoles = devices.BootSequence.Select(b => b.Role).ToHashSet(StringComparer.Ordinal);
            InitRole = devices.BootSequence.FirstOrDefault()?.Role
                ?? throw new InvalidOperationException(
                    "[Bootstrap] device.yml declares no bootSequence, so no resource has an FB to init from.");
        }

        static IReadOnlyList<TargetDescriptor> Join(DeviceConfig devices, TemplateIndex templates)
        {
            var declared = devices.Targets;
            var errors = new List<string>();
            // Backend-vs-declaration agreement is checked by CompilerSession: the index is not allowed to
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

            var sequence = devices.BootSequence;
            errors.AddRange(BootProfileErrors(declared, sequence));
            errors.AddRange(BringUpErrors(devices.BringUp, sequence, templates));
            if (errors.Count > 0)
                throw new InvalidOperationException(
                    "device.yml targets do not match the supported backends:" + Environment.NewLine +
                    "  - " + string.Join(Environment.NewLine + "  - ", errors));

            // A relationship names a target; validation has already refused one that names nothing.
            static Translation.PlcAssignment? Related(string? name) =>
                string.IsNullOrWhiteSpace(name) ? null : Translation.PlcAssignment.Named(name);

            // In DECLARATION order: every target is both declared and implemented by now, and the order a
            // descriptor list is walked in reaches artefacts, so it is the declaration that fixes it.
            return declared.Select(d => new TargetDescriptor(
                d.Plc, d.ResourceName, d.DeviceType,
                string.IsNullOrWhiteSpace(d.DeviceName) ? null : d.DeviceName,
                d.HcfTemplate,
                d.Identity,
                d.DeviceLocalCanvas, Related(d.StandsInFor), Related(d.ChainCommandedBy),
                string.IsNullOrWhiteSpace(d.IoBroker) ? null : d.IoBroker!.Trim(),
                d.SimulationDeployPort, d.SimulationArchivePort,
                d.HardwareModules,
                d.EtherNetIpDeviceType,
                d.HwConfigModelFolders,
                BootProfile(d, sequence))).ToList();
        }

        // The boot sequence is protocol and the ids are identity: a target answers the sequence role for
        // role, so the two are joined here once and every emitter reads the result.
        static IReadOnlyList<BootFbSpec> BootProfile(
            TargetIdentity target, IReadOnlyList<BootFbDeclaration> sequence) =>
            sequence.Select(shape => new BootFbSpec(
                shape.Role,
                target.BootFbs.First(b => RoleEquals(b.Role, shape.Role)).Id,
                shape.Type, shape.Namespace, shape.LayoutKey,
                shape.Parameters.Select(p => (p.Name, p.Value)).ToList())).ToList();

        static bool RoleEquals(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);

        // A boot id is an EAE identity: a missing, malformed or repeated one is a resource EAE cannot
        // load, so it is refused here - before the plan, and therefore before anything is written.
        internal static IEnumerable<string> BootProfileErrors(
            IReadOnlyList<TargetIdentity> declared, IReadOnlyList<BootFbDeclaration> sequence)
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
            IReadOnlyList<BringUpWire> wires, IReadOnlyList<BootFbDeclaration> sequence,
            TemplateIndex templates)
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
                    var role = BringUpWire.RoleOf(endpoint);
                    var port = BringUpWire.PortOf(endpoint);
                    if (role == null || port == null)
                    {
                        yield return $"device.yml bringUp {side} '{endpoint}' is not a '<role>.<PORT>' endpoint";
                        continue;
                    }
                    if (role == BringUpWire.ResourceEntry)
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
                    var contract = templates.Find(type);
                    if (contract is { Ports.Count: > 0 } && !contract.Ports.Contains(port, StringComparer.Ordinal))
                        yield return $"device.yml bringUp {side} '{endpoint}' names port '{port}', which " +
                                     $"'{type}' does not declare";
                }
                if (!seen.Add($"{w.From}->{w.To}"))
                    yield return $"device.yml bringUp declares '{w.From} -> {w.To}' more than once";
            }
        }

        // What a resource itself raises, which is EAE's contract rather than anything the Mapper emits.
        static readonly IReadOnlySet<string> ResourceEntryEvents =
            new HashSet<string>(StringComparer.Ordinal) { "COLD", "WARM", "ONLINECHANGE" };

        static readonly System.Text.RegularExpressions.Regex BootId =
            new("^[0-9A-F]{16}$", System.Text.RegularExpressions.RegexOptions.Compiled);

        // ---- queries -----------------------------------------------------------------------------

        /// Throws rather than returning a blank, which would emit a device with no resource name.
        public TargetDescriptor Of(PlcAssignment plc) =>
            _targets.FirstOrDefault(t => t.Plc == plc)
            ?? throw new InvalidOperationException(
                $"[Target] '{plc}' is not a supported deployment target. Registered: " +
                string.Join(", ", _targets.Select(t => t.Plc)) +
                ". A new controller needs a backend emitter and a device.yml targets entry.");

        public bool IsRegistered(PlcAssignment plc) => _targets.Any(t => t.Plc == plc);

        public IReadOnlyList<TargetDescriptor> All => _targets;

        /// Which target OWNS a report ring. A stand-in does not: it takes over components moved off
        /// another target, and the components moved while the ring they report on did not. Three passes
        /// used to spell this out for themselves - a capability, a planner and a frame owner - so they
        /// could disagree about where a relocated component's reports circulate.
        public static bool OwnsRing(TargetDescriptor t) => t.StandsInFor == null;

        /// The target whose ring this one reports on: its own, or the one it stands in for.
        public PlcAssignment RingHostOf(TargetDescriptor t) => t.StandsInFor ?? t.Plc;

        /// The targets that report on ONE ring: a ring owner and every stand-in that shares it. This is
        /// the native partition, before any chain a run decides to carry across a boundary.
        public IReadOnlyList<PlcAssignment> RingMembers(PlcAssignment host) =>
            _targets.Where(t => RingHostOf(t) == host).Select(t => t.Plc).ToList();

        /// Whether this target commands a chain another one carries, which is what makes its own ring
        /// close across the seam rather than locally. Declared at the carrying end and derived here, so
        /// a chain nobody commands - or a seam nobody carries - cannot be stated at all.
        public bool CommandsACarriedChain(PlcAssignment plc) =>
            _targets.Any(t => t.ChainCommandedBy == plc);

        /// The boot FBs a resource brings up, joined to where layout.yml draws each one.
        public IReadOnlyList<SystemFbSpec> BootFor(PlcAssignment plc, LayoutCatalog layout) =>
            Of(plc).BootFbs.Select(b =>
            {
                var at = layout.BootFbs.FirstOrDefault(l => string.Equals(l.Name, b.LayoutKey, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException(
                        $"[Bootstrap] layout.yml declares no bootFb '{b.LayoutKey}', so its canvas position is unknown.");
                return new SystemFbSpec(b.Id, b.Role, b.Type, b.Namespace, at.SysresX, at.SysresY, b.Parameters);
            }).ToList();
    }
}
