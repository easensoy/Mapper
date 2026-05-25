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
    /// <summary>
    /// Emits the canonical event + data wires into the deployed M262 sysres
    /// FBNetwork so EAE actually initialises the application chain
    /// (plcStart → DPAC_FULLINIT → Area → Station → … → Feeder → Process).
    /// No M262IO broker: Sensor/Actuator CATs read/write the M262 pins
    /// directly via their own internal SYMLINK FBs ($${PATH} macros).
    ///
    /// Always overwrites any existing &lt;EventConnections&gt; /
    /// &lt;DataConnections&gt; blocks. Endpoint references use the FB
    /// instance Name when present in the FBNetwork, otherwise fall back to
    /// resolving by FB Type (handles <c>plcStart</c>/<c>DPAC_FULLINIT</c>
    /// which appear with auto-generated names <c>FB2</c>/<c>FB1</c>).
    ///
    /// Each wire is validated against the source/destination FB's
    /// <c>.fbt</c> InterfaceList; ports that don't exist are logged as
    /// <c>[Wire] port not found: {FB}.{port}</c> and that one wire is
    /// skipped (the run continues).
    /// </summary>
    public static class M262SysresWireEmitter
    {
        // ── M262 Feed-Station structural anchors. These are the ONLY
        //    M262-specific names; everything component-driven is built from
        //    the CATs present in the sysres. Passed to EmitForResource so the
        //    same wiring core serves the M580/BX1 with their own anchors. The
        //    label "Sysres" is preserved so the M262 activity-log lines and
        //    file output stay byte-identical to the pre-generalisation path.
        private static readonly ResourceWireEmitter.ResourceAnchors M262Anchors = new(
            Label:        "Sysres",
            AreaFb:       "Area",
            StationFb:    "Station1",
            ProcessFb:    "Feed_Station",
            TerminatorFb: "Stn1_Term",
            HmiAdapterWires: ResourceWireEmitter.HmiAdapterWires);

        /// <summary>
        /// M262 Feed-Station entry point. UNCHANGED behaviour: locates the
        /// M262 sysres by device Type, wires it with the M262 anchors, and
        /// mirrors the canonical layout onto the deployed syslay. The wiring
        /// inputs (sysres path, anchors, layout) are identical to the
        /// pre-generalisation code path, so the emitted bytes are unchanged.
        /// </summary>
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
            ResourceWireEmitter.EmitForResource(cfg, sysresPath, M262Anchors, report);

            // Mirror the SAME CanonicalLayout onto the deployed syslay so the
            // EAE application canvas reads cleanly too. Best-effort:
            // ApplyLayoutToSyslay silently skips if the file/root is missing.
            // syslay-only; the M580/BX1 share this single application canvas so
            // EmitForResource intentionally does NOT touch it (only the M262
            // path mirrors layout to keep that output unchanged).
            ResourceWireEmitter.ApplyLayoutToSyslay(cfg.ActiveSyslayPath, report);
        }

        private static string? LocateM262Sysres(string eaeRoot)
            => ResourceWireEmitter.LocateSysresByDeviceType(eaeRoot, "M262_dPAC");
    }
}
