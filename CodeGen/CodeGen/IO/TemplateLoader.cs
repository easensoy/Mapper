using System.IO;

namespace CodeGen.IO
{
    public class TemplateLoader
    {
        private readonly string _templateDirectory;

        public TemplateLoader(string templateDirectory)
        {
            _templateDirectory = templateDirectory;
        }

        public string LoadTemplate(string templateName)
        {
            var path = GetTemplatePath(templateName);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Template not found: {path}");

            return File.ReadAllText(path);
        }

        public bool TemplateExists(string templateName)
            => File.Exists(GetTemplatePath(templateName));

        private string GetTemplatePath(string templateName)
            => Path.Combine(_templateDirectory, templateName);
    }
}