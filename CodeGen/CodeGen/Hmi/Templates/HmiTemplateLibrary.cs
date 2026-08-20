using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
                // No silent default. A symbol that declares no size is reported with 0x0 and the
                // planner REFUSES to place it - EAE lays out by the declared footprint, so inventing
                // one would overlap the neighbouring tile in a way nothing downstream could detect.
                var w = m.Success ? int.Parse(m.Groups[1].Value) : 0;
                var h = m.Success ? int.Parse(m.Groups[2].Value) : 0;

                var (outEvents, outTags) = ReadContractOutputs(contract);
                var behaviour = Behaviour(catDir, cat, name, outEvents);
                symbols.Add(new HmiSymbol(name, isFaceplate, w, h, hasContract,
                                          outEvents.Select(e => e.Name).ToList(), outTags,
                                          behaviour.Events, behaviour.Calls, behaviour.Tags,
                                          behaviour.Dead));
            }

            return new HmiCatTemplate(cat, catDir, symbols);
        }

        // What the CODE-BEHIND of one symbol can actually do, read from its own source.
        //
        // Three answers from one read, because the gate needs all three and they come from the same
        // two files:
        //   Events - the declared output events some handler raises (or whose data is fully bound).
        //   Calls  - the EXACT FireEvent_X(payload) calls. An action is one payload on an event, so
        //            the event name alone cannot tell a STOP from a RUN; the call can.
        //   Tags   - the output tags a control is actually BOUND to. A declared <Output> nothing
        //            binds is a promise, not a control the operator can press.
        private static (IReadOnlyList<string> Events, IReadOnlyList<string> Calls,
                        IReadOnlyList<string> Tags, IReadOnlyList<string> Dead)
            Behaviour(string catDir, string cat, string symbol, IReadOnlyList<HmiContractEvent> declared)
        {
            var text = new StringBuilder();
            foreach (var f in new[] { $"{cat}_{symbol}.cnv.cs", $"{cat}_{symbol}.cnv.Designer.cs" })
            {
                var path = Path.Combine(catDir, f);
                if (File.Exists(path)) text.Append(File.ReadAllText(path));
            }
            var body = text.ToString();

            var calls = FireCallRx.Matches(body)
                .Select(m => $"FireEvent_{m.Groups["e"].Value}({m.Groups["p"].Value.Trim()})")
                .Distinct(StringComparer.Ordinal).ToList();

            // control -> the tag it writes, so a hidden control can be matched back to its action.
            var tagOf = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in ControlTagRx.Matches(body))
                tagOf[m.Groups["c"].Value] = m.Groups["t"].Value;

            // A control the faceplate switches off and NEVER switches back on cannot be pressed, so
            // the tag it writes is not an operator action however well the contract declares it.
            // Jyotsna's Area faceplate ships exactly one: the plant RESET, hidden and never restored.
            var dead = tagOf.Keys.Where(c => AlwaysOff(body, c, "Visible") ||
                                             AlwaysOff(body, c, "Enabled"))
                .Select(c => tagOf[c]).ToHashSet(StringComparer.Ordinal);

            var tags = tagOf.Values.Distinct(StringComparer.Ordinal).ToList();

            // Two legitimate mechanisms, both proof that the canvas can command:
            //   1. code-behind raises the generated FireEvent_<Event>()  (Area: MCNF, CTCNF)
            //   2. every datum THIS event carries is bound to a control, which NxtControl writes and
            //      which raises the carrying event  (Setup: CheckButtons on "toWork"/"toHome")
            // Mechanism 2 is tested PER EVENT against that event's own With-vars. Accepting any bound
            // output tag as evidence for every declared event would let one working control vouch for
            // a second command the canvas cannot send.
            var events = declared.Where(e =>
                body.Contains("FireEvent_" + e.Name, StringComparison.Ordinal) ||
                (e.With.Count > 0 && e.With.All(v => tags.Contains(v, StringComparer.Ordinal))))
                .Select(e => e.Name).ToList();

            return (events, calls, tags, dead.ToList());
        }

        private static readonly Regex FireCallRx =
            new(@"FireEvent_(?<e>[A-Za-z_][A-Za-z0-9_]*)\s*\(\s*(?<p>[^);]*?)\s*\)", RegexOptions.Compiled);

        private static readonly Regex ControlTagRx =
            new(@"this\.(?<c>[A-Za-z_]\w*)\.TagName\s*=\s*""(?<t>[^""]+)""",
                RegexOptions.Compiled);

        // Every assignment to one property of one control says false, and there is at least one.
        private static bool AlwaysOff(string body, string control, string prop)
        {
            var hits = Regex.Matches(body,
                @"this\." + Regex.Escape(control) + @"\." + prop + @"\s*=\s*(?<v>[A-Za-z0-9_.]+)\s*;")
                .Select(m => m.Groups["v"].Value).ToList();
            return hits.Count > 0 && hits.All(v => v == "false");
        }

        // The contract is the authority on which direction a symbol can talk:
        //   <EventOutputs><Event Name="MCNF">Mode</Event></EventOutputs>   -> HMI drives the PLC
        //   <Outputs><Output Name="Mode" Type="INT" /></Outputs>
        internal static (IReadOnlyList<HmiContractEvent> Events, IReadOnlyList<string> Tags)
            ReadContractOutputs(string contractPath)
        {
            if (!File.Exists(contractPath))
                return (Array.Empty<HmiContractEvent>(), Array.Empty<string>());

            try
            {
                var root = XDocument.Load(contractPath).Root;
                if (root == null) return (Array.Empty<HmiContractEvent>(), Array.Empty<string>());

                // <Event Name="cmd_event">toWork;toHome</Event> - the element text is the data the
                // event carries, which is what makes a bound control evidence for THAT event.
                var events = root.Descendants().Where(e => e.Name.LocalName == "EventOutputs")
                    .SelectMany(e => e.Elements().Where(c => c.Name.LocalName == "Event"))
                    .Select(c => new HmiContractEvent(
                        (string?)c.Attribute("Name") ?? string.Empty,
                        (c.Value ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries)
                            .Select(v => v.Trim()).Where(v => v.Length > 0).ToList()))
                    .Where(c => c.Name.Length > 0)
                    .ToList();

                return (events, root.Descendants().Where(e => e.Name.LocalName == "Outputs")
                    .SelectMany(e => e.Elements().Where(c => c.Name.LocalName == "Output"))
                    .Select(c => (string?)c.Attribute("Name") ?? string.Empty)
                    .Where(n => n.Length > 0).ToList());
            }
            catch (Exception)
            {
                // An unreadable contract must never be treated as safe.
                return (new[] { new HmiContractEvent("<unreadable contract>", Array.Empty<string>()) },
                        Array.Empty<string>());
            }
        }


        // Copies exactly the faceplate files the plan places.
        //
        // Selection happens BEFORE the copy, so a dormant command symbol is never staged at all -
        // there is nothing to prune afterwards and nothing that can be shipped by an omission.
        //
        // The per-CAT .def.cs/.event.cs declare `partial class <symbol>`. A symbol those files still
        // declare is copied even when no screen places it, because dropping its .cnv.cs would leave
        // the partial halves overriding nothing and EAE fails the build with CS0115.
        internal static IReadOnlyList<string> CopySelected(
            HmiCatTemplate tpl, string destDir, IReadOnlyCollection<string> selected)
        {
            var declared = DeclaredInCatFiles(tpl);
            var skipped = new List<string>();

            Directory.CreateDirectory(destDir);
            foreach (var file in Directory.EnumerateFiles(tpl.SourceDir))
            {
                var name = Path.GetFileName(file);
                var symbol = SymbolOf(tpl.CatType, name);
                if (symbol != null && !selected.Contains(symbol) && !declared.Contains(symbol))
                { skipped.Add(tpl.CatType + "\\" + name); continue; }

                File.Copy(file, Path.Combine(destDir, name), overwrite: true);
            }
            return skipped;
        }

        // The symbol a faceplate file belongs to, or null for a file the whole CAT shares.
        private static string? SymbolOf(string cat, string fileName)
        {
            var prefix = cat + "_";
            if (!fileName.StartsWith(prefix, StringComparison.Ordinal)) return null;
            var rest = fileName[prefix.Length..];
            var dot = rest.IndexOf('.');
            return dot <= 0 ? null : rest[..dot];
        }

        private static HashSet<string> DeclaredInCatFiles(HmiCatTemplate tpl)
        {
            var text = new StringBuilder();
            foreach (var f in new[] { tpl.CatType + ".def.cs", tpl.CatType + ".event.cs" })
            {
                var path = Path.Combine(tpl.SourceDir, f);
                if (File.Exists(path)) text.Append(File.ReadAllText(path));
            }
            var body = text.ToString();
            return new HashSet<string>(
                tpl.Symbols.Select(x => x.Name).Where(n => body.Contains("class " + n, StringComparison.Ordinal)),
                StringComparer.OrdinalIgnoreCase);
        }

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
