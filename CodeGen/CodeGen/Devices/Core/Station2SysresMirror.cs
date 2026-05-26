using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Devices.Core;
using CodeGen.Translation;

namespace CodeGen.Devices.Core
{
    public static class Station2SysresMirror
    {
        /// <summary>
        /// Mirrors the Station-2 FBs from the syslay onto the M580 and BX1
        /// resources (each bucketed by <see cref="BucketFor"/>), so those PLCs
        /// carry their own actuators/sensors/station/process FBs instead of empty
        /// shells. Runs AFTER <c>Station2DeviceEmitter.EmitAll</c> has written the
        /// sysdev/sysres shells. Each resource gets its own DPAC_FULLINIT/plcStart
        /// boot pair with a distinct ID. Returns (M580 count, BX1 count).
        /// </summary>
        public static (int M580, int BX1) EmitStation2Sysres(MapperConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
            if (string.IsNullOrEmpty(eaeRoot)) return (0, 0);

            var syslayPath = cfg.ActiveSyslayPath;
            var all = (string.IsNullOrWhiteSpace(syslayPath) || !File.Exists(syslayPath))
                ? new List<SysresFbMirror.SyslayFb>()
                : SysresFbMirror.ReadSyslayTopLevelFbs(syslayPath);
            if (all.Count == 0) return (0, 0);

            int m580 = MirrorBucket(eaeRoot, "M580_dPAC",
                all.Where(f => SysresFbMirror.BucketFor(f.Name) == PlcAssignment.M580).ToList(),
                dpacFullInitId: "66C40EEF3F39D969", plcStartId: "ACED009B79DFCE69");
            int bx1 = MirrorBucket(eaeRoot, "Soft_dPAC",
                all.Where(f => SysresFbMirror.BucketFor(f.Name) == PlcAssignment.BX1).ToList(),
                dpacFullInitId: "0FE5E1B2C3D4A5B6", plcStartId: "1A2B3C4D5E6F7081");
            return (m580, bx1);
        }

        // Where the M262 sysres puts its leftmost / topmost FB. Mirroring
        // M580/BX1 to the SAME canvas origin keeps every PLC's local sysres
        // view compact next to FB1 (DPAC_FULLINIT @ x=1900) and FB2 (plcStart
        // @ x=820) — no horizontal panning needed to find the component chain.
        const int CanvasOriginX = 2000;
        const int CanvasOriginY = 2000;

        /// <summary>
        /// Returns a copy of <paramref name="bucket"/> with the FBs translated
        /// so their bounding box's top-left corner lands at
        /// (<see cref="CanvasOriginX"/>, <see cref="CanvasOriginY"/>) on the
        /// destination sysres. The syslay carries global coordinates (M262 at
        /// x=2000-9500, M580 at x=12200-27200, BX1 at x=29000+) — SAME coords
        /// land verbatim on each sysres unless translated, which leaves the
        /// M580/BX1 chains far off to the right of their own FB1/FB2 on each
        /// device-local canvas. Translation is X-only by default; Y is also
        /// floored to <see cref="CanvasOriginY"/> when the min Y of the bucket
        /// is more than CanvasOriginY higher than the syslay's M262 reference
        /// row. Preserves relative spacing between the bucket's FBs exactly.
        /// </summary>
        static List<SysresFbMirror.SyslayFb> TranslateBucketToCanvasOrigin(
            List<SysresFbMirror.SyslayFb> bucket)
        {
            if (bucket.Count == 0) return bucket;
            int minX = int.MaxValue, minY = int.MaxValue;
            foreach (var fb in bucket)
            {
                if (int.TryParse(fb.X, out var x) && x < minX) minX = x;
                if (int.TryParse(fb.Y, out var y) && y < minY) minY = y;
            }
            if (minX == int.MaxValue) return bucket;     // no parseable coords
            int dx = CanvasOriginX - minX;
            int dy = CanvasOriginY - minY;
            return bucket.Select(fb =>
            {
                int x = int.TryParse(fb.X, out var px) ? px + dx : 0;
                int y = int.TryParse(fb.Y, out var py) ? py + dy : 0;
                return fb with { X = x.ToString(), Y = y.ToString() };
            }).ToList();
        }

