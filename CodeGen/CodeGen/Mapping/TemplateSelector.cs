using VueOneMapper.Models;

namespace VueOneMapper.Mapping
{
    /// <summary>
    /// Selects the appropriate IEC 61499 template based on VueOne component type
    /// </summary>
    public class TemplateSelector
    {
        public FBTemplate SelectTemplate(VueOneComponent component)
        {
            // For Tuesday demo: Focus on Five-State Actuator
            if (component.Type == "Actuator" && component.States.Count == 5)
            {
                return new FBTemplate
                {
                    TemplateName = "five_state_actuator.fbt",
                    TemplateFilePath = "five_state_actuator.fbt",
                    ExpectedStateCount = 5,
                    ComponentType = "Actuator"
                };
            }

            // Future: Two-State Sensor
            if (component.Type == "Sensor" && component.States.Count == 2)
            {
                return new FBTemplate
                {
                    TemplateName = "two_state_sensor.fbt",
                    TemplateFilePath = "two_state_sensor.fbt",
                    ExpectedStateCount = 2,
                    ComponentType = "Sensor"
                };
            }

            // No matching template
            return null;
        }
    }
}