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
    }
}
