using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Devices.M262;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Services;
using CodeGen.Translation;

namespace CodeGen.Devices.Core
{
    // Emits Topology Physical-Views NETWORK objects (the L2 switch + the Wire JSONs) by RENDERING THE
    // DECLARED GRAPH in `device.yml topology:`. Which node is cabled to which, on which port, and which
    // of a node's identities the cable lands on are all declared; this emitter decides none of it.
    //
    // It used to carry five wires as literals, each naming a controller and a port number, with the
    // switch's port allocation stated only in a comment - so two devices claiming one port was
    // unrepresentable as an error, and a new device meant editing this file.
    public static class TopologyNetworkEmitter
    {

        public sealed class EmitResult
        {
            public System.Collections.Generic.List<string> FilesWritten { get; } = new();
            public System.Collections.Generic.List<string> Warnings { get; } = new();
            public int TopologyProjEntriesAdded { get; set; }
        }

        public static EmitResult Emit(GenerationContext ctx)
        {
            var cfg = ctx.Cfg;
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            var result = new EmitResult();

            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
            if (string.IsNullOrEmpty(eaeRoot))
            {
                throw new InvalidOperationException(
                    "[Topology] the EAE project root was not found, so the network and its wires were "
                    + "not emitted. EAE rejects a topology whose wires reference devices it cannot "
                    + "resolve, so the whole import would fail. Generation ABORTED.");
            }

            var topologyDir = Path.Combine(eaeRoot, "Topology");
            if (!Directory.Exists(topologyDir))
            {
                throw new InvalidOperationException(
                    $"[Topology] the Topology folder is missing at '{topologyDir}', so the network and "
                    + "its wires were not emitted. Generation ABORTED.");
            }

            // DomainTag must be the live SolutionId; a zero DomainTag fails topology-import.
            var solutionId = EaeProjectLayout.ReadProjectGuid(eaeRoot) ?? Artefacts.EaeAbi.UnknownSolution;

            var registerNames = new List<string>();
            var graph = cfg.Devices.Topology;

            // Nodes that are not deployment targets (the switch), in declaration order.
            foreach (var node in graph.Nodes.Where(n => n.Emit))
            {
                var name = "Equipment_" + node.Id + ".json";
                ForceWriteJson(topologyDir, name, BuildNodeJson(cfg, node, solutionId), result, eaeRoot);
                registerNames.Add(name);
            }

            // Links in DECLARATION order, which is the order they are registered in and therefore the
            // order TopologyManager.topologyproj carries them.
            foreach (var link in graph.Links)
            {
                if (link.RequiresRelocation && !ctx.Profile.HasAssignments) continue;
                var name = "Wire_" + link.Identifier + ".json";
                ForceWriteJson(topologyDir, name, BuildWireJson(cfg,
                    identifier:                 link.Identifier,
                    sourceEquipmentUuid:        Endpoint(cfg, link, link.From),
                    sourcePortIdentifier:       link.From.Port,
                    destinationEquipmentUuid:   Endpoint(cfg, link, link.To),
                    destinationPortIdentifier:  link.To.Port), result, eaeRoot);
                registerNames.Add(name);
            }

            var topologyProj = Path.Combine(topologyDir, "TopologyManager.topologyproj");
            if (File.Exists(topologyProj))
            {
                result.TopologyProjEntriesAdded = EaeProjectLayout.RegisterInTopologyProj(
                    topologyProj, registerNames.ToArray());
            }
            else
            {
                result.Warnings.Add(
                    "TopologyManager.topologyproj missing — Switch + Wire JSONs " +
                    "written but not registered with the TopologyManager build target.");
            }

            // A wire whose endpoint UUID is declared by no Equipment makes TopologyManager 500 the entire import.
            SweepOrphanWires(topologyDir, Path.Combine(topologyDir, "TopologyManager.topologyproj"), result);

            return result;
        }

