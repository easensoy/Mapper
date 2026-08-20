using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CodeGen.Hmi
{
    // Makes a withheld action physically non-fireable in the STAGED faceplate copy.
    //
    // Reporting a control as disabled while its handler still raises the event is the whole defect
    // this exists to close: a colour, a caption or an XML row is not a write block. Two mechanisms
    // reach the controller, and both are dealt with here or by symbol selection:
    //
    //   fired  - the code-behind calls FireEvent_X(payload). The call is DELETED, so the compiled
    //            panel has no code path that can raise it, and the control that reaches it is
    //            disabled in the constructor using the reference's own idiom (Enabled = false,
    //            exactly as Five_State_Actuator_CAT_sSetup disables its jog until Setup is confirmed).
    //   bound  - a control is bound to an output tag and NxtControl writes it with no code. There is
    //            nothing to delete, so those actions are suppressed by NOT SELECTING the symbol; the
    //            planner places a monitoring variant instead. This patcher asserts that outcome
    //            rather than assuming it.
    //
    // Only the staged copy is touched. The Template Library source and Jyotsna's reference archive
    // are never written.
    internal static class HmiFaceplatePatcher
    {
        private const string Quote = "\"";

        // Withheld actions, keyed by the symbol that presents them.
        internal static IReadOnlyList<string> Suppress(
            string stagingDir, IReadOnlyList<HmiCatTemplate> deployed,
            IReadOnlyList<HmiActionVerdict> verdicts, HmiDefinition def)
        {
            var notes = new List<string>();

            // One decision per (CAT, symbol, action): an action is withheld if EVERY placed instance
            // that presents it was refused. A symbol's source is shared by every instance of the CAT,
            // so a per-instance patch is not expressible - and enabling on behalf of one instance
            // while another is refused would hand that instance a live control.
            var withheld = verdicts
                .GroupBy(v => (v.CatType, v.Symbol, v.ActionId))
                .Where(g => g.All(v => !v.Effective))
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var tpl in deployed)
            {
                var dir = Path.Combine(stagingDir, tpl.CatType);
                if (!Directory.Exists(dir)) continue;

                foreach (var sym in tpl.Symbols)
                {
                    var drop = withheld
                        .Where(kv => kv.Key.CatType == tpl.CatType && kv.Key.Symbol == sym.Name)
                        .Select(kv => kv.Value).ToList();
                    if (drop.Count == 0) continue;

                    var code = Path.Combine(dir, $"{tpl.CatType}_{sym.Name}.cnv.cs");
                    var designer = Path.Combine(dir, $"{tpl.CatType}_{sym.Name}.cnv.Designer.cs");
                    if (!File.Exists(code)) continue;

                    var text = File.ReadAllText(code);
                    var design = File.Exists(designer) ? File.ReadAllText(designer) : string.Empty;
                    var handlers = HandlersOf(text);
                    var controls = ControlsOf(design);
                    var disable = new List<string>();

                    foreach (var v in drop.Where(x => x.Writes != null))
                    {
                        // A tag-bound control has no call to delete: NxtControl writes the tag and
                        // raises the carrying event by itself. UNBINDING it is the write block -
                        // the control stays, drawn exactly as the reference draws it, and writes
                        // nothing. Disabled as well, so it reads as unavailable.
                        var bind = Regex.Match(design,
                            @"this\.(?<c>[A-Za-z_]\w*)\.TagName\s*=\s*" +
                            Quote + Regex.Escape(v.Writes!) + Quote + @"\s*;");
                        if (!bind.Success)
                        {
                            notes.Add($"{tpl.CatType}_{sym.Name}: no control is bound to '{v.Writes}', " +
                                      $"so action '{v.ActionId}' could not be unbound.");
                            continue;
                        }
                        design = design.Remove(bind.Index, bind.Length).Insert(bind.Index,
                            $"// {def.WithheldMarker} {v.ActionId}: {v.Detail}");
                        disable.Add(bind.Groups["c"].Value);
                    }

                    foreach (var v in drop.Where(x => x.Call != null))
                    {
                        var before = text;
                        // Delete the call itself. The handler survives, so the control keeps whatever
                        // visibility bookkeeping the reference does; it simply cannot command.
                        text = Regex.Replace(text,
                            @"(?m)^[ \t]*" + Regex.Escape(v.Call!) + @"\s*;[ \t]*(//[^\n]*)?\r?\n",
                            $"\t\t\t// {def.WithheldMarker} {v.ActionId}: {v.Detail}\r\n");

                        if (ReferenceEquals(before, text) || before == text)
                        {
                            notes.Add($"{tpl.CatType}_{sym.Name}: could not locate '{v.Call}' to suppress " +
                                      $"action '{v.ActionId}' - the faceplate source has changed shape.");
                            continue;
                        }

                        // The control that reaches the deleted call, derived through the handler that
                        // contained it: no control name is written down anywhere in the generator.
                        var handler = handlers.FirstOrDefault(h => h.Body.Contains(v.Call!, StringComparison.Ordinal));
                        if (handler.Name != null && controls.TryGetValue(handler.Name, out var ctrl))
                            disable.Add(ctrl);
                    }

                    if (disable.Count > 0) text = Disable(text, sym.Name, disable.Distinct(StringComparer.Ordinal).ToList());
                    File.WriteAllText(code, text);
                    if (File.Exists(designer)) File.WriteAllText(designer, design);
                }
            }

            return notes;
        }

        private readonly record struct Handler(string Name, string Body);

        // Every method body in the file, so the call that was deleted can be traced to its handler.
        private static IReadOnlyList<Handler> HandlersOf(string text)
        {
            var result = new List<Handler>();
            foreach (Match m in Regex.Matches(text, @"(?m)^\s*(?:public|private|protected|internal)?\s*void\s+(?<n>[A-Za-z_]\w*)\s*\("))
            {
                var open = text.IndexOf('{', m.Index + m.Length);
                if (open < 0) continue;
                var depth = 0;
                var i = open;
                for (; i < text.Length; i++)
                {
                    if (text[i] == '{') depth++;
                    else if (text[i] == '}' && --depth == 0) break;
                }
                result.Add(new Handler(m.Groups["n"].Value, text.Substring(open, Math.Min(i, text.Length - 1) - open + 1)));
            }
            return result;
        }

        // handler name -> the control whose event it is wired to, read from the Designer.
        private static IReadOnlyDictionary<string, string> ControlsOf(string designer)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(designer,
                @"this\.(?<c>[A-Za-z_]\w*)\.\w+\s*\+=\s*new\s+[\w\.]+\(\s*this\.(?<h>[A-Za-z_]\w*)\s*\)"))
                map[m.Groups["h"].Value] = m.Groups["c"].Value;
            return map;
        }

        // Disable the controls in the constructor, after InitializeComponent so the fields exist.
        // This is the reference's own contract for an unusable control, not an invented one.
        private static string Disable(string text, string symbol, IReadOnlyList<string> controls)
        {
            var ctor = Regex.Match(text, @"public\s+" + Regex.Escape(symbol) + @"\s*\(\s*\)\s*\{");
            if (!ctor.Success) return text;

            var init = text.IndexOf("InitializeComponent();", ctor.Index, StringComparison.Ordinal);
            if (init < 0) return text;

            var at = init + "InitializeComponent();".Length;
            var block = "\r\n\t\t\t// Withheld by the Mapper: the deployed controller does not honour these\r\n" +
                        "\t\t\t// actions, so the control is disabled as well as unwired.\r\n" +
                        string.Concat(controls.Select(c => $"\t\t\t{c}.Enabled = false;\r\n")).TrimEnd('\r', '\n');
            return text.Substring(0, at) + block + text.Substring(at);
        }
    }
}
