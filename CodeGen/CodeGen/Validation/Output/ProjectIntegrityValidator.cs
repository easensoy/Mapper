using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Devices.Core;

namespace CodeGen.Validation.Output
{
    /// What EAE checks when it opens the project, checked here instead.
    ///
    /// EAE's Buildtime is a GUI tool and is not always installed beside the compiler, so a real
    /// Restore/Build cannot be part of every run. But the two ways a generated project actually fails
    /// on import are both structural and both answerable from the tree alone:
    ///
    ///   MISSING PROJECT FILES — a .dfbproj or .topologyproj entry naming a file that is not there.
    ///     EAE reports it under Solution Integrity and refuses to load the item.
    ///   ERR_NO_SUCH_TYPE     — an emitted FB whose Type has no deployed .fbt. The resource loads and
    ///     the instance silently is not there.
    ///
    /// Both look exactly like success until EAE is opened, which is usually on a rig. So they are
    /// refused here, against the staged tree, before anything is published.
    ///
    /// This is NOT a substitute for an EAE build: it cannot judge whether a type COMPILES, only whether
    /// what the project references exists. Where the real tool is available it should still be run.
    public static class ProjectIntegrityValidator
    {
        public sealed record Finding(string Kind, string Detail)
        {
            public override string ToString() => $"[{Kind}] {Detail}";
        }

        /// Throws on the first complete set of findings. Returns the artefacts it proved, for the log.
        public static (int Registrations, int Types) Validate(Configuration.CompilerConfiguration cfg)
        {
            var eae = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
            if (string.IsNullOrEmpty(eae) || !Directory.Exists(eae))
                throw new InvalidOperationException(
                    "[Integrity] the generated project root could not be derived, so nothing about the " +
                    "emitted tree can be proved. Generation ABORTED.");

            var findings = new List<Finding>();
            int registrations = CheckRegistrations(eae!, findings);
            int types = CheckReferencedTypes(eae!, findings);

            if (findings.Count > 0)
                throw new InvalidOperationException(
                    "[Integrity] the generated project would not load in EAE:" + Environment.NewLine +
                    "  - " + string.Join(Environment.NewLine + "  - ", findings.Select(f => f.ToString())) +
                    Environment.NewLine +
                    "Each of these opens cleanly and then silently omits what it names, so the run is " +
                    "ABORTED and the previous project is unchanged.");

            return (registrations, types);
        }

        // Every <Compile>/<None>/<Content> Include in a project file must name a file that is there.
        static int CheckRegistrations(string eae, List<Finding> findings)
        {
            var checked_ = 0;
            foreach (var proj in Directory.EnumerateFiles(eae, "*.dfbproj", SearchOption.AllDirectories)
                         .Concat(Directory.EnumerateFiles(eae, "*.topologyproj", SearchOption.AllDirectories))
                         .Concat(Directory.EnumerateFiles(eae, "*.hwconfigproj", SearchOption.AllDirectories)))
            {
                XDocument doc;
                try { doc = XDocument.Load(proj); }
                catch (Exception ex)
                {
                    findings.Add(new Finding("UNREADABLE PROJECT FILE",
                        $"{Rel(eae, proj)}: {ex.Message}"));
                    continue;
                }

                var dir = Path.GetDirectoryName(proj)!;
                foreach (var item in doc.Descendants()
                             .Where(e => e.Name.LocalName is "Compile" or "None" or "Content" or "EmbeddedResource"))
                {
                    var include = (string?)item.Attribute("Include");
                    // A wildcard is resolved by MSBuild, and a Link is a display name, not a path.
                    if (string.IsNullOrWhiteSpace(include) || include!.Contains('*')) continue;

                    checked_++;
                    var path = Path.GetFullPath(Path.Combine(dir, include.Replace('\\', Path.DirectorySeparatorChar)));
                    if (!File.Exists(path))
                        findings.Add(new Finding("MISSING PROJECT FILE",
                            $"{Rel(eae, proj)} registers '{include}', which is not on disk"));
                }
            }
            return checked_;
        }

        // Every FB Type an emitted resource or canvas instantiates must have a deployed .fbt.
        static int CheckReferencedTypes(string eae, List<Finding> findings)
        {
            var iec = Path.Combine(eae, "IEC61499");
            if (!Directory.Exists(iec)) return 0;

            // A type is deployed either flat (Basic/Composite) or in its own folder (a CAT).
            var deployed = Directory.EnumerateFiles(iec, "*.fbt", SearchOption.AllDirectories)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var artefact in Directory.EnumerateFiles(eae, "*.syslay", SearchOption.AllDirectories)
                         .Concat(Directory.EnumerateFiles(eae, "*.sysres", SearchOption.AllDirectories)))
            {
                XDocument doc;
                try { doc = XDocument.Load(artefact); }
                catch (Exception ex)
                {
                    findings.Add(new Finding("MALFORMED ARTEFACT", $"{Rel(eae, artefact)}: {ex.Message}"));
                    continue;
                }

                foreach (var fb in doc.Descendants().Where(e => e.Name.LocalName == "FB"))
                {
                    var type = (string?)fb.Attribute("Type");
                    if (string.IsNullOrWhiteSpace(type)) continue;

                    // A generic library FB (E_DELAY, SYMLINK*, MQTT_*) is resolved by EAE from a
                    // referenced library, not deployed into the project, so its absence proves nothing.
                    if (IsLibraryType(fb, type!)) continue;

                    referenced.Add(type!);
                    if (!deployed.Contains(type!))
                        findings.Add(new Finding("TYPE NOT DEPLOYED",
                            $"{Rel(eae, artefact)} instantiates '{(string?)fb.Attribute("Name")}' of type " +
                            $"'{type}', which has no .fbt under IEC61499"));
                }
            }
            return referenced.Count;
        }

        // A type EAE supplies rather than the generator: it carries a library namespace, or it is one of
        // the generic shapes EAE specialises at compile time from an InterfaceParams attribute.
        static bool IsLibraryType(XElement fb, string type)
        {
            var ns = (string?)fb.Attribute("Namespace") ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(ns) &&
                !ns.Equals(Configuration.GenerationConfig.Namespace, StringComparison.Ordinal))
                return true;

            return fb.Elements().Any(e => e.Name.LocalName == "Attribute" &&
                                          ((string?)e.Attribute("Name"))?.Contains("GenericFBType", StringComparison.Ordinal) == true);
        }

        static string Rel(string root, string path) =>
            path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path[(root.Length + 1)..] : path;
    }
}