        // Deletes + de-registers orphan wires. Conservative: if no equipment UUIDs are readable it sweeps nothing.
        static void SweepOrphanWires(string topologyDir, string topologyProj, EmitResult result)
        {
            try
            {
                var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var uuidRx = new Regex("\"uuid\"\\s*:\\s*\"([0-9a-fA-F-]{36})\"");
                foreach (var eq in Directory.EnumerateFiles(topologyDir, "Equipment_*.json"))
                {
                    string text;
                    try { text = File.ReadAllText(eq); } catch { continue; }
                    foreach (Match m in uuidRx.Matches(text)) known.Add(m.Groups[1].Value);
                }
                if (known.Count == 0) return;   // safety: never sweep blind

                const string Zero = Artefacts.EaeAbi.NullUuid;
                var endpointRx = new Regex(
                    "\"(?:sourceEquipment|destinationEquipment)\"\\s*:\\s*\"([0-9a-fA-F-]{36})\"");

                foreach (var wire in Directory.EnumerateFiles(topologyDir, "Wire_*.json").ToList())
                {
                    string text;
                    try { text = File.ReadAllText(wire); } catch { continue; }

                    bool orphan = false;
                    string badUuid = string.Empty;
                    foreach (Match m in endpointRx.Matches(text))
                    {
                        var u = m.Groups[1].Value;
                        if (string.Equals(u, Zero, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!known.Contains(u)) { orphan = true; badUuid = u; break; }
                    }
                    if (!orphan) continue;

                    var name = Path.GetFileName(wire);
                    try { File.Delete(wire); } catch { /* best-effort */ }
                    UnregisterFromTopologyProj(topologyProj, name);
                    result.Warnings.Add(
                        $"[Topology] Swept ORPHAN wire {name} — endpoint UUID {badUuid} is declared by no " +
                        "Equipment (dangling wire → EAE topology-import 500). De-registered from topologyproj.");
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"[Topology] the orphan-wire sweep failed: {ex.Message} A wire left pointing at a "
                    + "device no Equipment file declares makes EAE reject the entire topology import. "
                    + "Generation ABORTED.", ex);
            }
        }

        // A registration left behind for a file that is gone is the dangling reference EAE rejects the
        // whole topology on, so a failure here is reported by the caller rather than swallowed.
        static void UnregisterFromTopologyProj(string topologyProj, string fileName)
        {
            if (!File.Exists(topologyProj)) return;
            var doc = XDocument.Load(topologyProj);
            var ns = doc.Root!.GetDefaultNamespace();
            var nodes = doc.Descendants(ns + "None")
                .Where(e => string.Equals((string?)e.Attribute("Include"), fileName,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (nodes.Count == 0) return;
            foreach (var n in nodes) n.Remove();
            doc.Save(topologyProj);
        }

        // Delete before rewrite so a manual import or EAE merge cannot leave hybrid content behind.
        static void ForceWriteJson(string dir, string fileName, string content,
            EmitResult result, string eaeRoot)
        {
            var path = Path.Combine(dir, fileName);
            if (File.Exists(path))
            {
                try { File.Delete(path); }
                catch (Exception ex)
                {
                    result.Warnings.Add(
                        $"Could not delete stale {fileName} before re-emit: {ex.Message}. " +
                        "New content will overwrite but any merge corruption may persist.");
                }
            }
            File.WriteAllText(path, content);
            result.FilesWritten.Add(Path.GetRelativePath(eaeRoot, path));
        }

        // Both documents live in the Template Library, so their bytes - including their line
        // endings - come from a file rather than from the newlines of this .cs.
        // The uuid a cable end lands on. Every declared endpoint is validated when device.yml loads, so
        // reaching an empty one here means a node whose identity is blank - which would write a wire EAE
        // cannot resolve, and ONE unresolvable wire fails the whole topology import with a 500 that names
        // nothing. It stops the run instead.
        static string Endpoint(Configuration.CompilerConfiguration cfg,
            Configuration.TopologyLink link, Configuration.TopologyEndpoint e)
        {
            var node = cfg.Devices.Topology.Nodes.FirstOrDefault(n =>
                string.Equals(n.Id, e.Node, StringComparison.OrdinalIgnoreCase));
            var uuid = node != null
                ? node.Equipment
                : Configuration.DeviceConfig.EndpointUuid(
                    cfg.Devices.Targets.FirstOrDefault(t =>
                        string.Equals(t.Plc.Name, e.Node, StringComparison.OrdinalIgnoreCase))?.Identity
                    ?? new Configuration.DeviceIdentity(), e.Endpoint);

            if (string.IsNullOrWhiteSpace(uuid))
                throw new InvalidOperationException(
                    $"[Topology] link '{link.Identifier}' attaches to '{e.Endpoint}' on '{e.Node}', which " +
                    "resolves to no equipment uuid. EAE rejects the whole topology on one unresolvable " +
                    "endpoint, so nothing was written. Generation ABORTED.");
            return uuid;
        }

        static string BuildNodeJson(Configuration.CompilerConfiguration cfg,
            Configuration.TopologyNode node, string solutionId) =>
            TemplateDocument.Load(cfg, Path.Combine("Topology", node.Template),
                new Dictionary<string, string>
                {
                    ["SwitchUuid"] = node.Equipment,
                    ["SolutionId"] = solutionId,
                });

        static string BuildWireJson(Configuration.CompilerConfiguration cfg, string identifier,
                                    string sourceEquipmentUuid, string sourcePortIdentifier,
                                    string destinationEquipmentUuid, string destinationPortIdentifier) =>
            TemplateDocument.Load(cfg, @"Topology\Wire.json",
                new Dictionary<string, string>
                {
                    ["Identifier"] = identifier,
                    ["SourceEquipment"] = sourceEquipmentUuid,
                    ["SourcePort"] = sourcePortIdentifier,
                    ["DestinationEquipment"] = destinationEquipmentUuid,
                    ["DestinationPort"] = destinationPortIdentifier,
                });
    }
}
