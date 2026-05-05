using System;
using System.IO;
using CodeGen.Configuration;
using CodeGen.Translation;
using MapperUI.Services;
using Xunit;

namespace MapperTests
{
    public class EmitDiagnostics
    {
        const string ControlXml = @"C:\VueOne\system\SMC_Vue2VC_With_Processes\Control.xml";
        const string OutputDir = @"C:\VueOneMapper\Output";
        const string BindingsXlsx = @"C:\VueOneMapper\MapperUI\MapperUI\Input\SMC_Rig_IO_Bindings.xlsx";

        static MapperConfig ScratchConfig(string label)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"EmitDiag_{label}_" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            var syslay = Path.Combine(dir, "scratch.syslay");
            var sysres = Path.Combine(dir, "scratch.sysres");
            File.WriteAllText(syslay, "<Layer xmlns=\"https://www.se.com/LibraryElements\"><SubAppNetwork/></Layer>");
            File.WriteAllText(sysres, "<Layer xmlns=\"https://www.se.com/LibraryElements\"><FBNetwork/></Layer>");
            return new MapperConfig { SyslayPath2 = syslay, SysresPath2 = sysres };
        }

        // [Fact]
        public void EmitAllThreeButtons()
        {
            if (!File.Exists(ControlXml))
                return;
            Directory.CreateDirectory(OutputDir);

            IoBindingsLoader.InvalidateCache();
            IoBindings? bindings = File.Exists(BindingsXlsx)
                ? IoBindingsLoader.LoadBindings(BindingsXlsx)
                : null;

            var injector = new SystemInjector();

            var cfg1 = ScratchConfig("Btn1");
            injector.PrepareDemonstratorForGeneration(cfg1);
            SystemInjector.BindingApplicationReport r1 = null!;
            injector.GenerateProcessFBSyslay(cfg1, ControlXml, null, out r1);
            File.Copy(cfg1.SyslayPath2,
                Path.Combine(OutputDir, "generated_code_first_button.txt"), true);

            var cfg2 = ScratchConfig("Btn2");
            injector.PrepareDemonstratorForGeneration(cfg2);
            SystemInjector.BindingApplicationReport r2 = null!;
            injector.GenerateStation1TestSyslay(cfg2, ControlXml, bindings, out r2);
            File.Copy(cfg2.SyslayPath2,
                Path.Combine(OutputDir, "generated_code_second_button.txt"), true);

            var cfg3 = ScratchConfig("Btn3");
            injector.PrepareDemonstratorForGeneration(cfg3);
            SystemInjector.BindingApplicationReport r3 = null!;
            injector.GenerateFullSystemSyslay(cfg3, ControlXml, bindings, out r3);
            File.Copy(cfg3.SyslayPath2,
                Path.Combine(OutputDir, "generated_code_third_button.txt"), true);

            Assert.True(File.Exists(Path.Combine(OutputDir, "generated_code_first_button.txt")));
            Assert.True(File.Exists(Path.Combine(OutputDir, "generated_code_second_button.txt")));
            Assert.True(File.Exists(Path.Combine(OutputDir, "generated_code_third_button.txt")));
        }
    }
}
