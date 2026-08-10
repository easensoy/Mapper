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

// Emits the HMI logical device, its runtime properties and its topology equipment.
//
// Every identity, address, port, filename and version comes from Config/hmi.yml - this file
// holds no deployment constant of its own. The four facts the generated project already states
// authoritatively are DERIVED rather than restated, because a second copy could silently bind the
// panel to the wrong object:
//
//   SolutionId  <- IEC61499.dfbproj
//   SystemDir   <- the System GUID folder, discovered by EaeProjectLayout
//   NetworkId   <- the BroadcastDomain whose ipV4Address matches the configured subnet
//   SwitchId    <- the uuid inside the configured switch equipment file
//
// Each of those fails loudly when absent or ambiguous.
internal static class HmiRuntimeEmitter
{
    internal sealed class EmitResult
    {
        internal List<string> FilesWritten { get; } = new();
        internal List<string> Problems { get; } = new();
        internal int ProjectEntriesAdded { get; set; }
    }

    internal static EmitResult Emit(
        string eaeRoot, CodeGen.Translation.GenerationContext ctx, string firstCanvas,
        HmiDeviceDefinition device)
    {
        var config = ctx.Config;
        ArgumentException.ThrowIfNullOrWhiteSpace(eaeRoot);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(firstCanvas);
        ArgumentNullException.ThrowIfNull(device);

        var result = new EmitResult();
        var templateDir = HmiTemplateLibrary.DeploymentDir(config.TemplateLibraryPath);
        if (!Directory.Exists(templateDir))
        {
            result.Problems.Add($"HMI deployment templates are missing: {templateDir}");
            return result;
        }

        var iecDir = Path.Combine(eaeRoot, "IEC61499");
        var topologyDir = Path.Combine(eaeRoot, "Topology");

        // EaeProjectLayout owns this discovery. A local copy that merely counted directories saw
        // EAE's own RuntimeData folder as a second system and failed the whole generation.
        var systemDir = EaeProjectLayout.FindSystemGuidDir(eaeRoot);
        if (systemDir == null)
        {
            result.Problems.Add($"IEC61499/System has no system GUID folder: {Path.Combine(iecDir, "System")}");
            return result;
        }

        var networkId = ResolveNetworkId(topologyDir, device.Subnet, result);
        var switchId = ResolveSwitchId(topologyDir, device.SwitchEquipmentFile, result);
        if (networkId == null || switchId == null) return result;

        var solutionId = M262TopologyEmitter.ReadProjectGuid(eaeRoot);
        if (string.IsNullOrWhiteSpace(solutionId))
        {
            result.Problems.Add("The solution id could not be read from IEC61499.dfbproj.");
            return result;
        }

        var deviceDir = Path.Combine(systemDir, device.DeviceId);
        Directory.CreateDirectory(deviceDir);
        Directory.CreateDirectory(topologyDir);

        var tokens = new Dictionary<string, string>(device.Tokens(solutionId!, switchId), StringComparer.Ordinal)
        {
            ["NetworkId"] = networkId,
            ["FirstCanvas"] = firstCanvas,
        };

        var roots = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["system"] = systemDir,
            ["device"] = deviceDir,
            ["topology"] = topologyDir,
        };

        // One generic pass over the configured artefact list - no per-file call site.
        foreach (var spec in device.Artefacts)
        {
            var name = Substitute(spec.Name, tokens);
            WriteTemplate(templateDir, spec.Template, Path.Combine(roots[spec.Into], name),
                          tokens, eaeRoot, result);
        }

        var dfbproj = Path.Combine(iecDir, "IEC61499.dfbproj");
        if (File.Exists(dfbproj))
        {
            result.ProjectEntriesAdded += DfbprojRegistrar.RegisterReference(
                dfbproj, device.LibraryName, device.LibraryVersion);
            result.ProjectEntriesAdded += DfbprojRegistrar.RegisterSystemDevice(
                dfbproj, eaeRoot, Path.Combine(systemDir, device.DeviceId + ".sysdev"));
        }
        else
        {
            result.Problems.Add("IEC61499.dfbproj is missing; the HMI logical device was not registered.");
        }

        var folders = FoldersXmlEmitter.Register(config, ctx.Profile.PartialRevPi, device.DeviceId);
        result.Problems.AddRange(folders.Warnings);

