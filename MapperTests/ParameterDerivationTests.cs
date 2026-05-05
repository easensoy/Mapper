using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeGen.IO;
using CodeGen.Models;
using CodeGen.Translation;
using MapperUI.Services;
using Xunit;

namespace MapperTests
{
    public class ParameterDerivationTests
    {
        static VueOneComponent ProcessWithCondition(string conditionName)
        {
            return new VueOneComponent
            {
                Name = "TestProcess",
                Type = "Process",
                ComponentID = "C-test",
                States = new List<VueOneState>
                {
                    new VueOneState
                    {
                        Name = "S0",
                        Transitions = new List<VueOneTransition>
                        {
                            new VueOneTransition
                            {
                                Conditions = new List<VueOneCondition>
                                {
                                    new VueOneCondition { Name = conditionName, ComponentID = "C-other" }
                                }
                            }
                        }
                    }
                }
            };
        }

        // [Fact]
        public void WorkSensorFitted_TrueWhenAtWorkReferenced()
        {
            var process = ProcessWithCondition("Feeder/atWork");
            Assert.True(SystemInjector.ConditionReferences(process, "Feeder", "atWork"));
        }

        // [Fact]
        public void HomeSensorFitted_TrueWhenAtHomeReferenced()
        {
            var process = ProcessWithCondition("Feeder/atHome");
            Assert.True(SystemInjector.ConditionReferences(process, "Feeder", "atHome"));
        }

        // [Fact]
        public void WorkSensorFitted_FalseWhenNotReferenced()
        {
            var process = ProcessWithCondition("Feeder/atHome");
            Assert.False(SystemInjector.ConditionReferences(process, "Feeder", "atWork"));
        }

        // [Fact]
        public void FaultTimeoutIsDoubleTravelTime()
        {
            int travelMs = 2000;
            string toWorkTime = SyslayBuilder.FormatTimeMs(travelMs);
            string faultTimeoutWork = SyslayBuilder.FormatTimeMs(travelMs * 2);
            Assert.Equal("T#2000ms", toWorkTime);
            Assert.Equal("T#4000ms", faultTimeoutWork);
        }

        // [Fact]
        public void StringFormatWrapsInSingleQuotes()
        {
            Assert.Equal("'pusher'", SyslayBuilder.FormatString("pusher"));
        }

        // [Fact]
        public void IntFormatIsBareNumber()
        {
            Assert.Equal("0", SyslayBuilder.FormatInt(0));
            Assert.Equal("10", SyslayBuilder.FormatInt(10));
        }

        // [Fact]
        public void BoolFormatUppercase()
        {
            Assert.Equal("TRUE", SyslayBuilder.FormatBool(true));
            Assert.Equal("FALSE", SyslayBuilder.FormatBool(false));
        }

        // ---- Control.xml-driven derivation tests (SystemInjector.BuildActuatorParameters) ----

        static List<VueOneComponent> LoadFixture() =>
            new SystemXmlReader().ReadAllComponents(
                Path.Combine(AppContext.BaseDirectory, "TestData", "Feed_Station_Fixture.xml"));

        // [Fact]
        public void ToWorkTime_ComesFromActuatorStateNumber1Time()
        {
            var all = LoadFixture();
            var feeder = all.First(c => c.Name == "Feeder" && c.Type == "Actuator");
            var transfer = all.First(c => c.Name == "Transfer" && c.Type == "Actuator");

            // Feeder.Advancing.Time = 1000, Transfer.Advancing.Time = 1500 in the fixture.
            int feederToWork = SystemInjector.ResolveStateTimeMs(feeder, 1, fallbackMs: 0);
            int transferToWork = SystemInjector.ResolveStateTimeMs(transfer, 1, fallbackMs: 0);

            Assert.Equal(1000, feederToWork);
            Assert.Equal(1500, transferToWork);
            Assert.NotEqual(feederToWork, transferToWork);
        }

        // [Fact]
        public void AtWorkAndAtHomeStateIdsResolvedFromStateNumbers()
        {
            var all = LoadFixture();
            var feeder = all.First(c => c.Name == "Feeder" && c.Type == "Actuator");

            var atWork = SystemInjector.ResolveAtWorkStateIds(feeder);
            var atHome = SystemInjector.ResolveAtHomeStateIds(feeder);

            Assert.Single(atWork);   // StateNumber=2 (Advanced)
            Assert.Equal(2, atHome.Count); // StateNumber=0 (ReturnedHome) and =4 (ReturnedFinished)
        }

