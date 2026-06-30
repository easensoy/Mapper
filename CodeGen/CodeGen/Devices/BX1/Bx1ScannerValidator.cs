using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CodeGen.Devices.BX1
{
    /// <summary>
    /// SAFETY guard for the BX1 EtherNet/IP cover scanner. The cover I/O — including the CoverPNP_Hr
    /// safe-start gate — only reaches the TM3BC coupler if the generated scanner carries the
    /// 192.168.1.210 device adapter. An empty scanner means the broker's "cover home" command never
    /// reaches the coupler, so cover_hr can hold at Work (the swivel-collision hazard) and can be
    /// neither commanded nor homed.
    ///
    /// This validator (a) FAILS generation if the deployed scanner SOURCE (scanner.xml the Mapper
    /// emits) is missing the 192.168.1.210 coupler, so "generation completed" is never reported with a
    /// scanner that EAE would compile EMPTY; (b) WARNS loudly if the compiled EIPSCANNER2.xml build
    /// output is empty/stale — that is EAE's output, fixed by a clean Build, not by the Mapper; and
    /// (c) emits a STANDING notice that homing CoverPNP_Hr on EAE Clean/Stop/fault needs a manual TM3BC
    /// output-fallback setting (word 16#0002) that no EAE-owned file can express — see
    /// EmitCoverCleanFallbackNotice. Bx1CoverFailsafe covers only the start/run-time side.
    /// </summary>
    public static class Bx1ScannerValidator
    {
        public const string CouplerIp = "192.168.1.210";

        public sealed class Result
        {
            public bool Fatal;
            public readonly List<string> Lines = new();
        }

        public static Result Validate(string eaeRoot)
        {
            var r = new Result();
            if (string.IsNullOrEmpty(eaeRoot) || !Directory.Exists(eaeRoot))
            { r.Lines.Add("[BX1][Scanner] eaeRoot not found — scanner not validated."); return r; }

            var hwConfig = Path.Combine(eaeRoot, "HwConfiguration");

            // (a) the scanner SOURCE the Mapper deploys: HwConfiguration/EIPSolutionsV2/<id>/scanner.xml.
            var sources = Directory.Exists(hwConfig)
                ? Directory.EnumerateFiles(hwConfig, "scanner.xml", SearchOption.AllDirectories).ToList()
                : new List<string>();
            if (sources.Count == 0)
            {
                r.Fatal = true;
                r.Lines.Add("[BX1][Scanner] FATAL — no scanner.xml under HwConfiguration. The BX1 " +
                    "EtherNet/IP cover I/O cannot be built; the cover safe-start cannot reach the coupler.");
            }
            bool anyCoupler = false;
            foreach (var s in sources)
            {
                var txt = File.ReadAllText(s);
                bool hasCoupler = txt.Contains(CouplerIp) && txt.Contains("outputObjectID=\"1025\"");
                if (!hasCoupler)
                {
                    r.Fatal = true;
                    r.Lines.Add($"[BX1][Scanner] FATAL — {Path.GetFileName(s)} is MISSING the {CouplerIp} " +
                        "coupler (outputObjectID 1025). EAE would compile an EMPTY scanner and the cover " +
                        "I/O / CoverPNP_Hr safe-start would never reach the TM3BC. Refusing to complete.");
                }
                else
                {
                    anyCoupler = true;
                    r.Lines.Add($"[BX1][Scanner] OK — scanner source carries the {CouplerIp} coupler (out 1025).");
                }
            }

            // (c) STANDING SAFETY NOTICE — the EAE Clean/Stop/fault cover-home gap the Mapper CANNOT close.
            // Investigated exhaustively (2026-06-30): the IEC-side Bx1CoverFailsafe gate homes CoverPNP_Hr
            // only while the BX1 logic RUNS. On EAE Clean/Stop/fault the logic is torn down and no FB can
            // write ToHome, so the DOUBLE-ACTING cover HOLDS its last coupler output (CoverPNP_Hr <->
            // Bearing_PnP swivel-collision hazard). The ONLY mechanism that homes it while stopped is the
            // TM3BC coupler's own output fallback, and NO EAE-owned file can express it: the EtherNet/IP
            // device model (TM3BC_Ethe_*.prop.cs / .script.cs), scanner.xml, the M580Configuration.xsd
            // schema, and the compiled EIPSCANNER2.xml all carry ONLY objectid/length/ioevent per output —
            // there is no fallback/fault/substitute field anywhere. It is a coupler-side EcoStruxure
            // Machine Expert setting; warned loudly every Generate (it can never be auto-detected or
            // cleared by the Mapper, so it is a WARNING, not a fatal block).
            if (anyCoupler)
                EmitCoverCleanFallbackNotice(r);

            // (b) the compiled EIPSCANNER2.xml build output (EAE's). Empty/stale = cover I/O DEAD.
            foreach (var eip in Directory.EnumerateFiles(eaeRoot, "EIPSCANNER2.xml", SearchOption.AllDirectories))
            {
                long len = new FileInfo(eip).Length;
                var txt = File.ReadAllText(eip);
                if (len < 500 || !txt.Contains(CouplerIp))
                    r.Lines.Add($"[BX1][Scanner] WARN — compiled {Rel(eaeRoot, eip)} is EMPTY/stale " +
                        $"({len} bytes; coupler {(txt.Contains(CouplerIp) ? "present" : "ABSENT")}). EAE has " +
                        "not rebuilt it from the valid source. Cover I/O (and the cover safe-start) is DEAD " +
                        "until a clean Build: close EAE -> Clean -> Build. Until then cover_hr cannot be " +
                        "commanded OR homed by the logic.");
            }
            return r;
        }

        /// <summary>
        /// Loud, standing generation-time notice for the one CoverPNP_Hr safety gap the Mapper cannot
        /// generate: homing the double-acting cover on EAE Clean/Stop/fault. Emitted every Generate the
        /// BX1 EtherNet/IP cover I/O is present. States the exact manual coupler setting (fallback word
        /// 16#0002 = bit1 ToHome only) and why no EAE-owned file can carry it.
        /// </summary>
        static void EmitCoverCleanFallbackNotice(Result r)
        {
            const string T = "[BX1][Cover-Clean] ";
            r.Lines.Add(T + "**************************************************************************");
            r.Lines.Add(T + "MANUAL COUPLER SETTING REQUIRED — the Mapper CANNOT generate this.");
            r.Lines.Add(T + "Bx1CoverFailsafe homes CoverPNP_Hr only while the BX1 logic RUNS " +
                "(deploy/login/restart). It does NOT act on EAE Clean/Stop/fault: the logic stops,");
            r.Lines.Add(T + "no FB can write ToHome, and the double-acting cover HOLDS its last position " +
                "(CoverPNP_Hr <-> Bearing_PnP swivel-collision hazard).");
            r.Lines.Add(T + "FIX (once, on the coupler, in EcoStruxure Machine Expert): set the TM3BC / " +
                "TM3DQ16T output FALLBACK so the fallback word = 16#0002 ->");
            r.Lines.Add(T + "    bit0 CoverPNP_Hr_ToWork=0   bit1 CoverPNP_Hr_ToHome=1   " +
                "bit2 CoverPNP_Vr=0   bit3 Cover_Gripper=0");
            r.Lines.Add(T + "=> CoverPNP_Hr_ToHome is TRUE on Clean/Stop/fault, so the cover homes like the Clamp.");
            r.Lines.Add(T + "Why not the Mapper: no EAE-owned file (device .prop.cs/.script.cs, scanner.xml, " +
                "M580Configuration.xsd, compiled EIPSCANNER2.xml) has an output-fallback field —");
            r.Lines.Add(T + "it is TM3BC adapter firmware config, owned by Machine Expert, not EAE/the Mapper.");
            r.Lines.Add(T + "**************************************************************************");
        }

        static string Rel(string root, string p)
        { try { return Path.GetRelativePath(root, p); } catch { return p; } }
    }
}
