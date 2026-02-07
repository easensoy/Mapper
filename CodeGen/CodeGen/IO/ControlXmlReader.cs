using System;
using System.Xml.Linq;
using CodeGen.Models;

namespace CodeGen.IO
{
    public class ControlXmlReader
    {
        public VueOneComponent ReadComponent(string xmlFilePath)
        {
            var doc = XDocument.Load(xmlFilePath);
            var componentElement = doc.Root?.Element("Component")
                ?? throw new Exception("No Component element found");

            var component = new VueOneComponent
            {
                ComponentID = GetElementValue(componentElement, "ComponentID"),
                Name = GetElementValue(componentElement, "Name"),
                Description = GetElementValue(componentElement, "Description"),
                Type = GetElementValue(componentElement, "Type")
            };

            foreach (var stateElement in componentElement.Elements("State"))
            {
                component.States.Add(ParseState(stateElement));
            }

            return component;
        }

        private VueOneState ParseState(XElement stateElement)
        {
            return new VueOneState
            {
                StateID = GetElementValue(stateElement, "StateID"),
                Name = GetElementValue(stateElement, "Name"),
                StateNumber = GetIntValue(stateElement, "State_Number"),
                InitialState = GetBoolValue(stateElement, "Initial_State"),
                Time = GetIntValue(stateElement, "Time"),
                Position = GetDoubleValue(stateElement, "Position"),
                Counter = GetIntValue(stateElement, "Counter"),
                StaticState = GetBoolValue(stateElement, "StaticState")
            };
        }

        private string GetElementValue(XElement parent, string elementName)
            => parent.Element(elementName)?.Value ?? string.Empty;

        private int GetIntValue(XElement parent, string elementName)
            => int.TryParse(GetElementValue(parent, elementName), out var result) ? result : 0;

        private bool GetBoolValue(XElement parent, string elementName)
            => bool.TryParse(GetElementValue(parent, elementName), out var result) && result;

        private double GetDoubleValue(XElement parent, string elementName)
            => double.TryParse(GetElementValue(parent, elementName), out var result) ? result : 0;
    }
}