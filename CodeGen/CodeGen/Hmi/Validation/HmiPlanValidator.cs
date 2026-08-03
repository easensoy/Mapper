using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CodeGen.Hmi
{
    // Post-emission structural checks. Everything here is cheap and catches the failure modes that
    // would otherwise only surface as an EAE "missing project file" dialog - or, far worse, as a
    // live command control on a panel that is supposed to be monitoring-only.
    internal static class HmiPlanValidator
    {
        internal static IReadOnlyList<string> Validate(string hmiDir, string eaeProjectDir, string syslayPath, HmiPlan plan)
        {
            var problems = new List<string>();

            var fbIds = new HashSet<string>(
                XDocument.Load(syslayPath).Descendants().Where(e => e.Name.LocalName == "FB")
                    .Select(e => (string?)e.Attribute("ID")).Where(id => !string.IsNullOrEmpty(id))!,
                StringComparer.Ordinal);

            foreach (var screen in plan.Screens)
            {
                foreach (var item in screen.Items.Where(i => !fbIds.Contains(i.TagName)))
                    problems.Add($"{screen.Name}: '{item.Name}' binds TagName {item.TagName}, which is not an FB in the syslay.");

                // The same component may legitimately appear on several owning process screens;
                // twice on ONE canvas is always a fault.
                var names = screen.Items.Select(i => i.Name)
                    .Concat(screen.Buttons.Select(b => b.Name))
                    .Concat(screen.Captions.Select(c => c.Name)).ToList();
                foreach (var dup in names.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1))
                    problems.Add($"{screen.Name}: duplicate control name '{dup.Key}'.");

                foreach (var tag in screen.Items.GroupBy(i => i.TagName, StringComparer.Ordinal).Where(g => g.Count() > 1))
                    problems.Add($"{screen.Name}: TagName {tag.Key} is placed {tag.Count()} times on one canvas.");

                foreach (var item in screen.Items)
                {
                    if (item.X < 0 || item.Y < 0 ||
                        item.X + item.Symbol.Width > HmiPlanner.CanvasWidth ||
                        item.Y + item.Symbol.Height > HmiPlanner.CanvasHeight)
                        problems.Add($"{screen.Name}: '{item.Name}' overflows the canvas " +
                                     $"({item.X},{item.Y} {item.Symbol.Width}x{item.Symbol.Height}).");
                }
            }

            foreach (var dup in plan.Screens.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                problems.Add($"Duplicate canvas name '{dup.Key}'.");

            var targets = new HashSet<string>(plan.Screens.Select(s => s.Name), StringComparer.Ordinal);
            foreach (var screen in plan.Screens)
                foreach (var b in screen.Buttons.Where(b => !targets.Contains(b.CanvasName)))
                    problems.Add($"{screen.Name}: navigation button '{b.Name}' targets unknown canvas '{b.CanvasName}'.");

            // Every csproj registration must point at a file that exists.
            var csproj = Path.Combine(hmiDir, "HMI.csproj");
            if (File.Exists(csproj))
            {
                foreach (var include in XDocument.Load(csproj).Descendants()
                             .Where(e => e.Name.LocalName is "Compile" or "None" or "EmbeddedResource")
                             .Select(e => (string?)e.Attribute("Include")).Where(i => !string.IsNullOrEmpty(i)))
                {
                    if (!File.Exists(Path.Combine(hmiDir, include!.Replace('\\', Path.DirectorySeparatorChar))))
                        problems.Add($"HMI.csproj registers a missing file: {include}");
                }
            }

            // Every canvas named in the CAT .cfg must exist in the HMI project.
            foreach (var tpl in plan.UsedTemplates)
            {
                var cfg = Path.Combine(eaeProjectDir, "IEC61499", tpl.CatType, tpl.CatType + ".cfg");
                if (!File.Exists(cfg)) { problems.Add($"{tpl.CatType}: .cfg was not written."); continue; }

                foreach (var file in XDocument.Load(cfg).Descendants()
                             .Where(e => e.Name.LocalName is "Symbol" or "DependentFiles")
                             .Select(e => e.Name.LocalName == "Symbol" ? (string?)e.Attribute("FileName") : e.Value)
                             .Where(v => !string.IsNullOrEmpty(v)))
                {
                    var rel = file!.StartsWith("..\\HMI\\", StringComparison.OrdinalIgnoreCase) ? file[7..] : null;
                    if (rel != null && !File.Exists(Path.Combine(hmiDir, rel.Replace('\\', Path.DirectorySeparatorChar))))
                        problems.Add($"{tpl.CatType}.cfg references a missing HMI file: {file}");
                }
            }

            foreach (var xml in Directory.EnumerateFiles(hmiDir, "*.xml", SearchOption.AllDirectories))
            {
                try { XDocument.Load(xml); }
                catch (Exception ex) { problems.Add($"Malformed XML {Path.GetRelativePath(hmiDir, xml)}: {ex.Message}"); }
            }

            if (plan.ReadOnly) problems.AddRange(ReadOnlyViolations(hmiDir, plan));
            return problems;
        }

        // A monitoring HMI must not be able to reach the controller by ANY route: not a bound output,
        // not a button, not a leftover handler. These are hard failures, not warnings.
        private static IEnumerable<string> ReadOnlyViolations(string hmiDir, HmiPlan plan)
        {
            foreach (var screen in plan.Screens)
                foreach (var item in screen.Items.Where(i => i.Symbol.CommandCapable))
                    yield return $"READ-ONLY VIOLATION: {screen.Name} places '{item.Name}' using symbol " +
                                 $"'{item.Symbol.Name}', which declares controller outputs ({item.Symbol.Outputs}).";

            foreach (var screen in plan.Screens.Where(s => HmiNames.IsCommandSymbol(s.Name)))
                yield return $"READ-ONLY VIOLATION: command screen '{screen.Name}' was generated.";

            foreach (var contract in Directory.EnumerateFiles(hmiDir, "*.cnv.xml", SearchOption.AllDirectories))
            {
                var (events, tags) = HmiTemplateLibrary.ReadContractOutputs(contract);
                if (events.Count > 0 || tags.Count > 0)
                    yield return $"READ-ONLY VIOLATION: {Path.GetRelativePath(hmiDir, contract)} still declares " +
                                 $"controller outputs ({string.Join(", ", events.Concat(tags))}).";
            }

            foreach (var designer in Directory.EnumerateFiles(hmiDir, "*.cnv.Designer.cs", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(designer);
                var rel = Path.GetRelativePath(hmiDir, designer);

                foreach (Match m in Regex.Matches(text, @"=\s*new\s+[A-Za-z0-9_.]*?(?<t>[A-Za-z0-9_]*Button)\s*\(\s*\)"))
                {
                    var t = m.Groups["t"].Value;
                    if (t == "ChangeCanvasButton") continue;
                    yield return $"READ-ONLY VIOLATION: {rel} instantiates a command control ({t}).";
                }

                foreach (Match m in Regex.Matches(text, @"\.Click\s*\+="))
                    yield return $"READ-ONLY VIOLATION: {rel} wires a click handler ({m.Value.Trim()}).";
            }

            foreach (var code in Directory.EnumerateFiles(hmiDir, "*.cnv.cs", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(code);
                if (text.Contains("FireEvent", StringComparison.Ordinal))
                    yield return $"READ-ONLY VIOLATION: {Path.GetRelativePath(hmiDir, code)} calls FireEvent.";
            }
        }
    }
}
