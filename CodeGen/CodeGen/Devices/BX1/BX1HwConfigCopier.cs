using System;
using CodeGen.Configuration;
using CodeGen.Devices.M262;
using CodeGen.Devices.Core;

namespace CodeGen.Devices.BX1
{
    // Verbatim copy of the BX1 soft-dPAC .hcf (an EtherNet/IP EIPSCANNER2 scanner) into the EAE
    // project; authoritative final pass so the config survives the wiper's empty-shell reset.
    public static class BX1HwConfigCopier
    {
        public static HwConfigCopyResult Copy(MapperConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
            var template = HwConfigVerbatimCopier.ResolveTemplatePath(
                cfg.BX1HcfTemplatePath, cfg.IoFolderPath, "BX1IO.hcf");
            var copied = HwConfigVerbatimCopier.Deploy(eaeRoot, "Soft_dPAC", "SE.DPAC", template);
            // Must run AFTER HwConfiguration/ is rebuilt: an in-EmitAll deploy no-ops here, leaving an
            // EMPTY EIPSCANNER2.xml so the cover I/O never reaches the coupler.
            Station2DeviceEmitter.DeployBx1ScannerModelFinalPass(cfg);
            // Abort the Generate if the scanner model did not land (empty scanner = dead covers).
            Station2DeviceEmitter.ValidateBx1ScannerModelOrThrow(cfg);
            return copied;
        }
    }
}
