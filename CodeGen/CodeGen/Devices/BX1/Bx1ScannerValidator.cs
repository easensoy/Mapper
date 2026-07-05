using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CodeGen.Devices.BX1
{
    // SAFETY: the BX1 cover I/O (incl. the CoverPNP_Hr safe-start) only reaches the TM3BC coupler if
    // the scanner carries the 192.168.1.210 device adapter; an empty scanner means cover_hr can hold
    // at Work (swivel-collision hazard) and be neither commanded nor homed. FAILS generation if the
    // scanner source lacks the coupler; WARNS if the compiled EIPSCANNER2.xml is empty/stale.
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

            // The scanner SOURCE the Mapper deploys: HwConfiguration/EIPSolutionsV2/<id>/scanner.xml.
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

            // SAFETY NOTICE (warn, not fatal): homing CoverPNP_Hr on EAE Clean/Stop/fault needs the
            // TM3BC coupler's own output fallback (word 16#0002 = bit1 ToHome), set on the coupler at
            // 192.168.1.210 — no EAE-owned file carries an output-fallback field. Bx1CoverFailsafe only
            // covers the run-time side.
            if (anyCoupler)
                EmitCoverCleanFallbackNotice(r);

            // The compiled EIPSCANNER2.xml build output (EAE's). Empty/stale = cover I/O DEAD.
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

        // Standing generation-time notice for the CoverPNP_Hr Clean/Stop/fault safety gap
        // (manual coupler fallback word 16#0002); emitted whenever the BX1 cover I/O is present.
        static void EmitCoverCleanFallbackNotice(Result r)
        {
            const string T = "[BX1][Cover-Clean] ";
            r.Lines.Add(T + "**************************************************************************");
            r.Lines.Add(T + "MANUAL COUPLER SETTING REQUIRED — the Mapper CANNOT generate this.");
            r.Lines.Add(T + "Bx1CoverFailsafe homes CoverPNP_Hr only while the BX1 logic RUNS " +
                "(deploy/login/restart). It does NOT act on EAE Clean/Stop/fault: the logic stops,");
            r.Lines.Add(T + "no FB can write ToHome, and the double-acting cover HOLDS its last position " +
                "(CoverPNP_Hr <-> Bearing_PnP swivel-collision hazard).");
            r.Lines.Add(T + "FIX (once, on the coupler's OWN embedded web server - browse to http://192.168.1.210, " +
                "MAINTENANCE page): set the TM3DQ16T output module FALLBACK so the fallback word = 16#0002 ->");
            r.Lines.Add(T + "    bit0 CoverPNP_Hr_ToWork=0   bit1 CoverPNP_Hr_ToHome=1   " +
                "bit2 CoverPNP_Vr=0   bit3 Cover_Gripper=0");
            r.Lines.Add(T + "=> CoverPNP_Hr_ToHome is TRUE on Clean/Stop/fault, so the cover homes like the Clamp.");
            r.Lines.Add(T + "Why not the Mapper: no EAE-owned file (device .prop.cs/.script.cs, scanner.xml, " +
                "M580Configuration.xsd, compiled EIPSCANNER2.xml) has an output-fallback field —");
            r.Lines.Add(T + "it is TM3BCEIP coupler config (its embedded web server, applied on EtherNet/IP " +
                "timeout). EAE is only the scanner; the adapter owns its own output fallback.");
            r.Lines.Add(T + "**************************************************************************");
        }

        static string Rel(string root, string p)
        { try { return Path.GetRelativePath(root, p); } catch { return p; } }
    }
}
