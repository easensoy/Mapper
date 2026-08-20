using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeGen.Hmi
{
    // One deployment artefact: a template rendered to a destination under a named root.
    internal sealed record HmiArtefactSpec(string Template, string Into, string Name, bool RegisterInTopologyProj);

    // The validated HMI deployment definition. Every GUID, address, port, filename and version the
    // runtime emitter needs, and nothing the generated project already states authoritatively.
    internal sealed record HmiDeviceDefinition(
        string DeviceId,
        string WorkstationId,
        string WorkstationNicId,
        string WorkstationRuntimeId,
        string PanelId,
        string PanelRuntimeId,
        string PanelContainerId,
        string PanelContainerRuntimeId,
        string RuntimeTypeId,
        string ContainerRuntimeTypeId,
        string SimulationServiceId,
        string NullDeviceId,
        string HostIp,
        string InternalRuntimeIp,
        string Subnet,
        int LogicalPort,
        int SecurePort,
        string LibraryName,
        string LibraryVersion,
        string LogicalDeviceType,
        IReadOnlyList<HmiArtefactSpec> Artefacts,
        string SwitchEquipmentFile)
    {
        // The substitution tokens every deployment template is rendered with. Declared once here so a
        // template can never reference a token the definition does not supply - the renderer checks
        // that no placeholder survives.
        internal IReadOnlyDictionary<string, string> Tokens(string solutionId, string switchId) =>
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["DeviceId"] = DeviceId,
                ["SolutionId"] = solutionId,
                ["SwitchId"] = switchId,
                ["WorkstationId"] = WorkstationId,
                ["WorkstationNicId"] = WorkstationNicId,
                ["WorkstationRuntimeId"] = WorkstationRuntimeId,
                ["PanelId"] = PanelId,
                ["PanelRuntimeId"] = PanelRuntimeId,
                ["PanelContainerId"] = PanelContainerId,
                ["PanelContainerRuntimeId"] = PanelContainerRuntimeId,
                ["RuntimeTypeId"] = RuntimeTypeId,
                ["ContainerRuntimeTypeId"] = ContainerRuntimeTypeId,
                ["SimulationServiceId"] = SimulationServiceId,
                ["EmptyDeviceId"] = NullDeviceId,
                ["HostIp"] = HostIp,
                ["InternalRuntimeIp"] = InternalRuntimeIp,
                                ["LogicalPort"] = LogicalPort.ToString(),
                ["SecurePort"] = SecurePort.ToString(),
                                            };
    }

    // The deployment half of the ONE definition. It is read from the SAME already-parsed hmi.yml
    // node tree as everything else - there is no second file, no second cache and no second read.
    // HmiDefinitionLoader calls Bind() while it is building the root definition, and the result
    // hangs off HmiDefinition.Device.
    internal static class HmiDeviceBinder
    {
        private static readonly string[] Roots = { "system", "device", "topology" };

        internal static HmiDeviceDefinition Bind(HmiYaml.Node root, HmiYaml.Validator v)
        {
            var id = root.Sec("identities");
            var net = root.Sec("network");
            var lib = root.Sec("library");
            var dev = root.Sec("logicalDevice");

            var keys = new[]
            {
                "device", "workstation", "workstationNic", "workstationRuntime",
                "panel", "panelRuntime", "panelContainer", "panelContainerRuntime",
                "runtimeType", "containerRuntimeType", "simulationService",
            };
            var guids = keys.Select(k => (Key: k, Value: id.Guid(k))).ToArray();

            // Every identity must be distinct. nullDevice is excluded because it is deliberately the
            // shared null GUID; two REAL identities colliding would silently bind the HMI to the
            // wrong object, which is exactly the failure this check exists to prevent.
            v.Distinct(guids.Where(g => g.Value.Length > 0)
                            .Select(g => ("identities." + g.Key, g.Value.ToLowerInvariant())), "identity GUID");

            var nullDevice = id.Guid("nullDevice");
            if (nullDevice.Length > 0 && Guid.TryParse(nullDevice, out var parsed) && parsed != Guid.Empty)
                v.Fail("'identities.nullDevice' must be the all-zero GUID.");

            var hostIp = net.Ip("hostIp");
            var internalIp = net.Ip("internalRuntimeIp");
            if (hostIp.Length > 0 && hostIp == internalIp)
                v.Fail("'network.hostIp' and 'network.internalRuntimeIp' must differ - the workstation " +
                       "NIC and the panel container are distinct endpoints.");

            var logicalPort = net.Int("logicalPort", 1, 65535);
            var securePort = net.Int("securePort", 1, 65535);
            if (logicalPort == securePort) v.Fail("'network.logicalPort' and 'network.securePort' must differ.");

            var artefacts = new List<HmiArtefactSpec>();
            foreach (var a in root.Seq("artefacts"))
            {
                var template = a.SafeFileName("template");
                var name = a.Text("name");
                var into = a.Text("into").ToLowerInvariant();
                if (into.Length > 0 && !Roots.Contains(into))
                    v.Fail($"'{a.Path}.into' must be one of {string.Join("/", Roots)} (got '{into}').");

                // The destination may carry a {DeviceId} token, so it is checked for traversal rather
                // than as a plain file name.
                if (name.Contains("..", StringComparison.Ordinal) || name.Contains('/') || name.Contains('\\'))
                    v.Fail($"'{a.Path}.name' must stay inside its directory (got '{name}').");

                var register = a.Opt("register");
                if (register != null && !string.Equals(register, "topologyProj", StringComparison.Ordinal))
                    v.Fail($"'{a.Path}.register' may only be 'topologyProj' (got '{register}').");

                artefacts.Add(new HmiArtefactSpec(template, into, name, register != null));
            }

            v.Distinct(artefacts.Select(a => ("artefacts." + a.Template, a.Template)), "template");
            v.Distinct(artefacts.Select(a => ("artefacts." + a.Template, a.Into + "/" + a.Name)), "destination");

            // Every remaining field is validated into a local BEFORE Throw(), so a malformed value
            // here is reported alongside the others rather than being silently accepted because the
            // validator had already decided the file was clean.
            var subnet = net.Ip("subnet");
            var libName = lib.Text("name");
            var libVersion = lib.Text("version");
            var devType = dev.Text("type");
            var switchFile = root.Sec("switch").SafeFileName("equipmentFile");

            // No Throw here: the ROOT loader owns the single Throw, so a deployment fault is reported
            // together with every presentation and capability fault instead of masking them.
            return new HmiDeviceDefinition(
                guids[0].Value, guids[1].Value, guids[2].Value, guids[3].Value,
                guids[4].Value, guids[5].Value, guids[6].Value, guids[7].Value,
                guids[8].Value, guids[9].Value, guids[10].Value, nullDevice,
                hostIp, internalIp, subnet, logicalPort, securePort,
                libName, libVersion, devType,
                artefacts, switchFile);
        }
    }
}
