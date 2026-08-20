using System;
using CodeGen.Configuration;
using CodeGen.Devices.M262;
using CodeGen.Devices.Core;
using CodeGen.Mapping;
using CodeGen.Translation;

namespace CodeGen.Devices.BX1
{
    // Verbatim copy of the BX1 soft-dPAC .hcf (an EtherNet/IP EIPSCANNER2 scanner) into the EAE
    // project; authoritative final pass so the config survives the wiper's empty-shell reset.
    public static class BX1HwConfigCopier
    {
        public static HwConfigCopyResult Copy(MapperConfig cfg)
        {
            var copied = HwConfigVerbatimCopier.CopyFor(cfg, CodeGen.Translation.PlcAssignment.BX1, cfg.BX1HcfTemplatePath);
            // Must run AFTER HwConfiguration/ is rebuilt: an in-EmitAll deploy no-ops here, leaving an
            // EMPTY EIPSCANNER2.xml so the cover I/O never reaches the coupler.
            Station2DeviceEmitter.DeployBx1ScannerModelFinalPass(cfg);
            // Abort the Generate if the scanner model did not land (empty scanner = dead covers).
            Station2DeviceEmitter.ValidateBx1ScannerModelOrThrow(cfg);
            return copied;
        }
    }
}
