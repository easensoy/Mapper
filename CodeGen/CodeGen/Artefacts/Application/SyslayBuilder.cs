using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace CodeGen.Translation
{
    public class SyslayBuilder
    {
        private static readonly XNamespace Ns = CodeGen.Devices.Core.Station2DeviceEmitter.LibElNs;
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
                new XAttribute("Name", "SMC_Rig"),
                new XAttribute("Comment", string.Empty),
                new XAttribute("IsDefault", "true"),
                new XAttribute(XNamespace.Xmlns + "xsi", Xsi.NamespaceName),
                _subAppNetwork);
        }

        public SyslayBuilder AddFB(string id, string name, string type, string ns, double x, double y,
            IDictionary<string, string>? parameters = null,
            IDictionary<string, string>? attributes = null)
        {
            var fb = new XElement(Ns + "FB",
                new XAttribute("ID", id),
                new XAttribute("Name", name),
                new XAttribute("Type", type),
                new XAttribute("Namespace", ns),
                new XAttribute("x", x.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new XAttribute("y", y.ToString(System.Globalization.CultureInfo.InvariantCulture)));

            // <Attribute> children must precede <Parameter> children. Required for
            // generic FBs (e.g. MQTT_PUBLISH_115480E69E664F878) whose numbered
            // channel ports (Topic1/Payload1/QoS1/PUBLISH1) only materialise once
            // Configuration.GenericFBType.InterfaceParams sets the channel count
            // (CNTX:=1). The MQTT bridge publishers on BX1 use this hook.
            if (attributes != null)
            {
                foreach (var kv in attributes)
                {
                    fb.Add(new XElement(Ns + "Attribute",
                        new XAttribute("Name", kv.Key),
                        new XAttribute("Value", kv.Value)));
                }
            }

            if (parameters != null)
            {
                foreach (var kv in parameters)
                {
                    fb.Add(new XElement(Ns + "Parameter",
                        new XAttribute("Name", kv.Key),
                        new XAttribute("Value", kv.Value)));
                }
            }

            _subAppNetwork.Add(fb);
            return this;
        }

        // Adds a coloured rectangular Frame that visually groups FB instances on the syslay canvas
        // (one per Station/PLC). BackgroundColor accepts any .NET KnownColor name.
        public SyslayBuilder AddFrame(string name, double x, double y,
            double width, double height,
            string backgroundColor, string text,
            string textColor, string textAlignment, string font)
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
            AddParam("TextColor", textColor);
            AddParam("Font", font);
            AddParam("TextAlignment", textAlignment);
            // MoveStyle="None" pins the frame at the emitted X/Y/Width/Height.
            // Was "AnyContained" — that's EAE's auto-grow mode which re-anchored
            // the BX1 frame westward to swallow Station 1 / Station 2 (M580) on
            // every regen. "None" is what the SE DesignGuidelines reference
            // project uses; combined with non-overlapping abutting frame widths
            // from the run's layout catalog, each PLC zone stays in its lane.
            AddParam("MoveStyle", "None");
            AddParam("Text", text);
            AddParam("NxtLayerIdentifier", string.Empty);

            _subAppNetwork.Add(frame);
            return this;
        }

        public SyslayBuilder AddEventConnection(string source, string destination, bool crossReference = false)
        {
            _eventConnections.Add(BuildConnection(source, destination, crossReference));
            return this;
        }

        public SyslayBuilder AddDataConnection(string source, string destination, bool crossReference = false)
        {
            _dataConnections.Add(BuildConnection(source, destination, crossReference));
            return this;
        }

        // crossReference=True marks a connection whose endpoints partition to different resources/PLCs; EAE
        // generates the CrossComm proxy from it (the two Process1_Generic FBs live on M580 and M262).
        private static XElement BuildConnection(string source, string destination, bool crossReference)
        {
            var conn = new XElement(Ns + "Connection",
                new XAttribute("Source", source),
                new XAttribute("Destination", destination));
            if (crossReference)
                conn.Add(new XElement(Ns + "Attribute",
                    new XAttribute("Name", "Configuration.Connections.CrossReference"),
                    new XAttribute("Value", "True")));
            return conn;
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

        // Append text to the top comment (joined with two newlines) — e.g. recipe-skipped-conditions
        // layered on the main top-comment.
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

    }
}
