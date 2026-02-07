using CodeGen.Models;

namespace CodeGen.Mapping
{
    public class TemplateSelector
    {
        public FBTemplate? SelectTemplate(VueOneComponent component)
        {
            return (component.Type, component.States.Count) switch
            {
                ("Actuator", 5) => CreateTemplate("five_state_actuator.fbt", 5, "Actuator"),
                ("Sensor", 2) => CreateTemplate("two_state_sensor.fbt", 2, "Sensor"),
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