        static int MirrorBucket(string eaeRoot, string deviceType, List<SysresFbMirror.SyslayFb> bucket,
            string dpacFullInitId, string plcStartId)
        {
            if (bucket.Count == 0) return 0;
            // Pull the bucket back to the canvas origin so the M580/BX1 sysres
            // canvas opens with the component chain RIGHT BESIDE FB1/FB2 instead
            // of way off to the right (the syslay's global coordinate system
            // puts M580 at x=12200+, which on the M580-local canvas leaves
            // everything off-screen by default).
            bucket = TranslateBucketToCanvasOrigin(bucket);
            var sysdev = EaeProjectLayout.FindSysdevByDeviceType(eaeRoot, deviceType);
            if (sysdev == null) return 0;
            var sysres = EaeProjectLayout.FindSysresFor(sysdev);
            if (sysres == null) return 0;
            var added = SysresFbMirror.MirrorFbsIntoSysres(sysres, bucket, dpacFullInitId, plcStartId);

            // Force-reposition existing mirrored FBs. SysresFbMirror's "FB already
            // exists by name → only update parameters, keep x/y untouched" branch
            // (kept that way deliberately so EAE's stable-instance tracking does
            // not see a moved FB as new on every regen) means existing M580/BX1
            // sysres FBs hang on to the OLD global-syslay coordinates from prior
            // deploys (x=12200+ for M580). Sweep the sysres after mirror and stamp
            // the just-computed canvas-origin x/y onto matching FBs by name — FB1
            // (DPAC_FULLINIT) and FB2 (plcStart) are skipped because the mirror
            // owns their positions through EnsureSystemFb.
            ApplyTranslatedPositionsToSysres(sysres, bucket);

            // EAE Solution Integrity requires every deployed resource to have a
            // sibling "{resId}/" metadata folder containing at minimum an
            // opcua.xml whose UID equals the parent sysdev-folder GUID. The
            // M262 Feed-Station path gets this via the same helper (the M262
            // deployed "{resId}/" folder carries opcua.xml + EAE-generated
            // offline.xml/opcuaclient.xml; only opcua.xml is Mapper-written).
            // The M580/BX1 resources previously skipped it — exactly the gap
            // that made the prior mirror attempt fail integrity with "Missing
            // Project Files". Reuse the M262 helper so the folder name (sysres
            // stem) and the UID convention match the working M262 output. The
            // sister files offline.xml/opcuaclient.xml/symlink.xml are left for
            // EAE to generate on open, mirroring the M262 behaviour.
            SystemInjector.EnsureOpcuaXmlBesideArtefact(sysres);

            return added;
        }

        /// <summary>
        /// Rewrites every &lt;FB Name="…"&gt; element's x / y attributes on the
        /// sysres to match the (already-translated-to-canvas-origin) coords in
        /// <paramref name="translatedBucket"/>. Idempotent — only saves the file
        /// when at least one position actually changed. FBs not present in the
        /// bucket (FB1/FB2 boot pair, MqttConn, anything outside the M580 / BX1
        /// scope) are left alone.
        /// </summary>
        static void ApplyTranslatedPositionsToSysres(string sysresPath,
            List<SysresFbMirror.SyslayFb> translatedBucket)
        {
            if (!File.Exists(sysresPath) || translatedBucket.Count == 0) return;
            XDocument doc;
            try { doc = XDocument.Load(sysresPath); }
            catch { return; }
            var root = doc.Root;
            if (root == null) return;
            XNamespace ns = root.GetDefaultNamespace();
            var network = root.Element(ns + "FBNetwork");
            if (network == null) return;

            var targetByName = translatedBucket.ToDictionary(
                f => f.Name, StringComparer.Ordinal);

            bool changed = false;
            foreach (var fb in network.Elements(ns + "FB"))
            {
                var name = (string?)fb.Attribute("Name") ?? string.Empty;
                if (!targetByName.TryGetValue(name, out var target)) continue;
                var curX = (string?)fb.Attribute("x") ?? string.Empty;
                var curY = (string?)fb.Attribute("y") ?? string.Empty;
                if (!string.Equals(curX, target.X, StringComparison.Ordinal))
                {
                    fb.SetAttributeValue("x", target.X);
                    changed = true;
                }
                if (!string.Equals(curY, target.Y, StringComparison.Ordinal))
                {
                    fb.SetAttributeValue("y", target.Y);
                    changed = true;
                }
            }
            if (changed) doc.Save(sysresPath);
        }
    }
}
