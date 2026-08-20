using System;
using System.IO;
using System.Linq;
using CodeGen.Hmi;
using Xunit;

namespace MapperTests
{
    // The capability layer is what keeps the generated panel honest: a control is offered ONLY when
    // the deployed artefacts prove the controller acts on it. These tests pin that contract against
    // the shipped hmi.yml and against the production resolver - not a re-implementation of it.
    //
    // They live in their own file deliberately. The definition tests cover schema and validation; a
    // capability regression is a different failure with different consequences, and mixing them made
    // the capability assertions easy to lose in an edit.
    public class HmiCapabilityTests
    {
        private static string ShippedYaml()
        {
            var beside = Path.Combine(AppContext.BaseDirectory, "Config", "hmi.yml");
            if (File.Exists(beside)) return File.ReadAllText(beside);

            var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "CodeGen", "CodeGen", "Config", "hmi.yml"));
            Assert.True(File.Exists(repo), $"hmi.yml not found beside the test binary or at {repo}");
            return File.ReadAllText(repo);
        }

        private static HmiDefinition Def() => HmiDefinitionLoader.Parse(ShippedYaml());

        // hmi.yml is loaded ONCE and carries the deployment half as a nested record. A second file,
        // a second cache or a second read is exactly what let the two halves drift apart before.
        [Fact]
        public void OneDefinitionCarriesBothPresentationAndDeviceConfiguration()
        {
            var def = Def();

            Assert.NotNull(def.Device);
            Assert.True(Guid.TryParse(def.Device.DeviceId, out _));
            Assert.NotEmpty(def.Device.Artefacts);
            Assert.NotEmpty(def.Capabilities);
        }

        // No command may ever be offered on port existence alone.
        [Fact]
        public void EveryCommandDeclaresControllerProof()
        {
            foreach (var rule in Def().Capabilities)
                Assert.True(rule.Consumption.Tokens.Count > 0,
                    $"{rule.Purpose} declares no consumption tokens, so it would be enabled without proof");
        }

        [Fact]
        public void AConsumptionClauseWithNoTokensIsRejected()
        {
            var yaml = ShippedYaml().Replace("        tokens: [toHomeSetup]", "        tokens: []");
            Assert.Contains("consumption.tokens",
                Assert.Throws<HmiConfigException>(() => HmiDefinitionLoader.Parse(yaml)).Message);
        }

        // STOP must be judged against the component that RUNS the recipe. The station's own cycle
        // state machine consumes CycleType, so an unscoped proof would enable a STOP that cannot
        // halt a running recipe.
        [Fact]
        public void RunningStopIsProvenAgainstTheRecipeEngineSpecifically()
        {
            var stop = Def().Capabilities.Single(c => c.Purpose == HmiCapabilityPurpose.CycleStop);

            Assert.False(string.IsNullOrWhiteSpace(stop.Consumption.InType));
            Assert.Contains("CycleType", stop.Consumption.Tokens);
        }

        // Cycle SELECTION and running STOP are separate capabilities with different proofs - the
        // whole point is that one can be enabled while the other is not.
        [Fact]
        public void CycleSelectionAndRunningStopAreSeparateCapabilities()
        {
            var def = Def();
            var control = def.Capabilities.Single(c => c.Purpose == HmiCapabilityPurpose.CycleControl);
            var stop = def.Capabilities.Single(c => c.Purpose == HmiCapabilityPurpose.CycleStop);

            Assert.NotEqual(control.Consumption.InType, stop.Consumption.InType);
        }

        // Two- and three-position jogs are told apart by the CONTRACT SIGNATURE, never by a CAT name
        // or a state count.
        [Fact]
        public void SetupJogDeclaresBothPositionShapes()
        {
            var jog = Def().Capabilities.Single(c => c.Purpose == HmiCapabilityPurpose.SetupJog);

            Assert.Contains(jog.OutputData, v => v.Count == 2 && v.Contains("toWork"));
            Assert.Contains(jog.OutputData, v => v.Count == 3 && v.Contains("toWork1") && v.Contains("toWork2"));
            Assert.True(jog.NeedsModeChain, "a jog that cannot be reached must not be offered");
        }

        // ---- the resolver itself, against synthetic contracts ------------------------------------

        private static HmiContract Contract(
            string[] outEvents, string[] outData, string[] feedback) =>
            new("Test",
                outEvents.Select(e => new HmiContractEvent(e, outData)).ToArray(),
                new[] { new HmiContractEvent("input_event", feedback) },
                feedback,
                outData);

        private static HmiCapability Resolve(
            HmiCapabilityPurpose purpose, HmiContract contract, HmiEccIndex ecc, HmiModeReach reach) =>
            HmiCapabilityResolver.Resolve(contract, "Inst", reach, ecc, Def())
                .Single(c => c.Purpose == purpose);

        private static HmiEccIndex Ecc(string conditions)
        {
            var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(dir, "IEC61499"));
            File.WriteAllText(Path.Combine(dir, "IEC61499", "ProcessRuntime_Generic_v1.fbt"),
                $"<FBType Name=\"ProcessRuntime_Generic_v1\"><BasicFB><ECC>{conditions}</ECC></BasicFB></FBType>");
            try { return HmiDeployedTypes.Read(dir).Ecc; }
            finally { Directory.Delete(dir, true); }
        }

        private static HmiModeReach Reach()
        {
            var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".syslay");
            File.WriteAllText(path,
                "<SubAppNetwork><AdapterConnections>" +
                "<Connection Source=\"F.StationHMIAdptrOUT\" Destination=\"Inst.StationHMIAdptrIN\" />" +
                "</AdapterConnections></SubAppNetwork>");
            try { return HmiModeReach.From(HmiSyslay.Load(path), _ => true, System.Array.Empty<string>()); }
            finally { File.Delete(path); }
        }

        // A contract with no output event at all is monitoring-only, and says so precisely.
        [Fact]
        public void AReadOnlyContractYieldsNoCommandsAndAnExactReason()
        {
            var cap = Resolve(HmiCapabilityPurpose.ManualStep,
                Contract(Array.Empty<string>(), Array.Empty<string>(), new[] { "current_state_to_process" }),
                Ecc("<ECTransition Condition=\"Mode = 2 AND MREQ\" />"), Reach());

            Assert.False(cap.Supported);
            Assert.Equal(HmiUnavailableReason.NoOutputEvent, cap.Reason);
            Assert.Contains("MREQO", cap.Detail);
        }

        // The decisive case: the contract is complete, but the engine ignores the port.
        [Fact]
        public void ADeclaredPortTheEccNeverReadsIsDisabledWithTheExactReason()
        {
            var cap = Resolve(HmiCapabilityPurpose.CycleStop,
                Contract(new[] { "CTCNF" }, new[] { "CycleType" }, new[] { "LL_CycleType" }),
                Ecc("<ECTransition Condition=\"CurrentStepType = 1\" />"), Reach());

            Assert.False(cap.Supported);
            Assert.Equal(HmiUnavailableReason.NotConsumed, cap.Reason);
            Assert.Contains("CycleType", cap.Detail);
            Assert.Contains("ProcessRuntime_Generic_v1", cap.Detail);
        }

        // Same contract, an engine that DOES read it - the capability enables itself with no code
        // change. This is what makes a future backend fix take effect automatically.
        [Fact]
        public void TheSameContractEnablesItselfOnceTheEccConsumesTheValue()
        {
            var cap = Resolve(HmiCapabilityPurpose.CycleStop,
                Contract(new[] { "CTCNF" }, new[] { "CycleType" }, new[] { "LL_CycleType" }),
                Ecc("<ECTransition Condition=\"CycleType = 0\" />"), Reach());

            Assert.True(cap.Supported);
            Assert.Equal(HmiUnavailableReason.None, cap.Reason);
            Assert.Empty(cap.Detail);
        }

        // A whole-identifier match: 'Mode' must not be satisfied by 'CurrentStepType'.
        [Fact]
        public void ConsumptionMatchesWholeIdentifiersOnly()
        {
            var ecc = Ecc("<ECTransition Condition=\"CurrentStepType = 2\" />");

            Assert.False(ecc.Consumes("Mode", "ProcessRuntime_Generic_v1"));
            Assert.False(ecc.Consumes("Type", "ProcessRuntime_Generic_v1"));
            Assert.True(ecc.Consumes("CurrentStepType", "ProcessRuntime_Generic_v1"));
        }

        // An unknown capability is disabled, never assumed.
        [Fact]
        public void AContractThatDoesNotExistSupportsNothing()
        {
            var caps = HmiCapabilityResolver.Resolve(
                HmiContract.None, "Inst", Reach(), Ecc(string.Empty), Def());

            Assert.All(caps, c => Assert.False(c.Supported));
            Assert.Contains(caps, c => c.Reason == HmiUnavailableReason.NoContract);
        }
    }
}
