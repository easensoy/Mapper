using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Devices.Core;
using CodeGen.Translation;

namespace CodeGen.Devices.M262
{
    // Emits the canonical event + data wires into the deployed M262 sysres FBNetwork so EAE
    // initialises the application chain. No M262IO broker: CATs read/write M262 pins directly via
    // their internal SYMLINK FBs.
    public static class M262SysresWireEmitter
    {
        // The label "Sysres" must stay so the M262 file output is byte-identical to the
        // pre-generalisation path.
        private static readonly ResourceWireEmitter.ResourceAnchors M262Anchors = new(
            Label:        "Sysres",
            AreaFb:       "Area",
            StationFb:    "Station1",
            ProcessFb:    "Feed_Station",
            TerminatorFb: "Stn1_Term",
            HmiAdapterWires: ResourceWireEmitter.HmiAdapterWires);

        public static void Emit(MapperConfig cfg,
            SystemInjector.BindingApplicationReport report)
        {
            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
            if (eaeRoot == null)
            {
                report.Missing.Add("[Wire] skipped, EAE project root not derivable");
                return;
            }
            var sysresPath = LocateM262Sysres(eaeRoot);
            if (sysresPath == null)
            {
                report.Missing.Add("[Wire] skipped, M262 sysres not found");
                return;
            }
            EmitFeedRing(cfg, sysresPath, report);
        }

        // The Feed-station ring wiring (Area/Station1/Feed_Station/Stn1_Term). Applied to whichever
        // resource hosts the Feed station — the M262 sysres (Emit) or the RevPi sysres
        // (RevPiDeviceEmitter). The syslay layout mirror runs once here (the 3 PLCs share one canvas).
        internal static void EmitFeedRing(MapperConfig cfg, string sysresPath,
            SystemInjector.BindingApplicationReport report)
        {
            ResourceWireEmitter.EmitForResource(cfg, sysresPath, M262Anchors, report);
            ResourceWireEmitter.ApplyLayoutToSyslay(cfg.ActiveSyslayPath, report);
        }

        private static string? LocateM262Sysres(string eaeRoot)
            => ResourceWireEmitter.LocateSysresByDeviceType(eaeRoot, "M262_dPAC");
    }
}
