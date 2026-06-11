using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodeGen.Models;

namespace CodeGen.IO
{
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
                var doc = LoadXmlTolerant(xmlFilePath);
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

        /// <summary>
        /// Loads a VueOne Control.xml in a way that survives the declaration-
        /// vs-actual-encoding mismatch VueOne sometimes emits. Some VueOne
        /// builds write <c>&lt;?xml version="1.0" encoding="utf-16"?&gt;</c> at
        /// the top of the file even when the body bytes are plain ASCII / UTF-8
        /// (no BOM, single-byte chars). <see cref="XDocument.Load(string)"/>
        /// trusts the declaration, tries to decode the ASCII bytes as UTF-16,
        /// produces garbage, and silently yields an empty document — surfaced
        /// as "No components found." in the UI.
        ///
        /// This loader sniffs the BOM first, falls back to UTF-8 (safe
        /// superset for ASCII), then rewrites a lying encoding declaration to
        /// match the bytes actually read. Pure UTF-16 files (BOM present) are
        /// untouched.
        /// </summary>
        private static XDocument LoadXmlTolerant(string xmlFilePath)
        {
            var bytes = File.ReadAllBytes(xmlFilePath);
            string content;
            bool wasUtf16 = false;

            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                // UTF-16 LE with BOM — real binary UTF-16.
                content = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
                wasUtf16 = true;
            }
            else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                // UTF-16 BE with BOM.
                content = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
                wasUtf16 = true;
            }
            else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                // UTF-8 with BOM.
                content = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            }
            else
            {
                // No BOM. Treat as UTF-8 (which is byte-identical to ASCII for
                // single-byte characters). The VueOne case lands here even when
                // the declaration claims utf-16 — actual bytes are ASCII.
                content = Encoding.UTF8.GetString(bytes);
            }

            // Strip stray BOM character that survived the decode (UTF-16 BOM
            // sometimes appears as the first char of the resulting string).
            if (content.Length > 0 && content[0] == '﻿')
                content = content.Substring(1);

            // If the byte stream is NOT actually UTF-16 but the declaration
            // claims it is, rewrite the declaration to match — otherwise
            // XDocument.Parse will reject the document on the encoding
            // mismatch and the user sees "No components found".
            if (!wasUtf16)
            {
                content = Regex.Replace(
                    content,
                    @"encoding\s*=\s*[""']utf-16[""']",
                    @"encoding=""utf-8""",
                    RegexOptions.IgnoreCase);
            }

            return XDocument.Parse(content);
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
                VcID = GetElementValue(elem, "VcID"),
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
            // VueOne mixes <n> (compact form) and <Name> (long form) within the same
            // System-type file. Mirror the same try-both fallback ParseComponent uses
            // (line 96–97) so state.Name is populated regardless of the spelling.
            var stateName = GetElementValue(elem, isSystemFile ? "n" : "Name");
            if (string.IsNullOrEmpty(stateName))
                stateName = GetElementValue(elem, isSystemFile ? "Name" : "n");
            var state = new VueOneState
            {
                StateID = GetElementValue(elem, "StateID"),
                Name = stateName,
                StateNumber = GetIntValue(elem, "State_Number"),
                InitialState = GetBoolValue(elem, "Initial_State"),
                Time = GetIntValue(elem, "Time"),
                Position = GetDoubleValue(elem, "Position"),
                Counter = GetIntValue(elem, "Counter"),
                StaticState = GetBoolValue(elem, "StaticState")
            };

            foreach (var transElem in elem.Elements().Where(e => e.Name.LocalName == "Transition"))
                state.Transitions.Add(ParseTransition(transElem));

            // VueOne stores actuator interlocks in a STATE-level
            // <Interlock_Condition><ConditionValue><ConditionGroup>
            // <Condition .../></...> block (NOT the transition's
            // Sequence_Condition). Capture every <Condition> descendant so
            // SystemInjector.BuildInterlockRules can translate them.
            var ilk = elem.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "Interlock_Condition");
            if (ilk != null)
            {
                foreach (var cond in ilk.Descendants()
                    .Where(e => e.Name.LocalName == "Condition"))
                {
                    state.InterlockConditions.Add(new VueOneCondition
                    {
                        ID = cond.Attribute("ID")?.Value ?? string.Empty,
                        Name = cond.Attribute("Name")?.Value ?? string.Empty,
                        ComponentID = cond.Attribute("ComponentID")?.Value ?? string.Empty,
                        Operator = cond.Attribute("Operator")?.Value ?? string.Empty
                    });
                }
            }

            return state;
        }

        private VueOneTransition ParseTransition(XElement elem)
        {
            var rawType = GetElementValue(elem, "Type");
            var trans = new VueOneTransition
            {
                TransitionID = GetElementValue(elem, "TransitionID"),
                OriginStateID = GetElementValue(elem, "Origin_State"),
                DestinationStateID = GetElementValue(elem, "Destination_State"),
                Priority = GetIntValue(elem, "Priority"),
                TransitionType = string.IsNullOrWhiteSpace(rawType) ? "SINGLE" : rawType.ToUpperInvariant()
            };

            var seq = elem.Elements().FirstOrDefault(e => e.Name.LocalName == "Sequence_Condition");
            if (seq != null)
            {
                foreach (var cond in seq.Descendants().Where(e => e.Name.LocalName == "Condition"))
                {
                    trans.Conditions.Add(new VueOneCondition
                    {
                        ID = cond.Attribute("ID")?.Value ?? string.Empty,
                        Name = cond.Attribute("Name")?.Value ?? string.Empty,
                        ComponentID = cond.Attribute("ComponentID")?.Value ?? string.Empty,
                        Operator = cond.Attribute("Operator")?.Value ?? string.Empty
                    });
                }
            }

            return trans;
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