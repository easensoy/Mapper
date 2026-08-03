using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Devices.Core;
using CodeGen.Devices.M262;

namespace CodeGen.Hmi;

internal static class HmiRuntimeEmitter
{
    internal const string DeviceId = "a441cfb6-5523-4d2c-a152-aacecdcef78e";

    private const string SystemId = "00000000-0000-0000-0000-000000000000";
    private const string DeviceNetworkId = "db72f221-ece1-4b82-8132-731ce655044e";
    private const string WorkstationId = "c2d95ca9-038c-4234-94e7-dbd0d2e177d6";
    private const string WorkstationNicId = "d31ea8d5-6dbe-4894-9d5d-5b5885b68ccc";
    private const string WorkstationRuntimeId = "0a062e36-bf86-4578-83af-e7ea841f23ab";
    private const string PanelId = "25e3cc7c-d98a-486a-963b-c5ab645331c9";
    private const string PanelContainerId = "87e8a4b7-4c64-42cd-aa81-4c610b6e237c";
    private const string PanelContainerRuntimeId = "d0643772-7619-49ca-92e1-54a8649dbcac";
    private const string PanelRuntimeId = "3a2ddbd2-8267-4ebb-a73e-6743b3db76aa";
    private const string SwitchId = "11111111-2222-3333-4444-000000000060";
    private const string RuntimeTypeId = "422ee926-a34a-4ab5-9e8f-dce0782579f0";
    private const string ContainerRuntimeTypeId = "29797a55-a6b8-47c4-9c06-e8a42b1a38b5";
    private const string EmptyDeviceId = "00000000-0000-0000-0000-000000000000";
    private const string SimulationServiceId = "F7C90C9D-BD8B-4D0B-B8DE-C659AF6EABCC";
    private const string EmptyPropertiesFile = "E0601B81-4A3A-4A96-B6C2-007BDC680D59.Properties.xml";
    private const string RuntimePropertiesFile = "F513CAE3-7194-4086-936C-02912EA0B352.Properties.xml";

    internal sealed class EmitResult
    {
        internal List<string> FilesWritten { get; } = new();
        internal List<string> Problems { get; } = new();
        internal int ProjectEntriesAdded { get; set; }
    }

