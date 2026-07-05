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
        // Mirrors the Station-2 FBs from the syslay onto the M580 and BX1 resources (bucketed by
        // BucketFor), so those PLCs carry their own FBs not empty shells. Runs AFTER
        // Station2DeviceEmitter.EmitAll wrote the sysdev/sysres shells. Returns (M580 count, BX1 count).
        public static (int M580, int BX1) EmitStation2Sysres(MapperConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
            if (string.IsNullOrEmpty(eaeRoot)) return (0, 0);

            var syslayPath = cfg.ActiveSyslayPath;
            var all = (string.IsNullOrWhiteSpace(syslayPath) || !File.Exists(syslayPath))
                ? new List<SysresFbMirror.SyslayFb>()
                : SysresFbMirror.ReadTopLevelFbsWithSystemModelFallback(syslayPath);
            if (all.Count == 0) return (0, 0);

            int m580 = MirrorBucket(eaeRoot, "M580_dPAC",
                all.Where(f => SysresFbMirror.BucketFor(f.Name) == PlcAssignment.M580).ToList(),
                dpacFullInitId: "66C40EEF3F39D969", plcStartId: "ACED009B79DFCE69");
            int bx1 = MirrorBucket(eaeRoot, "Soft_dPAC",
                all.Where(f => SysresFbMirror.BucketFor(f.Name) == PlcAssignment.BX1).ToList(),
                dpacFullInitId: "0FE5E1B2C3D4A5B6", plcStartId: "1A2B3C4D5E6F7081");
            return (m580, bx1);
        }

        // Canvas origin every PLC's local sysres mirrors to, next to FB1/FB2.
        const int CanvasOriginX = 2000;
        const int CanvasOriginY = 2000;

        // Copy of bucket translated so its bounding box's top-left lands at (CanvasOriginX, CanvasOriginY)
        // on the destination sysres — the syslay's global coords (M580 at x=12200+) would otherwise land
        // off-screen on each device-local canvas. Preserves relative spacing exactly.
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
            bucket = TranslateBucketToCanvasOrigin(bucket);
            var sysdev = EaeProjectLayout.FindSysdevByDeviceType(eaeRoot, deviceType);
            if (sysdev == null) return 0;
            var sysres = EaeProjectLayout.FindSysresFor(sysdev);
            if (sysres == null) return 0;
            var added = SysresFbMirror.MirrorFbsIntoSysres(sysres, bucket, dpacFullInitId, plcStartId);

            // SysresFbMirror keeps x/y untouched on an existing FB (so EAE doesn't see it as new), so
            // existing M580/BX1 FBs keep OLD global-syslay coords — restamp the canvas-origin x/y here.
            ApplyTranslatedPositionsToSysres(sysres, bucket);

            // EAE Solution Integrity requires a sibling "{resId}/" folder with an opcua.xml whose UID =
            // the parent sysdev-folder GUID (same helper as the M262 path).
            SystemInjector.EnsureOpcuaXmlBesideArtefact(sysres);

            return added;
        }

        // Restamps each <FB> x/y on the sysres from translatedBucket (idempotent: saves only on change).
        // FBs not in the bucket (FB1/FB2, MqttConn, out-of-scope) are left alone.
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
