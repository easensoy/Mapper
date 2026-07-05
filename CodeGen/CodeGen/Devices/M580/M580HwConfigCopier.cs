using System;
using CodeGen.Configuration;
using CodeGen.Devices.M262;
using CodeGen.Devices.Core;

namespace CodeGen.Devices.M580
{
    // Verbatim copy of the M580 .hcf into the EAE project; authoritative final pass so the config
    // survives the wiper's empty-shell reset, channel symlinks preserved byte-for-byte.
    public static class M580HwConfigCopier
    {
        public static HwConfigCopyResult Copy(MapperConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
            var template = HwConfigVerbatimCopier.ResolveTemplatePath(
                cfg.M580HcfTemplatePath, cfg.IoFolderPath, "M580IO.hcf");
            return HwConfigVerbatimCopier.Deploy(eaeRoot, "M580_dPAC", "SE.DPAC", template);
        }
    }
}
