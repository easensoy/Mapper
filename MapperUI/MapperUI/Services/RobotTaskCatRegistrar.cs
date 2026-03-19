using System;
using System.IO;
using CodeGen.Configuration;

namespace MapperUI.Services
{
    public static class RobotTaskCatRegistrar
    {
        private const string CatName = "Robot_Task_CAT";
        private const string CoreName = "Robot_Task_Core";

        public static string Register(MapperConfig cfg, string dfbprojPath)
        {
            if (string.IsNullOrWhiteSpace(cfg.RobotTemplatePath) || !File.Exists(cfg.RobotTemplatePath))
                throw new FileNotFoundException($"Robot template not found:\n{cfg.RobotTemplatePath}");

            var projectDir = Path.GetDirectoryName(dfbprojPath)!;
            var catDir = Path.Combine(projectDir, CatName);
            if (!Directory.Exists(catDir))
                Directory.CreateDirectory(catDir);

            var templateDir = Path.GetDirectoryName(cfg.RobotTemplatePath)!;
            int copied = 0;
            foreach (var file in Directory.GetFiles(templateDir, "*", SearchOption.TopDirectoryOnly))
            {
                var dest = Path.Combine(catDir, Path.GetFileName(file));
                if (!File.Exists(dest))
                {
                    File.Copy(file, dest);
                    copied++;
                }
            }

            if (!string.IsNullOrWhiteSpace(cfg.RobotBasicTemplatePath) && File.Exists(cfg.RobotBasicTemplatePath))
            {
                var coreDir = Path.Combine(projectDir, CoreName);
                if (!Directory.Exists(coreDir))
                    Directory.CreateDirectory(coreDir);
                var coreDest = Path.Combine(coreDir, Path.GetFileName(cfg.RobotBasicTemplatePath));
                if (!File.Exists(coreDest))
                    File.Copy(cfg.RobotBasicTemplatePath, coreDest);
            }

            int registered = DfbprojRegistrar.RegisterCat(dfbprojPath, CatName);
            if (!string.IsNullOrWhiteSpace(cfg.RobotBasicTemplatePath))
                DfbprojRegistrar.RegisterBasicFb(dfbprojPath, $@"{CoreName}\{CoreName}.fbt");

            File.SetLastWriteTime(dfbprojPath, DateTime.Now);
            MapperLogger.Info($"[RobotTaskCat] Registered {CatName}. Copied: {copied}, dfbproj entries: {registered}");

            return $"{CatName} registered successfully.\n{copied} file(s) copied.\n{registered} entry(ies) added to .dfbproj.";
        }
    }
}
