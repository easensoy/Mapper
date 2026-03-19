using System.IO;

namespace MapperUI.Services
{
    public static class TemplatePackager
    {
        public static void Package(
            string templateIec61499Dir,
            string projectDir,
            string dfbprojPath,
            string templateHmiDir,
            string hmiDir)
        {
            int copied = 0;

            if (Directory.Exists(templateIec61499Dir))
            {
                foreach (var subdir in Directory.GetDirectories(templateIec61499Dir))
                {
                    var targetDir = Path.Combine(projectDir, Path.GetFileName(subdir));
                    if (!Directory.Exists(targetDir))
                        Directory.CreateDirectory(targetDir);

                    foreach (var file in Directory.GetFiles(subdir, "*", SearchOption.TopDirectoryOnly))
                    {
                        var dest = Path.Combine(targetDir, Path.GetFileName(file));
                        if (!File.Exists(dest))
                        {
                            File.Copy(file, dest);
                            copied++;
                        }
                    }
                }
            }

            if (Directory.Exists(templateHmiDir) && !string.IsNullOrWhiteSpace(hmiDir))
            {
                if (!Directory.Exists(hmiDir))
                    Directory.CreateDirectory(hmiDir);

                foreach (var file in Directory.GetFiles(templateHmiDir, "*", SearchOption.TopDirectoryOnly))
                {
                    var dest = Path.Combine(hmiDir, Path.GetFileName(file));
                    if (!File.Exists(dest))
                    {
                        File.Copy(file, dest);
                        copied++;
                    }
                }
            }

            MapperLogger.Info($"[Package] {copied} template file(s) copied to project.");
        }
    }
}
