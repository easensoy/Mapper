using System;
using System.IO;

namespace VueOneMapper.IO
{
    /// <summary>
    /// Loads IEC 61499 template files
    /// </summary>
    public class TemplateLoader
    {
        private readonly string _templateDirectory;

        public TemplateLoader(string templateDirectory)
        {
            _templateDirectory = templateDirectory;
        }

        public string LoadTemplate(string templateName)
        {
            string templatePath = Path.Combine(_templateDirectory, templateName);

            if (!File.Exists(templatePath))
            {
                throw new FileNotFoundException($"Template not found: {templatePath}");
            }

            return File.ReadAllText(templatePath);
        }

        public bool TemplateExists(string templateName)
        {
            string templatePath = Path.Combine(_templateDirectory, templateName);
            return File.Exists(templatePath);
        }
    }
}