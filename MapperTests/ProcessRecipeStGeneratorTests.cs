using System.IO;
using System.Linq;
using CodeGen.IO;
using CodeGen.Translation;
using Xunit;

namespace MapperTests
{
    public class ProcessRecipeStGeneratorTests
    {
        static string FixturePath() =>
            Path.Combine(AppContext.BaseDirectory, "TestData", "Feed_Station_Fixture.xml");

        [Fact]
        public void BuildComponentMapAssignsSensorsThenActuators()
        {
            var reader = new SystemXmlReader();
            var components = reader.ReadAllComponents(FixturePath());
            var process = components.First(c => c.Type == "Process");
            var contents = new StationGroupingService().GroupStationContents(process, components);

            var map = ProcessRecipeStGenerator.BuildComponentMap(contents);

            // Sensors come first (local IDs 0..N-1), then actuators.
            for (int i = 0; i < contents.Sensors.Count; i++)
                Assert.Equal(i, map.ComponentNameToLocalId[contents.Sensors[i].Name]);
            for (int i = 0; i < contents.Actuators.Count; i++)
                Assert.Equal(contents.Sensors.Count + i, map.ComponentNameToLocalId[contents.Actuators[i].Name]);
        }

        [Fact]
        public void GeneratesPusherIdAssignmentForFeeder()
        {
            var reader = new SystemXmlReader();
            var components = reader.ReadAllComponents(FixturePath());
            var process = components.First(c => c.Type == "Process");
            var contents = new StationGroupingService().GroupStationContents(process, components);
            var map = ProcessRecipeStGenerator.BuildComponentMap(contents);
            int expected = map.ComponentNameToLocalId["Feeder"];

            var st = ProcessRecipeStGenerator.GenerateInitializeInitSt(process, contents, components);

            Assert.Contains($"PusherID := {expected};", st);
        }

        [Fact]
        public void StateZeroBootstrapAssignmentsPresent()
        {
            var reader = new SystemXmlReader();
            var components = reader.ReadAllComponents(FixturePath());
            var process = components.First(c => c.Type == "Process");
            var contents = new StationGroupingService().GroupStationContents(process, components);

            var st = ProcessRecipeStGenerator.GenerateInitializeInitSt(process, contents, components);

            Assert.Contains("CurrentStep := 0;", st);
            Assert.Contains("CurrentStepType := 0;", st);
            Assert.Contains("WaitSatisfied := FALSE;", st);
        }

        [Fact]
        public void EmitsOneStepTypePerState()
        {
            var reader = new SystemXmlReader();
            var components = reader.ReadAllComponents(FixturePath());
            var process = components.First(c => c.Type == "Process");
            var contents = new StationGroupingService().GroupStationContents(process, components);

            var st = ProcessRecipeStGenerator.GenerateInitializeInitSt(process, contents, components);

            for (int i = 0; i < process.States.Count; i++)
                Assert.Contains($"StepType[{i}] :=", st);
        }

        [Fact]
        public void EndsWithStepTextAssignments()
        {
            var reader = new SystemXmlReader();
            var components = reader.ReadAllComponents(FixturePath());
            var process = components.First(c => c.Type == "Process");
            var contents = new StationGroupingService().GroupStationContents(process, components);

            var st = ProcessRecipeStGenerator.GenerateInitializeInitSt(process, contents, components);

            Assert.Contains("PreviousStepText := '';", st);
            Assert.Contains("ThisStepText :=", st);
            Assert.Contains("NextStepText :=", st);
        }
    }
}
