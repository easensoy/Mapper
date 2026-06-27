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
    /// <summary>
    /// Emits Topology Physical-Views NETWORK objects — the L2 Switch_1
    /// and the Wire JSON files that connect M262 ↔ Switch_1 ↔ M580.
    /// EAE's Physical Views diagram shows these as the cables running between
    /// device icons. Without them, every PLC icon sits isolated and EAE has no
    /// declared path between them.
    ///
    /// <para>Reference layout (from <c>SMC_Rig_Expo_withClamp/Topology/</c>):</para>
    /// <list type="bullet">
    ///   <item><c>Equipment_Switch_1.json</c> — GenericL2UnmanagedSwitch8Ports
    ///         catalog. Eight EthernetDEO ports, no IP, no interfaces.</item>
    ///   <item><c>Wire_M262_to_Switch1.json</c> — M262.Ethernet1 → Switch_1.Port1.</item>
    ///   <item><c>Wire_Switch1_to_M580.json</c> — Switch_1.Port2 → M580 BME D58 1020.ETH1.</item>
    /// </list>
    ///
    /// <para>Wire endpoints reference the Equipment JSON's <c>uuid</c> field —
    /// for the M580 wire the destination is the NESTED CPU UUID
    /// (<c>BME D58 1020 #0</c>), not the M580dPAC_1 root, because that's the
    /// device that actually owns the ETH1 port. Symmetric with the reference
    /// (<c>Wire 171.json</c>'s <c>destinationEquipment</c> is the reference's
    /// BME D58 CPU UUID, not the rack root).</para>
    ///
    /// <para>Idempotent — each write deletes any pre-existing file first then
    /// rewrites, so a previously-merged file from a manual import cannot
    /// survive a re-emit. Registered in <c>TopologyManager.topologyproj</c>.</para>
    /// </summary>
    public static class TopologyNetworkEmitter
    {
        // Stable per-project UUIDs picked to avoid colliding with the M262/M580/BX1
        // Equipment UUIDs (10/40/50 ranges). The 60+ range is the network slot.
        const string Switch1Uuid          = "11111111-2222-3333-4444-000000000060";
        const string WireM262SwitchUuid   = "11111111-2222-3333-4444-000000000061";
        const string WireSwitchM580Uuid   = "11111111-2222-3333-4444-000000000062";

        // The other endpoints — these MUST match what M262TopologyEmitter +
        // Station2DeviceEmitter actually write. Hardcoded as compile-time
        // constants here too (not borrowed) to keep this file standalone.
        const string M262EquipmentUuid    = "11111111-2222-3333-4444-000000000010";
        const string M580CpuUuid          = "11111111-2222-3333-4444-000000000044";
        const string FallbackSolutionUuid = "00000000-0000-0000-0000-000000000000";

        // BX1 EtherNet/IP daisy-chain endpoints (match Station2DeviceEmitter's
        // BX1EtherNetIpUuid + BX1EquipmentUuid, which match the reference). The
        // reference wires the TM3BC coupler between the Switch and the HMIB1X panel:
        // Switch Port4 -> EtherNetIPDevice Port2 (Wire 191), EtherNetIPDevice Port1
        // -> HMIB1X LAN1 (Wire 193). We mirror that so the BX1 panel reaches the
        // network through its coupler.
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

            // Pin DomainTag to the live SolutionId so EAE accepts the Equipment
            // JSONs at topology-import time (a zero DomainTag triggers "Object
            // reference not set" in EAE 24.1's TopologyManager).
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

            // BX1 EtherNet/IP daisy-chain (only when the coupler is emitted). Mirrors
            // the reference: Switch Port3 -> EtherNetIPDevice Port2, and
            // EtherNetIPDevice Port1 -> HMIB1X LAN1 — so the BX1 panel reaches the
            // network through its TM3BC coupler and the softdpac's EtherNet/IP scanner
            // (declared by the .hcf) has its physical-views counterpart. Both endpoints
            // are real Equipment, so SweepOrphanWires keeps them. Switch Port3 is free
            // (Port1=M262, Port2=M580).
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

            // Register in TopologyManager.topologyproj so the EAE build target picks
            // them up.
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

            // ORPHAN-WIRE SWEEP. EAE 24.1's TopologyManager resolves every Wire's
            // source/destination Equipment UUID against the loaded Equipment_*.json. A wire
            // pointing at a UUID that NO device declares makes the import throw HTTP 500 ("Unable
            // to import topology / Internal Server Error") in ~150 ms BEFORE any device is parsed —
            // aborting the WHOLE topology (empty Physical Views). Generic + future-proof: delete +
            // de-register ANY Wire_*.json whose endpoint UUID is declared by no Equipment, so any
            // future device-form change self-heals.
            SweepOrphanWires(topologyDir, Path.Combine(topologyDir, "TopologyManager.topologyproj"),
                result, eaeRoot);

            return result;
        }

        /// <summary>
        /// Deletes + de-registers any <c>Wire_*.json</c> whose <c>sourceEquipment</c>
        /// or <c>destinationEquipment</c> UUID is declared by NO <c>Equipment_*.json</c>
        /// in the Topology folder (a dangling wire from a device UUID/form change). A
        /// dangling wire endpoint makes EAE's TopologyManager 500 the entire import.
        /// Conservative: if no equipment UUIDs are readable it sweeps nothing.
        /// </summary>
        static void SweepOrphanWires(string topologyDir, string topologyProj,
            EmitResult result, string eaeRoot)
        {
            try
            {
                // Every UUID any Equipment declares (root + nested equipments + components).
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
            catch { /* best-effort; the deleted file alone is the primary fix */ }
        }

        // Force-clean write — delete any pre-existing file before rewrite so a
        // manual user import (or an EAE merge) can't leave hybrid content behind.
        // Same defensive pattern Station2DeviceEmitter uses for the Equipment JSONs.
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
