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

        var solutionId = EaeProjectLayout.ReadProjectGuid(eaeRoot);
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
            result.ProjectEntriesAdded += EaeProjectLayout.RegisterInTopologyProj(topologyProj, register);
        else
            result.Problems.Add("TopologyManager.topologyproj is missing; the HMI physical devices were not registered.");

        result.Problems.AddRange(Validate(eaeRoot, systemDir, device, tokens));
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

    // Everything the definition DECLARED must be on disk, and every value it supplies must have been
    // consumed by something - a token nothing reads is dead configuration, and an artefact that
    // dropped one is a deployment bound to a stale value. Substitution itself is already guaranteed
    // upstream: WriteTemplate refuses to write a file with a surviving placeholder.
    //
    // Driven entirely from device.Artefacts, so no artefact file name appears here. Naming them made
    // the check cover only the four that were spelled out, and silently skip any newly declared one.
    private static IReadOnlyList<string> Validate(
        string eaeRoot, string systemDir, HmiDeviceDefinition device,
        IReadOnlyDictionary<string, string> tokens)
    {
        var problems = new List<string>();
        var roots = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["system"] = systemDir,
            ["device"] = Path.Combine(systemDir, device.DeviceId),
            ["topology"] = Path.Combine(eaeRoot, "Topology"),
        };

        var rendered = new List<(string Name, string Text)>();
        foreach (var a in device.Artefacts)
        {
            var path = Path.Combine(roots[a.Into], Substitute(a.Name, tokens));
            if (File.Exists(path)) rendered.Add((Path.GetFileName(path), File.ReadAllText(path)));
            else problems.Add($"declared artefact '{a.Template}' was not written under {a.Into}.");
        }

        foreach (var t in tokens.Where(t => t.Value.Length > 0 &&
                     !rendered.Any(r => r.Text.Contains(t.Value, StringComparison.Ordinal))))
            problems.Add($"no deployed HMI artefact carries '{t.Key}' - it is configured but unused.");

        // The logical device is found by its EAE extension, not by a configured name: the type it
        // declares is a structural fact about the rendered XML, not a substituted value.
        var sysdev = rendered.FirstOrDefault(r => r.Name.EndsWith(".sysdev", StringComparison.OrdinalIgnoreCase));
        if (sysdev.Text == null ||
            !string.Equals((string?)XDocument.Parse(sysdev.Text).Root?.Attribute("Type"), device.LogicalDeviceType,
                StringComparison.Ordinal))
            problems.Add($"The generated {device.LogicalDeviceType} logical device is missing or malformed.");

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
