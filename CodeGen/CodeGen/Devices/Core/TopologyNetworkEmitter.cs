using System;
using System.IO;
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

            // Register all three in TopologyManager.topologyproj so the
            // EAE build target picks them up.
            var topologyProj = Path.Combine(topologyDir, "TopologyManager.topologyproj");
            if (File.Exists(topologyProj))
            {
                result.TopologyProjEntriesAdded = M262TopologyEmitter.RegisterInTopologyProj(
                    topologyProj, new[]
                    {
                        "Equipment_Switch_1.json",
                        "Wire_M262_to_Switch1.json",
                        "Wire_Switch1_to_M580.json",
                    });
            }
            else
            {
                result.Warnings.Add(
                    "TopologyManager.topologyproj missing — Switch + Wire JSONs " +
                    "written but not registered with the TopologyManager build target.");
            }

            return result;
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
