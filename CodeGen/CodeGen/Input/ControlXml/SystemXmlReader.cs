using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Models;

namespace CodeGen.IO
{
    // THE frontend. The only thing in this compiler that parses VueOne XML.
    //
    // It is FAIL-CLOSED, and that is the whole design. It used to accumulate a LastError string that
    // nothing outside this file ever read, and return whatever it had managed to parse - so a
    // malformed file, an unrecognised root, a missing system element and a component that threw
    // mid-parse all produced an EMPTY OR PARTIAL model that the pipeline then compiled and published
    // as a success. Every one of those now stops the run with a diagnostic naming what it could not
    // read. Nothing here returns a partial model.
    //
    // What it is deliberately TOLERANT of is VueOne's own spelling: <n> vs <Name>, and an encoding
    // declaration that disagrees with the bytes. Those are known properties of real exported twins,
    // not corruption, and refusing them would refuse files that are perfectly readable.
    public class SystemXmlReader
    {
        private readonly TwinSchema _schema;

        public string SystemName { get; private set; } = string.Empty;

        /// The schema names the component-kind vocabulary this reader classifies against. A run passes
        /// its OWN snapshot's schema so two runs holding different profiles cannot read one twin two ways.
        public SystemXmlReader(TwinSchema schema) =>
            _schema = schema ?? throw new ArgumentNullException(nameof(schema));

        public List<VueOneComponent> ReadAllComponents(string xmlFilePath)
        {
            if (string.IsNullOrWhiteSpace(xmlFilePath))
                throw new ArgumentException("Control.xml path is required.", nameof(xmlFilePath));
            if (!File.Exists(xmlFilePath))
                throw new FileNotFoundException($"Control.xml not found: {xmlFilePath}", xmlFilePath);

            var components = new List<VueOneComponent>();
            SystemName = string.Empty;

            XDocument doc;
            try
            {
                doc = LoadXmlTolerant(xmlFilePath);
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"[Twin] '{xmlFilePath}' is not readable as XML: {ex.Message}. Generation stops " +
                    "rather than compiling whatever parsed.", ex);
            }

            var root = doc.Root
                ?? throw new InvalidOperationException(
                    $"[Twin] '{xmlFilePath}' has no root element, so it declares no model.");

            var fileType = root.Attribute("Type")?.Value ?? string.Empty;

            if (string.Equals(fileType, "System", StringComparison.OrdinalIgnoreCase))
                ReadSystemFile(root, components, xmlFilePath);
            else if (string.Equals(fileType, "Component", StringComparison.OrdinalIgnoreCase))
                ReadComponentFile(root, components, xmlFilePath);
            else
                throw new InvalidOperationException(
                    $"[Twin] '{xmlFilePath}' declares root <{root.Name.LocalName} Type=" +
                    $"'{(fileType.Length == 0 ? "(absent)" : fileType)}'>. This compiler reads a VueOne " +
                    "export of Type 'System' or Type 'Component' and will not guess at another schema.");

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

        private void ReadSystemFile(XElement root, List<VueOneComponent> components, string path)
        {
            var s = root.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "s" ||
                                     e.Name.LocalName == "System");

            if (s == null)
                throw new InvalidOperationException(
                    $"[Twin] '{path}' declares Type='System' but carries no <s> or <System> element, " +
                    "so there is nothing to read the components out of. Children found: " +
                    $"[{string.Join(", ", root.Elements().Select(e => e.Name.LocalName))}].");

            SystemName = GetElementValue(s, "n");
            if (string.IsNullOrWhiteSpace(SystemName))
                SystemName = GetElementValue(s, "Name");

            var componentElements = s.Elements()
                .Where(e => e.Name.LocalName == "Component").ToList();

            // Every component is attempted, so ONE run reports EVERY unreadable row rather than making
            // the engineer fix them one generation at a time. A single failure still stops the run.
            var problems = new List<string>();
            for (int i = 0; i < componentElements.Count; i++)
            {
                try
                {
                    var c = ParseComponent(componentElements[i], isSystemFile: true);
                    if (c.Kind != ComponentKind.Excluded) components.Add(c);
                }
                catch (Exception ex)
                {
                    problems.Add($"component #{i + 1}" + Identify(componentElements[i]) + $": {ex.Message}");
                }
            }

            if (problems.Count > 0)
                throw new InvalidOperationException(
                    $"[Twin] '{path}' has {problems.Count} component(s) this compiler cannot read:" +
                    Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ", problems) +
                    Environment.NewLine +
                    "Generation stops rather than compiling the components that did parse.");
        }

        // Enough of the failing element to find it in the file, without assuming it parsed.
        private static string Identify(XElement elem)
        {
            var name = elem.Elements().FirstOrDefault(e => e.Name.LocalName is "n" or "Name")?.Value.Trim();
            var id = elem.Elements().FirstOrDefault(e => e.Name.LocalName == "ComponentID")?.Value.Trim();
            if (!string.IsNullOrEmpty(name)) return $" '{name}'";
            return string.IsNullOrEmpty(id) ? string.Empty : $" (ComponentID {id})";
        }

        private void ReadComponentFile(XElement root, List<VueOneComponent> components, string path)
        {
            var elem = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Component")
                ?? throw new InvalidOperationException(
                    $"[Twin] '{path}' declares Type='Component' but carries no <Component> element.");

            var c = ParseComponent(elem, isSystemFile: false);
            if (c.Kind != ComponentKind.Excluded) components.Add(c);
        }

        private VueOneComponent ParseComponent(XElement elem, bool isSystemFile)
        {
            // VueOne writes <n> in a system export and <Name> in a single-component one, and older
            // exports carry only <VcID>. All three are real spellings of the same fact.
            var name = GetElementValue(elem, "n");
            if (string.IsNullOrEmpty(name)) name = GetElementValue(elem, "Name");
            if (string.IsNullOrEmpty(name)) name = GetElementValue(elem, "VcID");

            var componentId = GetElementValue(elem, "ComponentID");

            // A component with no name cannot be placed on a roster row, addressed on the ring or bound
            // to a channel; the predecessor invented `Component_<id-prefix>` for it, which generated a
            // plant with a machine-made name that matched nothing an engineer had declared.
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException(
                    "carries no <n>, <Name> or <VcID>, so it cannot be named on a roster row, " +
                    "addressed on the report ring, or bound to a channel");

            // Every guard in the twin references its target BY ComponentID. A component without one can
            // be declared but never referred to, and a second such component cannot be told from it.
            if (string.IsNullOrWhiteSpace(componentId))
                throw new InvalidOperationException(
                    $"'{name}' carries no <ComponentID>, which is how every condition in the twin " +
                    "references a component, so nothing could refer to it");

            var type = GetElementValue(elem, "Type");

            var component = new VueOneComponent
            {
                ComponentID = componentId,
                Name = name,
                VcID = GetElementValue(elem, "VcID"),
                Description = GetElementValue(elem, "Description"),
                Type = type,
                // THE ONE PLACE a token becomes a kind. Refuses an unmapped token by name.
                Kind = _schema.KindOf(type, name),
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
