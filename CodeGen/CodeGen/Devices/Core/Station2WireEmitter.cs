using System;
using CodeGen.Configuration;
using CodeGen.Devices.Core;
using CodeGen.Devices.M262;
using CodeGen.Translation;

namespace CodeGen.Devices.Core
{
    public static class Station2WireEmitter
    {
        // ── Station-2 structural anchors. The M580 carries the full
        //    Assembly_Station slice (Station + Process + Terminator); the BX1
        //    carries only the cover pick-and-place actuators + sensor with NO
        //    Station/Process/Terminator of its own in this increment, so its
        //    anchors are all null and EmitForResource gives it just the INIT
        //    fan-out + the report ring among the BX1 components.
        private static readonly ResourceWireEmitter.ResourceAnchors M580Anchors = new(
            Label:        "M580",
            AreaFb:       "Area2",              // M580's OWN local Area drives Station2
            StationFb:    "Station2",
            ProcessFb:    "Assembly_Station",
            TerminatorFb: "Stn2_Term",
            HmiAdapterWires: new[]
            {
                // Area2 is the M580's local run-command source. EmitForResource
                // threads it into the init chain (FB1.INITO -> Area2.INIT ->
                // Station2.INIT -> components -> Assembly_Station) because AreaFb
                // is now non-null; here we add the three adapter wires that make
                // it the run driver, exactly mirroring the M262 Area->Station1
                // wiring (ResourceWireEmitter.HmiAdapterWires):
                //   Area2_HMI faceplate -> Area2  (operator start/stop socket)
                //   Station2_HMI faceplate -> Station2
                //   Area2.AreaAdptrOUT -> Station2.AreaAdptrIN  (THE run driver)
                // Without that last wire Station2 never enables and Assembly
                // never cycles. No Area2_Term needed: the M262 sysres likewise
                // leaves Station1.AreaAdptrOUT unterminated and runs fine (the
                // terminator only closes the chain on the shared syslay canvas).
                new ResourceWireEmitter.Wire("Area2_HMI.AreaHMIAdptrOUT", "Area2.AreaHMIAdptrIN"),
                new ResourceWireEmitter.Wire("Station2_HMI.StationHMIAdptrOUT", "Station2.StationHMIAdptrIN"),
                new ResourceWireEmitter.Wire("Area2.AreaAdptrOUT", "Station2.AreaAdptrIN"),
            });

        private static readonly ResourceWireEmitter.ResourceAnchors BX1Anchors = new(
            Label:        "BX1",
            AreaFb:       null,
            StationFb:    null,                 // no Station FB on BX1 (graceful skip)
            ProcessFb:    null,                 // no Process FB on BX1
            TerminatorFb: null,
            HmiAdapterWires: Array.Empty<ResourceWireEmitter.Wire>());

        /// <summary>
        /// Wires the deployed M580 + BX1 sysres FBNetworks (each located by
        /// device Type) with the SAME proven topology core as the M262, using
        /// each PLC's own structural anchors. Additive — does NOT touch the
        /// M262 sysres or the shared application syslay. The M580 gets the full
        /// init chain + CaS station chain + report ring; the BX1 (no Station FB)
        /// gets the init fan-out + report ring only. Returns true if at least
        /// one resource was located and wired.
        /// </summary>
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
            if (m580 != null) ResourceWireEmitter.EmitForResource(cfg, m580, M580Anchors, report);
            else report.Missing.Add("[Wire][M580] skipped, M580 sysres not found");

            var bx1 = ResourceWireEmitter.LocateSysresByDeviceType(eaeRoot, "Soft_dPAC");
            if (bx1 != null) ResourceWireEmitter.EmitForResource(cfg, bx1, BX1Anchors, report);
            else report.Missing.Add("[Wire][BX1] skipped, BX1 sysres not found");
        }
    }
}