        var topologyProj = Path.Combine(topologyDir, "TopologyManager.topologyproj");
        var register = device.Artefacts.Where(a => a.RegisterInTopologyProj)
                                       .Select(a => Substitute(a.Name, tokens)).ToArray();
        if (File.Exists(topologyProj))
            result.ProjectEntriesAdded += M262TopologyEmitter.RegisterInTopologyProj(topologyProj, register);
        else
            result.Problems.Add("TopologyManager.topologyproj is missing; the HMI physical devices were not registered.");

        result.Problems.AddRange(Validate(eaeRoot, systemDir, device, firstCanvas, switchId, tokens));
        return result;
    }

    // ---- derived project facts -------------------------------------------------------------

    private static string? ResolveNetworkId(string topologyDir, string subnet, EmitResult result)
    {
        var matches = new List<string>();
        foreach (var path in Directory.Exists(topologyDir)
                     ? Directory.EnumerateFiles(topologyDir, "BroadcastDomain_*.json")
                     : Enumerable.Empty<string>())
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                if (!root.TryGetProperty("ipV4Address", out var address) ||
                    !string.Equals(address.GetString(), subnet, StringComparison.Ordinal)) continue;
                if (root.TryGetProperty("uuid", out var uuid) && !string.IsNullOrWhiteSpace(uuid.GetString()))
                    matches.Add(uuid.GetString()!);
            }
            catch (JsonException) { }
        }

        if (matches.Count == 1) return matches[0];
        result.Problems.Add(matches.Count == 0
            ? $"No BroadcastDomain declares the configured HMI subnet '{subnet}'."
            : $"{matches.Count} BroadcastDomains declare subnet '{subnet}'; the HMI network is ambiguous.");
        return null;
    }

    private static string? ResolveSwitchId(string topologyDir, string equipmentFile, EmitResult result)
    {
        var path = Path.Combine(topologyDir, equipmentFile);
        if (!File.Exists(path))
        {
            result.Problems.Add($"The configured switch equipment '{equipmentFile}' does not exist in Topology.");
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("uuid", out var uuid) &&
                !string.IsNullOrWhiteSpace(uuid.GetString()))
                return uuid.GetString();
        }
        catch (JsonException ex) { result.Problems.Add($"'{equipmentFile}' is not valid JSON: {ex.Message}"); return null; }

        result.Problems.Add($"'{equipmentFile}' declares no uuid; the HMI wire has no destination.");
        return null;
    }

    // ---- validation --------------------------------------------------------------------------

    private static IReadOnlyList<string> Validate(
        string eaeRoot, string systemDir, HmiDeviceDefinition device, string firstCanvas,
        string switchId, IReadOnlyDictionary<string, string> tokens)
    {
        var problems = new List<string>();
        var deviceDir = Path.Combine(systemDir, device.DeviceId);
        var topologyDir = Path.Combine(eaeRoot, "Topology");

        string Dest(string template) =>
            device.Artefacts.Where(a => a.Template == template)
                .Select(a => Path.Combine(
                    a.Into == "system" ? systemDir : a.Into == "device" ? deviceDir : topologyDir,
                    Substitute(a.Name, tokens)))
                .FirstOrDefault() ?? string.Empty;

        var sysdev = Dest("HMI.sysdev.xml");
        if (!File.Exists(sysdev) ||
            !string.Equals((string?)XDocument.Load(sysdev).Root?.Attribute("Type"), device.LogicalDeviceType,
                StringComparison.Ordinal))
            problems.Add($"The generated {device.LogicalDeviceType} logical device is missing or malformed.");

        var runtimeProperties = Dest("Runtime.Properties.xml");
        if (!File.Exists(runtimeProperties) ||
            !File.ReadAllText(runtimeProperties).Contains($"FirstCanvas={firstCanvas}", StringComparison.Ordinal))
            problems.Add($"The HMI runtime does not start on generated canvas '{firstCanvas}'.");

        var workstation = ReadOrEmpty(Dest("Equipment_Workstation_1.json"));
        if (!workstation.Contains($"\"logicalDeviceId\": \"{device.DeviceId}\"", StringComparison.Ordinal) ||
            !workstation.Contains($"\"ipAddress\": \"{device.HostIp}\"", StringComparison.Ordinal))
            problems.Add("The workstation equipment is not bound to the generated HMI logical device and host IP.");

        var panel = ReadOrEmpty(Dest("Equipment_HMIP6_1.json"));
        if (!panel.Contains($"\"ipAddress\": \"{device.InternalRuntimeIp}\"", StringComparison.Ordinal))
            problems.Add("The HMI panel equipment does not contain the configured internal runtime address.");
        if (!panel.Contains(device.LogicalPort.ToString(), StringComparison.Ordinal) ||
            !panel.Contains(device.SecurePort.ToString(), StringComparison.Ordinal))
            problems.Add("The HMI panel equipment does not carry the configured logical/secure ports.");

        var wire = ReadOrEmpty(Dest("Wire_HMI_to_Switch1.json"));
        if (!wire.Contains($"\"sourceEquipment\": \"{device.PanelId}\"", StringComparison.Ordinal) ||
            !wire.Contains($"\"destinationEquipment\": \"{switchId}\"", StringComparison.Ordinal))
            problems.Add("The HMI panel-to-switch topology wire is missing or malformed.");

        var binding = ReadOrEmpty(Dest("Simulation.Binding.xml"));
        if (binding.Length > 0 && !binding.Contains(device.LogicalPort.ToString(), StringComparison.Ordinal))
            problems.Add("The HMI simulation binding does not carry the configured logical port.");

        var dfbproj = Path.Combine(eaeRoot, "IEC61499", "IEC61499.dfbproj");
        if (!File.Exists(dfbproj) ||
            !XDocument.Load(dfbproj).Descendants().Any(e =>
                ((string?)e.Attribute("Include") ?? string.Empty).Contains(device.DeviceId,
                    StringComparison.OrdinalIgnoreCase)))
            problems.Add("IEC61499.dfbproj does not register the HMI logical device.");

        var folders = Path.Combine(eaeRoot, "General", "Folders.xml");
        if (!File.Exists(folders) ||
            !XDocument.Load(folders).Descendants().Any(e =>
                e.Name.LocalName == "item" &&
                string.Equals(e.Value.Trim(), device.DeviceId, StringComparison.OrdinalIgnoreCase)))
            problems.Add("General/Folders.xml does not expose the HMI logical device.");

        return problems;
    }

    private static string ReadOrEmpty(string path) => File.Exists(path) ? File.ReadAllText(path) : string.Empty;

    // Templates use {{Token}}; the artefact file names in hmi.yml use the lighter {Token}.
    // The double form MUST be substituted first - replacing {Token} first would consume the inner
    // braces of {{Token}} and leave a literal '{value}' in a deployed artefact.
    internal static string Substitute(string text, IReadOnlyDictionary<string, string> tokens)
    {
        foreach (var token in tokens)
            text = text.Replace("{{" + token.Key + "}}", token.Value, StringComparison.Ordinal);
        foreach (var token in tokens)
            text = text.Replace("{" + token.Key + "}", token.Value, StringComparison.Ordinal);
        return text;
    }

    private static void WriteTemplate(
        string templateDir, string templateName, string destination,
        IReadOnlyDictionary<string, string> tokens, string eaeRoot, EmitResult result)
    {
        var source = Path.Combine(templateDir, templateName);
        if (!File.Exists(source))
        {
            result.Problems.Add($"HMI deployment template is missing: {source}");
            return;
        }

        var content = Substitute(File.ReadAllText(source), tokens).TrimEnd('\r', '\n');

        // A surviving placeholder means the template references a token the definition does not
        // supply - shipping it would write a literal {{...}} into a deployed artefact.
        var unresolved = System.Text.RegularExpressions.Regex.Matches(content, @"\{\{[A-Za-z]+\}\}")
            .Select(m => m.Value).Distinct().ToList();
        if (unresolved.Count > 0)
        {
            result.Problems.Add($"'{templateName}' has unresolved placeholder(s): {string.Join(", ", unresolved)}.");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination) &&
            string.Equals(File.ReadAllText(destination), content, StringComparison.Ordinal))
            return;

        File.WriteAllText(destination, content);
        result.FilesWritten.Add(Path.GetRelativePath(eaeRoot, destination));
    }
}
