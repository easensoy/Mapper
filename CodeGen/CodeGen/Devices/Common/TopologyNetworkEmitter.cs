using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Devices.M262;

namespace CodeGen.Devices.Core
{
    // Emits Topology Physical-Views NETWORK objects (L2 Switch_1 + Wire JSONs) connecting
    // M262 <-> Switch_1 <-> M580. A wire's destination for the M580 must be the nested CPU
    // UUID that owns ETH1, not the rack root.
    public static class TopologyNetworkEmitter
    {
        const string Switch1Uuid          = "11111111-2222-3333-4444-000000000060";
        const string WireM262SwitchUuid   = "11111111-2222-3333-4444-000000000061";
        const string WireSwitchM580Uuid   = "11111111-2222-3333-4444-000000000062";

        // Endpoint UUIDs MUST match what M262TopologyEmitter + Station2DeviceEmitter write.
        const string M262EquipmentUuid    = "11111111-2222-3333-4444-000000000010";
        const string M580CpuUuid          = "11111111-2222-3333-4444-000000000044";
        const string FallbackSolutionUuid = "00000000-0000-0000-0000-000000000000";

        const string Bx1EtherNetIpUuid    = "49d2ea8e-3a4f-4ead-add4-ec4ba00d5239"; // EtherNetIPDevice_1 (.210)
        const string Bx1HmiB1XUuid        = "49363b74-1a84-46c1-b4cd-93f02374daec"; // HMIB1X_1 (BX1 panel .209)

        public sealed class EmitResult
        {
            public System.Collections.Generic.List<string> FilesWritten { get; } = new();
            public System.Collections.Generic.List<string> Warnings { get; } = new();
            public int TopologyProjEntriesAdded { get; set; }
        }

        public static EmitResult Emit(MapperConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            var result = new EmitResult();

            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
            if (string.IsNullOrEmpty(eaeRoot))
            {
                result.Warnings.Add("EAE project root not found — Topology network not emitted.");
                return result;
            }

            var topologyDir = Path.Combine(eaeRoot, "Topology");
            if (!Directory.Exists(topologyDir))
            {
                result.Warnings.Add($"Topology folder missing at {topologyDir} — network not emitted.");
                return result;
            }

            // DomainTag must be the live SolutionId; a zero DomainTag fails topology-import.
            var solutionId = M262TopologyEmitter.ReadProjectGuid(eaeRoot) ?? FallbackSolutionUuid;

            ForceWriteJson(topologyDir, "Equipment_Switch_1.json", BuildSwitchJson(solutionId), result, eaeRoot);
            ForceWriteJson(topologyDir, "Wire_M262_to_Switch1.json", BuildWireJson(
                identifier:                 "M262_to_Switch1",
                sourceEquipmentUuid:        M262EquipmentUuid,
                sourcePortIdentifier:       "Ethernet1",
                destinationEquipmentUuid:   Switch1Uuid,
                destinationPortIdentifier:  "Port1"), result, eaeRoot);
            ForceWriteJson(topologyDir, "Wire_Switch1_to_M580.json", BuildWireJson(
                identifier:                 "Switch1_to_M580",
                sourceEquipmentUuid:        Switch1Uuid,
                sourcePortIdentifier:       "Port2",
                destinationEquipmentUuid:   M580CpuUuid,
                destinationPortIdentifier:  "ETH1"), result, eaeRoot);

            var registerNames = new List<string>
            {
                "Equipment_Switch_1.json",
                "Wire_M262_to_Switch1.json",
                "Wire_Switch1_to_M580.json",
            };

            // BX1 EtherNet/IP daisy-chain (only when the coupler is emitted): Switch Port3 ->
            // EtherNetIPDevice Port2, EtherNetIPDevice Port1 -> HMIB1X LAN1. Both endpoints are
            // real Equipment so SweepOrphanWires keeps them.
            if (cfg.EmitBx1EtherNetIpDevice)
            {
                ForceWriteJson(topologyDir, "Wire_Switch1_to_EtherNetIP.json", BuildWireJson(
                    identifier:                 "Switch1_to_EtherNetIP",
                    sourceEquipmentUuid:        Switch1Uuid,
                    sourcePortIdentifier:       "Port3",
                    destinationEquipmentUuid:   Bx1EtherNetIpUuid,
                    destinationPortIdentifier:  "Port2"), result, eaeRoot);
                ForceWriteJson(topologyDir, "Wire_EtherNetIP_to_BX1.json", BuildWireJson(
                    identifier:                 "EtherNetIP_to_BX1",
                    sourceEquipmentUuid:        Bx1EtherNetIpUuid,
                    sourcePortIdentifier:       "Port1",
                    destinationEquipmentUuid:   Bx1HmiB1XUuid,
                    destinationPortIdentifier:  "LAN1"), result, eaeRoot);
                registerNames.Add("Wire_Switch1_to_EtherNetIP.json");
                registerNames.Add("Wire_EtherNetIP_to_BX1.json");
            }

            // Register in TopologyManager.topologyproj so the EAE build target picks them up.
            var topologyProj = Path.Combine(topologyDir, "TopologyManager.topologyproj");
            if (File.Exists(topologyProj))
            {
                result.TopologyProjEntriesAdded = M262TopologyEmitter.RegisterInTopologyProj(
                    topologyProj, registerNames.ToArray());
            }
            else
            {
                result.Warnings.Add(
                    "TopologyManager.topologyproj missing — Switch + Wire JSONs " +
                    "written but not registered with the TopologyManager build target.");
            }

            // A wire whose endpoint UUID is declared by NO Equipment makes EAE's TopologyManager
            // 500 the entire import — delete + de-register any such orphan wire.
            SweepOrphanWires(topologyDir, Path.Combine(topologyDir, "TopologyManager.topologyproj"),
                result, eaeRoot);

            return result;
        }

