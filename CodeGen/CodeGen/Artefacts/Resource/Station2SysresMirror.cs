using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Translation;
using System.IO;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Devices.Core;
using CodeGen.Mapping;

namespace CodeGen.Devices.Core
{
    public static class Station2SysresMirror
    {
        // Mirrors the syslay's FBs onto every resource that draws its own device-local canvas, so those
        // targets carry their own FBs rather than empty shells. Which targets those are is declared;
        // running the mirror on a target whose canvas is the shared one would move its FBs.
        // Runs AFTER the device emitters wrote the shells.
        public static IReadOnlyList<(PlcAssignment Plc, int Count)> EmitStation2Sysres(GenerationContext ctx)
        {
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));
            var none = Array.Empty<(PlcAssignment, int)>();
            var cfg = ctx.Cfg;
            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
            if (string.IsNullOrEmpty(eaeRoot)) return none;

            var syslayPath = cfg.Paths.ActiveSyslayPath;
            var all = (string.IsNullOrWhiteSpace(syslayPath) || !File.Exists(syslayPath))
                ? new List<SysresFbMirror.SyslayFb>()
                : SysresFbMirror.ReadTopLevelFbsWithSystemModelFallback(syslayPath);
            if (all.Count == 0) return none;

            return ctx.Targets.All
                .Where(t => t.DeviceLocalCanvas && ctx.Emits(t.Plc))
                .Select(t => (t.Plc, MirrorBucket(eaeRoot, t.DeviceType,
                    all.Where(f => SysresFbMirror.BucketFor(f.Name, ctx.Allocation, ctx.Cfg) == t.Plc).ToList(),
                    ctx.Layout.Geometry.DeviceCanvasOrigin,
                    ctx.Targets.BootFor(t.Plc, ctx.Layout), ctx.Manifest)))
                .ToList();
        }

        // Translated so the bounding box's top-left lands on the device-local canvas origin: the syslay's
        // global coords would otherwise sit off-screen. Relative spacing is preserved exactly.
        static List<SysresFbMirror.SyslayFb> TranslateBucketToCanvasOrigin(
            List<SysresFbMirror.SyslayFb> bucket, CanvasPoint origin)
        {
            if (bucket.Count == 0) return bucket;
            int minX = int.MaxValue, minY = int.MaxValue;
            foreach (var fb in bucket)
            {
                if (int.TryParse(fb.X, out var x) && x < minX) minX = x;
                if (int.TryParse(fb.Y, out var y) && y < minY) minY = y;
            }
            if (minX == int.MaxValue) return bucket;     // no parseable coords
            int dx = origin.X - minX;
            int dy = origin.Y - minY;
            return bucket.Select(fb =>
            {
                int x = int.TryParse(fb.X, out var px) ? px + dx : 0;
                int y = int.TryParse(fb.Y, out var py) ? py + dy : 0;
                return fb with { X = x.ToString(), Y = y.ToString() };
            }).ToList();
        }

        static int MirrorBucket(string eaeRoot, string deviceType, List<SysresFbMirror.SyslayFb> bucket,
            CanvasPoint origin, IReadOnlyList<SystemFbSpec> systemFbs, Mapping.TemplateIndex manifest)
        {
            if (bucket.Count == 0) return 0;
            bucket = TranslateBucketToCanvasOrigin(bucket, origin);
            var sysdev = EaeProjectLayout.FindSysdevByDeviceType(eaeRoot, deviceType);
            if (sysdev == null) return 0;
            var sysres = EaeProjectLayout.FindSysresFor(sysdev);
            if (sysres == null) return 0;
            var added = SysresFbMirror.MirrorFbsIntoSysres(sysres, bucket, systemFbs, manifest);

            // SysresFbMirror leaves x/y alone on an existing FB, so restamp the canvas-origin x/y here.
            ApplyTranslatedPositionsToSysres(sysres, bucket);

            // EAE Solution Integrity requires a sibling "{resId}/" folder with an opcua.xml whose UID is
            // the parent sysdev-folder GUID.
            SystemInjector.EnsureOpcuaXmlBesideArtefact(sysres);

            return added;
        }

        // Idempotent: saves only on change, and FBs outside the bucket (FB1/FB2, MqttConn) are left alone.
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
