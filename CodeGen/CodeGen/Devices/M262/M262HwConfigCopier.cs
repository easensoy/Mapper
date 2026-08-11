using System;
using CodeGen.Configuration;
using CodeGen.Devices.Core;

namespace CodeGen.Devices.M262
{
    // Verbatim copy of the M262 .hcf into the EAE project. The IO-folder file is canonical and is
    // carried byte-for-byte: a transform could silently drop authored channel bindings.
    public static class M262HwConfigCopier
    {
        public static HwConfigCopyResult Copy(MapperConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
            var template = HwConfigVerbatimCopier.ResolveTemplatePath(
                cfg.M262HcfTemplatePath, cfg.IoFolderPath, "M262IO.hcf");
            return HwConfigVerbatimCopier.Deploy(eaeRoot, CodeGen.Mapping.PlcTargets.DeviceType(CodeGen.Translation.PlcAssignment.M262), CodeGen.Mapping.PlcTargets.DeviceNamespace, template);
        }
    }
}
