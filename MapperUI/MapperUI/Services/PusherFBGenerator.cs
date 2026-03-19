using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Models;

namespace MapperUI.Services
{
    public static class PusherFBGenerator
    {
        public static string Generate(MapperConfig cfg, List<VueOneComponent> components)
        {
            var robots = components
                .Where(c => string.Equals(c.Type, "Robot", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (robots.Count == 0)
                return "No Robot components found. No Pusher FBs generated.";

            if (string.IsNullOrWhiteSpace(cfg.RobotTemplatePath) || !File.Exists(cfg.RobotTemplatePath))
                throw new FileNotFoundException($"Robot template not found:\n{cfg.RobotTemplatePath}");

            var syslayDir = Path.GetDirectoryName(cfg.ActiveSyslayPath) ?? string.Empty;
            var projectDir = syslayDir;
            while (!string.IsNullOrEmpty(projectDir) &&
                   !Directory.GetFiles(projectDir, "*.dfbproj").Any())
                projectDir = Path.GetDirectoryName(projectDir) ?? string.Empty;

            if (string.IsNullOrWhiteSpace(projectDir))
                throw new InvalidOperationException("Cannot determine EAE project directory from syslay path.");

            var templateDir = Path.GetDirectoryName(cfg.RobotTemplatePath)!;
            int generated = 0;

            foreach (var robot in robots)
            {
                var fbName = $"{robot.Name}_Task_CAT";
                var fbDir = Path.Combine(projectDir, fbName);
                if (!Directory.Exists(fbDir))
                    Directory.CreateDirectory(fbDir);

                foreach (var file in Directory.GetFiles(templateDir, "*", SearchOption.TopDirectoryOnly))
                {
                    var destName = Path.GetFileName(file)
                        .Replace("Robot_Task_CAT", fbName, StringComparison.OrdinalIgnoreCase);
                    var dest = Path.Combine(fbDir, destName);
                    if (!File.Exists(dest))
                        File.Copy(file, dest);
                }

                MapperLogger.Info($"[PusherFB] Generated {fbName}");
                generated++;
            }

            return $"{generated} Pusher FB(s) generated for: {string.Join(", ", robots.Select(r => r.Name))}.";
        }
    }
}
