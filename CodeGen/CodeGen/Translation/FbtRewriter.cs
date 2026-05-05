using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace CodeGen.Translation
{
    public static class FbtRewriter
    {
        public const string AlgorithmName = "initializeinit";

        public static void RewriteInitializeInit(string fbtPath, string newStBody)
        {
            if (string.IsNullOrWhiteSpace(fbtPath))
                throw new ArgumentException("fbt path is required", nameof(fbtPath));
            if (!File.Exists(fbtPath))
                throw new FileNotFoundException("fbt file not found", fbtPath);
            if (newStBody == null)
                throw new ArgumentNullException(nameof(newStBody));

            var originalPath = fbtPath + ".original";
            if (!File.Exists(originalPath))
            {
                File.Copy(fbtPath, originalPath, overwrite: false);
            }

            // Always start from the pristine baseline so rewrites never compound.
            var baselineBytes = File.ReadAllBytes(originalPath);
            XDocument doc;
            using (var ms = new MemoryStream(baselineBytes))
            {
                doc = XDocument.Load(ms, LoadOptions.PreserveWhitespace);
            }

            var algo = doc.Descendants()
                .FirstOrDefault(e =>
                    e.Name.LocalName == "Algorithm" &&
                    string.Equals(
                        (string?)e.Attribute("Name"), AlgorithmName,
                        StringComparison.OrdinalIgnoreCase));

            if (algo == null)
                throw new InvalidOperationException(
                    $"Algorithm '{AlgorithmName}' not found in {fbtPath}");

            // Find the ST element under the algorithm (namespace agnostic).
            var stElement = algo.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "ST");

            if (stElement == null)
                throw new InvalidOperationException(
                    $"ST element not found under '{AlgorithmName}' in {fbtPath}");

            // Replace contents with a single CDATA carrying the new ST body.
            stElement.RemoveNodes();
            stElement.Add(new XCData(newStBody));

            var settings = new XmlWriterSettings
            {
                OmitXmlDeclaration = false,
                Indent = false,
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                NewLineHandling = NewLineHandling.Replace,
                NewLineChars = "\r\n",
            };

            using var fs = new FileStream(fbtPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = XmlWriter.Create(fs, settings);
            doc.Save(writer);
        }

        /// <summary>
        /// Reads back the current ST body inside the initializeinit algorithm. Useful for tests.
        /// </summary>
        public static string ReadInitializeInitSt(string fbtPath)
        {
            var doc = XDocument.Load(fbtPath, LoadOptions.PreserveWhitespace);
            var algo = doc.Descendants()
                .FirstOrDefault(e =>
                    e.Name.LocalName == "Algorithm" &&
                    string.Equals(
                        (string?)e.Attribute("Name"), AlgorithmName,
                        StringComparison.OrdinalIgnoreCase));
            if (algo == null) return string.Empty;
            var st = algo.Descendants().FirstOrDefault(e => e.Name.LocalName == "ST");
            if (st == null) return string.Empty;
            return string.Concat(st.Nodes().OfType<XCData>().Select(c => c.Value));
        }
    }
}
