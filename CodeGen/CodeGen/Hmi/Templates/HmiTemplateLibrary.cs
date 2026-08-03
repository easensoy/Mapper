using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CodeGen.Hmi
{
    // Reads Template Library\HMI: a project-constant Shell plus one faceplate folder per CAT type.
    // Nothing here is rig- or model-specific; the folder set IS the supported-faceplate registry.
    internal static class HmiTemplateLibrary
    {
        private static readonly Regex SymbolSizeRx =
            new(@"this\.SymbolSize\s*=\s*new System\.Drawing\.Size\((\d+),\s*(\d+)\)", RegexOptions.Compiled);
        private static readonly Regex SizeRx =
            new(@"this\.Size\s*=\s*new System\.Drawing\.Size\((\d+),\s*(\d+)\)", RegexOptions.Compiled);

        // Used when a symbol template declares no geometry at all (an empty stub canvas).
        internal const int FallbackWidth = 300;
        internal const int FallbackHeight = 204;

        internal static string Root(string templateLibraryPath) => Path.Combine(templateLibraryPath, "HMI");
        internal static string ShellDir(string templateLibraryPath) => Path.Combine(Root(templateLibraryPath), "Shell");
        internal static string FaceplatesDir(string templateLibraryPath) => Path.Combine(Root(templateLibraryPath), "Faceplates");
        internal static string DeploymentDir(string templateLibraryPath) => Path.Combine(Root(templateLibraryPath), "Deployment");
        internal static string ScreenResxTemplate(string templateLibraryPath) => Path.Combine(Root(templateLibraryPath), "_screen.cnv.resx");

        internal static IReadOnlyList<HmiCatTemplate> Load(string templateLibraryPath)
        {
            var dir = FaceplatesDir(templateLibraryPath);
            if (!Directory.Exists(dir)) return Array.Empty<HmiCatTemplate>();
            return LoadFrom(dir);
        }

        // Also used to re-read the DEPLOYED copies after the command stripper has run on them.
        internal static IReadOnlyList<HmiCatTemplate> LoadFrom(string faceplatesDir)
        {
            if (!Directory.Exists(faceplatesDir)) return Array.Empty<HmiCatTemplate>();

            return Directory.EnumerateDirectories(faceplatesDir)
                .Select(ReadTemplate)
                .Where(t => t.Symbols.Count > 0)
                .OrderBy(t => t.CatType, StringComparer.Ordinal)
                .ToList();
        }

        internal static HmiCatTemplate ReadTemplate(string catDir)
        {
            var cat = Path.GetFileName(catDir);
            var symbols = new List<HmiSymbol>();

            foreach (var cnv in Directory.EnumerateFiles(catDir, $"{cat}_*.cnv.cs").OrderBy(p => p, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(cnv);
                name = name.Substring(cat.Length + 1, name.Length - (cat.Length + 1) - ".cnv.cs".Length);

                var designer = Path.Combine(catDir, $"{cat}_{name}.cnv.Designer.cs");
                var text = File.Exists(designer) ? File.ReadAllText(designer) : string.Empty;

                var sym = SymbolSizeRx.Match(text);
                var pop = SizeRx.Match(text);
                var contract = Path.Combine(catDir, $"{cat}_{name}.cnv.xml");
                var hasContract = File.Exists(contract);

                // A placeable symbol declares SymbolSize and carries its own .cnv.xml contract;
                // a pop-up faceplate declares only Size and inherits the opening symbol's connection.
                var isFaceplate = !sym.Success && !hasContract;
                var m = sym.Success ? sym : pop;
                var w = m.Success ? int.Parse(m.Groups[1].Value) : FallbackWidth;
                var h = m.Success ? int.Parse(m.Groups[2].Value) : FallbackHeight;

                var (outEvents, outTags) = ReadContractOutputs(contract);
                symbols.Add(new HmiSymbol(name, isFaceplate, w, h, hasContract, outEvents, outTags));
            }

            return new HmiCatTemplate(cat, catDir, symbols);
        }

        // The contract is the authority on which direction a symbol can talk:
        //   <EventOutputs><Event Name="MCNF">Mode</Event></EventOutputs>   -> HMI drives the PLC
        //   <Outputs><Output Name="Mode" Type="INT" /></Outputs>
        internal static (IReadOnlyList<string> Events, IReadOnlyList<string> Tags) ReadContractOutputs(string contractPath)
        {
            if (!File.Exists(contractPath))
                return (Array.Empty<string>(), Array.Empty<string>());

            try
            {
                var root = XDocument.Load(contractPath).Root;
                if (root == null) return (Array.Empty<string>(), Array.Empty<string>());
                return (Section(root, "EventOutputs", "Event"), Section(root, "Outputs", "Output"));
            }
            catch (Exception)
            {
                // An unreadable contract must never be treated as safe.
                return (new[] { "<unreadable contract>" }, Array.Empty<string>());
            }
        }

        private static IReadOnlyList<string> Section(XElement root, string container, string item) =>
            root.Descendants().Where(e => e.Name.LocalName == container)
                .SelectMany(e => e.Elements().Where(c => c.Name.LocalName == item))
                .Select(c => (string?)c.Attribute("Name") ?? string.Empty)
                .Where(n => n.Length > 0)
                .ToList();

        internal static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(source))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            foreach (var sub in Directory.EnumerateDirectories(source))
                CopyDirectory(sub, Path.Combine(target, Path.GetFileName(sub)));
        }
    }
}
