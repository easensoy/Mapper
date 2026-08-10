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
                                            IReadOnlyList<string> screenNames, string firstCanvas,
                                            HmiDefinition def)
        {
            var canvases = new StringBuilder();
            foreach (var s in screenNames)
                canvases.Append(
                    $"        <Canvas Name=\"{s}\" Title=\"\" Tooltip=\"\" Instance=\"HMI.{libraryNamespace}.{def.Deployment.CanvasNamespaceSuffix}.{s}\">\r\n" +
                    "          <Children />\r\n" +
                    "        </Canvas>\r\n");

            // Every dimension, name and chrome flag below comes from hmi.yml. The work-area height is
            // DERIVED (canvas height minus the runtime navigation bar) rather than restated, because a
            // work area that disagrees with the placement geometry silently clips the bottom row of
            // faceplates the planner believed it had room for.
            var rt = def.Runtime;
            var g = def.Geometry;
            var c = rt.Chrome;
            var main = rt.Resolution;
            var fallback = rt.FallbackResolution;

            static string B(bool value) => value ? "true" : "false";

            var xml =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
                "<CanvasesResolutionList xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" " +
                $"Name=\"{projectName}\" Version=\"{rt.SchemaVersion}\" xmlns=\"http://www.nxtcontrol.com/IEC61499.xsd\">\r\n" +
                $"  <CanvasResolution Name=\"{main.Name}\" StartCanvasClass=\"HMI.{libraryNamespace}." +
                $"{def.Deployment.CanvasNamespaceSuffix}.{rt.StartCanvas}\" " +
                $"Width=\"{g.CanvasWidth}\" Height=\"{g.CanvasHeight}\" WorkAreaWidth=\"{g.CanvasWidth}\" " +
                $"WorkAreaHeight=\"{g.WorkHeight}\" Template=\"{main.Template}\" Logger=\"{B(c.Logger)}\" " +
                $"Login=\"{B(c.Login)}\" NavigationControl=\"{c.NavigationControl}\" CurrentUser=\"{B(c.CurrentUser)}\" " +
                $"LanguageButton=\"{B(c.LanguageButton)}\" RuntimeConnection=\"{B(c.RuntimeConnection)}\" " +
                $"NavigationBar=\"{B(c.NavigationBar)}\" NewVersionDeployed=\"{B(c.NewVersionDeployed)}\" " +
                $"IsCanvasTopologyPanel=\"true\" ResizeBehaviour=\"{main.ResizeBehaviour}\" " +
                $"CanvasButtonHeight=\"{main.CanvasButtonHeight}\">\r\n" +
                $"    <Topology Name=\"Default\" FirstCanvas=\"{firstCanvas}\">\r\n" +
                "      <Canvases>\r\n" + canvases + "      </Canvases>\r\n" +
                "    </Topology>\r\n" +
                "  </CanvasResolution>\r\n" +
                $"  <CanvasResolution Name=\"{fallback.Name}\" StartCanvasClass=\"\" Width=\"-1\" Height=\"-1\" WorkAreaWidth=\"-1\" " +
                $"WorkAreaHeight=\"-1\" Template=\"\" Login=\"{B(c.Login)}\" CurrentUser=\"{B(c.CurrentUser)}\" " +
                $"LanguageButton=\"{B(c.LanguageButton)}\" RuntimeConnection=\"{B(c.RuntimeConnection)}\" " +
                $"NavigationBar=\"{B(c.NavigationBar)}\" NewVersionDeployed=\"{B(c.NewVersionDeployed)}\" WarningText=\"\" " +
                $"SiblingButtonCount=\"{fallback.SiblingButtonCount}\" ChildButtonCount=\"{fallback.ChildButtonCount}\" " +
                $"IsCanvasTopologyPanel=\"true\" ResizeBehaviour=\"{fallback.ResizeBehaviour}\" " +
                $"CanvasButtonHeight=\"{fallback.CanvasButtonHeight}\">\r\n" +
                "    <Topology Name=\"Default\">\r\n      <Canvases />\r\n    </Topology>\r\n" +
                "  </CanvasResolution>\r\n" +
                "</CanvasesResolutionList>";

            File.WriteAllText(Path.Combine(hmiDir, "CanvasesResolutionList.xml"), xml, new UTF8Encoding(true));
        }
    }
}
