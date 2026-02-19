using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Models;

namespace CodeGen.IO
{
    public class SystemXmlReader
    {
        public string LastError { get; private set; } = string.Empty;

        public List<VueOneComponent> ReadAllComponents(string xmlFilePath)
        {
            var components = new List<VueOneComponent>();
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

                var typeAttribute = root.Attribute("Type")?.Value;
                LastError = $"Type={typeAttribute}, Root={root.Name.LocalName}";

                if (typeAttribute == "System")
                {
                    var systemElement = root.Elements().FirstOrDefault(e => e.Name.LocalName == "s" || e.Name.LocalName == "System");

                    if (systemElement == null)
                    {
                        LastError += ", No <s> or <System> element found";
                        var childNames = string.Join(", ", root.Elements().Select(e => e.Name.LocalName));
                        LastError += $", Children: [{childNames}]";
                        return components;
                    }

                    LastError += $", System element: {systemElement.Name.LocalName}";

                    var componentElements = systemElement.Elements()
                        .Where(e => e.Name.LocalName == "Component")
                        .ToList();

                    LastError += $", Found {componentElements.Count} Component elements";

                    foreach (var componentElement in componentElements)
                    {
                        try
                        {
                            var component = ParseComponent(componentElement, isSystemFile: true);

                            if (component.Type != "NonControl")
                            {
                                components.Add(component);
                            }
                        }
                        catch (Exception ex)
                        {
                            LastError += $", Error: {ex.Message}";
                        }
                    }
                }
                else if (typeAttribute == "Component")
                {
                    var componentElement = root.Elements()
                        .FirstOrDefault(e => e.Name.LocalName == "Component");

                    if (componentElement != null)
                    {
                        components.Add(ParseComponent(componentElement, isSystemFile: false));
                    }
                }
            }
            catch (Exception ex)
            {
                LastError = $"Exception: {ex.Message}";
            }

            return components;
        }

        private VueOneComponent ParseComponent(XElement componentElement, bool isSystemFile)
        {
            // System files use <n> for the component name; component files use <Name>.
            // NameTag is stored on the model so the UI can display the exact source tag.
            var nameTag = isSystemFile ? "n" : "Name";

            var name = GetElementValue(componentElement, nameTag);

            if (string.IsNullOrEmpty(name))
            {
                name = GetElementValue(componentElement, "Name");
            }
            if (string.IsNullOrEmpty(name))
            {
                name = GetElementValue(componentElement, "VcID");
            }
            if (string.IsNullOrEmpty(name))
            {
                name = $"Component_{GetElementValue(componentElement, "ComponentID").Substring(0, 8)}";
            }

            var component = new VueOneComponent
            {
                ComponentID = GetElementValue(componentElement, "ComponentID"),
                Name = name,
                Description = GetElementValue(componentElement, "Description"),
                Type = GetElementValue(componentElement, "Type"),
                NameTag = nameTag
            };

            foreach (var stateElement in componentElement.Elements().Where(e => e.Name.LocalName == "State"))
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
        {
            var element = parent.Elements().FirstOrDefault(e => e.Name.LocalName == elementName);
            return element?.Value.Trim() ?? string.Empty;
        }

        private int GetIntValue(XElement parent, string elementName)
            => int.TryParse(GetElementValue(parent, elementName), out var result) ? result : 0;

        private bool GetBoolValue(XElement parent, string elementName)
            => bool.TryParse(GetElementValue(parent, elementName), out var result) && result;

        private double GetDoubleValue(XElement parent, string elementName)
            => double.TryParse(GetElementValue(parent, elementName), out var result) ? result : 0;
    }
}