    internal static EmitResult Emit(string eaeRoot, MapperConfig config, string firstCanvas)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eaeRoot);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(firstCanvas);

        var result = new EmitResult();
        var templateDir = HmiTemplateLibrary.DeploymentDir(config.TemplateLibraryPath);
        if (!Directory.Exists(templateDir))
        {
            result.Problems.Add($"HMI deployment templates are missing: {templateDir}");
            return result;
        }
        if (string.IsNullOrWhiteSpace(config.HmiHostIp) ||
            string.IsNullOrWhiteSpace(config.HmiInternalRuntimeIp) ||
            config.HmiLogicalPort <= 0 ||
            config.HmiSecurePort <= 0)
        {
            result.Problems.Add("HMI deployment settings in Config/device.yml are incomplete.");
            return result;
        }

        var iecDir = Path.Combine(eaeRoot, "IEC61499");
        var systemDir = Path.Combine(iecDir, "System", SystemId);
        var deviceDir = Path.Combine(systemDir, DeviceId);
        var topologyDir = Path.Combine(eaeRoot, "Topology");
        Directory.CreateDirectory(deviceDir);
        Directory.CreateDirectory(topologyDir);

        var tokens = Tokens(
            M262TopologyEmitter.ReadProjectGuid(eaeRoot) ?? EmptyDeviceId,
            FindDeviceNetwork(topologyDir) ?? DeviceNetworkId,
            firstCanvas,
            config);

        WriteTemplate(templateDir, "HMI.sysdev.xml", Path.Combine(systemDir, DeviceId + ".sysdev"),
            tokens, eaeRoot, result);
        WriteTemplate(templateDir, "Simulation.Binding.xml",
            Path.Combine(deviceDir, DeviceId + ".Simulation.Binding.xml"), tokens, eaeRoot, result);
        WriteTemplate(templateDir, "Empty.Properties.xml",
            Path.Combine(deviceDir, EmptyPropertiesFile), tokens, eaeRoot, result);
        WriteTemplate(templateDir, "Runtime.Properties.xml",
            Path.Combine(deviceDir, RuntimePropertiesFile), tokens, eaeRoot, result);
        WriteTemplate(templateDir, "Equipment_Workstation_1.json",
            Path.Combine(topologyDir, "Equipment_Workstation_1.json"), tokens, eaeRoot, result);
        WriteTemplate(templateDir, "Equipment_HMIP6_1.json",
            Path.Combine(topologyDir, "Equipment_HMIP6_1.json"), tokens, eaeRoot, result);
        WriteTemplate(templateDir, "Wire_HMI_to_Switch1.json",
            Path.Combine(topologyDir, "Wire_HMI_to_Switch1.json"), tokens, eaeRoot, result);

        var dfbproj = Path.Combine(iecDir, "IEC61499.dfbproj");
        if (File.Exists(dfbproj))
        {
            result.ProjectEntriesAdded += DfbprojRegistrar.RegisterReference(
                dfbproj, "SE.Standard", "24.1.0.4");
            result.ProjectEntriesAdded += DfbprojRegistrar.RegisterSystemDevice(
                dfbproj, eaeRoot, Path.Combine(systemDir, DeviceId + ".sysdev"));
        }
        else
        {
            result.Problems.Add("IEC61499.dfbproj is missing; the HMI logical device was not registered.");
        }

        var folders = FoldersXmlEmitter.Register(config, DeviceId);
        result.Problems.AddRange(folders.Warnings);

        var topologyProj = Path.Combine(topologyDir, "TopologyManager.topologyproj");
        if (File.Exists(topologyProj))
        {
            result.ProjectEntriesAdded += M262TopologyEmitter.RegisterInTopologyProj(topologyProj, new[]
            {
                "Equipment_Workstation_1.json",
                "Equipment_HMIP6_1.json",
                "Wire_HMI_to_Switch1.json"
            });
        }
        else
        {
            result.Problems.Add("TopologyManager.topologyproj is missing; the HMI physical devices were not registered.");
        }

        result.Problems.AddRange(Validate(eaeRoot, config, firstCanvas));
        return result;
    }

    private static Dictionary<string, string> Tokens(
        string solutionId,
        string networkId,
        string firstCanvas,
        MapperConfig config) =>
        new(StringComparer.Ordinal)
        {
            ["DeviceId"] = DeviceId,
            ["SolutionId"] = solutionId,
            ["NetworkId"] = networkId,
            ["FirstCanvas"] = firstCanvas,
            ["HostIp"] = config.HmiHostIp,
            ["InternalRuntimeIp"] = config.HmiInternalRuntimeIp,
            ["LogicalPort"] = config.HmiLogicalPort.ToString(),
            ["SecurePort"] = config.HmiSecurePort.ToString(),
            ["SimulationServiceId"] = SimulationServiceId,
            ["WorkstationId"] = WorkstationId,
            ["WorkstationNicId"] = WorkstationNicId,
            ["WorkstationRuntimeId"] = WorkstationRuntimeId,
            ["PanelId"] = PanelId,
            ["PanelContainerId"] = PanelContainerId,
            ["PanelContainerRuntimeId"] = PanelContainerRuntimeId,
            ["PanelRuntimeId"] = PanelRuntimeId,
            ["SwitchId"] = SwitchId,
            ["RuntimeTypeId"] = RuntimeTypeId,
            ["ContainerRuntimeTypeId"] = ContainerRuntimeTypeId,
            ["EmptyDeviceId"] = EmptyDeviceId
        };

    private static IReadOnlyList<string> Validate(string eaeRoot, MapperConfig config, string firstCanvas)
    {
        var problems = new List<string>();
        var systemDir = Path.Combine(eaeRoot, "IEC61499", "System", SystemId);
        var deviceDir = Path.Combine(systemDir, DeviceId);
        var sysdev = Path.Combine(systemDir, DeviceId + ".sysdev");
        var runtimeProperties = Path.Combine(deviceDir, RuntimePropertiesFile);
        var workstation = Path.Combine(eaeRoot, "Topology", "Equipment_Workstation_1.json");
        var panel = Path.Combine(eaeRoot, "Topology", "Equipment_HMIP6_1.json");
        var wire = Path.Combine(eaeRoot, "Topology", "Wire_HMI_to_Switch1.json");

        if (!File.Exists(sysdev) ||
            !string.Equals((string?)XDocument.Load(sysdev).Root?.Attribute("Type"), "HMI_NET",
                StringComparison.Ordinal))
            problems.Add("The generated HMI_NET logical device is missing or malformed.");

        if (!File.Exists(runtimeProperties) ||
            !File.ReadAllText(runtimeProperties).Contains($"FirstCanvas={firstCanvas}", StringComparison.Ordinal))
            problems.Add($"The HMI runtime does not start on generated canvas '{firstCanvas}'.");

        var workstationText = File.Exists(workstation) ? File.ReadAllText(workstation) : string.Empty;
        if (!workstationText.Contains($"\"logicalDeviceId\": \"{DeviceId}\"", StringComparison.Ordinal) ||
            !workstationText.Contains($"\"ipAddress\": \"{config.HmiHostIp}\"", StringComparison.Ordinal))
            problems.Add("Workstation_1 is not bound to the generated HMI logical device and host IP.");

        var panelText = File.Exists(panel) ? File.ReadAllText(panel) : string.Empty;
        if (!panelText.Contains($"\"ipAddress\": \"{config.HmiInternalRuntimeIp}\"",
                StringComparison.Ordinal))
            problems.Add("HMIP6_1 does not contain the configured internal Soft_dPAC runtime.");

        var wireText = File.Exists(wire) ? File.ReadAllText(wire) : string.Empty;
        if (!wireText.Contains($"\"sourceEquipment\": \"{PanelId}\"", StringComparison.Ordinal) ||
            !wireText.Contains($"\"destinationEquipment\": \"{SwitchId}\"", StringComparison.Ordinal))
            problems.Add("The HMI panel-to-switch topology wire is missing or malformed.");

        var dfbproj = Path.Combine(eaeRoot, "IEC61499", "IEC61499.dfbproj");
        if (!File.Exists(dfbproj) ||
            !XDocument.Load(dfbproj).Descendants().Any(e =>
                ((string?)e.Attribute("Include") ?? string.Empty).Contains(DeviceId,
                    StringComparison.OrdinalIgnoreCase)))
            problems.Add("IEC61499.dfbproj does not register the HMI logical device.");

        var folders = Path.Combine(eaeRoot, "General", "Folders.xml");
        if (!File.Exists(folders) ||
            !XDocument.Load(folders).Descendants().Any(e =>
                e.Name.LocalName == "item" &&
                string.Equals(e.Value.Trim(), DeviceId, StringComparison.OrdinalIgnoreCase)))
            problems.Add("General/Folders.xml does not expose the HMI logical device.");

        return problems;
    }

    private static string? FindDeviceNetwork(string topologyDir)
    {
        foreach (var path in Directory.EnumerateFiles(topologyDir, "BroadcastDomain_*.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                if (!root.TryGetProperty("ipV4Address", out var address) ||
                    !string.Equals(address.GetString(), "192.168.1.0", StringComparison.Ordinal))
                    continue;
                if (root.TryGetProperty("uuid", out var uuid) && !string.IsNullOrWhiteSpace(uuid.GetString()))
                    return uuid.GetString();
            }
            catch (JsonException) { }
        }
        return null;
    }

    private static void WriteTemplate(
        string templateDir,
        string templateName,
        string destination,
        IReadOnlyDictionary<string, string> tokens,
        string eaeRoot,
        EmitResult result)
    {
        var source = Path.Combine(templateDir, templateName);
        if (!File.Exists(source))
        {
            result.Problems.Add($"HMI deployment template is missing: {source}");
            return;
        }

        var content = File.ReadAllText(source);
        foreach (var token in tokens)
            content = content.Replace("{{" + token.Key + "}}", token.Value, StringComparison.Ordinal);
        content = content.TrimEnd('\r', '\n');

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination) &&
            string.Equals(File.ReadAllText(destination), content, StringComparison.Ordinal))
            return;

        File.WriteAllText(destination, content);
        result.FilesWritten.Add(Path.GetRelativePath(eaeRoot, destination));
    }
}
