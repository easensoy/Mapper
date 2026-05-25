using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        static int MirrorBucket(string eaeRoot, string deviceType, List<SysresFbMirror.SyslayFb> bucket,
            string dpacFullInitId, string plcStartId)
        {
            if (bucket.Count == 0) return 0;
            var sysdev = EaeProjectLayout.FindSysdevByDeviceType(eaeRoot, deviceType);
            if (sysdev == null) return 0;
            var sysres = EaeProjectLayout.FindSysresFor(sysdev);
            if (sysres == null) return 0;
            var added = SysresFbMirror.MirrorFbsIntoSysres(sysres, bucket, dpacFullInitId, plcStartId);

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
    }
}
