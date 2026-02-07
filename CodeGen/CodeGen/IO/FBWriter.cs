using System;
using System.IO;

namespace VueOneMapper.IO
{
    /// <summary>
    /// Writes generated Function Block files to disk
    /// </summary>
    public class FBWriter
    {
        private readonly string _outputDirectory;

        public FBWriter(string outputDirectory)
        {
            _outputDirectory = outputDirectory;

            // Ensure output directory exists
            if (!Directory.Exists(_outputDirectory))
            {
                Directory.CreateDirectory(_outputDirectory);
            }
        }

        public void WriteFile(string fileName, string content)
        {
            string filePath = Path.Combine(_outputDirectory, fileName);
            File.WriteAllText(filePath, content);
        }

        public string GetOutputPath(string fileName)
        {
            return Path.Combine(_outputDirectory, fileName);
        }
    }
}