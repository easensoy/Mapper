using System;
using CodeGen.Configuration;
using CodeGen.Devices.M262;
using CodeGen.Devices.Shared;

namespace CodeGen.Devices.BX1
{
    /// <summary>
    /// Deploys the BX1 soft-dPAC's hardware configuration (<c>.hcf</c>) into the
    /// target EAE project — pure verbatim copy of the user-authored IO-folder
    /// export (<c>cfg.BX1HcfTemplatePath</c>, falling back to
    /// <c>cfg.IoFolderPath\BX1IO.hcf</c> then <c>C:\VueOneMapper\IO\BX1IO.hcf</c>).
    /// The BX1 export is an EtherNet/IP scanner (EIPSCANNER2) whose TM3 module
    /// words route through single VTQWORD symlinks; carried verbatim. Authoritative
    /// final pass so the config survives the wiper's empty-shell reset.
    /// </summary>
    public static class BX1HwConfigCopier
    {
        public static HwConfigCopyResult Copy(MapperConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            var eaeRoot = M262SysdevEmitter.DeriveEaeProjectRoot(cfg);
            var template = HwConfigVerbatimCopier.ResolveTemplatePath(
                cfg.BX1HcfTemplatePath, cfg.IoFolderPath, "BX1IO.hcf");
            return HwConfigVerbatimCopier.Deploy(eaeRoot, "Soft_dPAC", "SE.DPAC", template);
        }
    }
}
