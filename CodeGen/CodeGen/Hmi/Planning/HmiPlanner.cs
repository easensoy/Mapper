using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

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
            HmiPlant plant, IReadOnlyList<HmiCatTemplate> templates, HmiDefinition def)
        {
            var readOnly = def.ReadOnly;
            var diagnostics = new List<string>(plant.Diagnostics);
            var byType = templates.ToDictionary(t => t.CatType, StringComparer.OrdinalIgnoreCase);

            var drawable = Drawable(plant, byType, def, diagnostics);

            // Ownership comes from the compiled recipe in GenerationContext, not from re-parsing the
            // serialised Recipe parameter back out of the syslay. Same answer, one source of truth.
            var owners = plant.Processes
                .Where(p => drawable.ContainsKey(p.InstanceName))
                .ToDictionary(
                    p => p.InstanceName,
                    p => p.Owned.Concat(p.Observed)
                          .Where(drawable.ContainsKey)
                          .Distinct(StringComparer.Ordinal)
                          .ToList(),
                    StringComparer.Ordinal);

            var families = new List<(string Base, string Title, List<Drawn> Items)>();

            foreach (var proc in owners.Keys.OrderBy(n => n, StringComparer.Ordinal))
            {
                var members = new List<Drawn> { drawable[proc] };
                members.AddRange(owners[proc].OrderBy(n => n, StringComparer.Ordinal).Select(n => drawable[n]));
                families.Add((ScreenBase(proc, def), Humanise(proc), members));
            }

            var claimed = new HashSet<string>(owners.SelectMany(o => o.Value).Concat(owners.Keys), StringComparer.Ordinal);
            var residual = drawable.Keys.Where(n => !claimed.Contains(n))
                .OrderBy(n => n, StringComparer.Ordinal).Select(n => drawable[n]).ToList();
            if (residual.Count > 0) families.Add((def.Screens.ResidualName, def.Screens.ResidualTitle, residual));

            var screens = new List<HmiScreen>();
            var hub = new List<(string First, string Label)>();

            foreach (var fam in families)
            {
                var pages = Paginate(fam.Items, plant, def);
                for (var i = 0; i < pages.Count; i++)
                {
                    var title = pages.Count > 1 ? $"{fam.Title} ({i + 1}/{pages.Count})" : fam.Title;
                    screens.Add(new HmiScreen(PageName(fam.Base, i), title,
                        pages[i].Items, Array.Empty<HmiNavButton>(), pages[i].Captions));
                }
                hub.Add((PageName(fam.Base, 0), fam.Title));
            }

            var all = new List<HmiScreen>
            {
                new(def.Screens.HubName, def.Screens.HubTitle, Array.Empty<HmiPlaceable>(), NavHub(hub, def), Array.Empty<HmiCaption>())
            };
            all.AddRange(screens.Select(s => s with { Buttons = PageNav(s.Name, screens, def) }));

            // Every screen carries the banner and its own title.
            all = all.Select(s => s with { Captions = Chrome(s, def).Concat(s.Captions).ToList() }).ToList();

            var used = families.SelectMany(f => f.Items).Select(i => i.Template)
                .GroupBy(t => t.CatType, StringComparer.Ordinal).Select(g => g.First()).ToList();

            if (readOnly) diagnostics.Add(def.UnsupportedCommandNotice);
            return new HmiPlan(all, used, diagnostics, readOnly);
        }

        private sealed record Drawn(string Name, string TagName, HmiCatTemplate Template, HmiSymbol Symbol);

        // Everything drawable, taken from the semantic model. A component reaches this point only if
        // the twin declared it AND the deployment emitted it, so a placement can never dangle.
        private static Dictionary<string, Drawn> Drawable(
            HmiPlant plant, IReadOnlyDictionary<string, HmiCatTemplate> byType,
            HmiDefinition def, List<string> diagnostics)
        {
            var readOnly = def.ReadOnly;
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
                if (readOnly && symbol.CommandCapable)
                {
                    diagnostics.Add(
                        $"'{name}' ({catType}) is not placed: its '{symbol.Name}' symbol declares controller " +
                        $"outputs ({symbol.Outputs}). Monitoring for this component is lost.");
                    return;
                }
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

        // Shelf packing over the symbol's DECLARED footprint (what EAE uses for placement), with a
        // caption above each tile and MODEL-DERIVED detail lines below it. Those detail lines are the
        // point: state labels, the decoded interlock rules and the controller allocation land in a
        // compiled canvas, so the deployed panel shows model data instead of a side file nothing reads.
        private static List<Page> Paginate(List<Drawn> items, HmiPlant plant, HmiDefinition def)
        {
            var g = def.Geometry;
            var pages = new List<Page>();
            var page = new Page(new List<HmiPlaceable>(), new List<HmiCaption>());
            int x = g.Margin, y = g.ContentTop, rowH = 0;

            foreach (var it in items)
            {
                var detail = Detail(it.Name, plant, def);
                var cellH = it.Symbol.Height + g.CaptionHeight + detail.Count * g.CaptionHeight;
                if (x > g.Margin && x + it.Symbol.Width > g.ContentRight) { x = g.Margin; y += rowH + g.Gap; rowH = 0; }
                if (y + cellH > g.ContentBottom && page.Items.Count > 0)
                {
                    pages.Add(page);
                    page = new Page(new List<HmiPlaceable>(), new List<HmiCaption>());
                    x = g.Margin; y = g.ContentTop; rowH = 0;
                }

                page.Captions.Add(new HmiCaption($"cap_{it.Name}", Humanise(it.Name), x, y, false));
                page.Items.Add(new HmiPlaceable(it.Name, it.TagName, it.Template.CatType, it.Symbol, x, y + g.CaptionHeight));

                var dy = y + g.CaptionHeight + it.Symbol.Height;
                for (var i = 0; i < detail.Count; i++)
                    page.Captions.Add(new HmiCaption($"det{i}_{it.Name}", detail[i], x, dy + i * g.CaptionHeight, false, true));

                x += it.Symbol.Width + g.Gap;
                rowH = Math.Max(rowH, cellH);
            }

            if (page.Items.Count > 0) pages.Add(page);
            return pages;
        }

        // The model-derived lines for one placed instance. Everything here comes from Control.xml via
        // the semantic model or from the exact emitted RuleTable - never from a template.
        private static IReadOnlyList<string> Detail(string instance, HmiPlant plant, HmiDefinition def)
        {
            var lines = new List<string>();
            var d = def.Screens;

            var c = plant.ByInstance(instance);
            if (c != null)
            {
                if (d.ShowAllocation)
                    lines.Add($"{c.Controller} / {c.Resource}" + (c.Slot >= 0 ? $" / slot {c.Slot}" : string.Empty));

                if (d.ShowStates && c.States.Count > 0)
                    lines.Add(Clip("States: " + string.Join(", ", c.States.Select(s => $"{s.Value}={s.Name}")),
                                   d.MaxStateChars));

                if (d.ShowInterlocks)
                    foreach (var r in c.Interlocks)
                        lines.Add(Clip(r.Explain(c.DisplayName), d.MaxInterlockChars));
                return lines;
            }

            var p = plant.Processes.FirstOrDefault(x => string.Equals(x.InstanceName, instance, StringComparison.Ordinal));
            if (p != null)
            {
                if (d.ShowAllocation)
                    lines.Add($"{p.Controller} / {p.Resource}" + (p.Slot >= 0 ? $" / slot {p.Slot}" : string.Empty));
                if (d.ShowStates && p.Phases.Count > 0)
                    lines.Add(Clip("Phases: " + string.Join(", ", p.Phases.Select(s => $"{s.Value}={s.Name}")),
                                   d.MaxStateChars));
                lines.Add($"Recipe: {p.Rows.Count} step(s), owns {p.Owned.Count}, observes {p.Observed.Count}");
                return lines;
            }

            var st = plant.Stations.FirstOrDefault(x => string.Equals(x.InstanceName, instance, StringComparison.Ordinal));
            if (st != null)
            {
                if (d.ShowAllocation)
                    lines.Add($"{st.Controller} / {st.Resource}" +
                              (st.ReceivesModeBroadcast ? " / mode chain" : " / no mode broadcast"));

                // The mode/cycle vocabulary decodes the live LL_Mode / LL_CycleType the faceplate
                // already binds; without it the operator reads a bare number. Printed ONLY where the
                // station actually receives the mode chain - on one that does not, the numbers never
                // change and the legend would imply a selection the controller cannot see.
                if (d.ShowProtocolLegend && st.ReceivesModeBroadcast)
                {
                    lines.Add(Clip("Modes: " + Vocabulary(def.ModeLabels), d.MaxProtocolChars));
                    lines.Add(Clip("Cycle: " + Vocabulary(def.CycleLabels), d.MaxProtocolChars));
                }
            }
            return lines;
        }

        private static string Vocabulary(IReadOnlyDictionary<int, string> labels) =>
            string.Join(", ", labels.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));

        private static string Clip(string text, int max) =>
            text.Length <= max ? text : text.Substring(0, Math.Max(0, max - 3)) + "...";

        private static IReadOnlyList<HmiCaption> Chrome(HmiScreen s, HmiDefinition def)
        {
            var g = def.Geometry;
            var chrome = new List<HmiCaption> { new("screenTitle", s.Title, g.Margin, g.TitleY, true) };
            if (def.ReadOnly) chrome.Insert(0, new HmiCaption("roBanner", def.BannerText, g.Margin, g.BannerY, true));
            return chrome;
        }

        private static IReadOnlyList<HmiNavButton> NavHub(List<(string First, string Label)> families, HmiDefinition def)
        {
            var g = def.Geometry;
            var buttons = new List<HmiNavButton>();
            int x = g.Margin, y = g.ContentTop;
            foreach (var f in families)
            {
                if (x + g.ButtonWidth > g.ContentRight) { x = g.Margin; y += g.ButtonHeight + g.Gap; }
                buttons.Add(new HmiNavButton(f.First, f.First, f.Label, x, y));
                x += g.ButtonWidth + g.Gap;
            }
            return buttons;
        }

        private static IReadOnlyList<HmiNavButton> PageNav(string screenName, List<HmiScreen> screens, HmiDefinition def)
        {
            var g = def.Geometry;
            var buttons = new List<HmiNavButton>
            {
                new(def.Screens.HubName, def.Screens.HubName, def.Screens.HubButtonText, g.Margin, g.NavBandY)
            };

            var idx = screens.FindIndex(s => s.Name == screenName);
            var baseName = BaseOf(screenName);
            var prev = screens.Take(idx).LastOrDefault(s => BaseOf(s.Name) == baseName);
            var next = screens.Skip(idx + 1).FirstOrDefault(s => BaseOf(s.Name) == baseName);

            var x = g.Margin + g.ButtonWidth + g.Gap;
            if (prev != null)
            {
                buttons.Add(new HmiNavButton("prev_" + prev.Name, prev.Name, def.Screens.PreviousText, x, g.NavBandY));
                x += g.ButtonWidth + g.Gap;
            }
            if (next != null)
                buttons.Add(new HmiNavButton("next_" + next.Name, next.Name, def.Screens.NextText, x, g.NavBandY));
            return buttons;
        }

        // A component name becomes a C# field, so it must be a legal identifier. Component identity
        // travels in TagName, never in this name, so sanitising here is safe.
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

        private static string ScreenBase(string processName, HmiDefinition def) =>
            Identifier(processName).Replace("_", string.Empty) + def.Screens.Suffix;
    }

    internal sealed record SyslayFb(string Id, string Name, string Type, IReadOnlyDictionary<string, string> Parameters)
    {
        public string? Param(string name) => Parameters.TryGetValue(name, out var v) ? v : null;
    }

    internal static class SyslayReader
    {
        internal static IReadOnlyList<SyslayFb> Read(string syslayPath)
        {
            var doc = XDocument.Load(syslayPath);
            var list = new List<SyslayFb>();
            foreach (var fb in doc.Descendants().Where(e => e.Name.LocalName == "FB"))
            {
                var id = (string?)fb.Attribute("ID");
                var name = (string?)fb.Attribute("Name");
                var type = (string?)fb.Attribute("Type");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(type)) continue;

                var ps = fb.Elements().Where(e => e.Name.LocalName == "Parameter")
                    .GroupBy(e => (string?)e.Attribute("Name") ?? string.Empty, StringComparer.Ordinal)
                    .Where(g => g.Key.Length > 0)
                    .ToDictionary(g => g.Key, g => (string?)g.First().Attribute("Value") ?? string.Empty, StringComparer.Ordinal);

                list.Add(new SyslayFb(id!, name!, type!, ps));
            }
            return list;
        }
    }
}
