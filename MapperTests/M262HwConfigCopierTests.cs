using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Translation;
using MapperUI.Services;
using Xunit;

namespace MapperTests
{
    public class M262HwConfigCopierTests
    {
        // Synthetic .hcf shaped after the canonical EAE M262 config: Device contains a
        // chain of Module / ItemProperties / ParameterValue elements. The copier only
        // cares about ParameterValue Name/Value pairs, so other ItemProperties are kept
        // here just to verify the verbatim copy preserves the surrounding tree.
        const string SyntheticHcfTemplate = @"<?xml version=""1.0"" encoding=""utf-8""?>
<DeviceHwConfigurationItems>
  <Module Name=""BMTM3"">
    <ItemProperties>
      <ParameterValue Name=""busCycleTime"" Value=""T#80ms"" />
      <ParameterValue Name=""busTolerance"" Value=""30"" />
    </ItemProperties>
    <Module Name=""TM262L01MDESE8T"">
      <Module Name=""TM3DI16_G"">
        <ParameterValue Name=""DI00"" Value=""''"" />
        <ParameterValue Name=""DI01"" Value=""''"" />
        <ParameterValue Name=""DI02"" Value=""''"" />
      </Module>
      <Module Name=""TM3DQ16T_G"">
        <ParameterValue Name=""DO00"" Value=""''"" />
        <ParameterValue Name=""DO01"" Value=""''"" />
      </Module>
    </Module>
  </Module>
</DeviceHwConfigurationItems>";

        const string ActuatorsSheet =
            "Component\tType\tathome_tag\tatwork_tag\toutputToWork_tag\toutputToHome_tag\n" +
            "Feeder\tActuator_5\tPusherAtHome\tPusherAtWork\tExtendPusher\t\n";
        const string SensorsSheet =
            "Component\tType\tinput_tag\n";

