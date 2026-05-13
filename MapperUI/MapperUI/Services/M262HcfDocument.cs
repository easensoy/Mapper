using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using CodeGen.Translation;

namespace MapperUI.Services
{
    /// <summary>
    /// In-memory wrapper around an existing M262 <c>.hcf</c> file on disk.
    /// Load → mutate → write workflow for the Button-2 / Generate path that
    /// must patch the *deployed* .hcf (not replay the baseline that
    /// <see cref="M262HwConfigCopier.Copy"/> uses).
    ///
    /// Usage:
    /// <code>
    ///   var hcf = M262HcfDocument.Load(hcfPath);
    ///   hcf.OverwriteHcfParameterValuesInMemory(bindings, resourceId, m262IoFbId);
    ///   hcf.WriteHcfToDisk(hcfPath);
    /// </code>
    /// </summary>
    public sealed class M262HcfDocument
    {
        public XDocument Doc { get; }
        public HwConfigCopyResult LastResult { get; private set; } = new HwConfigCopyResult();

        private M262HcfDocument(XDocument doc) { Doc = doc; }

        /// <summary>
        /// Load the .hcf from disk into memory. Throws if the file is missing or
        /// malformed (caller should guard with <see cref="File.Exists"/> if optional).
        /// </summary>
        public static M262HcfDocument Load(string hcfPath)
        {
            if (string.IsNullOrWhiteSpace(hcfPath))
                throw new ArgumentException("hcfPath is null/empty", nameof(hcfPath));
            if (!File.Exists(hcfPath))
                throw new FileNotFoundException($".hcf not found: {hcfPath}", hcfPath);
            return new M262HcfDocument(XDocument.Load(hcfPath, LoadOptions.PreserveWhitespace));
        }

        /// <summary>
        /// Rewrite every TM3 module ParameterValue with the symbolic binding
        /// <c>{resourceId}.{m262IoFbId}.{varName}</c> for pins that have an
        /// in-scope binding; blank pins clear back to empty string. Returns the
        /// number of ParameterValue elements actually mutated.
        /// </summary>
        /// <param name="bindings">Pin assignments from SMC_Rig_IO_Bindings.xlsx.</param>
        /// <param name="resourceId">GUID of the M262 .sysres root.</param>
        /// <param name="m262IoFbId">GUID of the M262IO PLC_RW_M262 FB instance.</param>
        /// <param name="syslayFbNames">FB names actually present on the syslay
        /// — bindings for components not in scope are skipped.</param>
        public int OverwriteHcfParameterValuesInMemory(IoBindings bindings,
            string resourceId, string m262IoFbId, HashSet<string> syslayFbNames)
        {
            if (bindings == null) return 0;
            LastResult = new HwConfigCopyResult();
            return M262HwConfigCopier.OverwriteHcfParameterValuesInMemory(
                Doc, bindings, syslayFbNames, LastResult, resourceId, m262IoFbId);
        }

        /// <summary>
        /// Serialise the in-memory document back to <paramref name="hcfPath"/>
        /// as UTF-8 without BOM, preserving the original <c>&lt;?xml ?&gt;</c>
        /// declaration. Atomic FileStream.Create — overwrites the existing file.
        /// </summary>
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

        /// <summary>
        /// Iterates over every rewritten pin in <see cref="HwConfigCopyResult.ParametersOverwritten"/>
        /// and yields <c>(pin, value)</c> tuples so a UI layer can render
        /// per-pin Activity-panel lines like <c>[Hcf] DI00 ← RES0.M262IO.PusherAtHome</c>.
        /// </summary>
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