        // Deletes + de-registers any Wire_*.json whose endpoint UUID is declared by no Equipment.
        // Conservative: if no equipment UUIDs are readable it sweeps nothing.
        static void SweepOrphanWires(string topologyDir, string topologyProj,
            EmitResult result, string eaeRoot)
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
                result.Warnings.Add($"[Topology] Orphan-wire sweep failed: {ex.Message}");
            }
        }

        static void UnregisterFromTopologyProj(string topologyProj, string fileName)
        {
            if (!File.Exists(topologyProj)) return;
            try
            {
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
            catch { }
        }

        // Delete any pre-existing file before rewrite so a manual import / EAE merge can't leave
        // hybrid content behind.
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

        static string BuildSwitchJson(string solutionId) =>
            $$"""
            {
              "catalogReference": "GenericL2UnmanagedSwitch8Ports_V01.00_01.00",
              "uuid": "{{Switch1Uuid}}",
              "identifier": "Switch_1",
              "path": "Topology",
              "properties": [
                { "propertyName": "IsUnderConstruction", "propertyValue": "False" },
                { "propertyName": "DomainTag",            "propertyValue": "{{solutionId}}" }
              ],
              "references": [
                { "diagramPath": "Physical Views", "x": 200, "y": -200 }
              ],
              "components": [
                {
                  "ports": [
                    { "identifier": "Port1", "side": "Default" },
                    { "identifier": "Port2", "side": "Default" },
                    { "identifier": "Port3", "side": "Default" },
                    { "identifier": "Port4", "side": "Default" },
                    { "identifier": "Port5", "side": "Default" },
                    { "identifier": "Port6", "side": "Default" },
                    { "identifier": "Port7", "side": "Default" },
                    { "identifier": "Port8", "side": "Default" }
                  ],
                  "componentType": "EthernetDEO"
                }
              ]
            }
            """;

        static string BuildWireJson(string identifier,
                                    string sourceEquipmentUuid, string sourcePortIdentifier,
                                    string destinationEquipmentUuid, string destinationPortIdentifier) =>
            $$"""
            {
              "references": [
                {
                  "diagramPath": "Physical Views",
                  "sourceSide": 8,
                  "destinationSide": 8
                }
              ],
              "label": "{{identifier}}",
              "identifier": "{{identifier}}",
              "sourceEquipment": "{{sourceEquipmentUuid}}",
              "sourcePortIdentifier": "{{sourcePortIdentifier}}",
              "destinationEquipment": "{{destinationEquipmentUuid}}",
              "destinationPortIdentifier": "{{destinationPortIdentifier}}"
            }
            """;
    }
}
