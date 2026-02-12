using System;
using CodeGen.Models;

namespace CodeGen.Mapping
{
    public class TemplateSelector
    {
        public FBTemplate? SelectTemplate(VueOneComponent component)
        {
            if (component == null)
            {
                throw new ArgumentNullException(nameof(component));
            }

            if (component.States == null)
            {
                return null;
            }

            if (string.Equals(component.Type, "Actuator", StringComparison.OrdinalIgnoreCase) &&
                component.States.Count == 5)
            {
                return CreateTemplate(
                    fileName: "Five_State_Actuator.fbt",
                    stateCount: 5,
                    componentType: "Actuator");
            }

            if (string.Equals(component.Type, "Sensor", StringComparison.OrdinalIgnoreCase) &&
                component.States.Count == 2)
            {
                return CreateTemplate(
                    fileName: "Sensor_Bool_CAT.fbt",
                    stateCount: 2,
                    componentType: "Sensor");
            }

            return null;
        }

        private static FBTemplate CreateTemplate(string fileName, int stateCount, string componentType)
        {
            return new FBTemplate
            {
                TemplateName = fileName,
                TemplateFilePath = fileName,
                ExpectedStateCount = stateCount,
                ComponentType = componentType
            };
        }
    }
}
