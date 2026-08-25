using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeGen.Hmi;
using Xunit;

namespace MapperTests
{
    // Does the faceplate agree with the interface it will actually be connected to?
    //
    // A .cnv.xml is a promise about a CAT; the deployed <Cat>_HMI.fbt is what the controller really
    // offers. EAE compiles a mismatch happily, because a TagName is just a string - so a clean build
    // proves nothing here. The binding simply never updates and the operator reads a field frozen at
    // its initial value, which is worse than a blank: a stale zero looks like data.
    //
    // The shipped Process1 faceplates were authored against Jyotsna's richer service interface, and
    // this Mapper deploys the older five-field one. These tests pin that gap honestly in both
    // directions: refused against the interface we actually deploy, accepted against a richer one.
    public class HmiContractAuditTests
    {
        private static string RepoPath(params string[] parts)
        {
            var beside = Path.Combine(new[] { AppContext.BaseDirectory }.Concat(parts).ToArray());
            if (File.Exists(beside) || Directory.Exists(beside)) return beside;
            return Path.GetFullPath(Path.Combine(
                new[] { AppContext.BaseDirectory, "..", "..", "..", ".." }.Concat(parts).ToArray()));
        }

        private static IReadOnlyList<HmiCatTemplate> Templates() =>
            HmiTemplateLibrary.Load(RepoPath("Template Library"));

        private static HmiSymbol Sym(string cat, string symbol) =>
            Templates().First(t => t.CatType == cat).Symbols.First(s => s.Name == symbol);

        private static HmiContractEvent Ev(string name, params string[] with) => new(name, with);

        // The interface this Mapper actually deploys today: five monitoring fields, no manual events.
        private static HmiContract DeployedProcessContract() =>
            new("Process1_Generic",
                Outputs: new[] { Ev("INITO", "QO", "STATUS") },
                Inputs: new[]
                {
                    Ev("INIT", "QI"),
                    Ev("stateUpdate", "ActuatorName", "StateValue"),
                    Ev("SCNF", "ThisStepText", "NextStepText", "PreviousStepText"),
                },
                InputVars: new[]
                {
                    "QI", "ThisStepText", "NextStepText", "PreviousStepText", "ActuatorName", "StateValue",
                },
                OutputVars: new[] { "QO", "STATUS" });

        // A synthetic RICHER interface, shaped like the reference: manual events and the fields the
        // faceplates were drawn against.
        private static HmiContract RicherProcessContract() =>
            new("Process1_Generic",
                Outputs: new[]
                {
                    Ev("INITO", "QO", "STATUS"),
                    Ev("MREQO", "ManualExecuteStep"),
                    Ev("NSREQO", "ManualNextStep"),
                },
                Inputs: new[]
                {
                    Ev("INIT", "QI"),
                    Ev("stateUpdate", "ActuatorName", "StateValue", "ModeCMD", "CurrentStep",
                       "CurrentStepType", "WaitSatisfied", "ManualStepReady", "ManualStepComplete",
                       "ProcessComplete", "ProcessName", "OperatorInstruction"),
                },
                InputVars: new[]
                {
                    "QI", "ThisStepText", "NextStepText", "PreviousStepText", "ActuatorName", "StateValue",
                    "ModeCMD", "CurrentStep", "CurrentStepType", "WaitSatisfied", "ManualStepReady",
                    "ManualStepComplete", "ProcessComplete", "ProcessName", "OperatorInstruction",
                },
                OutputVars: new[] { "QO", "STATUS", "ManualExecuteStep", "ManualNextStep" });

        // ---- 8. a bound field the deployed interface cannot serve is rejected -------------------

        [Fact]
        public void AFieldAbsentFromTheDeployedInterfaceIsRejected()
        {
            var dead = HmiBindingAudit.DeadInputs(
                "Process1_Generic", Sym("Process1_Generic", "sAutomatic"), DeployedProcessContract());

            // The monitoring faceplate binds these; today's service interface declares none of them.
            foreach (var tag in new[] { "ModeCMD", "ProcessComplete", "ProcessName" })
                Assert.Contains(dead, d => d.Tag == tag);

            // ...and the ONE field it does serve is left alone.
            Assert.DoesNotContain(dead, d => d.Tag == "ThisStepText");
            Assert.All(dead, d => Assert.Contains("declares no", d.Reason, StringComparison.Ordinal));
        }

        [Fact]
        public void AFieldTheDeployedInterfaceServesIsAccepted()
        {
            var dead = HmiBindingAudit.DeadInputs(
                "Process1_Generic", Sym("Process1_Generic", "sManual"), RicherProcessContract());

            // Against the richer interface every monitoring field it binds is legitimate.
            foreach (var tag in new[]
                     {
                         "ModeCMD", "ProcessName", "ProcessComplete", "OperatorInstruction",
                         "ManualStepReady", "ManualStepComplete", "ThisStepText", "ActuatorName",
                     })
                Assert.DoesNotContain(dead, d => d.Tag == tag);
        }

        // ---- 9. MREQO / NSREQO against the interface we actually deploy -------------------------

