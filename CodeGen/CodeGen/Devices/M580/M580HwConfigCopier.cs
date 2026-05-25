using System;
using CodeGen.Configuration;
using CodeGen.Devices.M262;
using CodeGen.Devices.Core;

namespace CodeGen.Devices.M580
{
    /// <summary>
    /// Deploys the M580 X80 PLC's hardware configuration (<c>.hcf</c>) into the
    /// target EAE project — pure verbatim copy of the user-authored IO-folder
    /// export (<c>cfg.M580HcfTemplatePath</c>, falling back to
    /// <c>cfg.IoFolderPath\M580IO.hcf</c> then <c>C:\VueOneMapper\IO\M580IO.hcf</c>),
    /// re-rooted to <c>DeviceHwConfigurationItems</c>. Authoritative final pass so
    /// the config survives the wiper's empty-shell reset. Channel symlinks are
    /// preserved byte-for-byte.
    /// </summary>
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
