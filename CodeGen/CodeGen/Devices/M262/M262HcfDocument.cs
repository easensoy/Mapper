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

        // EAE requires UTF-8 without BOM; retries briefly through EAE's file lock.
        public void WriteHcfToDisk(string hcfPath)
        {
            if (string.IsNullOrWhiteSpace(hcfPath))
                throw new ArgumentException("hcfPath is null/empty", nameof(hcfPath));

            var settings = new XmlWriterSettings
            {
                OmitXmlDeclaration = false,
                Indent = true,
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),  // no BOM
                NewLineHandling = NewLineHandling.Replace,
            };

            const int MaxAttempts = 8;
            int delayMs = 50;
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    using var fs = new FileStream(hcfPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                    using var w = XmlWriter.Create(fs, settings);
                    Doc.Save(w);
                    if (attempt > 1)
                        LastResult.Warnings.Add(
                            $".hcf write succeeded on attempt {attempt} (EAE briefly held a lock).");
                    LastResult.HcfPath = hcfPath;
                    return;
                }
                catch (IOException) when (attempt < MaxAttempts)
                {
                    System.Threading.Thread.Sleep(delayMs);
                    delayMs = Math.Min(delayMs * 2, 800);
                }
            }
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
