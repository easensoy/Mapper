namespace CodeGen.Configuration
{
    public sealed class DeviceConfig
    {
        public DeviceNet DefaultNetwork { get; set; } = new();

        // A target's addresses, asked for by the name device.yml declares it under. The four per-target
        // sections this replaces were a second place to state the same fact, and a target added to
        // `targets:` had no network at all until someone remembered to add a section for it too.
        public DeviceNet NetworkOf(string plc) =>
            Targets.FirstOrDefault(t =>
                string.Equals(t.Plc.Name, plc, System.StringComparison.OrdinalIgnoreCase))?.Network
            ?? throw new System.InvalidOperationException(
                $"device.yml declares no target '{plc}', so it has no addresses.");

        // The names the prebuilt VueOne runner's MapperConfig members ask for; each is one lookup, not
        // a stored second copy.
        public DeviceNet M262 => NetworkOf("M262");
        public DeviceNet M580 => NetworkOf("M580");
        public DeviceNet Bx1 => NetworkOf("BX1");
        public DeviceNet RevPi => NetworkOf("RevPi");

        // The BX1 coupler word, as the rig wired it. See Config/device.yml.
        public Bx1IoProfile Bx1Io { get; set; } = new();

        // EAE resource and device identity per target. Facts only; backend behaviour stays in C#.
        public System.Collections.Generic.List<TargetIdentity> Targets { get; set; } = new();

        // In declaration order: DfbprojRegistrar appends references in this sequence and the
        // .dfbproj is a generated artefact, so the order is part of the output.
        public System.Collections.Generic.List<LibraryReference> Libraries { get; set; } = new();

        public InstallationIdentity Installation { get; set; } = new();

        // The physical network, declared as a graph. See Config/device.yml.
        public TopologyProfile Topology { get; set; } = new();

        // The order a run drives the backends, declared because it is NOT the declaration order:
        // a device whose System folder another one creates has to come after it.
        public System.Collections.Generic.List<CodeGen.Translation.PlcAssignment> BackendEmitOrder { get; set; } = new();

        // The system FBs every resource boots with, in emission order. Shape once; ids per target.
        public System.Collections.Generic.List<BootFbDeclaration> BootSequence { get; set; } = new();

        // The runtime bring-up wires every resource emits, in emission order. Endpoints name boot roles.
        public System.Collections.Generic.List<BringUpWire> BringUp { get; set; } = new();

        private static readonly YamlConfigFile<DeviceConfig> _file =
            new("Config", "device.yml") { OnLoaded = Validate };

        // Refused at LOAD, before any plan exists: a library row with no name or no version, a
        // duplicate name, or a version that is not a dotted number would each reach the .dfbproj as
        // a reference EAE cannot resolve, and the topology import fails as a whole rather than on
        // the one device that needed it.
        // Public so a test can ask the same validator a run asks, exactly as RigCatalogValidator is.
        public static void Validate(DeviceConfig c)
        {
            var errors = new System.Collections.Generic.List<string>();
            foreach (var lib in c.Libraries)
            {
                if (string.IsNullOrWhiteSpace(lib.Name))
                    errors.Add("a libraries row has no name");
                else if (string.IsNullOrWhiteSpace(lib.Version))
                    errors.Add($"library '{lib.Name}' has no version");
                else if (!System.Text.RegularExpressions.Regex.IsMatch(lib.Version, @"^\d+(\.\d+)*$"))
                    errors.Add($"library '{lib.Name}' version '{lib.Version}' is not a dotted version");
            }
            foreach (var g in c.Libraries
                         .Where(l => !string.IsNullOrWhiteSpace(l.Name))
                         .GroupBy(l => l.Name.Trim(), System.StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Count() > 1))
                errors.Add($"library '{g.Key}' is declared {g.Count()} times");

            // An identity is what EAE keys a device on, so a malformed one produces a project that
            // imports as a DIFFERENT device, and a shared one produces two devices EAE cannot tell
            // apart. Both are refused here rather than discovered at import.
            void Uuid(string owner, string field, string v)
            {
                if (v.Length > 0 && !System.Guid.TryParse(v, out _))
                    errors.Add($"{owner} identity.{field} '{v}' is not a UUID");
            }
            void Hex16(string owner, string field, string v)
            {
                if (v.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(v, "^[0-9A-F]{16}$"))
                    errors.Add($"{owner} identity.{field} '{v}' is not a 16-digit upper-case hex id");
            }
            var claimed = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            void Unique(string owner, string field, string v)
            {
                if (v.Length == 0) return;
                var key = field + "|" + v;
                if (claimed.TryGetValue(key, out var first))
                    errors.Add($"{owner} and {first} both claim {field} '{v}'");
                else claimed[key] = owner;
            }
            foreach (var t in c.Targets)
            {
                var id = t.Identity;
                var who = $"target '{t.Plc}'";
                if (string.IsNullOrWhiteSpace(id.Sysdev))
                    errors.Add($"{who} declares no identity.sysdev, so it has no system device to be emitted as");
                foreach (var (field, value) in new[]
                         {
                             ("sysdev", id.Sysdev), ("equipment", id.Equipment), ("runtime", id.Runtime),
                             ("runtimeType", id.RuntimeType), ("rack", id.Rack), ("cps", id.Cps),
                             ("cpu", id.Cpu), ("nic", id.Nic), ("container", id.Container),
                             ("containerDomain", id.ContainerDomain), ("etherNetIp", id.EtherNetIp),
                         })
                    Uuid(who, field, value ?? string.Empty);
                Hex16(who, "resource", id.Resource ?? string.Empty);
                Hex16(who, "scanner", id.Scanner ?? string.Empty);
                Hex16(who, "ioBrokerFb", id.IoBrokerFb ?? string.Empty);
                Unique(who, "sysdev", id.Sysdev ?? string.Empty);
                Unique(who, "resource", id.Resource ?? string.Empty);
                Unique(who, "equipment", id.Equipment ?? string.Empty);
                Unique(who, "runtime", id.Runtime ?? string.Empty);
            }
            Uuid("installation", "switchEquipment", c.Installation.SwitchEquipment ?? string.Empty);
            foreach (var (field, value) in new[]
                     {
                         ("deployPluginProperties", c.Installation.DeployPluginProperties),
                         ("systemDeviceProperties", c.Installation.SystemDeviceProperties),
                     })
                if (string.IsNullOrWhiteSpace(value) || !value.EndsWith(".Properties.xml", System.StringComparison.OrdinalIgnoreCase))
                    errors.Add($"installation.{field} '{value}' is not a <plugin-guid>.Properties.xml file name");

            errors.AddRange(TopologyErrors(c));
            errors.AddRange(RelationErrors(c));

            if (errors.Count > 0)
                throw new System.InvalidOperationException(
                    "device.yml is invalid:" + System.Environment.NewLine +
                    "  - " + string.Join(System.Environment.NewLine + "  - ", errors));
        }

        // A RELATIONSHIP BETWEEN TARGETS RESOLVES, OR THE RUN STOPS.
        //
        // Each edge is stated once and its other end is derived, so an edge naming a target that is not
        // declared would silently derive to nothing: a stand-in that relieves no one still gets emitted
        // and owns no ring, and a chain nobody commands stays open at both ends and never closes. Both
        // deploy and neither runs, so they are refused here rather than found on the rig.
        static System.Collections.Generic.IEnumerable<string> RelationErrors(DeviceConfig c)
        {
            var errors = new System.Collections.Generic.List<string>();
            var declared = new System.Collections.Generic.HashSet<string>(
                c.Targets.Select(t => t.Plc.Name), System.StringComparer.OrdinalIgnoreCase);

            foreach (var t in c.Targets)
                foreach (var (field, other) in new[]
                         { ("standsInFor", t.StandsInFor), ("chainCommandedBy", t.ChainCommandedBy) })
                {
                    if (string.IsNullOrWhiteSpace(other)) continue;
                    if (string.Equals(other, t.Plc.Name, System.StringComparison.OrdinalIgnoreCase))
                        errors.Add($"target '{t.Plc}' declares {field} pointing at itself");
                    else if (!declared.Contains(other!))
                        errors.Add($"target '{t.Plc}' declares {field} '{other}', which is not a declared target");
                }

            // A stand-in owns no ring, so standing in for one is a chain of borrowed rings that ends
            // nowhere: the ring a relocated component reports on would not exist.
            foreach (var t in c.Targets.Where(t => !string.IsNullOrWhiteSpace(t.StandsInFor)))
            {
                var host = c.Targets.FirstOrDefault(o =>
                    string.Equals(o.Plc.Name, t.StandsInFor, System.StringComparison.OrdinalIgnoreCase));
                if (host != null && !string.IsNullOrWhiteSpace(host.StandsInFor))
                    errors.Add($"target '{t.Plc}' stands in for '{host.Plc}', which itself stands in for " +
                               $"'{host.StandsInFor}'. A stand-in owns no ring, so there is none to share.");
            }

            // A target whose chain another one commands cannot also command that one's: the two rings
            // would each be open waiting for the other to close them.
            foreach (var t in c.Targets.Where(t => !string.IsNullOrWhiteSpace(t.ChainCommandedBy)))
            {
                var commander = c.Targets.FirstOrDefault(o =>
                    string.Equals(o.Plc.Name, t.ChainCommandedBy, System.StringComparison.OrdinalIgnoreCase));
                if (commander != null &&
                    string.Equals(commander.ChainCommandedBy, t.Plc.Name, System.StringComparison.OrdinalIgnoreCase))
                    errors.Add($"targets '{t.Plc}' and '{commander.Plc}' each declare the other commands " +
                               "their chain, so neither ring can close.");
            }
            return errors;
        }

        // EVERY ENDPOINT MUST RESOLVE BEFORE A WIRE IS WRITTEN.
        //
        // TopologyManager rejects the WHOLE topology on one unresolvable endpoint - a 500 at import, with
        // nothing to say which wire caused it. So a link naming a node that is not declared, an identity
        // that node does not carry, or a port nothing names is refused here, at load, before a plan
        // exists and therefore before anything is written.
        //
        // Two devices claiming one switch port is refused for the same reason it was never checked
        // before: the allocation lived in a comment, so nothing could check it.
        static System.Collections.Generic.IEnumerable<string> TopologyErrors(DeviceConfig c)
        {
            var errors = new System.Collections.Generic.List<string>();
            var t = c.Topology;
            if (t.Links.Count == 0) return errors;

            var nodeIds = new System.Collections.Generic.HashSet<string>(
                t.Nodes.Select(n => n.Id ?? string.Empty), System.StringComparer.OrdinalIgnoreCase);
            foreach (var n in t.Nodes)
            {
                if (string.IsNullOrWhiteSpace(n.Id)) errors.Add("topology.nodes has an entry with no id");
                if (!System.Guid.TryParse(n.Equipment ?? string.Empty, out _))
                    errors.Add($"topology node '{n.Id}' equipment '{n.Equipment}' is not a UUID");
                if (n.Emit && string.IsNullOrWhiteSpace(n.Template))
                    errors.Add($"topology node '{n.Id}' is emitted but names no template");
            }
            foreach (var g in t.Nodes.GroupBy(n => n.Id ?? string.Empty,
                         System.StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                errors.Add($"topology declares node '{g.Key}' {g.Count()} times");

            foreach (var g in t.Links.GroupBy(l => l.Identifier ?? string.Empty,
                         System.StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                errors.Add($"topology declares link '{g.Key}' {g.Count()} times");

            // A port is claimed by exactly one cable end. Two cables on one port is a wiring error the
            // topology importer does not catch and the rig cannot honour.
            var claimed = new System.Collections.Generic.Dictionary<string, string>(
                System.StringComparer.OrdinalIgnoreCase);

            foreach (var link in t.Links)
            {
                if (string.IsNullOrWhiteSpace(link.Identifier))
                    errors.Add("topology.links has an entry with no identifier");
                foreach (var (side, e) in new[] { ("from", link.From), ("to", link.To) })
                {
                    var where = $"topology link '{link.Identifier}' {side}";
                    if (string.IsNullOrWhiteSpace(e.Port)) { errors.Add($"{where} names no port"); continue; }
                    if (string.IsNullOrWhiteSpace(e.Node)) { errors.Add($"{where} names no node"); continue; }

                    var key = e.Node.Trim() + "." + e.Endpoint?.Trim() + "." + e.Port.Trim();
                    if (claimed.TryGetValue(key, out var owner))
                        errors.Add($"{where} claims '{key}', which link '{owner}' already uses");
                    else claimed[key] = link.Identifier ?? string.Empty;

                    if (nodeIds.Contains(e.Node)) continue;          // a declared non-target node
                    var target = c.Targets.FirstOrDefault(x =>
                        string.Equals(x.Plc.Name, e.Node, System.StringComparison.OrdinalIgnoreCase));
                    if (target == null)
                    {
                        errors.Add($"{where} names node '{e.Node}', which is neither a declared target " +
                                   "nor a topology node");
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(EndpointUuid(target.Identity, e.Endpoint)))
                        errors.Add($"{where} attaches to '{e.Endpoint}' on target '{e.Node}', which " +
                                   "declares no such identity");
                }
            }
            return errors;
        }

        // Which declared identity a cable end attaches to. Empty when the target does not carry it,
        // which is what the validation above turns into a refusal.
        public static string EndpointUuid(DeviceIdentity id, string? endpoint) =>
            (endpoint ?? "equipment").Trim().ToLowerInvariant() switch
            {
                "equipment" or "" => id.Equipment,
                "cpu" => id.Cpu,
                "nic" => id.Nic,
                "ethernetip" => id.EtherNetIp,
                "container" => id.Container,
                "rack" => id.Rack,
                _ => string.Empty,
            };

        public static DeviceConfig Current => _file.Load();

        /// The same declaration read from a run's OWN profile bundle. A root of null is the
        /// bundle shipped beside CodeGen.dll, which is what a normal run reads.
        public static DeviceConfig LoadFrom(string? root) => _file.Load(root);

        // The identities declared for one target. Asked for by target, never spelled at a call site.
        public DeviceIdentity IdentityOf(CodeGen.Translation.PlcAssignment plc) =>
            Targets.FirstOrDefault(t => t.Plc == plc)?.Identity
            ?? throw new System.InvalidOperationException(
                $"device.yml declares no target '{plc}', so it has no identities to be emitted with.");

        public static DeviceIdentity Identity(CodeGen.Translation.PlcAssignment plc) =>
            Current.IdentityOf(plc);

        // Components a target's own hardware is the only reader of, so hosting anything there takes
        // them along. A hardware contract of the TARGET, declared beside its addresses.
        // Components a target's own hardware is the only reader of, from that target's own row.
        public System.Collections.Generic.IReadOnlyList<string> AlwaysHostedBy(
            CodeGen.Translation.PlcAssignment plc) =>
            Targets.FirstOrDefault(t => t.Plc == plc)?.AlwaysHosts
            ?? (System.Collections.Generic.IReadOnlyList<string>)System.Array.Empty<string>();
    }

    // One module on a device's bus. The XML shape it is written into is EAE's schema and lives in the
    // emitter; these are the facts about the module itself.
    public sealed class HardwareModule
    {
        public string Name { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string TypeNamespace { get; set; } = string.Empty;

        // A bus master: the modules after it go inside its Items rather than beside it.
        public bool Nest { get; set; }

        // Which half of the .hcf binding table lands on this module, or null if it takes none.
        public string? PinPrefix { get; set; }
        public string MasterConfigFile { get; set; } = string.Empty;
        public System.Collections.Generic.List<HardwareModuleProperty> ItemProperties { get; set; } = new();
        public System.Collections.Generic.List<HardwareModuleProperty> ParameterValues { get; set; } = new();

        // Repeats ChannelProperties once per channel, numbered from 0.
        public int Channels { get; set; }
        public System.Collections.Generic.List<HardwareModuleProperty> ChannelProperties { get; set; } = new();
    }

    // kind selects the xsd type the value is written as, or `declared` to name a value config.yaml owns.
    public sealed class HardwareModuleProperty
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public string Kind { get; set; } = "string";
        public string? HwParam { get; set; }
    }

    public sealed class DeviceNet
    {
        // The broadcast domain this network is, where it has its own identity.
        public string DomainUuid { get; set; } = string.Empty;

        public string TargetIp { get; set; } = string.Empty;

        // Components only this target's own I/O hardware can read, so it must host them.
        public System.Collections.Generic.List<string> AlwaysHosts { get; set; } = new();
        public string HostIp { get; set; } = string.Empty;
        public string CouplerIp { get; set; } = string.Empty;
        public string SubnetAddress { get; set; } = string.Empty;
        public string SubnetMask { get; set; } = string.Empty;
        public string Gateway { get; set; } = string.Empty;
    }


    public sealed class Bx1Signal
    {
        public string Signal { get; set; } = string.Empty;
        public int Bit { get; set; }
    }

    public sealed class Bx1Cover
    {
        public string Component { get; set; } = string.Empty;
        public string Event { get; set; } = string.Empty;
        public Bx1Signal? SensorFromHome { get; set; }
        public Bx1Signal? SensorFromWork { get; set; }
        public Bx1Signal? CoilToWork { get; set; }
        public Bx1Signal? CoilToHome { get; set; }
    }

    // One EAE library the generated project references. A version is a fact about the EAE
    // installation this rig is built against, so it is declared beside the addresses rather
    // than compiled into the deployer.
    // The identities EAE keys one device on. A member a device does not need is simply not declared:
    // the RevPi reads its resource id from its coupler, and only the M580 has a rack.
    public sealed class DeviceIdentity
    {
        public string Sysdev { get; set; } = string.Empty;
        public string Resource { get; set; } = string.Empty;
        public string Equipment { get; set; } = string.Empty;
        public string Runtime { get; set; } = string.Empty;
        public string RuntimeType { get; set; } = string.Empty;
        public string Rack { get; set; } = string.Empty;
        public string Cps { get; set; } = string.Empty;
        public string Cpu { get; set; } = string.Empty;
        public string Nic { get; set; } = string.Empty;
        public string Container { get; set; } = string.Empty;
        public string ContainerDomain { get; set; } = string.Empty;
        public string EtherNetIp { get; set; } = string.Empty;
        public string Scanner { get; set; } = string.Empty;
        public string IoBrokerFb { get; set; } = string.Empty;
    }

    // Identities of the EAE installation, shared by every device it hosts.
    public sealed class InstallationIdentity
    {
        public string SwitchEquipment { get; set; } = string.Empty;
        public string DeployPluginProperties { get; set; } = string.Empty;
        public string SystemDeviceProperties { get; set; } = string.Empty;
    }

    // THE PHYSICAL NETWORK GRAPH. A node that is not a deployment target (the switch); a link between
    // two endpoints, each naming a node and which of that node's identities the cable lands on.
    public sealed class TopologyProfile
    {
        public System.Collections.Generic.List<TopologyNode> Nodes { get; set; } = new();
        public System.Collections.Generic.List<TopologyLink> Links { get; set; } = new();
    }

    public sealed class TopologyNode
    {
        public string Id { get; set; } = string.Empty;
        public string Equipment { get; set; } = string.Empty;
        // The template rendered for this node, when the node is one this emitter writes.
        public string Template { get; set; } = string.Empty;
        public bool Emit { get; set; }
    }

    public sealed class TopologyLink
    {
        public string Identifier { get; set; } = string.Empty;
        public TopologyEndpoint From { get; set; } = new();
        public TopologyEndpoint To { get; set; } = new();
        // A link to a device that exists only when this run relocates something onto it.
        public bool RequiresRelocation { get; set; }
    }

    public sealed class TopologyEndpoint
    {
        public string Node { get; set; } = string.Empty;
        // Which identity on that node the cable attaches to: equipment, cpu, nic, etherNetIp, container.
        public string Endpoint { get; set; } = "equipment";
        public string Port { get; set; } = string.Empty;
    }

    public sealed class LibraryReference
    {
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
    }

    public sealed class TargetIdentity
    {
        public CodeGen.Translation.PlcAssignment Plc { get; set; }
        public string ResourceName { get; set; } = string.Empty;
        public string DeviceType { get; set; } = string.Empty;
        // Null where the Type already identifies the device on its own.
        public string? DeviceName { get; set; }
        public string HcfTemplate { get; set; } = string.Empty;

        // The EAE simulation binding's two service ports for this device. Deployment facts of the
        // installation, not of the plant, and distinct per device because the services co-exist.
        public int SimulationDeployPort { get; set; }
        public int SimulationArchivePort { get; set; }
        // The backend that emits this target. A kind is implemented once; a target names one.
        public string BackendKind { get; set; } = string.Empty;
        public DeviceIdentity Identity { get; set; } = new();
        public DeviceNet Network { get; set; } = new();

        // The hardware modules this device carries, IN BUS ORDER. Order is the PreviousItem chain.
        public System.Collections.Generic.List<HardwareModule> HardwareModules { get; set; } = new();

        // The EtherNet/IP coupler type this device's scanner instantiates, and the HwConfiguration
        // model folders that carry it.
        public string EtherNetIpDeviceType { get; set; } = string.Empty;
        public System.Collections.Generic.List<string> HwConfigModelFolders { get; set; } = new();

        // Components only this target's own I/O hardware can read, so it must host them.
        public System.Collections.Generic.List<string> AlwaysHosts { get; set; } = new();

        // Its sysres canvas is device-local, so FBs translate to a local origin.
        public bool DeviceLocalCanvas { get; set; }

        // HOW THIS TARGET RELATES TO ANOTHER ONE. Both are edges, and each is stated ONCE, from the
        // end that owns it; the other end is derived. They replace four booleans that each named a
        // role in one plant ("hosts the feed station", "opens the cover seam") and had to be kept
        // consistent by hand - two of them were the two ends of a single relationship, so a target
        // could declare it carried a chain nobody commanded, or command one nobody carried.
        //
        // This target stands in for another: it takes over components moved off it. A stand-in shares
        // that target's report ring rather than owning one, and exists only when something is moved.
        public string? StandsInFor { get; set; }

        // This target's components are commanded from another one, so its chain is spliced onto that
        // target's ring and is open at BOTH ends here. The commanding target opens the seam.
        public string? ChainCommandedBy { get; set; }

        // The IO broker FB this target hosts, where it has one. An emitted FB that is not a plant
        // component still needs an OWNER, and the target that hosts it is the one place that knows.
        public string? IoBroker { get; set; }

        // One frozen EAE instance id per bootSequence role, in that order.
        public System.Collections.Generic.List<TargetBootFb> BootFbs { get; set; } = new();
    }

    // One boot FB's SHAPE: what it is on every target. Its identity is per target, in TargetBootFb.
    public sealed class BootFbDeclaration
    {
        public string Role { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Namespace { get; set; } = string.Empty;
        // The layout.yml bootFbs row that gives this FB its canvas position.
        public string LayoutKey { get; set; } = string.Empty;
        // A list, not a map: parameters are emitted as child elements and their order is artefact bytes.
        public System.Collections.Generic.List<BootFbParameter> Parameters { get; set; } = new();
    }

    public sealed class BootFbParameter
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    public sealed class TargetBootFb
    {
        public string Role { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
    }

    // One bring-up connection. Both endpoints are '<role>.<PORT>': a bootSequence role, or the START
    // pseudo-role that is the resource's own entry rather than an FB the Mapper emits.
    public sealed class BringUpWire
    {
        public const string ResourceEntry = "START";

        public string From { get; set; } = string.Empty;
        public string To { get; set; } = string.Empty;

        public static string? RoleOf(string endpoint)
        {
            int dot = (endpoint ?? string.Empty).IndexOf('.');
            return dot <= 0 || dot == endpoint!.Length - 1 ? null : endpoint.Substring(0, dot);
        }

        public static string? PortOf(string endpoint)
        {
            int dot = (endpoint ?? string.Empty).IndexOf('.');
            return dot <= 0 || dot == endpoint!.Length - 1 ? null : endpoint.Substring(dot + 1);
        }
    }

    public sealed class Bx1IoProfile
    {
        public System.Collections.Generic.List<Bx1Cover> Covers { get; set; } = new();
        public string SafeStartComponent { get; set; } = string.Empty;
    }
}
