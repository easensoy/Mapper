using System;
using CodeGen.Configuration;

namespace MapperUI.Services
{
    /// <summary>
    /// No-op for back-compat. The M580 .hcf is verbatim-copied from
    /// <c>cfg.M580HcfTemplatePath</c> (the user's
    /// <c>C:\VueOneMapper\IO\M580IO.hcf</c>) into the M580 sysdev folder
    /// by <see cref="Station2DeviceEmitter"/> at the start of the Test
    /// Runtime flow. This class previously did a second-pass rewrite +
    /// per-pin filter; that path was retired on 2026-05-21 in favour of
    /// pure verbatim copy (same rule we apply to M262 + BX1).
    ///
    /// <para>If per-pin patching of the M580 .hcf is ever needed, do it
    /// in the IO-folder authoring step OR add it here as a SECOND pass
    /// AFTER the verbatim copy — never as a filter that can silently drop
    /// channels.</para>
    /// </summary>
    public static class M580HwConfigCopier
    {
        public static HwConfigCopyResult Copy(MapperConfig cfg)
        {
            _ = cfg;
            var result = new HwConfigCopyResult();
            result.Warnings.Add(
                "M580 .hcf is verbatim-copied by Station2DeviceEmitter — " +
                "M580HwConfigCopier.Copy is a no-op (retained for API stability).");
            return result;
        }
    }
}
