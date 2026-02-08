using System.Collections.Generic;
using System.Xml.Linq;
using CodeGen.Models;

namespace CodeGen.IO
{
    public class SystemXmlReader
    {
        public List<VueOneComponent> ReadAllComponents(string xmlFilePath)
        {
            var components = new List<VueOneComponent>();
            var doc = XDocument.Load(xmlFilePath);

            var root = doc.Root;
            if (root == null) return components;

            var typeAttribute = root.Attribute("Type")?.Value;

            if (typeAttribute == "System")
            {
                var systemElement = root.Element("s");
                if (systemElement != null)
                {
                    foreach (var componentElement in systemElement.Elements("Component"))
                    {
                        var component = ParseComponent(componentElement, true);
                        if (component.Type != "NonControl")
                        {
                            components.Add(component);
                        }
                    }
                }
            }
            else if (typeAttribute == "Component")
            {
                var componentElement = root.Element("Component");
                if (componentElement != null)
                {
                    components.Add(ParseComponent(componentElement, false));
                }
            }

            return components;
        }

        private VueOneComponent ParseComponent(XElement componentElement, bool isSystemFile)
        {
            var nameElement = isSystemFile ? "n" : "Name";

            var component = new VueOneComponent
            {
                ComponentID = GetElementValue(componentElement, "ComponentID"),
                Name = GetElementValue(componentElement, nameElement),
                Description = GetElementValue(componentElement, "Description"),
                Type = GetElementValue(componentElement, "Type")
            };

            foreach (var stateElement in componentElement.Elements("State"))
            {
                component.States.Add(ParseState(stateElement, isSystemFile));
            }

            return component;
        }

        private VueOneState ParseState(XElement stateElement, bool isSystemFile)
        {
            var nameElement = isSystemFile ? "n" : "Name";

            return new VueOneState
            {
                StateID = GetElementValue(stateElement, "StateID"),
                Name = GetElementValue(stateElement, nameElement),
                StateNumber = GetIntValue(stateElement, "State_Number"),
                InitialState = GetBoolValue(stateElement, "Initial_State"),
                Time = GetIntValue(stateElement, "Time"),
                Position = GetDoubleValue(stateElement, "Position"),
                Counter = GetIntValue(stateElement, "Counter"),
                StaticState = GetBoolValue(stateElement, "StaticState")
            };
        }

        private string GetElementValue(XElement parent, string elementName)
            => parent.Element(elementName)?.Value.Trim() ?? string.Empty;

        private int GetIntValue(XElement parent, string elementName)
            => int.TryParse(GetElementValue(parent, elementName), out var result) ? result : 0;

        private bool GetBoolValue(XElement parent, string elementName)
            => bool.TryParse(GetElementValue(parent, elementName), out var result) && result;

        private double GetDoubleValue(XElement parent, string elementName)
            => double.TryParse(GetElementValue(parent, elementName), out var result) ? result : 0;
    }
}