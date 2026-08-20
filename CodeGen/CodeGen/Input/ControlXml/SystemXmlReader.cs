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
            catch (InvalidOperationException)
            {
                throw;   // a schema the compiler does not understand: never read as "no components"
            }
            catch (Exception ex)
            {
                LastError = $"Exception: {ex.Message}";
            }

            return components;
        }

        // VueOne sometimes declares encoding="utf-16" while the body bytes are plain ASCII/UTF-8;
        // XDocument.Load trusts the declaration and yields an empty document. Sniff the BOM, fall back
        // to UTF-8, then rewrite a lying declaration to match the bytes. Real UTF-16 (BOM) is untouched.
        private static XDocument LoadXmlTolerant(string xmlFilePath)
        {
            var bytes = File.ReadAllBytes(xmlFilePath);
            string content;
            bool wasUtf16 = false;

            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                content = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
                wasUtf16 = true;
            }
            else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                content = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
                wasUtf16 = true;
            }
            else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                content = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            }
            else
            {
                // No BOM: treat as UTF-8 (byte-identical to ASCII for single-byte chars).
                content = Encoding.UTF8.GetString(bytes);
            }

            if (content.Length > 0 && content[0] == '﻿')
                content = content.Substring(1);

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
                catch (InvalidOperationException) { throw; }   // a schema we cannot read, not a bad row
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
            // VueOne mixes <n> and <Name> in the same file; try both so state.Name is always populated.
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

            // VueOne stores actuator interlocks in a STATE-level <Interlock_Condition> block, NOT in
            // the transition's Sequence_Condition. Both nest the same way, so both read the same way.
            state.InterlockGuard = ReadGuard(elem, "Interlock_Condition");

            return state;
        }

        // Control.xml nests <holder> -> ConditionValue -> ConditionGroup* -> Condition*. The groups
        // are alternatives and the conditions inside one hold together, so the guard is read as a sum
        // of products. Reading it structurally rather than as a flat Descendants() sweep is what lets
        // a multi-group guard be recognised as a choice instead of silently becoming a longer list.
        private static ConditionExpr? ReadGuard(XElement owner, string holderName)
        {
            var holder = owner.Elements().FirstOrDefault(e => e.Name.LocalName == holderName);
            if (holder == null) return null;

            // A guard with no ConditionValue wrapper still reads: the groups are found either way.
            var groups = holder.Descendants().Where(e => e.Name.LocalName == "ConditionGroup").ToList();
            foreach (var g in groups)
            {
                var op = (g.Attribute("Operator")?.Value ?? string.Empty).Trim();
                if (op.Length > 0)
                    throw new InvalidOperationException(
                        $"[Twin] ConditionGroup '{g.Attribute("GroupName")?.Value}' declares Operator " +
                        $"'{op}'. This compiler reads a group as one alternative and knows no other " +
                        "combinator, so it will not guess what that operator means.");
            }
            if (groups.Count > 0)
                return ConditionExpr.Disjunction(groups.Select(g => ConditionExpr.Conjunction(
                    g.Elements().Where(e => e.Name.LocalName == "Condition")
                        .Select(c => new ConditionExpr.Ref(ReadCondition(c))))!)!);

            // Ungrouped conditions: every one has to hold.
            return ConditionExpr.Conjunction(holder.Descendants()
                .Where(e => e.Name.LocalName == "Condition")
                .Select(c => new ConditionExpr.Ref(ReadCondition(c))));
        }

        // A <Condition> element, wherever it appears: the twin spells them the same way under an
        // interlock and under a transition, so they are read the same way.
        private static VueOneCondition ReadCondition(XElement cond) => new()
        {
            ID = cond.Attribute("ID")?.Value ?? string.Empty,
            Name = cond.Attribute("Name")?.Value ?? string.Empty,
            ComponentID = cond.Attribute("ComponentID")?.Value ?? string.Empty,
            Operator = cond.Attribute("Operator")?.Value ?? string.Empty,
        };

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

            trans.Guard = ReadGuard(elem, "Sequence_Condition");

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