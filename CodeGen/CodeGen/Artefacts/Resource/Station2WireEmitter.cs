using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Devices.Core;
using CodeGen.Devices.M262;
using CodeGen.Translation;

namespace CodeGen.Devices.Core
{
    public static class Station2WireEmitter
    {
        // M580 carries the full Assembly_Station slice; BX1 has no
        // Station/Process/Terminator (null anchors -> INIT fan-out + report ring only).
        private static readonly ResourceWireEmitter.ResourceAnchors M580Anchors = new(
            Label:        "M580",
            AreaFb:       null,                 // Area lives on the M262 only
            StationFb:    "Station2",
            ProcessFb:    "Assembly_Station",
            TerminatorFb: "Stn2_Term",
            HmiAdapterWires: new[]
            {
                new ResourceWireEmitter.Wire("Station2_HMI.StationHMIAdptrOUT", "Station2.StationHMIAdptrIN"),
            });

        private static readonly ResourceWireEmitter.ResourceAnchors BX1Anchors = new(
            Label:        "BX1",
            AreaFb:       null,
            StationFb:    null,                 // no Station FB on BX1 (graceful skip)
            ProcessFb:    null,                 // no Process FB on BX1
            TerminatorFb: null,
            HmiAdapterWires: Array.Empty<ResourceWireEmitter.Wire>());

        public static void EmitStation2Resources(MapperConfig cfg,
            SystemInjector.BindingApplicationReport report)
        {
            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
            if (eaeRoot == null)
            {
                report.Missing.Add("[Wire][Stn2] skipped, EAE project root not derivable");
                return;
            }

            var m580 = ResourceWireEmitter.LocateSysresByDeviceType(eaeRoot, "M580_dPAC");
            if (m580 != null)
            {
                var paramSynced = SysresFbMirror.SyncMirroredFbParametersFromSyslay(cfg.ActiveSyslayPath, m580);
                if (paramSynced > 0)
                    report.Missing.Add($"[Wire][M580] synced {paramSynced} mirrored FB parameter set(s) from syslay to sysres");
                var synced = SysresFbMirror.SyncProcessRecipesFromSyslay(cfg.ActiveSyslayPath, m580);
                if (synced > 0)
                    report.Missing.Add($"[Wire][M580] synced {synced} Process recipe(s) from syslay to sysres");
                ResourceWireEmitter.EmitForResource(cfg, m580, M580Anchors, report);
                paramSynced = SysresFbMirror.SyncMirroredFbParametersFromSyslay(cfg.ActiveSyslayPath, m580);
                if (paramSynced > 0)
                    report.Missing.Add($"[Wire][M580] post-wire synced {paramSynced} mirrored FB parameter set(s) from syslay to sysres");
                synced = SysresFbMirror.SyncProcessRecipesFromSyslay(cfg.ActiveSyslayPath, m580);
                if (synced > 0)
                    report.Missing.Add($"[Wire][M580] post-wire synced {synced} Process parameter set(s) from syslay to sysres");
            }
            else report.Missing.Add("[Wire][M580] skipped, M580 sysres not found");

            var bx1 = ResourceWireEmitter.LocateSysresByDeviceType(eaeRoot, "Soft_dPAC");
            if (bx1 != null)
            {
                var paramSynced = SysresFbMirror.SyncMirroredFbParametersFromSyslay(cfg.ActiveSyslayPath, bx1);
                if (paramSynced > 0)
                    report.Missing.Add($"[Wire][BX1] synced {paramSynced} mirrored FB parameter set(s) from syslay to sysres");
                var synced = SysresFbMirror.SyncProcessRecipesFromSyslay(cfg.ActiveSyslayPath, bx1);
                if (synced > 0)
                    report.Missing.Add($"[Wire][BX1] synced {synced} Process recipe(s) from syslay to sysres");
                // Sweep any leftover Cover_Station FB before wiring; ResourceWireEmitter
                // would otherwise re-splice it by type-scan.
                SweepCoverStationFromSysres(bx1, report);
                ResourceWireEmitter.EmitForResource(cfg, bx1, BX1Anchors, report);
                paramSynced = SysresFbMirror.SyncMirroredFbParametersFromSyslay(cfg.ActiveSyslayPath, bx1);
                if (paramSynced > 0)
                    report.Missing.Add($"[Wire][BX1] post-wire synced {paramSynced} mirrored FB parameter set(s) from syslay to sysres");
                synced = SysresFbMirror.SyncProcessRecipesFromSyslay(cfg.ActiveSyslayPath, bx1);
                if (synced > 0)
                    report.Missing.Add($"[Wire][BX1] post-wire synced {synced} Process parameter set(s) from syslay to sysres");
            }
            else report.Missing.Add("[Wire][BX1] skipped, BX1 sysres not found");
        }

        // Removes a stale Cover_Station Process FB + its connections from a BX1 sysres so
        // ResourceWireEmitter.EmitForResource cannot re-discover and re-wire it. Idempotent.
        private static void SweepCoverStationFromSysres(string sysresPath,
            SystemInjector.BindingApplicationReport report)
        {
            try
            {
                if (string.IsNullOrEmpty(sysresPath) || !File.Exists(sysresPath)) return;
                var doc = XDocument.Load(sysresPath);
                var net = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "FBNetwork");
                if (net == null) return;

                static string FbOf(string? ep) =>
                    ep == null ? "" : (ep.Contains('.') ? ep[..ep.IndexOf('.')] : ep);
                const string target = "Cover_Station";

                var fbs = net.Elements().Where(e => e.Name.LocalName == "FB" &&
                    string.Equals((string?)e.Attribute("Name"), target, StringComparison.Ordinal)).ToList();
                if (fbs.Count == 0) return;
                foreach (var fb in fbs) fb.Remove();

                int conns = 0;
                foreach (var grp in net.Elements().Where(e =>
                    e.Name.LocalName is "EventConnections" or "DataConnections" or "AdapterConnections").ToList())
                {
                    foreach (var c in grp.Elements().Where(e => e.Name.LocalName == "Connection" &&
                        (string.Equals(FbOf((string?)e.Attribute("Source")), target, StringComparison.Ordinal) ||
                         string.Equals(FbOf((string?)e.Attribute("Destination")), target, StringComparison.Ordinal))).ToList())
                    { c.Remove(); conns++; }
                }

                doc.Save(sysresPath);
                report.Missing.Add(
                    $"[Wire][BX1] STAGE 4: swept stale Cover_Station FB + {conns} connection(s) " +
                    $"from {Path.GetFileName(sysresPath)} (covers now commanded by Assembly_Station)");
            }
            catch (Exception ex)
            {
                report.Missing.Add($"[Wire][BX1] Cover_Station sweep failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
