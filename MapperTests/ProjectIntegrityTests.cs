using System;
using System.IO;
using CodeGen.Configuration;
using CodeGen.Validation.Output;
using Xunit;

namespace MapperTests
{
    /// The two ways a generated project actually fails when EAE opens it. EAE's Buildtime is a GUI tool
    /// and is not always installed beside the compiler, so these are answered from the tree instead —
    /// which is what makes them runnable on every build rather than only on a rig.
    public sealed class ProjectIntegrityTests : IDisposable
    {
        readonly string _root;
        readonly string _eae;

        public ProjectIntegrityTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "integ_" + Guid.NewGuid().ToString("N")[..10]);
            _eae = Path.Combine(_root, "Demonstrator");
            Directory.CreateDirectory(Path.Combine(_eae, "IEC61499", "System", "app"));
            File.WriteAllText(Dfbproj, "<Project><ItemGroup /></Project>");
            File.WriteAllText(Path.Combine(_eae, "IEC61499", "Real_Type.fbt"), "<FBType Name=\"Real_Type\" />");
        }

        string Dfbproj => Path.Combine(_eae, "IEC61499", "IEC61499.dfbproj");
        string Syslay => Path.Combine(_eae, "IEC61499", "System", "app", "app.syslay");

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* temp */ }
        }

        CompilerConfiguration Config()
        {
            var paths = TestConfig.Cfg.Paths.Clone();
            paths.SyslayPath2 = Syslay;
            paths.SysresPath2 = Path.Combine(_eae, "IEC61499", "System", "app", "app.sysres");
            return TestConfig.Cfg.With(paths);
        }

        static void WriteSyslay(string path, string fbType, string ns) =>
            File.WriteAllText(path,
                $"<SystemConfiguration><SubAppNetwork>" +
                $"<FB Name=\"Anything\" Type=\"{fbType}\" Namespace=\"{ns}\" />" +
                $"</SubAppNetwork></SystemConfiguration>");

        [Fact]
        public void A_project_whose_every_reference_resolves_passes()
        {
            WriteSyslay(Syslay, "Real_Type", TestConfig.Cfg.Generation.ProjectNamespace);
            var (registrations, types) = ProjectIntegrityValidator.Validate(Config());
            Assert.Equal(0, registrations);            // nothing registered yet, nothing dangling
            Assert.Equal(1, types);                    // and the one referenced type is deployed
        }

        [Fact]
        public void A_registration_naming_a_file_that_is_not_there_is_refused()
        {
            // EAE reports this under Solution Integrity and quietly drops the item, so the project opens
            // looking correct while missing whatever the entry named.
            WriteSyslay(Syslay, "Real_Type", TestConfig.Cfg.Generation.ProjectNamespace);
            File.WriteAllText(Dfbproj,
                "<Project><ItemGroup><None Include=\"DoesNotExist.fbt\" /></ItemGroup></Project>");

            var boom = Assert.Throws<InvalidOperationException>(() => ProjectIntegrityValidator.Validate(Config()));
            Assert.Contains("MISSING PROJECT FILE", boom.Message);
            Assert.Contains("DoesNotExist.fbt", boom.Message);
        }

        [Fact]
        public void An_FB_whose_type_was_never_deployed_is_refused()
        {
            // ERR_NO_SUCH_TYPE: the resource loads and the instance is simply not there, which on a rig
            // reads as "the actuator does nothing" rather than as a build failure.
            WriteSyslay(Syslay, "Type_That_Was_Never_Deployed", TestConfig.Cfg.Generation.ProjectNamespace);

            var boom = Assert.Throws<InvalidOperationException>(() => ProjectIntegrityValidator.Validate(Config()));
            Assert.Contains("TYPE NOT DEPLOYED", boom.Message);
            Assert.Contains("Type_That_Was_Never_Deployed", boom.Message);
        }

        [Fact]
        public void A_library_type_EAE_supplies_is_not_mistaken_for_a_missing_one()
        {
            // E_DELAY and the symlink shapes are resolved by EAE from a referenced library, never
            // deployed into the project. Demanding a .fbt for them would fail every correct project.
            WriteSyslay(Syslay, "E_DELAY", "IEC61499.Standard");
            var (_, types) = ProjectIntegrityValidator.Validate(Config());
            Assert.Equal(0, types);
        }

        [Fact]
        public void A_malformed_artefact_is_refused_rather_than_skipped()
        {
            File.WriteAllText(Syslay, "<SystemConfiguration><SubAppNetwork></SystemConfiguration>");
            var boom = Assert.Throws<InvalidOperationException>(() => ProjectIntegrityValidator.Validate(Config()));
            Assert.Contains("MALFORMED ARTEFACT", boom.Message);
        }
    }
}
