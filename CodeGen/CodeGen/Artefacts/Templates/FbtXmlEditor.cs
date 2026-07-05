using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace CodeGen.Services
{
    // Shared .fbt/.xml load/save primitives for the deploy-time template patchers. Both retry on a
    // transient file lock (EAE holding the file during a background scan) and preserve byte-identical
    // formatting. Consumed via `using static` so the patcher call sites stay unqualified.
    internal static class FbtXmlEditor
    {
        // Load an .fbt/.xml with a short retry on a transient file lock (EAE holding the file during a
        // background scan). Throws the last lock exception after the final attempt.
        internal static XDocument LoadXmlWithRetry(string path, LoadOptions opts)
        {
            for (int attempt = 1, delay = 50; ; attempt++, delay = Math.Min(delay * 2, 800))
            {
                try { return XDocument.Load(path, opts); }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 8)
                {
                    System.Threading.Thread.Sleep(delay);
                }
            }
        }

        // Save an XDocument with a short retry on a transient file lock. Uses plain doc.Save formatting so
        // the on-disk bytes are identical to XDocument.Save(path) — byte-identical generation preserved.
        internal static void SaveXmlWithRetry(XDocument doc, string path)
        {
            for (int attempt = 1, delay = 50; ; attempt++, delay = Math.Min(delay * 2, 800))
            {
                try { doc.Save(path); return; }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 8)
                {
                    System.Threading.Thread.Sleep(delay);
                }
            }
        }

        // Remove matching elements via instance Remove() (no IEnumerable<XElement>.Remove() extension in
        // the callers). Returns true if anything was removed.
        internal static bool RemoveElems(IEnumerable<XElement>? src, Func<XElement, bool> pred)
        {
            if (src == null) return false;
            var hits = src.Where(pred).ToList();
            foreach (var h in hits) h.Remove();
            return hits.Count > 0;
        }

        // The deployed CAT/type .fbt under IEC61499/ (excluding its _HMI faceplate); "" if absent.
        internal static string FindDeployedFbt(string eaeProjectDir, string fbtFileName)
            => Directory.EnumerateFiles(Path.Combine(eaeProjectDir, "IEC61499"), fbtFileName, SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal)) ?? string.Empty;

        // Locate the deployed <fbtFileName>, load it, and hand (doc, root, ns, path) to `edit` — which mutates,
        // saves via doc.Save(path), and logs. Wraps the file-lock retry and warn-on-exception scaffold shared
        // by the deploy-time patchers. Absent .fbt: warn notFoundNote (if given) and no-op. Null root: no-op.
        internal static void EditDeployedFbt(string eaeProjectDir, string fbtFileName, string failNote,
            DeployResult result, Action<XDocument, XElement, XNamespace, string> edit, string? notFoundNote = null)
        {
            var fbt = FindDeployedFbt(eaeProjectDir, fbtFileName);
            if (string.IsNullOrEmpty(fbt))
            {
                if (notFoundNote != null) result.Warnings.Add(notFoundNote);
                return;
            }
            try
            {
                var doc = LoadXmlWithRetry(fbt, LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                edit(doc, root, root.GetDefaultNamespace(), fbt);
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"{failNote}: {ex.Message}");
            }
        }

        // Write IEC61499/DataType/<name>.dt (copy-if-absent) and record it in DataTypesDeployed so the
        // dfbproj registers the type. patchNote appends to the PatchesApplied log line.
        internal static void DeployDatatype(string eaeProjectDir, string name, string dtXml,
            DeployResult result, string? patchNote = null)
        {
            try
            {
                var dtDir = Path.Combine(eaeProjectDir, "IEC61499", "DataType");
                Directory.CreateDirectory(dtDir);
                var dtPath = Path.Combine(dtDir, name + ".dt");
                if (!File.Exists(dtPath)) File.WriteAllText(dtPath, dtXml);
                if (!result.DataTypesDeployed.Contains(name)) result.DataTypesDeployed.Add(name);
                result.PatchesApplied.Add($"{name}.dt deployed + registered{(patchNote is null ? "" : " " + patchNote)}");
                MapperLogger.Info($"[Deploy] {name}.dt written + registered");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"{name}.dt deploy failed: {ex.Message}");
            }
        }
    }
}
