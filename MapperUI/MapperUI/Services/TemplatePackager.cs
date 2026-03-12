using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MapperUI.Services
{
    public static class TemplatePackager
    {
        static readonly Dictionary<string, string> CatToBasicFb = new()
        {
            { "Five_State_Actuator_CAT", "FiveStateActuator.fbt" },
            { "Sensor_Bool_CAT", "Sensor_Bool.fbt" },
        };

        public static string Package(string sourceIec, string targetIec, string dfbprojPath,
            string sourceHmi, string targetHmi)
        {
            int cats = 0, basics = 0, hmis = 0, reg = 0;

            foreach (var (catName, basicFb) in CatToBasicFb)
            {
                cats += CopyDir(Path.Combine(sourceIec, catName), Path.Combine(targetIec, catName));
                basics += CopyFbWithCompanions(sourceIec, targetIec, basicFb);
                hmis += CopyDir(Path.Combine(sourceHmi, catName), Path.Combine(targetHmi, catName));

                reg += DfbprojRegistrar.RegisterCat(dfbprojPath, catName);
                reg += DfbprojRegistrar.RegisterBasicFb(dfbprojPath, basicFb);
            }

            File.SetLastWriteTime(dfbprojPath, DateTime.Now);

            return $"Templates: {cats} CAT folders, {basics} Basic FBs, {hmis} HMI folders copied. {reg} dfbproj entries added.";
        }

        static int CopyDir(string src, string dst)
        {
            if (!Directory.Exists(src) || Directory.Exists(dst)) return 0;
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
            return 1;
        }

        static int CopyFbWithCompanions(string srcDir, string dstDir, string fbFile)
        {
            var src = Path.Combine(srcDir, fbFile);
            var dst = Path.Combine(dstDir, fbFile);
            if (!File.Exists(src) || File.Exists(dst)) return 0;
            File.Copy(src, dst);
            var baseName = Path.GetFileNameWithoutExtension(fbFile);
            foreach (var suffix in new[] { ".doc.xml", ".meta.xml" })
            {
                var s = Path.Combine(srcDir, baseName + suffix);
                var d = Path.Combine(dstDir, baseName + suffix);
                if (File.Exists(s) && !File.Exists(d)) File.Copy(s, d);
            }
            return 1;
        }
    }
}