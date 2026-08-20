using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeGen.Hmi
{
    // Turns the finished syslay into a screen plan.
    //
    // Grouping is derived, never named: each Process1_Generic instance carries its compiled Recipe as
    // a syslay parameter, so CmdTargetName tells us which actuators it drives and Wait1Id which
    // sensors it observes. Components no process claims fall to a residual overview screen - a
    // component is never silently dropped.
    internal static class HmiPlanner
    {
        internal static HmiPlan Plan(
            HmiPlant plant, IReadOnlyList<HmiCatTemplate> templates, HmiEccIndex ecc, HmiDefinition def)
        {
            var diagnostics = new List<string>(plant.Diagnostics);
            var byType = templates.ToDictionary(t => t.CatType, StringComparer.OrdinalIgnoreCase);

            var drawable = Drawable(plant, byType, def, diagnostics);

            // ROLES, derived from the model: a component a process COMMANDS is an actuator, one it
            // only OBSERVES is a sensor. This is the compiled recipe talking - no CAT names, no
            // instance names, no state counts, and it holds for clamp and no-clamp alike.
            var commanded = new HashSet<string>(plant.Processes.SelectMany(p => p.Owned), StringComparer.Ordinal);

            HmiRole RoleOf(string instance) =>
                plant.Stations.Any(s => s.InstanceName == instance) ? HmiRole.Station
                : plant.Processes.Any(p => p.InstanceName == instance) ? HmiRole.Process
                : commanded.Contains(instance) ? HmiRole.Actuator
                : HmiRole.Sensor;

            // One family per declared screen, drawn by the SAME paginator. A family is always
            // generated even when its capability is unsupported - the operator still needs to see the
            // process, with its controls disabled and the reason stated.
            var families = new List<(string Base, string Title, List<Drawn> Items)>();

            foreach (var fam in def.Screens.Families)
            {
                var members = new List<Drawn>();

                foreach (var name in drawable.Keys.OrderBy(n => n, StringComparer.Ordinal))
                {
                    var role = RoleOf(name);
                    if (!fam.Include.Contains(role)) continue;

                    // A family may place only the instances whose own capability passed (Setup jog).
                    if (fam.OnlySupported && fam.Requires is { } need &&
                        !CapabilitiesOf(plant, name).Any(c => c.Purpose == need && c.Supported))
                        continue;

                    // The family's symbol for this role - the Automatic process tile, the Setup jog
                    // canvas - falling back to the primary when the template does not ship it.
                    var d = drawable[name];
                    var wanted = fam.SymbolFor(role, def.Deployment.PrimarySymbol);
                    var symbol = d.Template.Placeable(wanted) ?? d.Symbol;

                    // A command symbol whose EVERY action is withheld is not shipped: the operator
                    // gets the monitoring variant instead, so the screen still shows the live values
                    // and the compiled panel carries no control that could raise a refused command.
                    if (!ReferenceEquals(symbol, d.Symbol) && Offers(symbol, def) &&
                        !Verdicts(plant, name, d.Template.CatType, symbol, ecc, def).Any(v => v.Effective) &&
                        !Offers(d.Symbol, def))
                        symbol = d.Symbol;

                    if (symbol.Width <= 0 || symbol.Height <= 0) continue;
                    members.Add(d with { Symbol = symbol });
                }

                if (members.Count == 0)
                {
                    diagnostics.Add($"screen family '{fam.Name}' has no members in this model - not generated.");
                    continue;
                }
                families.Add((fam.Name, fam.Title, members));
            }

            var screens = new List<HmiScreen>();


            foreach (var fam in families)
            {
                var pages = Paginate(fam.Items, plant, ecc, def);
                for (var i = 0; i < pages.Count; i++)
                {
                    var title = pages.Count > 1 ? $"{fam.Title} ({i + 1}/{pages.Count})" : fam.Title;
                    screens.Add(new HmiScreen(PageName(fam.Base, i), title,
                        pages[i].Items, Array.Empty<HmiNavButton>(), pages[i].Captions));
                }
            }

            screens.AddRange(Detail(plant, def));

            var all = screens
                .Select(s => s with
                {
                    Buttons = Nav(s.Name, screens, def)
                })
                .ToList();

            // Every screen carries its title, and - where it actually has one - a line naming the
            // actions this build withholds. The reason reaches the operator on the panel instead of
            // living only in a log line and a side file.
            all = all.Select(s => s with
            {
                Captions = Chrome(s, def).Concat(Withheld(s, def)).Concat(s.Captions).ToList()
            }).ToList();

            var used = families.SelectMany(f => f.Items).Select(i => i.Template)
                .GroupBy(t => t.CatType, StringComparer.Ordinal).Select(g => g.First()).ToList();

            var selected = SelectSymbols(families.SelectMany(f => f.Items).ToList(), plant, ecc, def);

            foreach (var v in all.SelectMany(x => x.Items).SelectMany(i => i.Actions)
                         .Where(v => !v.Effective)
                         .GroupBy(v => (v.Symbol, v.ActionId)).Select(g => g.First()))
                diagnostics.Add($"{def.UnsupportedCommandNotice} '{v.Label}' on '{v.Symbol}': {v.Detail}");

            // Judged over EVERY symbol of every deployed CAT, because the CAT-shared partial
            // classes are compiled whether or not a canvas places the symbol.
            var allVerdicts = used.SelectMany(tpl =>
                    drawable.Values.Where(d => d.Template.CatType == tpl.CatType)
                        .SelectMany(d => tpl.Symbols.SelectMany(sym =>
                            Verdicts(plant, d.Name, tpl.CatType, sym, ecc, def))))
                .ToList();

            return new HmiPlan(all, used, diagnostics, selected, allVerdicts);
        }

        private sealed record Drawn(string Name, string TagName, HmiCatTemplate Template, HmiSymbol Symbol);

        // The symbols this generation owns. Three kinds, and the distinction is what stops a dormant
        // command canvas shipping:
        //   * the PLACED tile symbol - always kept
        //   * a pop-up FACEPLATE that only displays (fault, interlock) - kept, because the placed
        //     symbol opens it
        //   * a pop-up COMMAND faceplate (setup/jog) - kept ONLY where the capability that drives it
        //     is actually supported on an instance of that CAT. Registering it otherwise compiles a
        //     canvas whose controls the controller would ignore.
        private static IReadOnlyList<HmiSelectedSymbol> SelectSymbols(
            IReadOnlyList<Drawn> placed, HmiPlant plant, HmiEccIndex ecc, HmiDefinition def)
        {
            var selected = new HashSet<HmiSelectedSymbol>();

            foreach (var group in placed.GroupBy(p => p.Template.CatType, StringComparer.OrdinalIgnoreCase))
            {
                var tpl = group.First().Template;

                foreach (var d in group) selected.Add(new HmiSelectedSymbol(tpl.CatType, d.Symbol.Name));

                // Does ANY instance of this CAT support a command? Judged from the resolved
                // capabilities, never from the symbol's name.
                var supportsCommand = group.Select(d => d.Name).Distinct(StringComparer.Ordinal)
                    .Any(n => tpl.Symbols.Any(sym =>
                        Verdicts(plant, n, tpl.CatType, sym, ecc, def).Any(v => v.Effective)));

                foreach (var sym in tpl.Symbols)
                {
                    // Already selected as a placed tile.
                    if (selected.Contains(new HmiSelectedSymbol(tpl.CatType, sym.Name))) continue;

                    // A command canvas ships only where the capability driving it is supported.
                    // This is the whole point of the selection: an unsupported one is dead source.
                    if (sym.CommandCapable) { if (supportsCommand) selected.Add(new HmiSelectedSymbol(tpl.CatType, sym.Name)); continue; }

                    // A display pop-up (fault, interlock) is opened BY the placed tile, so it ships
                    // with it. A non-placed, non-command, non-pop-up symbol is genuinely unused.
                    if (sym.IsFaceplate) selected.Add(new HmiSelectedSymbol(tpl.CatType, sym.Name));
                }
            }

            return selected
                .OrderBy(s => s.CatType, StringComparer.Ordinal)
                .ThenBy(s => s.Symbol, StringComparer.Ordinal)
                .ToList();
        }

        // The ONE effective verdict per placed symbol. Everything downstream reads this.
        private static IReadOnlyList<HmiActionVerdict> Verdicts(
            HmiPlant plant, string instance, string catType, HmiSymbol symbol,
            HmiEccIndex ecc, HmiDefinition def) =>
            HmiActionResolver.For(instance, catType, symbol,
                                  CapabilitiesOf(plant, instance).ToList(), ecc, def);

        // Does this symbol put any operator command in front of the operator at all?
        private static bool Offers(HmiSymbol symbol, HmiDefinition def) =>
            def.Actions.Any(symbol.Presents);

        // Everything drawable, taken from the semantic model. A component reaches this point only if
        // the twin declared it AND the deployment emitted it, so a placement can never dangle.
        private static Dictionary<string, Drawn> Drawable(
            HmiPlant plant, IReadOnlyDictionary<string, HmiCatTemplate> byType,
            HmiDefinition def, List<string> diagnostics)
        {
            var result = new Dictionary<string, Drawn>(StringComparer.Ordinal);
            var missing = new SortedSet<string>(StringComparer.Ordinal);

            void Add(string name, string tag, string catType)
            {
                if (result.ContainsKey(name)) return;
                // Resolve the template first so both it and the symbol are provably non-null at the
                // point of construction - the compiler could not see that through a combined lookup.
                if (!byType.TryGetValue(catType, out var tpl)) { missing.Add(catType); return; }

                var symbol = tpl.Primary(def.Deployment.PrimarySymbol);
                if (symbol == null) { missing.Add(catType); return; }

                // A symbol with no usable footprint is REJECTED rather than placed at a guessed size:
                // EAE lays out by the declared size, so a wrong one silently overlaps its neighbour.
                if (symbol.Width <= 0 || symbol.Height <= 0)
                {
                    diagnostics.Add(
                        $"'{name}' ({catType}) is not placed: its '{symbol.Name}' symbol declares no usable " +
                        "size, so its footprint on the canvas is unknown.");
                    return;
                }

                // A command-capable symbol IS placed. Whether its controls are live is decided per
                // capability from the deployed contract - not by refusing the whole tile, which used
                // to cost the operator the monitoring as well.
                result[name] = new Drawn(Identifier(name), tag, tpl, symbol);
            }

            foreach (var p in plant.Processes) Add(p.InstanceName, p.TagName, p.CatType);
            foreach (var c in plant.Components) Add(c.InstanceName, c.TagName, c.CatType);
            foreach (var s in plant.Stations) Add(s.InstanceName, s.TagName, s.CatType);

            foreach (var type in missing)
                diagnostics.Add($"CAT '{type}' declares an HMI interface but no faceplate template exists in " +
                                "Template Library\\HMI\\Faceplates - its instances are not placed on any screen.");
            return result;
        }

        private sealed record Page(List<HmiPlaceable> Items, List<HmiCaption> Captions);

        // Shelf packing over the symbol's DECLARED footprint (what EAE uses for placement), with one
        // caption above each tile. The tile itself shows the LIVE values, bound by TagName; the
        // model-derived text a faceplate cannot show - state legend, interlock rules, allocation -
        // goes on the read-only detail canvases rather than under every tile, where it would both
        // overflow the canvas and sit as static text beside a live one.
        private static List<Page> Paginate(List<Drawn> items, HmiPlant plant, HmiEccIndex ecc, HmiDefinition def)
        {
            var g = def.Geometry;
            var pages = new List<Page>();
            var page = new Page(new List<HmiPlaceable>(), new List<HmiCaption>());
            int x = g.Margin, y = g.ContentTop, rowH = 0;

            foreach (var it in items)
            {
                var cellH = it.Symbol.Height + g.CaptionHeight;
                if (x > g.Margin && x + it.Symbol.Width > g.ContentRight) { x = g.Margin; y += rowH + g.Gap; rowH = 0; }
                if (y + cellH > g.ContentBottom && page.Items.Count > 0)
                {
                    pages.Add(page);
                    page = new Page(new List<HmiPlaceable>(), new List<HmiCaption>());
                    x = g.Margin; y = g.ContentTop; rowH = 0;
                }

                page.Captions.Add(new HmiCaption($"cap_{it.Name}", Humanise(it.Name), x, y, false));
                page.Items.Add(new HmiPlaceable(it.Name, it.TagName, it.Template.CatType, it.Symbol, x,
                                                y + g.CaptionHeight,
                                                Verdicts(plant, it.Name, it.Template.CatType, it.Symbol, ecc, def)));

                x += it.Symbol.Width + g.Gap;
                rowH = Math.Max(rowH, cellH);
            }

            if (page.Items.Count > 0) pages.Add(page);
            return pages;
        }

        // The model data the faceplates cannot show, on a read-only surface.
        //
        // A faceplate renders a live NUMBER and fixed labels; the twin's own state names, the rules
        // the interlock evaluator will actually run and the controller each instance is allocated to
        // exist only in the plan. Putting them on a compiled canvas is what makes the deployed panel
        // explain itself instead of a side file nothing on the rig can open.
        //
        // The HMI never computes or enforces an interlock. Every rule below is read from
        // GenerationContext.Interlocks and only joined to names - the controller stays authoritative.
        private static IReadOnlyList<HmiScreen> Detail(HmiPlant plant, HmiDefinition def)
        {
            var g = def.Geometry;
            var lines = new List<string>();

            foreach (var c in plant.Components.OrderBy(x => x.InstanceName, StringComparer.Ordinal))
            {
                lines.Add($"{c.DisplayName}  -  {c.Controller}/{c.Resource}" +
                          (c.Slot >= 0 ? $"  slot {c.Slot}" : "  unallocated") +
                          (c.Ring == null ? string.Empty : $"  ring {c.Ring}"));
                if (c.States.Count > 0)
                    lines.Add("    states: " + string.Join("  ", c.States.Select(s => $"{s.Value} {s.Name}")));
                foreach (var r in c.Interlocks) lines.Add("    " + r.Explain(c.DisplayName));
            }

            foreach (var p in plant.Processes.OrderBy(x => x.InstanceName, StringComparer.Ordinal))
                lines.Add($"{p.DisplayName}  -  {p.Controller}/{p.Resource}" +
                          (p.Slot >= 0 ? $"  slot {p.Slot}" : string.Empty) +
                          (p.Owned.Count > 0 ? $"  commands {p.Owned.Count}" : string.Empty));

            if (lines.Count == 0) return Array.Empty<HmiScreen>();

            var perPage = Math.Max(1, (g.ContentBottom - g.ContentTop) / g.CaptionHeight);
            var screens = new List<HmiScreen>();
            for (var page = 0; page * perPage < lines.Count; page++)
            {
                var take = lines.Skip(page * perPage).Take(perPage).ToList();
                var captions = take.Select((t, i) => new HmiCaption(
                    $"det{page}_{i}", t, g.Margin, g.ContentTop + i * g.CaptionHeight, false)).ToList();
                var pages = (lines.Count + perPage - 1) / perPage;
                screens.Add(new HmiScreen(
                    PageName(def.Screens.DetailName, page),
                    pages > 1 ? $"{def.Screens.DetailTitle} ({page + 1}/{pages})" : def.Screens.DetailTitle,
                    Array.Empty<HmiPlaceable>(), Array.Empty<HmiNavButton>(), captions));
            }
            return screens;
        }

        // One line per screen naming what it cannot do, drawn in the caption style the reference
        // already uses. Nothing new is invented: a withheld action is stated, not silently missing.
        private static IReadOnlyList<HmiCaption> Withheld(HmiScreen s, HmiDefinition def)
        {
            var refused = s.Items.SelectMany(i => i.Actions).Where(v => !v.Effective)
                .Select(v => v.Label).Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (refused.Count == 0) return Array.Empty<HmiCaption>();

            var g = def.Geometry;
            return new[]
            {
                new HmiCaption("withheld", $"{def.WithheldHeading} {string.Join(", ", refused)}",
                               g.Margin, g.TitleY + g.CaptionHeight, false),
            };
        }

        private static IReadOnlyList<HmiCaption> Chrome(HmiScreen s, HmiDefinition def)
        {
            var g = def.Geometry;
            return new[] { new HmiCaption("screenTitle", s.Title, g.Margin, g.TitleY, true) };
        }

        // Navigation is ONE band at the foot of every canvas: the family links (on the hub) or the
        // way back to it (everywhere else), then this screen's own previous/next page.
        //
        // The band's height is decided BEFORE pagination and taken off the content area, so a button
        // can never land on a tile - which is what happened when the hub drew its family row at
        // ContentTop, on top of the first row of faceplates. Rows grow UPWARD from the foot into
        // space pagination has already given up, never downward off the canvas.
        // Navigation, as the reference draws it: at most ONE ChangeCanvasButton, bottom-right, named
        // after the canvas it opens. Everything else is the EAE runtime's own canvas-topology panel,
        // which the canvas list already registers every screen with - so a screen needs no in-canvas
        // button to be reachable, and the reference gives its second pages none.
        //
        // Forward through the emitted order, so clicking through also walks the whole panel.
        private static IReadOnlyList<HmiNavButton> Nav(string screenName, List<HmiScreen> screens, HmiDefinition def)
        {
            var idx = screens.FindIndex(s => s.Name == screenName);
            if (idx < 0 || idx + 1 >= screens.Count) return Array.Empty<HmiNavButton>();

            var g = def.Geometry;
            var next = screens[idx + 1];
            return new[] { new HmiNavButton(next.Name, next.Name, next.Title,
                                            g.ContentRight - g.ButtonWidth, g.NavBandY) };
        }

        private static IEnumerable<HmiCapability> CapabilitiesOf(HmiPlant plant, string instance) =>
            plant.AllCapabilities()
                .Where(x => string.Equals(x.Owner, instance, StringComparison.Ordinal)).Select(x => x.Cap);

        private static string Identifier(string name)
        {
            var s = new string(name.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_').ToArray());
            return s.Length > 0 && char.IsDigit(s[0]) ? "_" + s : s;
        }

        // Feeder -> Feeder ; Bearing_PnP -> Bearing PnP ; PartInHopper -> Part In Hopper.
        // A capital starts a new word only between two lower-case letters, so runs of capitals
        // (PnP, CoverPNP) are never broken apart.
        internal static string Humanise(string name)
        {
            var raw = name.Replace('_', ' ');
            var b = new StringBuilder(raw.Length + 8);
            for (var i = 0; i < raw.Length; i++)
            {
                if (i > 0 && char.IsUpper(raw[i]) && char.IsLower(raw[i - 1]) && raw[i - 1] != ' ' &&
                    i + 1 < raw.Length && char.IsLower(raw[i + 1]))
                    b.Append(' ');
                b.Append(raw[i]);
            }
            return Regex.Replace(b.ToString(), @"\s+", " ").Trim();
        }

        private static string BaseOf(string screenName) => screenName.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

        private static string PageName(string baseName, int index) => index == 0 ? baseName : baseName + (index + 1);

    }
}
