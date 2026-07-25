using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using CodeGen.Translation;
using CodeGen.Devices.Core;

namespace CodeGen.Devices.M262
{
    // Load -> mutate -> write wrapper for the DEPLOYED M262 .hcf (not the baseline M262HwConfigCopier.Copy replays).
    public sealed class M262HcfDocument
    {
        public XDocument Doc { get; }
        public HwConfigCopyResult LastResult { get; private set; } = new HwConfigCopyResult();

        private M262HcfDocument(XDocument doc) { Doc = doc; }

        public static M262HcfDocument Load(string hcfPath)
        {
            if (string.IsNullOrWhiteSpace(hcfPath))
                throw new ArgumentException("hcfPath is null/empty", nameof(hcfPath));
            if (!File.Exists(hcfPath))
                throw new FileNotFoundException($".hcf not found: {hcfPath}", hcfPath);
            return new M262HcfDocument(XDocument.Load(hcfPath, LoadOptions.PreserveWhitespace));
        }

        // Rewrite each in-scope TM3 pin's ParameterValue to the Form-1 triple {resourceId}.{m262IoFbId}.{varName};
        // blank pins clear to "". Out-of-scope (not on syslay) bindings are skipped.
        public int OverwriteHcfParameterValuesInMemory(IoBindings bindings,
            string resourceId, string m262IoFbId, HashSet<string> syslayFbNames)
        {
            if (bindings == null) return 0;
            LastResult = new HwConfigCopyResult();
            return M262HwConfigCopier.OverwriteHcfParameterValuesInMemory(
                Doc, bindings, syslayFbNames, LastResult, resourceId, m262IoFbId);
        }


        public IEnumerable<(string Pin, string Value)> EnumerateOverwrittenPins()
        {
            foreach (var entry in LastResult.ParametersOverwritten)
            {
                var idx = entry.IndexOf('=');
                if (idx <= 0) continue;
                yield return (entry.Substring(0, idx), entry.Substring(idx + 1));
            }
        }
    }
}
