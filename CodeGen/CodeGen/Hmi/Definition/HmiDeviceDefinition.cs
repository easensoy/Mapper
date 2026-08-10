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
        string LogicalDeviceName,
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

    internal static class HmiDeviceLoader
    {
        internal const string FileName = HmiDefinitionLoader.FileName;
        private static readonly string[] Roots = { "system", "device", "topology" };

        private static readonly HmiYaml.Cache<HmiDeviceDefinition> Cached = new(FileName, Parse);

        internal static HmiDeviceDefinition Load() => Cached.Load();

        internal static HmiDeviceDefinition Parse(string yaml)
        {
            var dto = HmiYaml.Deserialize<HmiDefinitionDto>(FileName, yaml);
            var v = new HmiYaml.Validator(FileName);

            var id = v.Section(dto.Identities, "identities");
            var net = v.Section(dto.Network, "network");
            var lib = v.Section(dto.Library, "library");
            var dev = v.Section(dto.LogicalDevice, "logicalDevice");
            var sw = v.Section(dto.Switch, "switch");

            string G(string? value, string key) => v.Guid(value, "identities." + key);

            var guids = new (string Key, string Value)[]
            {
                ("device", G(id.Device, "device")),
                ("workstation", G(id.Workstation, "workstation")),
                ("workstationNic", G(id.WorkstationNic, "workstationNic")),
                ("workstationRuntime", G(id.WorkstationRuntime, "workstationRuntime")),
                ("panel", G(id.Panel, "panel")),
                ("panelRuntime", G(id.PanelRuntime, "panelRuntime")),
                ("panelContainer", G(id.PanelContainer, "panelContainer")),
                ("panelContainerRuntime", G(id.PanelContainerRuntime, "panelContainerRuntime")),
                ("runtimeType", G(id.RuntimeType, "runtimeType")),
                ("containerRuntimeType", G(id.ContainerRuntimeType, "containerRuntimeType")),
                ("simulationService", G(id.SimulationService, "simulationService")),
            };

            // Every identity must be distinct. nullDevice is excluded because it is deliberately the
            // shared null GUID; two REAL identities colliding would silently bind the HMI to the
            // wrong object, which is exactly the failure this check exists to prevent.
            v.Distinct(guids.Where(g => g.Value.Length > 0)
                            .Select(g => ("identities." + g.Key, g.Value.ToLowerInvariant())), "identity GUID");

            var nullDevice = G(id.NullDevice, "nullDevice");
            if (nullDevice.Length > 0 && Guid.TryParse(nullDevice, out var parsed) && parsed != Guid.Empty)
                v.Fail("'identities.nullDevice' must be the all-zero GUID.");

            var hostIp = v.Ip(net.HostIp, "network.hostIp");
            var internalIp = v.Ip(net.InternalRuntimeIp, "network.internalRuntimeIp");
            if (hostIp.Length > 0 && hostIp == internalIp)
                v.Fail("'network.hostIp' and 'network.internalRuntimeIp' must differ - the workstation " +
                       "NIC and the panel container are distinct endpoints.");

            var logicalPort = v.Port(net.LogicalPort, "network.logicalPort");
            var securePort = v.Port(net.SecurePort, "network.securePort");
            if (logicalPort == securePort) v.Fail("'network.logicalPort' and 'network.securePort' must differ.");

            var artefacts = new List<HmiArtefactSpec>();
            foreach (var a in v.List(dto.Artefacts, "artefacts"))
            {
                var template = v.SafeFileName(a.Template, "artefacts[].template");
                var name = v.Text(a.Name, $"artefacts[{template}].name");
                var into = v.Text(a.Into, $"artefacts[{template}].into").ToLowerInvariant();
                if (into.Length > 0 && !Roots.Contains(into))
                    v.Fail($"'artefacts[{template}].into' must be one of {string.Join("/", Roots)} (got '{into}').");

                // The destination may carry a {DeviceId} token, so it is checked for traversal rather
                // than as a plain file name.
                if (name.Contains("..", StringComparison.Ordinal) || name.Contains('/') || name.Contains('\\'))
                    v.Fail($"'artefacts[{template}].name' must stay inside its directory (got '{name}').");

                var register = (a.Register ?? string.Empty).Trim();
                if (register.Length > 0 && !string.Equals(register, "topologyProj", StringComparison.Ordinal))
                    v.Fail($"'artefacts[{template}].register' may only be 'topologyProj' (got '{register}').");

                artefacts.Add(new HmiArtefactSpec(template, into, name, register.Length > 0));
            }

            v.Distinct(artefacts.Select(a => ("artefacts." + a.Template, a.Template)), "template");
            v.Distinct(artefacts.Select(a => ("artefacts." + a.Template, a.Into + "/" + a.Name)), "destination");

            // Every remaining field is validated into a local BEFORE Throw(), so a malformed value
            // here is reported alongside the others rather than being silently accepted because the
            // validator had already decided the file was clean.
            var subnet = v.Ip(net.Subnet, "network.subnet");
            var libName = v.Text(lib.Name, "library.name");
            var libVersion = v.Text(lib.Version, "library.version");
            var devType = v.Text(dev.Type, "logicalDevice.type");
            var devName = v.Text(dev.Name, "logicalDevice.name");
            var switchFile = v.SafeFileName(sw.EquipmentFile, "switch.equipmentFile");

            v.Throw();

            // Constructed exclusively from already-validated locals.
            return new HmiDeviceDefinition(
                guids[0].Value, guids[1].Value, guids[2].Value, guids[3].Value,
                guids[4].Value, guids[5].Value, guids[6].Value, guids[7].Value,
                guids[8].Value, guids[9].Value, guids[10].Value, nullDevice,
                hostIp, internalIp, subnet, logicalPort, securePort,
                libName, libVersion, devType, devName,
                artefacts, switchFile);
        }
    }
}
