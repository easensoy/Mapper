using CodeGen.Models;

namespace CodeGen.Mapping
{
    public class TemplateSelector
    {
        public FBTemplate? SelectTemplate(VueOneComponent component)
        {
            return (component.Type, component.States.Count) switch
            {
                ("Actuator", 5) => new FBTemplate
                {
                    TemplateName = "Five_State_Actuator_CAT.fbt",  
                    ExpectedStateCount = 5,
                    ComponentType = "Actuator"
                },
                ("Sensor", 2) => new FBTemplate
                {
                    TemplateName = "Sensor_Bool_CAT.fbt",
                    ExpectedStateCount = 2,
                    ComponentType = "Sensor"
                },
                _ => null
            };
        }

        private FBTemplate CreateTemplate(string fileName, int stateCount, string componentType)
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