using System;
using System.IO;
using System.Linq;
using CodeGen.Hmi;
using Xunit;

namespace MapperTests
{
    // hmi.yml is the single declarative source for HMI presentation, protocol and capability data.
    // These tests pin the contract that makes that safe: it must load, and it must REFUSE anything
    // ambiguous rather than fall back to a hidden C# default.
    public class HmiDefinitionTests
    {
        private static string ShippedYaml()
        {
            // The file that ships beside CodeGen.dll is the one generation actually reads.
            var beside = Path.Combine(AppContext.BaseDirectory, "Config", "hmi.yml");
            if (File.Exists(beside)) return File.ReadAllText(beside);

            var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "CodeGen", "CodeGen", "Config", "hmi.yml"));
            Assert.True(File.Exists(repo), $"hmi.yml not found beside the test binary or at {repo}");
            return File.ReadAllText(repo);
        }

        [Fact]
        public void ShippedDefinitionLoadsAndIsComplete()
        {
            var def = HmiDefinitionLoader.Parse(ShippedYaml());

            Assert.Equal(HmiDefinition.SupportedSchemaVersion, def.SchemaVersion);
            Assert.True(def.ReadOnly, "the shipped policy must stay monitoring-only");
            Assert.True(def.Geometry.CanvasWidth > 0 && def.Geometry.WorkHeight > 0);
            Assert.True(def.Geometry.WorkHeight < def.Geometry.CanvasHeight,
                        "the work area must exclude the runtime navigation bar");
            Assert.NotEmpty(def.ModeLabels);
            Assert.NotEmpty(def.CycleLabels);
            Assert.NotEmpty(def.StatesProfiles);
            Assert.NotEmpty(def.Capabilities);
            Assert.NotEmpty(def.InterlockFeedback);
            Assert.False(string.IsNullOrWhiteSpace(def.Deployment.GeneratedBanner));
        }

        [Fact]
        public void UnknownKeyIsRejected()
        {
            var yaml = ShippedYaml() + "\nthisKeyDoesNotExist: 1\n";
            var ex = Assert.ThrowsAny<Exception>(() => HmiDefinitionLoader.Parse(yaml));
            Assert.Contains("hmi.yml", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void WrongSchemaVersionIsRejected()
        {
            var yaml = ShippedYaml().Replace("schemaVersion: 1", "schemaVersion: 99");
            var ex = Assert.Throws<HmiConfigException>(() => HmiDefinitionLoader.Parse(yaml));
            Assert.Contains("schemaVersion", ex.Message);
        }

        [Fact]
        public void MissingRequiredValueIsRejectedWithItsPath()
        {
            // No newline in the needle: the file is LF in git and CRLF after checkout, and a "\n"
            // here silently matches nothing on Windows, leaving the key present and the test green
            // for the wrong reason. Removing the text alone leaves a blank line, which YAML ignores.
            var yaml = ShippedYaml().Replace("  canvasWidth: 1024", string.Empty);
            var ex = Assert.Throws<HmiConfigException>(() => HmiDefinitionLoader.Parse(yaml));
            Assert.Contains("geometry.canvasWidth", ex.Message);
        }

        [Fact]
        public void InvalidDimensionIsRejected()
        {
            var yaml = ShippedYaml().Replace("  canvasWidth: 1024", "  canvasWidth: 0");
            var ex = Assert.Throws<HmiConfigException>(() => HmiDefinitionLoader.Parse(yaml));
            Assert.Contains("greater than zero", ex.Message);
        }

        [Fact]
        public void DuplicateStateProfileIdIsRejected()
        {
            var yaml = ShippedYaml().Replace("    - id: booleanSensor", "    - id: twoPosition");
            var ex = Assert.Throws<HmiConfigException>(() => HmiDefinitionLoader.Parse(yaml));
            Assert.Contains("duplicate state profile id", ex.Message);
        }

        [Fact]
        public void ProfileSelectionPrefersTheMoreSpecificSignature()
        {
            var def = HmiDefinitionLoader.Parse(ShippedYaml());

            // A three-position contract satisfies the two-position signature too; the wider one wins.
            var threePos = Contract(inputs: new[] { "atHome", "atWork", "atWork1", "atWork2" });
            var chosen = def.ProfileFor(threePos, out var ambiguity);
            Assert.Null(ambiguity);
            Assert.NotNull(chosen);
            Assert.Equal(7, chosen!.Labels.Count);

            var twoPos = Contract(inputs: new[] { "atHome", "atWork" });
            Assert.Equal(5, def.ProfileFor(twoPos, out _)!.Labels.Count);
        }

        [Fact]
        public void NoMatchingProfileReturnsNullRatherThanGuessing()
        {
            var def = HmiDefinitionLoader.Parse(ShippedYaml());
            Assert.Null(def.ProfileFor(Contract(inputs: new[] { "somethingElse" }), out var ambiguity));
            Assert.Null(ambiguity);
        }

        private static HmiContract Contract(string[] inputs) =>
            new("Test",
                Array.Empty<HmiContractEvent>(),
                new[] { new HmiContractEvent("input_event", inputs) },
                inputs,
                Array.Empty<string>());
    }
}
