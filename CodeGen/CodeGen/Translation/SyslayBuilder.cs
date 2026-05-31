using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace CodeGen.Translation
{
    public class SyslayBuilder
    {
        private static readonly XNamespace Ns = "https://www.se.com/LibraryElements";
        private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

        private readonly string _layerId;
        private readonly XElement _subAppNetwork;
        private readonly XElement _eventConnections;
        private readonly XElement _dataConnections;
        private readonly XElement _adapterConnections;
        private readonly XElement _layer;
        private string? _topComment;

        public SyslayBuilder(string layerId)
        {
            _layerId = layerId ?? throw new ArgumentNullException(nameof(layerId));

            _eventConnections = new XElement(Ns + "EventConnections");
            _dataConnections = new XElement(Ns + "DataConnections");
            _adapterConnections = new XElement(Ns + "AdapterConnections");

            _subAppNetwork = new XElement(Ns + "SubAppNetwork");

            _layer = new XElement(Ns + "Layer",
                new XAttribute("ID", _layerId),
                new XAttribute("Name", "Default"),
                new XAttribute("Comment", string.Empty),
                new XAttribute("IsDefault", "true"),
                new XAttribute(XNamespace.Xmlns + "xsi", Xsi.NamespaceName),
                _subAppNetwork);
        }

        public SyslayBuilder AddFB(string id, string name, string type, string ns, double x, double y,
            IDictionary<string, string>? parameters = null,
            IDictionary<string, IDictionary<string, string>>? nestedFbParameters = null)
        {
            var fb = new XElement(Ns + "FB",
                new XAttribute("ID", id),
                new XAttribute("Name", name),
                new XAttribute("Type", type),
                new XAttribute("Namespace", ns),
                new XAttribute("x", x.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new XAttribute("y", y.ToString(System.Globalization.CultureInfo.InvariantCulture)));

            if (parameters != null)
            {
                foreach (var kv in parameters)
                {
                    fb.Add(new XElement(Ns + "Parameter",
                        new XAttribute("Name", kv.Key),
                        new XAttribute("Value", kv.Value)));
                }
            }

            // Nested FB overrides intentionally NOT emitted: EAE rejects FBs containing nested
            // FB elements as schema-invalid. Inner FB parameter overrides are not part of the
            // syslay surface; they belong inside the CAT's .fbt initialize algorithm or via PLC
            // I/O variable renames. The nestedFbParameters parameter is preserved for binary
            // compatibility but ignored.
            _ = nestedFbParameters;

            _subAppNetwork.Add(fb);
            return this;
        }

        /// <summary>
        /// Adds a coloured rectangular Frame to the SubAppNetwork. EAE uses
        /// these to visually group FB instances on the syslay canvas (one per
        /// Station, one per PLC, etc.). Matches the Frame XML shape the
        /// hardcoded reference SMC_Rig_Expo_withClamp.syslay uses for its
        /// Station 1 (LavenderBlush) and Station 2 (AliceBlue) frames plus
        /// the nested PLC frames (Honeydew). BackgroundColor accepts any
        /// .NET KnownColor name (LightYellow, Plum, AliceBlue etc.).
        /// </summary>
        public SyslayBuilder AddFrame(string name, double x, double y,
            double width, double height,
            string backgroundColor, string text,
            string textAlignment = "TopCenter",
            string font = "Microsoft Sans Serif, 36pt, style=Bold")
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var frame = new XElement(Ns + "Frame",
                new XAttribute("Name", name),
                new XAttribute("X", x.ToString(inv)),
                new XAttribute("Y", y.ToString(inv)),
                new XAttribute("Width", width.ToString(inv)),
                new XAttribute("Height", height.ToString(inv)),
                new XAttribute("IsComment", "false"));

            void AddParam(string n, string v) =>
                frame.Add(new XElement(Ns + "Parameter",
                    new XAttribute("Name", n), new XAttribute("Value", v)));

            AddParam("BackgroundColor", backgroundColor);
            AddParam("TextColor", "Black");
            AddParam("Font", font);
            AddParam("TextAlignment", textAlignment);
            // MoveStyle="None" pins the frame at the emitted X/Y/Width/Height.
            // Was "AnyContained" — that's EAE's auto-grow mode which re-anchored
            // the BX1 frame westward to swallow Station 1 / Station 2 (M580) on
            // every regen. "None" is what the SE DesignGuidelines reference
            // project uses; combined with non-overlapping abutting frame widths
            // from CodeGen.Mapping.LayoutGrid, each PLC zone stays in its lane.
            AddParam("MoveStyle", "None");
            AddParam("Text", text);
            AddParam("NxtLayerIdentifier", string.Empty);

            _subAppNetwork.Add(frame);
            return this;
        }

        public SyslayBuilder AddEventConnection(string source, string destination)
        {
            _eventConnections.Add(new XElement(Ns + "Connection",
                new XAttribute("Source", source),
                new XAttribute("Destination", destination)));
            return this;
        }

        public SyslayBuilder AddDataConnection(string source, string destination)
        {
            _dataConnections.Add(new XElement(Ns + "Connection",
                new XAttribute("Source", source),
                new XAttribute("Destination", destination)));
            return this;
        }

        public SyslayBuilder AddAdapterConnection(string source, string destination)
        {
            _adapterConnections.Add(new XElement(Ns + "Connection",
                new XAttribute("Source", source),
                new XAttribute("Destination", destination)));
            return this;
        }

        public SyslayBuilder SetTopComment(string commentText)
        {
            _topComment = commentText;
            return this;
        }

        /// <summary>
        /// Append additional text to the top comment (joined with two newlines for
        /// readability). Useful for callers that want to layer auxiliary notes —
        /// e.g. recipe-skipped-conditions — on top of the main top-comment that
        /// SystemLayoutInjector sets up.
        /// </summary>
        public SyslayBuilder AppendTopComment(string additionalText)
        {
            if (string.IsNullOrEmpty(additionalText)) return this;
            _topComment = string.IsNullOrEmpty(_topComment)
                ? additionalText
                : _topComment + "\n\n" + additionalText;
            return this;
        }

        public XDocument Build()
        {
            if (_eventConnections.HasElements && _eventConnections.Parent == null)
                _subAppNetwork.Add(_eventConnections);
            if (_dataConnections.Parent == null)
                _subAppNetwork.Add(_dataConnections);
            if (_adapterConnections.HasElements && _adapterConnections.Parent == null)
                _subAppNetwork.Add(_adapterConnections);

            if (!string.IsNullOrEmpty(_topComment))
                _layer.AddFirst(new XComment(" " + _topComment + " "));

            return new XDocument(new XDeclaration("1.0", "utf-8", null), _layer);
        }

        public static string FormatString(string value) => $"'{value}'";
        public static string FormatInt(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public static string FormatBool(bool value) => value ? "TRUE" : "FALSE";
        public static string FormatTimeMs(int ms) => $"T#{ms.ToString(System.Globalization.CultureInfo.InvariantCulture)}ms";

        /// <summary>
        /// Formats an INT array as an EAE square-bracket literal, e.g. [1, 2, 9].
        /// IEC 61131-3 partial initialisation: trailing array slots default to the
        /// element type's initial value (0 for INT). Caller may pass an empty list to
        /// emit "[]".
        /// </summary>
        public static string FormatIntArray(IEnumerable<int> values)
        {
            var formatted = string.Join(", ",
                values.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return $"[{formatted}]";
        }

        /// <summary>
        /// Formats a STRING array as an EAE square-bracket literal of single-quoted entries,
        /// e.g. ['Feeder', '', 'PartInHopper']. Single quotes inside an entry are doubled
        /// (IEC 61131-3 STRING escaping).
        /// </summary>
        public static string FormatStringArray(IEnumerable<string> values)
        {
            var formatted = string.Join(", ",
                values.Select(v => "'" + (v ?? string.Empty).Replace("'", "''") + "'"));
            return $"[{formatted}]";
        }

        /// <summary>
        /// Formats an array-of-struct literal for the InterlockRule datatype, e.g.
        /// <c>[(FromState:=2, ToState:=2, SourceID:=6, BlockedState:=0), (...)]</c>.
        /// Emits only the first <paramref name="count"/> rules (IEC 61131-3 partial
        /// array init defaults the trailing slots). This is the EXACT struct-literal
        /// syntax whose EAE acceptance the StructLiteralProbe spike verifies before
        /// the real 4-arrays-&gt;RuleTable collapse is built. Named-field form
        /// (Field:=value) is the standard IEC 61131-3 structured initialiser.
        /// </summary>
        public static string FormatRuleTable(
            IReadOnlyList<int> from, IReadOnlyList<int> to,
            IReadOnlyList<int> src, IReadOnlyList<int> blk, int count)
        {
            // Emit ALL slots (mirrors the original four INT arrays, which were
            // always written as full 10-element literals, zero-filled past
            // RuleCount). RuleCount still bounds the InterlockManager.Evaluate
            // loop, so trailing zero-rows are never read — behaviour is identical.
            // Emitting every slot also means the literal is never the empty "[]",
            // avoiding an untested empty-struct-array edge case for actuators with
            // RuleCount=0. (count is kept for call-site clarity; the literal spans
            // the whole array.)
            _ = count;
            int n = from.Count;
            var elems = new List<string>();
            for (int i = 0; i < n; i++)
                elems.Add(
                    $"(FromState:={from[i]}, ToState:={to[i]}, " +
                    $"SourceID:={src[i]}, BlockedState:={blk[i]})");
            return "[" + string.Join(", ", elems) + "]";
        }

        /// <summary>
        /// Formats an array-of-struct literal for the RecipeStep datatype (mixed
        /// INT + STRING fields), e.g.
        /// <c>[(StepType:=2, CmdTargetName:='feeder', CmdStateArr:=1, Wait1Id:=0,
        /// Wait1State:=0, NextStep:=1), ...]</c>. Emits every row (never the empty
        /// "[]"), mirroring the six parallel arrays it replaces. The STRING member
        /// is single-quoted with internal quotes doubled (IEC 61131-3). Replaces
        /// FormatIntArray/FormatStringArray for the Process recipe when the
        /// simulator RecipeStep struct is active.
        /// </summary>
        public static string FormatRecipeTable(
            IReadOnlyList<int> stepType, IReadOnlyList<string> cmdTargetName,
            IReadOnlyList<int> cmdStateArr, IReadOnlyList<int> wait1Id,
            IReadOnlyList<int> wait1State, IReadOnlyList<int> nextStep)
        {
            int n = stepType.Count;
            var elems = new List<string>();
            for (int i = 0; i < n; i++)
            {
                var name = "'" + (cmdTargetName[i] ?? string.Empty).Replace("'", "''") + "'";
                elems.Add(
                    $"(StepType:={stepType[i]}, CmdTargetName:={name}, " +
                    $"CmdStateArr:={cmdStateArr[i]}, Wait1Id:={wait1Id[i]}, " +
                    $"Wait1State:={wait1State[i]}, NextStep:={nextStep[i]})");
            }
            return "[" + string.Join(", ", elems) + "]";
        }
    }
}
