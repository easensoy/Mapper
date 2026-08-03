using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace CodeGen.Hmi
{
    // Registers the HMI project (HMI.csproj) and the canvas navigation topology
    // (CanvasesResolutionList.xml). The csproj item groups are rebuilt from the files actually
    // present on disk, so a registration can never dangle and a generated file can never be missed.
    internal static class HmiProjectEmitter
    {
        private const string StartCanvas = "StartCanvas_2";

        internal static void EmitCsproj(string hmiDir, IReadOnlyCollection<string> screenNames)
        {
            var csproj = Path.Combine(hmiDir, "HMI.csproj");
            var doc = XDocument.Load(csproj);
            var ns = doc.Root!.Name.Namespace;

            // Drop the generated item groups; keep PropertyGroups, References and the SDK imports.
            doc.Root.Elements(ns + "ItemGroup")
                .Where(g => g.Elements().Any(e => e.Name.LocalName is "Compile" or "None" or "EmbeddedResource"))
                .Remove();

            var compile = new List<XElement>();
            var none = new List<XElement>();
            var embedded = new List<XElement>();

            foreach (var full in Directory.EnumerateFiles(hmiDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(hmiDir, full).Replace('/', '\\');
                var file = Path.GetFileName(rel);

                if (rel.Equals("HMI.csproj", StringComparison.OrdinalIgnoreCase)) continue;
                if (rel.StartsWith("bin\\", StringComparison.OrdinalIgnoreCase) ||
                    rel.StartsWith("obj\\", StringComparison.OrdinalIgnoreCase)) continue;

                if (file.EndsWith(".cnv.Designer.cs", StringComparison.OrdinalIgnoreCase))
                    compile.Add(Item(ns, "Compile", rel, Dependent(ns, file, ".cnv.Designer.cs")));
                else if (file.EndsWith(".cnv.cs", StringComparison.OrdinalIgnoreCase))
                {
                    // Only work-area canvases carry <Canvas>true</Canvas>; the shell canvas does not.
                    var isScreen = !rel.Contains('\\') && screenNames.Contains(file[..^".cnv.cs".Length]);
                    compile.Add(isScreen
                        ? Item(ns, "Compile", rel, new XElement(ns + "Canvas", "true"))
                        : Item(ns, "Compile", rel));
                }
                else if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    compile.Add(Item(ns, "Compile", rel));
                else if (file.EndsWith(".cnv.resx", StringComparison.OrdinalIgnoreCase))
                    embedded.Add(Item(ns, "EmbeddedResource", rel, Dependent(ns, file, ".cnv.resx")));
                else if (file.EndsWith(".cnv.xml", StringComparison.OrdinalIgnoreCase))
                    embedded.Add(Item(ns, "EmbeddedResource", rel, Dependent(ns, file, ".cnv.xml")));
                else if (file.EndsWith(".Design.resx", StringComparison.OrdinalIgnoreCase))
                    none.Add(Item(ns, "None", rel));
                else if (rel.StartsWith("Alarms\\", StringComparison.OrdinalIgnoreCase))
                {
                    embedded.Add(Item(ns, "EmbeddedResource", rel));
                    if (file.Equals("AlarmClasses.xml", StringComparison.OrdinalIgnoreCase))
                        none.Add(Item(ns, "None", rel));
                }
                else if (rel.StartsWith("Configurations\\", StringComparison.OrdinalIgnoreCase))
                    embedded.Add(Item(ns, "EmbeddedResource", rel));
                else if (file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                         file.EndsWith(".theme", StringComparison.OrdinalIgnoreCase))
                    none.Add(Item(ns, "None", rel));
            }

            var imports = doc.Root.Elements(ns + "Import").ToList();
            foreach (var group in new[] { compile, none, embedded })
            {
                if (group.Count == 0) continue;
                var ig = new XElement(ns + "ItemGroup",
                    group.OrderBy(e => (string)e.Attribute("Include")!, StringComparer.OrdinalIgnoreCase));
                if (imports.Count > 0) imports[0].AddBeforeSelf(ig); else doc.Root.Add(ig);
            }

            doc.Save(csproj);
        }

        private static XElement Item(XNamespace ns, string name, string include, params object[] children) =>
            new(ns + name, new XAttribute("Include", include), children);

        // EAE writes DependentUpon as a bare filename, never a path.
        private static XElement Dependent(XNamespace ns, string file, string suffix) =>
            new(ns + "DependentUpon", file[..^suffix.Length] + ".cnv.cs");

        internal static void EmitCanvasList(string hmiDir, string projectName, string libraryNamespace,
                                            IReadOnlyList<string> screenNames, string firstCanvas)
        {
            var canvases = new StringBuilder();
            foreach (var s in screenNames)
                canvases.Append(
                    $"        <Canvas Name=\"{s}\" Title=\"\" Tooltip=\"\" Instance=\"HMI.{libraryNamespace}.Canvases.{s}\">\r\n" +
                    "          <Children />\r\n" +
                    "        </Canvas>\r\n");

            var xml =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
                "<CanvasesResolutionList xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" " +
                $"Name=\"{projectName}\" Version=\"1.6.0.0\" xmlns=\"http://www.nxtcontrol.com/IEC61499.xsd\">\r\n" +
                $"  <CanvasResolution Name=\"1024x768\" StartCanvasClass=\"HMI.{libraryNamespace}.Canvases.{StartCanvas}\" " +
                "Width=\"1024\" Height=\"768\" WorkAreaWidth=\"1024\" WorkAreaHeight=\"698\" Template=\"Default\" Logger=\"true\" " +
                "Login=\"true\" NavigationControl=\"1\" CurrentUser=\"true\" LanguageButton=\"true\" RuntimeConnection=\"true\" " +
                "NavigationBar=\"true\" NewVersionDeployed=\"true\" IsCanvasTopologyPanel=\"true\" ResizeBehaviour=\"Standard\" " +
                "CanvasButtonHeight=\"30\">\r\n" +
                $"    <Topology Name=\"Default\" FirstCanvas=\"{firstCanvas}\">\r\n" +
                "      <Canvases>\r\n" + canvases + "      </Canvases>\r\n" +
                "    </Topology>\r\n" +
                "  </CanvasResolution>\r\n" +
                "  <CanvasResolution Name=\"Without resolution\" StartCanvasClass=\"\" Width=\"-1\" Height=\"-1\" WorkAreaWidth=\"-1\" " +
                "WorkAreaHeight=\"-1\" Template=\"\" Login=\"true\" CurrentUser=\"true\" LanguageButton=\"true\" RuntimeConnection=\"true\" " +
                "NavigationBar=\"true\" NewVersionDeployed=\"true\" WarningText=\"\" SiblingButtonCount=\"5\" ChildButtonCount=\"5\" " +
                "IsCanvasTopologyPanel=\"true\" ResizeBehaviour=\"None\" CanvasButtonHeight=\"30\">\r\n" +
                "    <Topology Name=\"Default\">\r\n      <Canvases />\r\n    </Topology>\r\n" +
                "  </CanvasResolution>\r\n" +
                "</CanvasesResolutionList>";

            File.WriteAllText(Path.Combine(hmiDir, "CanvasesResolutionList.xml"), xml, new UTF8Encoding(true));
        }
    }
}
