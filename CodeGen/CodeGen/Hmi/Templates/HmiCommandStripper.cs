using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeGen.Hmi
{
    // Makes the DEPLOYED copy of every faceplate symbol incapable of driving the controller.
    //
    // Why this exists: the HMI STOP button fires CycleType=0, but ProcessRuntime_Generic_v1's ECC has
    // no transition that reads Mode or CycleType, so STOP cannot stop recipe execution. An HMI must
    // not present a control it cannot honour. The same audit found live jog buttons on the DEFAULT
    // actuator tiles (Five_State cmd_event toWork/toHome; Seven_State cmd_event toWork1/toHome/toWork2),
    // so suppressing only the Setup screens would not have produced a read-only HMI.
    //
    // The Template Library is never modified - only the copy inside the generated HMI project.
    // Symbols keep their names, so <CAT>.cfg, <CAT>.def.cs and <CAT>.event.cs stay coherent.
    internal static class HmiCommandStripper
    {
        // Navigation between generated canvases is not a controller command.
        private const string NavigationButton = "ChangeCanvasButton";

        internal sealed record Result(
            string CatType,
            string Symbol,
            IReadOnlyList<string> RemovedControls,
            IReadOnlyList<string> RemovedOutputs,
            bool CodeBehindReplaced,
            string? Failure)
        {
            internal bool Changed => RemovedControls.Count > 0 || RemovedOutputs.Count > 0 || CodeBehindReplaced;
        }

        internal static IReadOnlyList<Result> StripAll(string hmiDir, IReadOnlyList<HmiCatTemplate> templates)
        {
            var results = new List<Result>();
            foreach (var tpl in templates)
            {
                var dir = Path.Combine(hmiDir, tpl.CatType);
                if (!Directory.Exists(dir)) continue;
                foreach (var sym in tpl.Symbols)
                    results.Add(Strip(dir, tpl.CatType, sym.Name));
            }
            return results;
        }

        private static Result Strip(string catDir, string cat, string symbol)
        {
            var stem = Path.Combine(catDir, $"{cat}_{symbol}");
            var designerPath = stem + ".cnv.Designer.cs";
            var contractPath = stem + ".cnv.xml";
            var codePath = stem + ".cnv.cs";
            var resxPath = stem + ".cnv.resx";

            var none = (IReadOnlyList<string>)Array.Empty<string>();
            if (!File.Exists(designerPath))
                return new Result(cat, symbol, none, none, false, null);

            try
            {
                var (outEvents, outTags) = HmiTemplateLibrary.ReadContractOutputs(contractPath);
                var designer = File.ReadAllText(designerPath);
                var declared = Declarations(designer);

                var remove = declared
                    .Where(d => IsCommandControl(d.Key, d.Value, outTags, designer))
                    .Select(d => d.Key)
                    .ToList();

                var removedOutputs = outEvents.Concat(outTags).ToList();
                if (remove.Count == 0 && removedOutputs.Count == 0)
                    return new Result(cat, symbol, none, none, false, null);

                if (remove.Count > 0)
                {
                    designer = RemoveControls(designer, remove, $"{cat}_{symbol}", symbol);
                    File.WriteAllText(designerPath, designer, Utf8Bom);

                    if (File.Exists(resxPath))
                        File.WriteAllText(resxPath, RemoveResxMetadata(File.ReadAllText(resxPath), remove), Utf8Bom);
                }

                if (removedOutputs.Count > 0 && File.Exists(contractPath))
                    File.WriteAllText(contractPath, EmptyContractOutputs(File.ReadAllText(contractPath)), Utf8Bom);

                // Any handler that fired a command, or that touched a control we just removed, must go.
                var replaced = false;
                if (File.Exists(codePath))
                {
                    var code = File.ReadAllText(codePath);
                    if (code.Contains("FireEvent_", StringComparison.Ordinal) ||
                        remove.Any(n => Regex.IsMatch(code, $@"\b{Regex.Escape(n)}\b")))
                    {
                        File.WriteAllText(codePath, MinimalCodeBehind(cat, symbol), Utf8Bom);
                        replaced = true;
                    }
                }

                // Nothing may survive that still references a removed control.
                var dangling = remove.Where(n => Regex.IsMatch(designer, $@"\b{Regex.Escape(n)}\b")).ToList();
                if (dangling.Count > 0)
                    return new Result(cat, symbol, remove, removedOutputs, replaced,
                        $"controls still referenced after removal: {string.Join(", ", dangling)}");

                // Removing a control must never leave the file syntactically broken - EAE only reports
                // that as "} expected" at HMI load time, long after generation has claimed success.
                var unbalanced = Unbalanced(designer);
                if (unbalanced != null)
                    return new Result(cat, symbol, remove, removedOutputs, replaced,
                        $"stripped designer is unbalanced ({unbalanced})");

                return new Result(cat, symbol, remove, removedOutputs, replaced, null);
            }
            catch (Exception ex)
            {
                return new Result(cat, symbol, none, none, false, ex.Message);
            }
        }

        private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

        private static readonly Regex DeclRx =
            new(@"^\s*this\.(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*new\s+(?<type>[A-Za-z0-9_.<>]+)\s*\(\s*\)\s*;",
                RegexOptions.Compiled | RegexOptions.Multiline);

        private static Dictionary<string, string> Declarations(string designer) =>
            DeclRx.Matches(designer)
                .GroupBy(m => m.Groups["name"].Value, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Groups["type"].Value, StringComparer.Ordinal);

        // A control can reach the controller when it is a button (other than canvas navigation), when
        // it writes a variable the contract declares as an Output, or when it is not input-only.
        private static bool IsCommandControl(
            string name, string type, IReadOnlyList<string> outputTags, string designer)
        {
            var shortType = type.Split('.').Last();
            if (shortType.StartsWith(NavigationButton, StringComparison.Ordinal)) return true; // targets canvases we do not generate
            if (shortType.EndsWith("Button", StringComparison.Ordinal)) return true;

            var tag = Match(designer, $@"this\.{Regex.Escape(name)}\.TagName\s*=\s*""(?<v>[^""]*)""");
            if (tag != null && outputTags.Contains(tag, StringComparer.Ordinal)) return true;

            return Regex.IsMatch(designer, $@"this\.{Regex.Escape(name)}\.IsOnlyInput\s*=\s*false");
        }

        private static string? Match(string text, string pattern)
        {
            var m = Regex.Match(text, pattern);
            return m.Success ? m.Groups["v"].Value : null;
        }

        // The designer is line-structured: declarations, then one comment-delimited block per control,
        // then the Shapes list, then the private fields. Every one of those must lose the control.
        private static string RemoveControls(string designer, IReadOnlyList<string> remove, string fileStem, string symbol)
        {
            var drop = new HashSet<string>(remove, StringComparer.Ordinal);
            var lines = designer.Replace("\r\n", "\n").Split('\n');
            var keep = new List<string>(lines.Length);

            for (var i = 0; i < lines.Length; i++)
            {
                // A control block opens with:  "// " / "// <name>" / "// "
                var header = BlockName(lines, i);
                if (header != null && drop.Contains(header))
                {
                    i += 2;
                    while (i + 1 < lines.Length && BlockName(lines, i + 1) == null && !IsBlockTerminator(lines[i + 1]))
                        i++;
                    continue;
                }

                var line = lines[i];
                var trimmed = line.TrimStart();

                // The Shapes list is rebuilt as a unit: dropping entries one at a time loses the
                // "});" terminator when the last entry goes, and empties the list entirely when
                // every entry was a command control (the seven-state setup canvas is only buttons).
                if (trimmed.StartsWith("this.Shapes.AddRange(", StringComparison.Ordinal))
                {
                    var start = i;
                    while (i < lines.Length && !lines[i].TrimEnd().EndsWith("});", StringComparison.Ordinal)) i++;
                    var block = string.Join("\n", lines[start..Math.Min(i + 1, lines.Length)]);
                    var kept = Regex.Matches(block, @"this\.(?<n>[A-Za-z_][A-Za-z0-9_]*)\s*(?=[,}])")
                        .Select(x => x.Groups["n"].Value)
                        .Where(n => !drop.Contains(n))
                        .ToList();

                    if (kept.Count > 0)
                    {
                        keep.Add("\t\t\tthis.Shapes.AddRange(new System.ComponentModel.IComponent[] {");
                        keep.Add(string.Join(",\r\n", kept.Select(n => $"\t\t\tthis.{n}")) + "});");
                    }
                    continue;
                }

                // this.X = new T();  |  this.X.Anything...  |  private T X;
                var owner = OwnerOf(trimmed);
                if (owner != null && drop.Contains(owner)) continue;

                keep.Add(line);
            }

            return string.Join("\r\n", keep);
        }

        private static bool IsBlockTerminator(string line)
        {
            var t = line.TrimStart();
            return t.StartsWith("this.Shapes.AddRange", StringComparison.Ordinal) ||
                   t.StartsWith("}", StringComparison.Ordinal);
        }

        private static string? BlockName(string[] lines, int i)
        {
            if (i + 2 >= lines.Length) return null;
            if (lines[i].Trim() != "//" || lines[i + 2].Trim() != "//") return null;
            var mid = lines[i + 1].Trim();
            return mid.StartsWith("// ", StringComparison.Ordinal) ? mid[3..].Trim() : null;
        }

        private static string? OwnerOf(string trimmed)
        {
            var m = Regex.Match(trimmed, @"^this\.(?<n>[A-Za-z_][A-Za-z0-9_]*)\s*(=|\.)");
            if (m.Success) return m.Groups["n"].Value;
            m = Regex.Match(trimmed, @"^private\s+[A-Za-z0-9_.<>]+\s+(?<n>[A-Za-z_][A-Za-z0-9_]*)\s*;$");
            return m.Success ? m.Groups["n"].Value : null;
        }

        // Cheap balance check over code with comments and string literals removed.
        private static string? Unbalanced(string code)
        {
            var stripped = Regex.Replace(code, @"//[^\r\n]*|/\*.*?\*/|""(?:\\.|[^""\\])*""", string.Empty,
                                         RegexOptions.Singleline);
            int braces = 0, parens = 0;
            foreach (var c in stripped)
            {
                if (c == '{') braces++;
                else if (c == '}') braces--;
                else if (c == '(') parens++;
                else if (c == ')') parens--;
            }
            if (braces != 0) return $"braces off by {braces}";
            return parens != 0 ? $"parentheses off by {parens}" : null;
        }

        private static string EmptyContractOutputs(string xml) =>
            Regex.Replace(
                Regex.Replace(xml, @"<EventOutputs>.*?</EventOutputs>", "<EventOutputs />", RegexOptions.Singleline),
                @"<Outputs>.*?</Outputs>", "<Outputs />", RegexOptions.Singleline);

        private static string RemoveResxMetadata(string resx, IReadOnlyList<string> remove)
        {
            foreach (var name in remove)
                resx = Regex.Replace(
                    resx,
                    $@"[ \t]*<metadata name=""{Regex.Escape(name)}\.Name""[^>]*>.*?</metadata>\r?\n",
                    string.Empty, RegexOptions.Singleline);
            return resx;
        }

        private static string MinimalCodeBehind(string cat, string symbol) =>
            "/*\r\n * Generated by VueOneMapper. The original code-behind only issued controller\r\n" +
            " * commands, which a monitoring HMI must not do.\r\n */\r\n\r\n" +
            "using System;\r\nusing NxtControl.GuiFramework;\r\n\r\n" +
            $"namespace HMI.Main.Symbols.{cat}\r\n{{\r\n" +
            $"\tpublic partial class {symbol} : NxtControl.GuiFramework.HMISymbol\r\n\t{{\r\n" +
            $"\t\tpublic {symbol}()\r\n\t\t{{\r\n\t\t\tInitializeComponent();\r\n\t\t}}\r\n\t}}\r\n}}\r\n";
    }
}
