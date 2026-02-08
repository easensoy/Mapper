using System.Threading.Tasks;
using CodeGen.IO;
using CodeGen.Validation;
using CodeGen.Models;

namespace MapperUI.Services
{
    public class ValidationService
    {
        public async Task<ValidationResult> ValidateControlXml(string xmlPath)
        {
            return await Task.Run(() =>
            {
                var xmlReader = new ControlXmlReader();
                var component = xmlReader.ReadComponent(xmlPath);

                var validator = new ComponentValidator();
                return validator.Validate(component);
            });
        }

        public async Task<VueOneComponent> ReadComponent(string xmlPath)
        {
            return await Task.Run(() =>
            {
                var xmlReader = new ControlXmlReader();
                return xmlReader.ReadComponent(xmlPath);
            });
        }
    }
}