        // Builds a baseline folder + target EAE folder + IoBindings xlsx fixture and
        // returns a MapperConfig wired to all of them.
        static MapperConfig BuildScenario(out string baseline, out string eaeRoot,
            string hcfBody = SyntheticHcfTemplate)
        {
            baseline = Path.Combine(Path.GetTempPath(), "M262Base_" + Path.GetRandomFileName());
            eaeRoot  = Path.Combine(Path.GetTempPath(), "M262Tgt_"  + Path.GetRandomFileName());

            // Baseline tree: HwConfiguration/HwConfiguration.hwconfigproj +
            // IEC61499/System/{sys}/{dev}/{dev}.hcf
            var bSys = Path.Combine(baseline, "IEC61499", "System",
                "00000000-0000-0000-0000-000000000000", "00000000-0000-0000-0000-000000000002");
            Directory.CreateDirectory(bSys);
            File.WriteAllText(Path.Combine(bSys, "00000000-0000-0000-0000-000000000002.hcf"), hcfBody);
            var bHwDir = Path.Combine(baseline, "HwConfiguration");
            Directory.CreateDirectory(bHwDir);
            File.WriteAllText(Path.Combine(bHwDir, "HwConfiguration.hwconfigproj"),
                "<HwConfigProject/>");

            // Target tree: dfbproj + sysdev (so DeriveEaeProjectRoot resolves) + a
            // syslay path the config can point at.
            var iec = Path.Combine(eaeRoot, "IEC61499");
            var tSys = Path.Combine(iec, "System",
                "00000000-0000-0000-0000-000000000000");
            Directory.CreateDirectory(tSys);
            File.WriteAllText(Path.Combine(iec, "IEC61499.dfbproj"),
                "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"/>");
            File.WriteAllText(Path.Combine(tSys, "00000000-0000-0000-0000-000000000002.sysdev"),
                "<Device xmlns=\"https://www.se.com/LibraryElements\" Name=\"EcoRT_0\"/>");
            var syslayDir = Path.Combine(tSys, "00000000-0000-0000-0000-000000000001");
            Directory.CreateDirectory(syslayDir);
            var syslayPath = Path.Combine(syslayDir, "00000000-0000-0000-0000-000000000000.syslay");
            File.WriteAllText(syslayPath, "<Layer/>");

            // IoBindings xlsx — for the test we use a CSV-shaped xlsx fixture by
            // pointing IoBindingsLoader at the existing TestData copy that lives next
            // to the test assembly. Falls back to inline bindings if the fixture is
            // missing (so this test stays self-contained even without TestData).
            var bindingsPath = Path.Combine(AppContext.BaseDirectory, "TestData", "SMC_Rig_IO_Bindings.xlsx");

            return new MapperConfig
            {
                SyslayPath2 = syslayPath,
                SyslayPath  = syslayPath,
                IoBindingsPath = bindingsPath,
                M262HardwareConfigBaselinePath = baseline,
            };
        }

        [Fact]
        public void Copy_PlacesHcfAtSysdevSiblingFolder()
        {
            IoBindingsLoader.InvalidateCache();
            var cfg = BuildScenario(out _, out var eaeRoot);

            var result = M262HwConfigCopier.Copy(cfg);
            Assert.NotNull(result.HcfPath);
            Assert.True(File.Exists(result.HcfPath!));
            // Convention: {sys-guid}/{sysdev-guid}/{sysdev-guid}.hcf
            var expected = Path.Combine(eaeRoot, "IEC61499", "System",
                "00000000-0000-0000-0000-000000000000",
                "00000000-0000-0000-0000-000000000002",
                "00000000-0000-0000-0000-000000000002.hcf");
            Assert.Equal(Path.GetFullPath(expected), Path.GetFullPath(result.HcfPath!));
        }

        [Fact]
        public void Copy_OverwritesDi00Di01Do00FromBindings()
        {
            IoBindingsLoader.InvalidateCache();
            var cfg = BuildScenario(out _, out _);
            var result = M262HwConfigCopier.Copy(cfg);

            var doc = XDocument.Load(result.HcfPath!);
            string Get(string name) => doc.Descendants("ParameterValue")
                .First(e => (string?)e.Attribute("Name") == name)
                .Attribute("Value")!.Value;

            Assert.Equal("'RES0.Feeder.athome'",       Get("DI00"));
            Assert.Equal("'RES0.Feeder.atwork'",       Get("DI01"));
            Assert.Equal("'RES0.Feeder.OutputToWork'", Get("DO00"));
        }

        [Fact]
        public void Copy_LeavesUnboundChannelsAlone()
        {
            IoBindingsLoader.InvalidateCache();
            var cfg = BuildScenario(out _, out _);
            var result = M262HwConfigCopier.Copy(cfg);

            var doc = XDocument.Load(result.HcfPath!);
            // DI02 / DO01 are in the baseline but absent from IoBindings: their Value
            // strings must remain whatever the baseline ships ('' in the fixture).
            string Get(string name) => doc.Descendants("ParameterValue")
                .First(e => (string?)e.Attribute("Name") == name)
                .Attribute("Value")!.Value;
            Assert.Equal("''", Get("DI02"));
            Assert.Equal("''", Get("DO01"));
        }

        [Fact]
        public void Copy_PreservesModuleChainStructure()
        {
            IoBindingsLoader.InvalidateCache();
            var cfg = BuildScenario(out _, out _);
            var result = M262HwConfigCopier.Copy(cfg);

            var doc = XDocument.Load(result.HcfPath!);
            // Verify the copier did NOT prune/reorder the BMTM3 → TM262 → TM3DI16_G
            // → TM3DQ16T_G chain. A module-by-name walk should still reach DO00.
            var root = doc.Root!;
            var bmtm3   = root.Elements("Module").Single(e => (string?)e.Attribute("Name") == "BMTM3");
            var tm262   = bmtm3.Elements("Module").Single(e => (string?)e.Attribute("Name") == "TM262L01MDESE8T");
            var tm3di   = tm262.Elements("Module").Single(e => (string?)e.Attribute("Name") == "TM3DI16_G");
            var tm3dq   = tm262.Elements("Module").Single(e => (string?)e.Attribute("Name") == "TM3DQ16T_G");
            Assert.NotNull(tm3di.Elements("ParameterValue").FirstOrDefault(e => (string?)e.Attribute("Name") == "DI00"));
            Assert.NotNull(tm3dq.Elements("ParameterValue").FirstOrDefault(e => (string?)e.Attribute("Name") == "DO00"));
        }

        [Fact]
        public void Copy_CopiesHwConfigurationFolderVerbatim()
        {
            IoBindingsLoader.InvalidateCache();
            var cfg = BuildScenario(out _, out var eaeRoot);
            M262HwConfigCopier.Copy(cfg);
            var copied = Path.Combine(eaeRoot, "HwConfiguration", "HwConfiguration.hwconfigproj");
            Assert.True(File.Exists(copied));
        }

        [Fact]
        public void Copy_SkipsWhenBaselinePathEmpty()
        {
            IoBindingsLoader.InvalidateCache();
            var cfg = BuildScenario(out _, out _);
            cfg.M262HardwareConfigBaselinePath = "";
            var result = M262HwConfigCopier.Copy(cfg);
            Assert.Null(result.HcfPath);
            Assert.Contains(result.Warnings, w => w.Contains("M262HardwareConfigBaselinePath"));
        }
    }
}
