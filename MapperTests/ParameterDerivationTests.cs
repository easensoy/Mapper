using System.Collections.Generic;
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

        [Fact]
        public void WorkSensorFitted_TrueWhenAtWorkReferenced()
        {
            var process = ProcessWithCondition("Feeder/atWork");
            Assert.True(SystemInjector.ConditionReferences(process, "Feeder", "atWork"));
        }

        [Fact]
        public void HomeSensorFitted_TrueWhenAtHomeReferenced()
        {
            var process = ProcessWithCondition("Feeder/atHome");
            Assert.True(SystemInjector.ConditionReferences(process, "Feeder", "atHome"));
        }

        [Fact]
        public void WorkSensorFitted_FalseWhenNotReferenced()
        {
            var process = ProcessWithCondition("Feeder/atHome");
            Assert.False(SystemInjector.ConditionReferences(process, "Feeder", "atWork"));
        }

        [Fact]
        public void FaultTimeoutIsDoubleTravelTime()
        {
            int travelMs = 2000;
            string toWorkTime = SyslayBuilder.FormatTimeMs(travelMs);
            string faultTimeoutWork = SyslayBuilder.FormatTimeMs(travelMs * 2);
            Assert.Equal("T#2000ms", toWorkTime);
            Assert.Equal("T#4000ms", faultTimeoutWork);
        }

        [Fact]
        public void StringFormatWrapsInSingleQuotes()
        {
            Assert.Equal("'pusher'", SyslayBuilder.FormatString("pusher"));
        }

        [Fact]
        public void IntFormatIsBareNumber()
        {
            Assert.Equal("0", SyslayBuilder.FormatInt(0));
            Assert.Equal("10", SyslayBuilder.FormatInt(10));
        }

        [Fact]
        public void BoolFormatUppercase()
        {
            Assert.Equal("TRUE", SyslayBuilder.FormatBool(true));
            Assert.Equal("FALSE", SyslayBuilder.FormatBool(false));
        }
    }
}
