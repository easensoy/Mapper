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
    // Emits Topology Physical-Views NETWORK objects (L2 Switch_1 + Wire JSONs). A wire's M580 destination must
    // be the nested CPU UUID that owns ETH1, not the rack root.
    public static class TopologyNetworkEmitter
    {
        static string Switch1Uuid(Configuration.CompilerConfiguration cfg) =>
            cfg.Devices.Installation.SwitchEquipment;

        // Endpoint UUIDs MUST match what M262TopologyEmitter + Station2DeviceEmitter write.
        static string M262EquipmentUuid => CodeGen.Devices.M262.M262TopologyEmitter.DefaultM262Uuid;
        static string M580CpuUuid => Station2DeviceEmitter.M580CpuUuid;
        const string FallbackSolutionUuid = "00000000-0000-0000-0000-000000000000";

        static string Bx1EtherNetIpUuid => Station2DeviceEmitter.BX1EtherNetIpUuid;  // EtherNetIPDevice_1
        static string Bx1HmiB1XUuid => Station2DeviceEmitter.BX1EquipmentUuid;   // HMIB1X_1 (BX1 panel)
        // RevPi NIC_2 uuid — MUST match RevPiDeviceEmitter.RevPiNicUuid.
        static string RevPiNicUuid => CodeGen.Devices.RevPi.RevPiDeviceEmitter.NicUuid;

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
            var solutionId = EaeProjectLayout.ReadProjectGuid(eaeRoot) ?? FallbackSolutionUuid;

            ForceWriteJson(topologyDir, "Equipment_Switch_1.json", BuildSwitchJson(cfg, solutionId), result, eaeRoot);
            ForceWriteJson(topologyDir, "Wire_M262_to_Switch1.json", BuildWireJson(cfg,
                identifier:                 "M262_to_Switch1",
                sourceEquipmentUuid:        M262EquipmentUuid,
                sourcePortIdentifier:       "Ethernet1",
                destinationEquipmentUuid:   Switch1Uuid(cfg),
                destinationPortIdentifier:  "Port1"), result, eaeRoot);
            ForceWriteJson(topologyDir, "Wire_Switch1_to_M580.json", BuildWireJson(cfg,
                identifier:                 "Switch1_to_M580",
                sourceEquipmentUuid:        Switch1Uuid(cfg),
                sourcePortIdentifier:       "Port2",
                destinationEquipmentUuid:   M580CpuUuid,
                destinationPortIdentifier:  "ETH1"), result, eaeRoot);

            var registerNames = new List<string>
            {
                "Equipment_Switch_1.json",
                "Wire_M262_to_Switch1.json",
                "Wire_Switch1_to_M580.json",
            };

            // BX1 EtherNet/IP daisy-chain: Switch Port3 -> coupler -> HMIB1X LAN1.
            {
                ForceWriteJson(topologyDir, "Wire_Switch1_to_EtherNetIP.json", BuildWireJson(cfg,
                    identifier:                 "Switch1_to_EtherNetIP",
                    sourceEquipmentUuid:        Switch1Uuid(cfg),
                    sourcePortIdentifier:       "Port3",
                    destinationEquipmentUuid:   Bx1EtherNetIpUuid,
                    destinationPortIdentifier:  "Port2"), result, eaeRoot);
                ForceWriteJson(topologyDir, "Wire_EtherNetIP_to_BX1.json", BuildWireJson(cfg,
                    identifier:                 "EtherNetIP_to_BX1",
                    sourceEquipmentUuid:        Bx1EtherNetIpUuid,
                    sourcePortIdentifier:       "Port1",
                    destinationEquipmentUuid:   Bx1HmiB1XUuid,
                    destinationPortIdentifier:  "LAN1"), result, eaeRoot);
                registerNames.Add("Wire_Switch1_to_EtherNetIP.json");
                registerNames.Add("Wire_EtherNetIP_to_BX1.json");
            }

            // RevPi connects to free Switch Port4 via its NIC_2 (Port1=M262, Port2=M580, Port3=BX1); without this wire it floats.
            if (ctx.Profile.HasAssignments)
            {
                ForceWriteJson(topologyDir, "Wire_RevPi_to_Switch1.json", BuildWireJson(cfg,
                    identifier:                 "RevPi_to_Switch1",
                    sourceEquipmentUuid:        RevPiNicUuid,
                    sourcePortIdentifier:       "Port1",
                    destinationEquipmentUuid:   Switch1Uuid(cfg),
                    destinationPortIdentifier:  "Port4"), result, eaeRoot);
                registerNames.Add("Wire_RevPi_to_Switch1.json");
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

                const string Zero = "00000000-0000-0000-0000-000000000000";
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
        static string BuildSwitchJson(Configuration.CompilerConfiguration cfg, string solutionId) =>
            TemplateDocument.Load(cfg, @"Topology\Equipment_Switch.json",
                new Dictionary<string, string>
                {
                    ["SwitchUuid"] = Switch1Uuid(cfg),
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
