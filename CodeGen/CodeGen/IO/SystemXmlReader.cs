using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Models;

namespace CodeGen.IO
{
    /// <summary>
    /// Reads a VueOne Control.xml (Type="System") or single-component XML (Type="Component").
    /// After calling ReadAllComponents():
    ///   SystemName → value of <n> inside <s>       e.g. "SMC_Vue2VC_With_Processes"
    ///   SystemID   → value of <SystemID> inside <s> e.g. "SYS-8133a338-..."
    /// For single-component files these remain empty.
    /// </summary>
    public class SystemXmlReader
    {
        public string SystemName { get; private set; } = string.Empty;
        public string SystemID { get; private set; } = string.Empty;
        public string LastError { get; private set; } = string.Empty;

        public List<VueOneComponent> ReadAllComponents(string xmlFilePath)
        {
            var components = new List<VueOneComponent>();
            SystemName = string.Empty;
            SystemID = string.Empty;
            LastError = string.Empty;

            try
            {
                var doc = XDocument.Load(xmlFilePath);
                var root = doc.Root;

                if (root == null)
                {
                    LastError = "XML root is null";
                    return components;
                }

                var fileType = root.Attribute("Type")?.Value ?? string.Empty;
                LastError = $"Type={fileType}, Root={root.Name.LocalName}";

                if (string.Equals(fileType, "System", StringComparison.OrdinalIgnoreCase))
                    ReadSystemFile(root, components);
                else if (string.Equals(fileType, "Component", StringComparison.OrdinalIgnoreCase))
                    ReadComponentFile(root, components);
                else
                    LastError += $" — unrecognised Type '{fileType}'";
            }
            catch (Exception ex)
            {
                LastError = $"Exception: {ex.Message}";
            }

            return components;
        }

        private void ReadSystemFile(XElement root, List<VueOneComponent> components)
        {
            var s = root.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "s" ||
                                     e.Name.LocalName == "System");

            if (s == null)
            {
                LastError += $" — no <s> element. Children: [{string.Join(", ", root.Elements().Select(e => e.Name.LocalName))}]";
                return;
            }

            // VueOne stores system name as <n>, not <Name>
            SystemName = GetElementValue(s, "n");
            if (string.IsNullOrWhiteSpace(SystemName))
                SystemName = GetElementValue(s, "Name");

            SystemID = GetElementValue(s, "SystemID");
            LastError += $", System='{SystemName}', ID={SystemID}";

            var componentElements = s.Elements()
                .Where(e => e.Name.LocalName == "Component").ToList();

            LastError += $", Components={componentElements.Count}";

            foreach (var elem in componentElements)
            {
                try
                {
                    var c = ParseComponent(elem, isSystemFile: true);
                    if (!string.Equals(c.Type, "NonControl", StringComparison.OrdinalIgnoreCase))
                        components.Add(c);
                }
                catch (Exception ex) { LastError += $", ParseError:{ex.Message}"; }
            }
        }

        private void ReadComponentFile(XElement root, List<VueOneComponent> components)
        {
            var elem = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Component");
            if (elem != null)
                components.Add(ParseComponent(elem, isSystemFile: false));
        }

        private VueOneComponent ParseComponent(XElement elem, bool isSystemFile)
        {
            var name = GetElementValue(elem, "n");
            if (string.IsNullOrEmpty(name)) name = GetElementValue(elem, "Name");
            if (string.IsNullOrEmpty(name)) name = GetElementValue(elem, "VcID");
            if (string.IsNullOrEmpty(name))
            {
                var id = GetElementValue(elem, "ComponentID");
                name = id.Length >= 8 ? $"Component_{id.Substring(0, 8)}" : "Component_unknown";
            }

            var component = new VueOneComponent
            {
                ComponentID = GetElementValue(elem, "ComponentID"),
                Name = name,
                Description = GetElementValue(elem, "Description"),
                Type = GetElementValue(elem, "Type"),
                NameTag = isSystemFile ? "n" : "Name"
            };

            foreach (var stateElem in elem.Elements().Where(e => e.Name.LocalName == "State"))
                component.States.Add(ParseState(stateElem, isSystemFile));

            return component;
        }

        private VueOneState ParseState(XElement elem, bool isSystemFile)
        {
            var nameTag = isSystemFile ? "n" : "Name";
            return new VueOneState
            {
                StateID = GetElementValue(elem, "StateID"),
                Name = GetElementValue(elem, nameTag),
                StateNumber = GetIntValue(elem, "State_Number"),
                InitialState = GetBoolValue(elem, "Initial_State"),
                Time = GetIntValue(elem, "Time"),
                Position = GetDoubleValue(elem, "Position"),
                Counter = GetIntValue(elem, "Counter"),
                StaticState = GetBoolValue(elem, "StaticState")
            };
        }

        private string GetElementValue(XElement parent, string elementName)
        {
            var e = parent.Elements().FirstOrDefault(x => x.Name.LocalName == elementName);
            return e?.Value.Trim() ?? string.Empty;
        }

        private int GetIntValue(XElement p, string n)
            => int.TryParse(GetElementValue(p, n), out var v) ? v : 0;
        private bool GetBoolValue(XElement p, string n)
            => bool.TryParse(GetElementValue(p, n), out var v) && v;
        private double GetDoubleValue(XElement p, string n)
            => double.TryParse(GetElementValue(p, n), out var v) ? v : 0.0;
    }
}