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
        // The target is the one whose backend is running, not a named controller: the scanner model
        // belongs to whichever device declares an EtherNet/IP coupler.
        public static HwConfigCopyResult Copy(Configuration.CompilerConfiguration cfg,
            CodeGen.Translation.PlcAssignment target)
        {
            var copied = HwConfigVerbatimCopier.CopyFor(cfg, target,
                Bx1DeviceRenderer.ResolveBx1HcfPath(cfg));
            // Must run AFTER HwConfiguration/ is rebuilt: an in-EmitAll deploy no-ops here, leaving an
            // EMPTY EIPSCANNER2.xml so the cover I/O never reaches the coupler.
            Bx1DeviceRenderer.DeployBx1ScannerModelFinalPass(cfg, target);
            // Abort the Generate if the scanner model did not land (empty scanner = dead covers).
            Bx1DeviceRenderer.ValidateBx1ScannerModelOrThrow(cfg, target);
            return copied;
        }
    }
}
