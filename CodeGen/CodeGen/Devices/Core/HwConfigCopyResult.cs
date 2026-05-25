using System;
using System.Collections.Generic;

namespace CodeGen.Devices.Core
{
    public class HwConfigCopyResult
    {
        public string? HcfPath { get; set; }
        public int FilesCopied { get; set; }
        public List<string> ParametersOverwritten { get; } = new();
        public HashSet<string> ParametersOverwrittenSet { get; } = new(StringComparer.Ordinal);
        public List<string> Warnings { get; } = new();
    }
}
