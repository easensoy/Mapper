using System;
using System.IO;
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
    }
}