        [Fact]
        public void ManualEventsAreRejectedAgainstTheDeployedInterface()
        {
            var manual = Sym("Process1_Generic", "sManual");
            var dead = HmiBindingAudit.DeadOutputs("Process1_Generic", manual, DeployedProcessContract());

            Assert.Contains(dead, d => d.Tag == "MREQO");
            Assert.Contains(dead, d => d.Tag == "NSREQO");
            Assert.All(dead.Where(d => d.Tag is "MREQO" or "NSREQO"),
                       d => Assert.Contains("no output event", d.Reason, StringComparison.Ordinal));
        }

        // ---- 10. ...and against a richer one ----------------------------------------------------

        [Fact]
        public void ManualEventsAreAcceptedAgainstARicherInterface()
        {
            var manual = Sym("Process1_Generic", "sManual");
            var dead = HmiBindingAudit.DeadOutputs("Process1_Generic", manual, RicherProcessContract());

            Assert.DoesNotContain(dead, d => d.Tag == "MREQO");
            Assert.DoesNotContain(dead, d => d.Tag == "NSREQO");
        }

        // The audit must be driven by the DEPLOYED interface, not by the faceplate's own promise -
        // otherwise it would always agree with itself and never catch anything.
        [Fact]
        public void TheAuditFollowsTheDeployedInterfaceNotTheFaceplateContract()
        {
            var manual = Sym("Process1_Generic", "sManual");

            var againstOld = HmiBindingAudit.DeadInputs("Process1_Generic", manual, DeployedProcessContract());
            var againstNew = HmiBindingAudit.DeadInputs("Process1_Generic", manual, RicherProcessContract());

            Assert.NotEmpty(againstOld);
            Assert.Empty(againstNew);
        }

        // A CAT with no deployed companion is not audited: absence of a contract is a different
        // condition, already reported as NoContract, and guessing here would flood the report.
        [Fact]
        public void AMissingContractProducesNoDeadBindings()
        {
            var manual = Sym("Process1_Generic", "sManual");
            Assert.Empty(HmiBindingAudit.DeadInputs("Process1_Generic", manual, HmiContract.None));
            Assert.Empty(HmiBindingAudit.DeadOutputs("Process1_Generic", manual, HmiContract.None));
        }

        // ---- 7. processes appear in the capability evidence -------------------------------------

        // The evidence file must describe the PROCESSES too: a recipe with no allocation, no row
        // count and no capability verdict is not reviewable.
        [Fact]
        public void ProcessesAppearInTheCapabilityEvidenceWithAllocationAndRows()
        {
            var dir = Path.Combine(Path.GetTempPath(), "hmi-evidence-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var process = new HmiProcess(
                    ComponentId: "C-line", InstanceName: "Line_Process", DisplayName: "Line Process",
                    Controller: CodeGen.Translation.PlcAssignment.M262, Resource: "RES0",
                    TagName: "TAG1", CatType: "Process1_Generic", Slot: 4,
                    Owned: new[] { "Pusher" },
                    Capabilities: new[]
                    {
                        new HmiCapability(HmiCapabilityPurpose.ManualStep, "MREQO", Array.Empty<string>(),
                                          false, HmiUnavailableReason.NoOutputEvent, "no MREQO on the deployed CAT"),
                    },
                    Rows: new[]
                    {
                        new HmiRecipeRow(0, 1, HmiRowKind.Command, 1, "Run", "pusher", "Pusher", "Pusher",
                                         2, "Work", 0, null, null, 0, null, false, 1, "Command Pusher to Work", true),
                    },
                    Observed: new[] { "Gate" },
                    Phases: new[] { new HmiStateName(1, "Run") },
                    Ring: "line");

                var plant = new HmiPlant("T", Array.Empty<HmiStation>(), new[] { process },
                                         Array.Empty<HmiComponent>(), Array.Empty<string>());
                var plan = new HmiPlan(Array.Empty<HmiScreen>(), Array.Empty<HmiCatTemplate>(),
                                       Array.Empty<string>(), Array.Empty<HmiSelectedSymbol>(),
                                       Array.Empty<HmiActionVerdict>(), Array.Empty<HmiDeadBinding>());

                var file = HmiCapabilityReportEmitter.Emit(dir, plant, plan);
                var xml = System.Xml.Linq.XDocument.Load(Path.Combine(dir, file));
                var p = xml.Descendants("Process").Single();

                Assert.Equal("Line_Process", (string?)p.Attribute("name"));
                Assert.Equal("TAG1", (string?)p.Attribute("tag"));
                Assert.Equal("Process1_Generic", (string?)p.Attribute("catType"));
                Assert.Equal("M262", (string?)p.Attribute("controller"));
                Assert.Equal("4", (string?)p.Attribute("slot"));
                Assert.Equal("line", (string?)p.Attribute("ring"));
                Assert.Equal("1", (string?)p.Attribute("rows"));

                Assert.Contains(p.Descendants("Capability"),
                                c => (string?)c.Attribute("purpose") == "ManualStep" &&
                                     (string?)c.Attribute("supported") == "false");

                var step = p.Descendants("Step").Single();
                Assert.Equal("Command", (string?)step.Attribute("kind"));
                Assert.Equal("Command Pusher to Work", step.Value);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }
    }
}
