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
        internal static IReadOnlyList<string> Validate(string hmiDir, string eaeProjectDir, string cfgDir, HmiSyslay syslay, HmiPlan plan, HmiDefinition def)
        {
            var problems = new List<string>();

            problems.AddRange(ValidateFireable(hmiDir, plan, def));

            var fbIds = syslay.Ids;

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
                        item.X + item.Symbol.Width > def.Geometry.CanvasWidth ||
                        item.Y + item.Symbol.Height > def.Geometry.WorkHeight)
                        problems.Add($"{screen.Name}: '{item.Name}' overflows the canvas " +
                                     $"({item.X},{item.Y} {item.Symbol.Width}x{item.Symbol.Height}).");
                }

                // A navigation button drawn over a faceplate is not a cosmetic fault: it covers a
                // live tile and swallows the click meant for it. The hub used to draw its family row
                // at ContentTop, exactly where its own first row of tiles starts.
                var g = def.Geometry;
                foreach (var b in screen.Buttons)
                {
                    if (b.X < 0 || b.Y < 0 ||
                        b.X + g.ButtonWidth > g.CanvasWidth || b.Y + g.ButtonHeight > g.WorkHeight)
                        problems.Add($"{screen.Name}: navigation button '{b.Name}' is off the canvas " +
                                     $"({b.X},{b.Y}).");

                    foreach (var item in screen.Items.Where(i =>
                                 b.X < i.X + i.Symbol.Width && i.X < b.X + g.ButtonWidth &&
                                 b.Y < i.Y + i.Symbol.Height && i.Y < b.Y + g.ButtonHeight))
                        problems.Add($"{screen.Name}: navigation button '{b.Name}' at ({b.X},{b.Y}) " +
                                     $"overlaps '{item.Name}' at ({item.X},{item.Y}).");
                }
            }

            foreach (var dup in plan.Screens.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                problems.Add($"Duplicate canvas name '{dup.Key}'.");

            var targets = new HashSet<string>(plan.Screens.Select(s => s.Name), StringComparer.Ordinal);
            foreach (var screen in plan.Screens)
                foreach (var b in screen.Buttons.Where(b => !targets.Contains(b.CanvasName)))
                    problems.Add($"{screen.Name}: navigation button '{b.Name}' targets unknown canvas '{b.CanvasName}'.");

            // ... and the other direction: a canvas no button reaches is registered, compiled and
            // deployed, and the operator can never open it. That is how the hub's own later pages
            // were lost when it carried family links but not its own paging.
            var linked = new HashSet<string>(
                plan.Screens.SelectMany(s => s.Buttons).Select(b => b.CanvasName), StringComparer.Ordinal);
            foreach (var s in plan.Screens.Where(s => !linked.Contains(s.Name) && s.Name != plan.FirstCanvas))
                problems.Add($"canvas '{s.Name}' is generated but no navigation button reaches it.");

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
            //
            // The STAGED .cfg is checked, not the one already in IEC61499: the committed copy belongs
            // to the previous generation, so validating it would judge this HMI against last run's
            // symbol set and fail whenever the selection legitimately changed.
            // Only the CATs this plan actually placed have a .cfg - a template the model does not use
            // (the centre-home swivel on a no-clamp model, say) is legitimately not registered at all.
            var registered = new HashSet<string>(plan.SelectedSymbols.Select(s => s.CatType),
                                                 StringComparer.OrdinalIgnoreCase);
            foreach (var tpl in plan.UsedTemplates.Where(t => registered.Contains(t.CatType)))
            {
                var cfg = Path.Combine(cfgDir, tpl.CatType + ".cfg");
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

            problems.AddRange(CommandSafetyViolations(hmiDir, plan));
            return problems;
        }

        // THE SAFETY INVARIANT. A generated control may reach the controller ONLY through an output
        // its own symbol contract declares - which by construction is a CAT command port. It may
        // never bind a physical output, and it may never carry a handler that writes one.
        //
        // This is checked on the STAGED FILES, not on the plan, so a template that regains a rogue
        // control is caught even if the planner believed the symbol was harmless.
        private static IEnumerable<string> CommandSafetyViolations(string hmiDir, HmiPlan plan)
        {
            // Every output a placed symbol declares, as the contract states it.
            var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in plan.Screens.SelectMany(s => s.Items))
            {
                foreach (var e in item.Symbol.OutputEvents) declared.Add(e);
                foreach (var d in item.Symbol.OutputTags) declared.Add(d);
            }

            foreach (var designer in Directory.EnumerateFiles(hmiDir, "*.cnv.Designer.cs", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(designer);
                var rel = Path.GetRelativePath(hmiDir, designer);

                // A bound tag that no placed contract declares as an output cannot be a CAT command
                // port. Binding one would be reaching past the contract - the definition of writing
                // a physical output directly.
                foreach (Match m in Regex.Matches(text, @"\.(?<ctl>[A-Za-z0-9_]+)\.TagName\s*=\s*""(?<tag>[^""]+)"""))
                {
                    var tag = m.Groups["tag"].Value;
                    if (LooksPhysical(tag) && !declared.Contains(tag))
                        yield return $"COMMAND SAFETY: {rel} binds '{tag}', which no placed symbol contract " +
                                     "declares as an output - a control must only fire a declared CAT command port.";
                }
            }
        }

        // A physical output is a coil/symlink-shaped name. Deliberately conservative: it flags the
        // shapes the device emitters use for real outputs, and anything it flags must also be absent
        // from every placed contract before it is reported.
        private static bool LooksPhysical(string tag) =>
            tag.StartsWith("$${", StringComparison.Ordinal) ||
            tag.Contains("OutputTo", StringComparison.OrdinalIgnoreCase) ||
            tag.EndsWith("_Q", StringComparison.OrdinalIgnoreCase) ||
            tag.Contains("Coil", StringComparison.OrdinalIgnoreCase);

        // Model-level validation: the checks that need the semantic model rather than the emitted
        // files. These FAIL the generation rather than warn - a panel that silently drops a component
        // is worse than one that refuses to build, because the operator cannot tell the difference
        // between "this machine has no such part" and "the generator lost it".
        internal static IReadOnlyList<string> ValidateModel(HmiPlant plant, HmiPlan plan)
        {
            var problems = new List<string>();

            var placedTags = new HashSet<string>(
                plan.Screens.SelectMany(s => s.Items).Select(i => i.TagName), StringComparer.Ordinal);

            // 1. Every twin-declared component that this deployment emitted must be reachable on some
            //    screen. Infrastructure (couplers, terminators) is exempt: it is not a twin component.
            foreach (var c in plant.Components)
                if (!placedTags.Contains(c.TagName))
                    problems.Add($"'{c.InstanceName}' ({c.CatType}, tag {c.TagName}) is emitted in the syslay but " +
                                 "appears on no generated screen.");

            foreach (var p in plant.Processes)
                if (!placedTags.Contains(p.TagName))
                    problems.Add($"process '{p.InstanceName}' (tag {p.TagName}) appears on no generated screen.");

            // 2. Nothing may be placed that the model does not know about - that is how a stale screen
            //    survives a component being removed from the twin.
            var known = new HashSet<string>(
                plant.Components.Select(c => c.TagName)
                    .Concat(plant.Processes.Select(p => p.TagName))
                    .Concat(plant.Stations.Select(s => s.TagName)),
                StringComparer.Ordinal);
            foreach (var tag in placedTags.Where(t => !known.Contains(t)))
                problems.Add($"a screen places TagName {tag}, which is not in the semantic model (stale placement).");

            // 3. Every command a placed symbol can raise must be GOVERNED BY AN ACTION that was
            //    judged. A symbol that reaches the controller through a route no action covers is an
            //    ungoverned command - the one thing that must never ship, because nothing proved the
            //    controller honours it and nothing could have suppressed it.
            foreach (var i in plan.Screens.SelectMany(s => s.Items)
                         .Where(i => i.Symbol.CanRaiseAnything && i.Actions.Count == 0)
                         .GroupBy(i => (i.Name, i.Symbol.Name)).Select(g => g.First()))
                problems.Add($"UNGOVERNED COMMAND: '{i.Name}' places command-capable symbol " +
                             $"'{i.Symbol.Name}' but no declared action covers what it can raise.");

            return problems;
        }

        // THE DECISIVE CHECK: what the EMITTED SOURCE can actually raise, read back off disk.
        //
        // Everything else in this file reasons about the plan. This one reasons about the files EAE
        // is about to compile, because that is the only thing an operator's finger reaches. A verdict
        // of "withheld" that leaves a live call or a live tag binding is a lie, and it fails here.
        private static IReadOnlyList<string> ValidateFireable(string hmiDir, HmiPlan plan, HmiDefinition def)
        {
            var problems = new List<string>();
            var actions = plan.AllVerdicts;

            // Every symbol whose source is ON DISK, placed or not. Symbol selection prunes most
            // unused symbols, but the CAT-shared partial classes force some back in, and a live
            // call in one of those is still a live call in the compiled assembly - the csproj is
            // rebuilt from whatever files are present. Asking the disk is the only honest test.
            var placed = actions.Select(a => (a.CatType, Symbol: a.Symbol)).Distinct()
                .Where(x => File.Exists(Path.Combine(hmiDir, x.CatType,
                                                     $"{x.CatType}_{x.Symbol}.cnv.cs")))
                .ToList();

            foreach (var (catType, symbol) in placed)
            {
                var code = Path.Combine(hmiDir, catType, $"{catType}_{symbol}.cnv.cs");
                var designer = Path.Combine(hmiDir, catType, $"{catType}_{symbol}.cnv.Designer.cs");
                var body = (File.Exists(code) ? File.ReadAllText(code) : string.Empty) +
                           (File.Exists(designer) ? File.ReadAllText(designer) : string.Empty);
                if (body.Length == 0) continue;

                foreach (var group in actions
                             .Where(a => a.CatType == catType && a.Symbol == symbol)
                             .GroupBy(a => a.ActionId))
                {
                    var a = group.First();
                    // A tag-bound control the faceplate hides for good is bound but unreachable,
                    // which is how the reference itself ships the plant RESET.
                    var sym = plan.UsedTemplates
                        .FirstOrDefault(t => t.CatType == catType)?.Symbols
                        .FirstOrDefault(x => x.Name == symbol);
                    var live = a.Call != null
                        ? Regex.IsMatch(body, @"(?<![\w.])" + Regex.Escape(a.Call) + @"\s*;")
                        : a.Writes != null &&
                          body.Contains($"TagName = \"{a.Writes}\"", StringComparison.Ordinal) &&
                          sym?.DeadTags.Contains(a.Writes, StringComparer.Ordinal) != true;

                    // NEGATIVE: withheld on every instance, yet the source can still raise it.
                    if (group.All(v => !v.Effective) && live)
                        problems.Add($"NON-FIREABLE VIOLATION: action '{a.ActionId}' is withheld on every " +
                                     $"instance of '{catType}_{symbol}' ({a.Detail}), but the emitted source " +
                                     $"still {(a.Call != null ? $"calls {a.Call}" : $"binds a control to '{a.Writes}'")}.");

                    // POSITIVE: effective, so the source MUST still be able to raise it. This is what
                    // stops the suppressor quietly disarming a supported command.
                    if (group.Any(v => v.Effective) && !live)
                        problems.Add($"action '{a.ActionId}' is reported effective on '{catType}_{symbol}' " +
                                     "but the emitted source can no longer raise it.");
                }
            }

            // No binding the deployed interface cannot serve may remain in the emitted source.
            // This is what stops a faceplate authored for a richer CAT displaying a frozen value.
            foreach (var d in plan.DeadBindings.Where(x => !x.Tag.Contains('.', StringComparison.Ordinal)))
            {
                var designer = Path.Combine(hmiDir, d.CatType, $"{d.CatType}_{d.Symbol}.cnv.Designer.cs");
                if (File.Exists(designer) &&
                    File.ReadAllText(designer).Contains($"TagName = \"{d.Tag}\"", StringComparison.Ordinal))
                    problems.Add($"UNSERVED BINDING: '{d.CatType}_{d.Symbol}' still binds '{d.Tag}', which " +
                                 $"the deployed {d.CatType}_HMI interface does not serve.");
            }

            // A withheld FIRED action must also leave its control disabled, so the panel shows the
            // operator it is unavailable rather than accepting a click that does nothing.
            foreach (var group in actions.Where(a => a.Call != null)
                         .GroupBy(a => (a.CatType, a.Symbol, a.ActionId))
                         .Where(g => g.All(v => !v.Effective)))
            {
                var a = group.First();
                var catType = a.CatType;
                var code = Path.Combine(hmiDir, catType, $"{catType}_{a.Symbol}.cnv.cs");
                if (File.Exists(code) && !File.ReadAllText(code).Contains(def.WithheldMarker, StringComparison.Ordinal))
                    problems.Add($"action '{a.ActionId}' is withheld on '{catType}_{a.Symbol}' but the emitted " +
                                 "source carries no withheld marker, so nothing recorded the suppression.");
            }

            return problems;
        }

    }
}
