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
    /// scanner that EAE would compile EMPTY; and (b) WARNS loudly if the compiled EIPSCANNER2.xml build
    /// output is empty/stale — that is EAE's output, fixed by a clean Build, not by the Mapper.
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
                    r.Lines.Add($"[BX1][Scanner] OK — scanner source carries the {CouplerIp} coupler (out 1025).");
            }

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

        static string Rel(string root, string p)
        { try { return Path.GetRelativePath(root, p); } catch { return p; } }
    }
}
