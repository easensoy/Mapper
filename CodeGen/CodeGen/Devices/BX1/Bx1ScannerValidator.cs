using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace CodeGen.Devices.BX1
{
    // SAFETY: BX1 cover I/O only reaches the TM3BC coupler if the scanner carries its device adapter.
    // An empty scanner leaves cover_hr able to hold at Work (swivel-collision hazard) with no way to
    // command or home it, so a scanner source without the coupler FAILS generation.
    public static class Bx1ScannerValidator
    {
        public static string CouplerIp => CodeGen.Configuration.DeviceConfig.Current.Bx1.CouplerIp;

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

            // Warn, not fatal: homing CoverPNP_Hr on EAE Clean/Stop/fault needs the TM3BC coupler's own
            // output fallback, set at the coupler. No EAE-owned file carries an output-fallback field.
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

        // The actuator named and the word are DERIVED from the same bx1Io declaration the broker wires,
        // so a re-bitted or renamed coil cannot leave this notice quoting a value that no longer homes.
        static void EmitCoverCleanFallbackNotice(Result r)
        {
            const string T = "[BX1][Cover-Clean] ";
            var io = Configuration.DeviceConfig.Current.Bx1Io;
            string safe = io.SafeStartComponent;
            // The word that leaves the safe-start actuator's ToHome coil energised and every other coil off.
            var coils = io.Covers
                .SelectMany(c => new[] { (c.Component, Coil: c.CoilToWork, Home: false),
                                         (c.Component, Coil: c.CoilToHome, Home: true) })
                .Where(x => x.Coil != null)
                .OrderBy(x => x.Coil!.Bit)
                .ToList();
            int word = coils
                .Where(x => x.Home && string.Equals(x.Component, safe, System.StringComparison.OrdinalIgnoreCase))
                .Aggregate(0, (w, x) => w | (1 << x.Coil!.Bit));
            string bits = string.Join("   ", coils.Select(x =>
                $"bit{x.Coil!.Bit} {x.Coil.Signal}=" +
                ((word & (1 << x.Coil.Bit)) != 0 ? "1" : "0")));
            r.Lines.Add(T + "**************************************************************************");
            r.Lines.Add(T + "MANUAL COUPLER SETTING REQUIRED — the Mapper CANNOT generate this.");
            r.Lines.Add(T + $"Bx1CoverFailsafe homes {safe} only while the BX1 logic RUNS " +
                "(deploy/login/restart). It does NOT act on EAE Clean/Stop/fault: the logic stops,");
            r.Lines.Add(T + "no FB can write ToHome, and the double-acting cover HOLDS its last position " +
                $"({safe} <-> swivel collision hazard).");
            r.Lines.Add(T + $"FIX (once, on the coupler's OWN embedded web server - browse to http://{CouplerIp}, " +
                $"MAINTENANCE page): set the TM3DQ16T output module FALLBACK so the fallback word = 16#{word:X4} ->");
            r.Lines.Add(T + "    " + bits);
            r.Lines.Add(T + $"=> {safe}'s ToHome coil is TRUE on Clean/Stop/fault, so the cover homes like the Clamp.");
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
