using System.IO;

namespace CodeGen.IO
{
    public class FBWriter
    {
        private readonly string _outputDirectory;

        public FBWriter(string outputDirectory)
        {
            _outputDirectory = outputDirectory;
            Directory.CreateDirectory(_outputDirectory);
        }

        public void WriteFile(string fileName, string content)
        {
            File.WriteAllText(GetOutputPath(fileName), content);
        }

        public string GetOutputPath(string fileName)
            => Path.Combine(_outputDirectory, fileName);
    }
}