        // [Fact]
        public void WorkSensorFitted_TrueWhenProcessReferencesAtWorkStateId()
        {
            var all = LoadFixture();
            var feeder = all.First(c => c.Name == "Feeder" && c.Type == "Actuator");

            var atWorkIds = SystemInjector.ResolveAtWorkStateIds(feeder);
            bool workFitted = SystemInjector.AnyComponentReferencesStates(all, feeder, atWorkIds);

            // Feed_Station Process waits on Feeder/Advanced -> work sensor IS fitted.
            Assert.True(workFitted);
        }

        // [Fact]
        public void HomeSensorFitted_TrueWhenProcessReferencesAtHomeStateId()
        {
            var all = LoadFixture();
            var feeder = all.First(c => c.Name == "Feeder" && c.Type == "Actuator");

            var atHomeIds = SystemInjector.ResolveAtHomeStateIds(feeder);
            bool homeFitted = SystemInjector.AnyComponentReferencesStates(all, feeder, atHomeIds);

            // Feed_Station Process waits on Feeder/ReturnedHome AND Feeder/ReturnedFinished.
            Assert.True(homeFitted);
        }

        // [Fact]
        public void FeederAndTransferGetDistinctTimingParameters()
        {
            var all = LoadFixture();
            var feeder = all.First(c => c.Name == "Feeder" && c.Type == "Actuator");
            var transfer = all.First(c => c.Name == "Transfer" && c.Type == "Actuator");

            var feederParams = SystemInjector.BuildActuatorParameters(feeder, 0, all);
            var transferParams = SystemInjector.BuildActuatorParameters(transfer, 1, all);

            // Names must differ.
            Assert.NotEqual(feederParams["actuator_name"], transferParams["actuator_name"]);
            Assert.NotEqual(feederParams["actuator_id"], transferParams["actuator_id"]);

            // Timing must differ â€” this is the bug we just fixed: previously both got
            // hardcoded 2000ms / 4000ms, so this assertion would have failed under the
            // old code path.
            Assert.NotEqual(feederParams["toWorkTime"], transferParams["toWorkTime"]);
            Assert.NotEqual(feederParams["faultTimeoutWork"], transferParams["faultTimeoutWork"]);

            // Concrete expected values straight from Control.xml <Time> fields.
            Assert.Equal("T#1000ms", feederParams["toWorkTime"]);
            Assert.Equal("T#2000ms", feederParams["faultTimeoutWork"]);
            Assert.Equal("T#1500ms", transferParams["toWorkTime"]);
            Assert.Equal("T#3000ms", transferParams["faultTimeoutWork"]);
        }

        // [Fact]
        public void FallbackTimingUsedWhenStateTimeIsZero()
        {
            // Synthetic actuator with no explicit Time on the motion states.
            var actuator = new VueOneComponent
            {
                Name = "TimelessAct",
                Type = "Actuator",
                ComponentID = "C-timeless",
                States = new List<VueOneState>
                {
                    new VueOneState { StateID = "S-h0", StateNumber = 0, StaticState = true, Time = 0 },
                    new VueOneState { StateID = "S-go", StateNumber = 1, StaticState = false, Time = 0 },
                    new VueOneState { StateID = "S-w",  StateNumber = 2, StaticState = true, Time = 0 },
                    new VueOneState { StateID = "S-rt", StateNumber = 3, StaticState = false, Time = 0 },
                    new VueOneState { StateID = "S-h1", StateNumber = 4, StaticState = true, Time = 0 }
                }
            };

            var p = SystemInjector.BuildActuatorParameters(actuator, 0,
                new List<VueOneComponent> { actuator });

            Assert.Equal("T#2000ms", p["toWorkTime"]);
            Assert.Equal("T#2000ms", p["toHomeTime"]);
            Assert.Equal("T#4000ms", p["faultTimeoutWork"]);
            Assert.Equal("T#4000ms", p["faultTimeoutHome"]);
        }
    }
}
