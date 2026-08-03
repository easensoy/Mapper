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
        // Work area from CanvasesResolutionList (1024x768 minus the 70px runtime navigation bar).
        internal const int CanvasWidth = 1024;
        internal const int CanvasHeight = 698;
        private const int Margin = 16;
        private const int Gap = 16;
        private const int ButtonW = 208;
        private const int ButtonH = 64;
        private const int BannerY = 8;
        private const int TitleY = 34;
        private const int CaptionH = 22;
        private const int ContentTop = 68;
        private const int NavBandY = CanvasHeight - ButtonH - 10;
        private const int ContentBottom = NavBandY - Gap;
        private const int ContentRight = CanvasWidth - Margin;

        private const string MainScreen = "MainScreen";
        private const string ResidualBase = "PlantOverviewScreen";

        internal static HmiPlan Plan(
            string syslayPath, string eaeProjectDir, IReadOnlyList<HmiCatTemplate> templates, bool readOnly)
        {
            var diagnostics = new List<string>();
            var byType = templates.ToDictionary(t => t.CatType, StringComparer.OrdinalIgnoreCase);
            var fbs = SyslayReader.Read(syslayPath);

            var drawable = Drawable(fbs, eaeProjectDir, byType, readOnly, diagnostics);
            var owners = Ownership(fbs, drawable);

            var families = new List<(string Base, string Title, List<Drawn> Items)>();

            foreach (var proc in owners.Keys.OrderBy(n => n, StringComparer.Ordinal))
            {
                var members = new List<Drawn> { drawable[proc] };
                members.AddRange(owners[proc].OrderBy(n => n, StringComparer.Ordinal).Select(n => drawable[n]));
                families.Add((ScreenBase(proc), Humanise(proc), members));
            }

            var claimed = new HashSet<string>(owners.SelectMany(o => o.Value).Concat(owners.Keys), StringComparer.Ordinal);
            var residual = drawable.Keys.Where(n => !claimed.Contains(n))
                .OrderBy(n => n, StringComparer.Ordinal).Select(n => drawable[n]).ToList();
            if (residual.Count > 0) families.Add((ResidualBase, "Plant Overview", residual));

            var screens = new List<HmiScreen>();
            var hub = new List<(string First, string Label)>();

            foreach (var fam in families)
            {
                var pages = Paginate(fam.Items);
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
                new(MainScreen, "Plant Monitoring", Array.Empty<HmiPlaceable>(), NavHub(hub), Array.Empty<HmiCaption>())
            };
            all.AddRange(screens.Select(s => s with { Buttons = PageNav(s.Name, screens) }));

            // Every screen carries the banner and its own title.
            all = all.Select(s => s with { Captions = Chrome(s, readOnly).Concat(s.Captions).ToList() }).ToList();

            var used = families.SelectMany(f => f.Items).Select(i => i.Template)
                .GroupBy(t => t.CatType, StringComparer.Ordinal).Select(g => g.First()).ToList();

            if (readOnly) diagnostics.Add(HmiNames.CommandContractDiagnostic);
            return new HmiPlan(all, used, diagnostics, readOnly);
        }

        private sealed record Drawn(string Name, string TagName, HmiCatTemplate Template, HmiSymbol Symbol);

        private static Dictionary<string, Drawn> Drawable(
            IReadOnlyList<SyslayFb> fbs, string eaeProjectDir,
            IReadOnlyDictionary<string, HmiCatTemplate> byType, bool readOnly, List<string> diagnostics)
        {
            var iec = Path.Combine(eaeProjectDir, "IEC61499");
            var result = new Dictionary<string, Drawn>(StringComparer.Ordinal);
            var missing = new SortedSet<string>(StringComparer.Ordinal);

            foreach (var fb in fbs)
            {
                // Infrastructure blocks (brokers, symlinks, terminators) declare no HMI interface.
                if (!File.Exists(Path.Combine(iec, fb.Type, fb.Type + "_HMI.fbt"))) continue;

                if (!byType.TryGetValue(fb.Type, out var tpl) || tpl.Primary == null) { missing.Add(fb.Type); continue; }

                var symbol = tpl.Primary;
                if (readOnly && symbol.CommandCapable)
                {
                    diagnostics.Add(
                        $"'{fb.Name}' ({fb.Type}) is not placed: its '{symbol.Name}' symbol still declares controller " +
                        $"outputs ({symbol.Outputs}) after stripping. Monitoring for this component is lost.");
                    continue;
                }
                result[fb.Name] = new Drawn(Identifier(fb.Name), fb.Id, tpl, symbol);
            }

            foreach (var type in missing)
                diagnostics.Add($"CAT '{type}' declares an HMI interface but no faceplate template exists in " +
                                "Template Library\\HMI\\Faceplates - its instances are not placed on any screen.");
            return result;
        }

        // process FB name -> the component FB names its own recipe drives or observes.
        private static Dictionary<string, List<string>> Ownership(
            IReadOnlyList<SyslayFb> fbs, IReadOnlyDictionary<string, Drawn> drawable)
        {
            var byRingKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var byId = new Dictionary<int, string>();
            foreach (var fb in fbs)
            {
                var key = (fb.Param("actuator_name") ?? fb.Param("name"))?.Trim('\'');
                if (!string.IsNullOrEmpty(key)) byRingKey[key!] = fb.Name;
                var id = fb.Param("actuator_id") ?? fb.Param("id");
                if (int.TryParse(id, out var n)) byId[n] = fb.Name;
            }

            var owners = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var fb in fbs)
            {
                var recipe = fb.Param("Recipe");
                if (recipe == null || !drawable.ContainsKey(fb.Name)) continue;

                var members = new List<string>();
                void Claim(string? name)
                {
                    // A recipe also names process sentinels (cycle_ready, the process itself), which
                    // resolve to no component - dropping them is correct, not a gap.
                    if (name == null || name == fb.Name || !drawable.ContainsKey(name) || members.Contains(name)) return;
                    members.Add(name);
                }

                foreach (Match m in Regex.Matches(recipe, @"CmdTargetName:='([^']*)'"))
                    if (m.Groups[1].Value.Length > 0 && byRingKey.TryGetValue(m.Groups[1].Value, out var n)) Claim(n);
                foreach (Match m in Regex.Matches(recipe, @"Wait1Id:=(\d+)"))
                    if (byId.TryGetValue(int.Parse(m.Groups[1].Value), out var n)) Claim(n);

                owners[fb.Name] = members;
            }
            return owners;
        }

        private sealed record Page(List<HmiPlaceable> Items, List<HmiCaption> Captions);

        // Shelf packing over the symbol's DECLARED footprint (what EAE uses for placement),
        // with a caption reserved above each tile.
        private static List<Page> Paginate(List<Drawn> items)
        {
            var pages = new List<Page>();
            var page = new Page(new List<HmiPlaceable>(), new List<HmiCaption>());
            int x = Margin, y = ContentTop, rowH = 0;

            foreach (var it in items)
            {
                var cellH = it.Symbol.Height + CaptionH;
                if (x > Margin && x + it.Symbol.Width > ContentRight) { x = Margin; y += rowH + Gap; rowH = 0; }
                if (y + cellH > ContentBottom && page.Items.Count > 0)
                {
                    pages.Add(page);
                    page = new Page(new List<HmiPlaceable>(), new List<HmiCaption>());
                    x = Margin; y = ContentTop; rowH = 0;
                }

                page.Captions.Add(new HmiCaption($"cap_{it.Name}", Humanise(it.Name), x, y, false));
                page.Items.Add(new HmiPlaceable(it.Name, it.TagName, it.Template.CatType, it.Symbol, x, y + CaptionH));
                x += it.Symbol.Width + Gap;
                rowH = Math.Max(rowH, cellH);
            }

            if (page.Items.Count > 0) pages.Add(page);
            return pages;
        }

        private static IReadOnlyList<HmiCaption> Chrome(HmiScreen s, bool readOnly)
        {
            var chrome = new List<HmiCaption> { new("screenTitle", s.Title, Margin, TitleY, true) };
            if (readOnly) chrome.Insert(0, new HmiCaption("roBanner", HmiNames.ReadOnlyBanner, Margin, BannerY, true));
            return chrome;
        }

        private static IReadOnlyList<HmiNavButton> NavHub(List<(string First, string Label)> families)
        {
            var buttons = new List<HmiNavButton>();
            int x = Margin, y = ContentTop;
            foreach (var f in families)
            {
                if (x + ButtonW > ContentRight) { x = Margin; y += ButtonH + Gap; }
                buttons.Add(new HmiNavButton(f.First, f.First, f.Label, x, y));
                x += ButtonW + Gap;
            }
            return buttons;
        }

        private static IReadOnlyList<HmiNavButton> PageNav(string screenName, List<HmiScreen> screens)
        {
            var buttons = new List<HmiNavButton> { new(MainScreen, MainScreen, "Main Screen", Margin, NavBandY) };

            var idx = screens.FindIndex(s => s.Name == screenName);
            var baseName = BaseOf(screenName);
            var prev = screens.Take(idx).LastOrDefault(s => BaseOf(s.Name) == baseName);
            var next = screens.Skip(idx + 1).FirstOrDefault(s => BaseOf(s.Name) == baseName);

            var x = Margin + ButtonW + Gap;
            if (prev != null)
            {
                buttons.Add(new HmiNavButton("prev_" + prev.Name, prev.Name, "Previous Page", x, NavBandY));
                x += ButtonW + Gap;
            }
            if (next != null)
                buttons.Add(new HmiNavButton("next_" + next.Name, next.Name, "Next Page", x, NavBandY));
            return buttons;
        }

        // A component name becomes a C# field, so it must be a legal identifier. Component identity
        // travels in TagName, never in this name, so sanitising here is safe.
        private static string Identifier(string name)
        {
            var s = new string(name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
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

        private static string ScreenBase(string processName) => Identifier(processName).Replace("_", string.Empty) + "Screen";
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
