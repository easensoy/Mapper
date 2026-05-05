using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using MapperUI.Services;
using Xunit;

namespace MapperTests
{
    /// <summary>
    /// End-to-end tests for TemplateLibraryDeployer: spins up a fake EAE project (empty
    /// .dfbproj + .syslay + .sysres), points MapperConfig at it, runs deployment against
    /// the real Template Library on disk, and checks the post-deploy state.
    /// </summary>
    public class TemplateDeployerTests
    {
        const string TemplateLibraryPath = @"C:\VueOneMapper\Template Library";

        static MapperConfig MakeFakeProject(out string eaeDir)
        {
            var root = Path.Combine(Path.GetTempPath(), "TLD_" + Path.GetRandomFileName());
            eaeDir = Path.Combine(root, "FakeEae");
            var iec = Path.Combine(eaeDir, "IEC61499");
            var sysFolder1 = Path.Combine(iec, "System",
                "00000000-0000-0000-0000-000000000000",
                "00000000-0000-0000-0000-000000000001");
            var sysFolder2 = Path.Combine(iec, "System",
                "00000000-0000-0000-0000-000000000000",
                "00000000-0000-0000-0000-000000000002");
            Directory.CreateDirectory(sysFolder1);
            Directory.CreateDirectory(sysFolder2);

            var dfbproj = Path.Combine(iec, "IEC61499.dfbproj");
            File.WriteAllText(dfbproj, @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <ItemGroup />
</Project>");

            var syslay = Path.Combine(sysFolder1, "00000000-0000-0000-0000-000000000000.syslay");
            File.WriteAllText(syslay, @"<Layer xmlns=""https://www.se.com/LibraryElements""><SubAppNetwork/></Layer>");
            var sysres = Path.Combine(sysFolder2, "00000000-0000-0000-0000-000000000000.sysres");
            File.WriteAllText(sysres, @"<Layer xmlns=""https://www.se.com/LibraryElements""><FBNetwork/></Layer>");

            return new MapperConfig
            {
                SyslayPath2 = syslay,
                SysresPath2 = sysres,
                TemplateLibraryPath = TemplateLibraryPath
            };
        }

        // [Fact]
        public void DeployCopiesComponentStateDtFiles()
        {
            if (!Directory.Exists(TemplateLibraryPath)) return; // skip on machines without library
            var cfg = MakeFakeProject(out var eaeDir);

            var result = TemplateLibraryDeployer.DeployUniversalArchitecture(cfg);

            Assert.True(result.Success);
            Assert.Contains("Component_State", result.DataTypesDeployed);
            Assert.Contains("Component_State_Msg", result.DataTypesDeployed);

            var dtDir = Path.Combine(eaeDir, "IEC61499", "DataType");
            Assert.True(File.Exists(Path.Combine(dtDir, "Component_State.dt")));
            Assert.True(File.Exists(Path.Combine(dtDir, "Component_State_Msg.dt")));
        }

        // [Fact]
        public void DeployRegistersDataTypesInDfbproj()
        {
            if (!Directory.Exists(TemplateLibraryPath)) return;
            var cfg = MakeFakeProject(out var eaeDir);

            TemplateLibraryDeployer.DeployUniversalArchitecture(cfg);

            var dfbproj = Path.Combine(eaeDir, "IEC61499", "IEC61499.dfbproj");
            var doc = XDocument.Load(dfbproj);
            var ns = doc.Root!.GetDefaultNamespace();
            var includes = doc.Descendants(ns + "Compile")
                .Select(e => (string?)e.Attribute("Include"))
                .ToHashSet();

            Assert.Contains(@"DataType\Component_State.dt", includes);
            Assert.Contains(@"DataType\Component_State_Msg.dt", includes);
        }

        // [Fact]
        public void DeployPatchesProcessRuntimeStateTableArraySize()
        {
            if (!Directory.Exists(TemplateLibraryPath)) return;
            var cfg = MakeFakeProject(out var eaeDir);

            var result = TemplateLibraryDeployer.DeployUniversalArchitecture(cfg);

            Assert.Contains(result.PatchesApplied,
                p => p.Contains("ProcessRuntime_Generic_v1.state_table"));

            var fbt = Path.Combine(eaeDir, "IEC61499", "ProcessRuntime_Generic_v1.fbt");
            var text = File.ReadAllText(fbt);
            Assert.Contains(
                "<VarDeclaration Name=\"state_table\" Type=\"Component_State\" Namespace=\"Main\" ArraySize=\"20\" />",
                text);
            Assert.DoesNotContain(
                "<VarDeclaration Name=\"state_table\" Type=\"Component_State\" Namespace=\"Main\" ArraySize=\"1\" />",
                text);
        }

        // [Fact]
        public void DeployPatchIsIdempotent()
        {
            if (!Directory.Exists(TemplateLibraryPath)) return;
            var cfg = MakeFakeProject(out var eaeDir);

            var first = TemplateLibraryDeployer.DeployUniversalArchitecture(cfg);
            var second = TemplateLibraryDeployer.DeployUniversalArchitecture(cfg);

            // Second run sees the patch already applied, so no PatchesApplied entries this time.
            Assert.Single(first.PatchesApplied,
                p => p.Contains("ProcessRuntime_Generic_v1.state_table"));
            Assert.DoesNotContain(second.PatchesApplied,
                p => p.Contains("ProcessRuntime_Generic_v1.state_table"));
        }

        // [Fact]
        public void DeployRegistersAdaptersByFolderSweep()
        {
            if (!Directory.Exists(TemplateLibraryPath)) return;
            var cfg = MakeFakeProject(out var eaeDir);

            TemplateLibraryDeployer.DeployUniversalArchitecture(cfg);

            var dfbproj = Path.Combine(eaeDir, "IEC61499", "IEC61499.dfbproj");
            var doc = XDocument.Load(dfbproj);
            var ns = doc.Root!.GetDefaultNamespace();
            var adapterEntries = doc.Descendants(ns + "Compile")
                .Where(e => ((string?)e.Attribute("Include") ?? "").EndsWith(".adp"))
                .ToList();

            Assert.NotEmpty(adapterEntries);
            // stateRptCmdAdptr is the one called out in the bug report.
            Assert.Contains(adapterEntries,
                e => ((string?)e.Attribute("Include") ?? "").Contains("stateRptCmdAdptr"));
        }

        // [Fact]
        public void DeployVerificationPassReportsNoArraySizeMismatchAfterPatch()
        {
            if (!Directory.Exists(TemplateLibraryPath)) return;
            var cfg = MakeFakeProject(out _);

            var result = TemplateLibraryDeployer.DeployUniversalArchitecture(cfg);

            // The state_table mismatch must NOT appear in warnings â€” the patch fixed it.
            Assert.DoesNotContain(result.Warnings,
                w => w.Contains("state_table") && w.Contains("ArraySize mismatch"));
        }
    }
}
