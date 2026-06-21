using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using CodeGen.Devices.M262;
using CodeGen.Devices.M580;
using CodeGen.Devices.Shared;
using CodeGen.Services;
using Xunit;

namespace MapperTests
{
    /// <summary>
    /// Phase 1: verifies that the deploy-time FBT patcher correctly promotes the recipe
    /// arrays from InternalVars to InputVars on ProcessRuntime_Generic_v1.fbt and adds
    /// matching InputVars + DataConnections on Process1_Generic.fbt. Operates against
    /// minimal synthetic FBT XML so the tests do not depend on the Demonstrator working tree.
    /// </summary>
    public class RecipeInputVarPatchTests
    {
        const string EngineFbtSeed = """
<?xml version="1.0" encoding="utf-8"?>
<FBType xmlns="https://www.se.com/LibraryElements" Name="ProcessRuntime_Generic_v1" Namespace="Main">
  <InterfaceList>
    <EventInputs>
      <Event Name="INIT">
        <With Var="Mode" />
      </Event>
    </EventInputs>
    <InputVars>
      <VarDeclaration Name="Mode" Type="INT" />
    </InputVars>
  </InterfaceList>
  <BasicFB>
    <InternalVars>
      <VarDeclaration Name="CurrentStep" Type="INT" />
      <VarDeclaration Name="StepType" Type="INT" ArraySize="10" />
      <VarDeclaration Name="CmdTargetName" Type="STRING[15]" ArraySize="10" />
      <VarDeclaration Name="CmdStateArr" Type="INT" ArraySize="10" />
      <VarDeclaration Name="Wait1Id" Type="INT" ArraySize="10" />
      <VarDeclaration Name="Wait1State" Type="INT" ArraySize="10" />
      <VarDeclaration Name="NextStep" Type="INT" ArraySize="10" />
    </InternalVars>
    <Algorithm Name="initializeinit">
      <ST><![CDATA[StepType[0] := 1;
CmdTargetName[0] := 'Pusher';]]></ST>
    </Algorithm>
  </BasicFB>
</FBType>
""";

        const string CompositeFbtSeed = """
<?xml version="1.0" encoding="utf-8"?>
<FBType xmlns="https://www.se.com/LibraryElements" Name="Process1_Generic" Namespace="Main">
  <InterfaceList>
    <EventInputs>
      <Event Name="INIT">
        <With Var="process_name" />
        <With Var="process_id" />
      </Event>
    </EventInputs>
    <InputVars>
      <VarDeclaration Name="process_name" Type="STRING[15]" />
      <VarDeclaration Name="process_id" Type="INT" />
    </InputVars>
  </InterfaceList>
  <FBNetwork>
    <FB ID="22" Name="ProcessEngine" Type="ProcessRuntime_Generic_v1" Namespace="Main" />
    <Input Name="process_name" Type="Data" />
    <DataConnections>
      <Connection Source="process_name" Destination="ProcessEngine.process_name" />
    </DataConnections>
  </FBNetwork>
</FBType>
""";

        static readonly XNamespace LibElNs = "https://www.se.com/LibraryElements";

        static (string root, string enginePath, string compositePath) StageEaeProject()
        {
            var root = Path.Combine(Path.GetTempPath(), "MapperTests_PatchEAE_" + Path.GetRandomFileName());
            var iec = Path.Combine(root, "IEC61499");
            var compositeDir = Path.Combine(iec, "Process1_Generic");
            Directory.CreateDirectory(compositeDir);

            var enginePath = Path.Combine(iec, "ProcessRuntime_Generic_v1.fbt");
            var compositePath = Path.Combine(compositeDir, "Process1_Generic.fbt");
            File.WriteAllText(enginePath, EngineFbtSeed);
            File.WriteAllText(compositePath, CompositeFbtSeed);
            return (root, enginePath, compositePath);
        }

        static void InvokeRecipePatcher(string eaeRoot, DeployResult result)
        {
            var method = typeof(TemplateLibraryDeployer).GetMethod(
                "PatchProcessFbsForRecipeAsInputVars",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);
            method!.Invoke(null, new object[] { eaeRoot, result });
        }

        // ---------------------------------------------------------------
        // Engine FBT — ProcessRuntime_Generic_v1
        // ---------------------------------------------------------------

        [Fact]
        public void EngineFbtGetsSixRecipeArraysAsInputVars()
        {
            var (root, enginePath, _) = StageEaeProject();
            InvokeRecipePatcher(root, new DeployResult());

            var doc = XDocument.Load(enginePath);
            var inputVarNames = doc.Descendants(LibElNs + "InputVars")
                .Single()
                .Elements(LibElNs + "VarDeclaration")
                .Select(v => (string?)v.Attribute("Name"))
                .ToList();

            foreach (var name in new[] { "StepType", "CmdTargetName", "CmdStateArr", "Wait1Id", "Wait1State", "NextStep" })
                Assert.Contains(name, inputVarNames);
        }

        [Fact]
        public void EngineFbtRemovesRecipeArraysFromInternalVars()
        {
            var (root, enginePath, _) = StageEaeProject();
            InvokeRecipePatcher(root, new DeployResult());

            var doc = XDocument.Load(enginePath);
            var internalVarNames = doc.Descendants(LibElNs + "InternalVars")
                .Single()
                .Elements(LibElNs + "VarDeclaration")
                .Select(v => (string?)v.Attribute("Name"))
                .ToList();

            foreach (var name in new[] { "StepType", "CmdTargetName", "CmdStateArr", "Wait1Id", "Wait1State", "NextStep" })
                Assert.DoesNotContain(name, internalVarNames);
        }

        [Fact]
        public void EngineFbtInitEventGetsSixWithClauses()
        {
            var (root, enginePath, _) = StageEaeProject();
            InvokeRecipePatcher(root, new DeployResult());

            var doc = XDocument.Load(enginePath);
            var initEvent = doc.Descendants(LibElNs + "Event")
                .Single(e => (string?)e.Attribute("Name") == "INIT");
            var withVars = initEvent.Elements(LibElNs + "With")
                .Select(w => (string?)w.Attribute("Var"))
                .ToList();

            foreach (var name in new[] { "StepType", "CmdTargetName", "CmdStateArr", "Wait1Id", "Wait1State", "NextStep" })
                Assert.Contains(name, withVars);
        }

        [Fact]
        public void EngineFbtInitializeAlgorithmStrippedOfRecipePopulation()
        {
            var (root, enginePath, _) = StageEaeProject();
            InvokeRecipePatcher(root, new DeployResult());

            var doc = XDocument.Load(enginePath);
            var algo = doc.Descendants(LibElNs + "Algorithm")
                .Single(a => (string?)a.Attribute("Name") == "initializeinit");
            var st = algo.Descendants(LibElNs + "ST").Single();
            var stText = string.Concat(st.Nodes().OfType<XCData>().Select(c => c.Value));

            Assert.DoesNotContain("StepType[0] := 1", stText);
            Assert.DoesNotContain("CmdTargetName[0] := 'Pusher'", stText);
            Assert.Contains("CurrentStep := 0", stText);
            Assert.Contains("WaitSatisfied := FALSE", stText);
        }

        // ---------------------------------------------------------------
        // Composite FBT — Process1_Generic
        // ---------------------------------------------------------------

        [Fact]
        public void CompositeFbtGetsSixArrayInputVars()
        {
            var (root, _, compositePath) = StageEaeProject();
            InvokeRecipePatcher(root, new DeployResult());

            var doc = XDocument.Load(compositePath);
            var inputVarNames = doc.Descendants(LibElNs + "InputVars")
                .Single()
                .Elements(LibElNs + "VarDeclaration")
                .Select(v => (string?)v.Attribute("Name"))
                .ToList();

            foreach (var name in new[] { "StepType", "CmdTargetName", "CmdStateArr", "Wait1Id", "Wait1State", "NextStep" })
                Assert.Contains(name, inputVarNames);
        }

        [Fact]
        public void CompositeFbtGetsSixDataConnectionsToProcessEngine()
        {
            var (root, _, compositePath) = StageEaeProject();
            InvokeRecipePatcher(root, new DeployResult());

            var doc = XDocument.Load(compositePath);
            var conns = doc.Descendants(LibElNs + "DataConnections")
                .Single()
                .Elements(LibElNs + "Connection")
                .Select(c => ((string?)c.Attribute("Source"), (string?)c.Attribute("Destination")))
                .ToList();

            foreach (var name in new[] { "StepType", "CmdTargetName", "CmdStateArr", "Wait1Id", "Wait1State", "NextStep" })
                Assert.Contains((name, "ProcessEngine." + name), conns);
        }

        [Fact]
        public void CompositeFbtGetsSixInputStubs()
        {
            var (root, _, compositePath) = StageEaeProject();
            InvokeRecipePatcher(root, new DeployResult());

            var doc = XDocument.Load(compositePath);
            var stubNames = doc.Descendants(LibElNs + "FBNetwork")
                .Single()
                .Elements(LibElNs + "Input")
                .Select(i => (string?)i.Attribute("Name"))
                .ToList();

            foreach (var name in new[] { "StepType", "CmdTargetName", "CmdStateArr", "Wait1Id", "Wait1State", "NextStep" })
                Assert.Contains(name, stubNames);
        }

        // ---------------------------------------------------------------
        // Idempotency
        // ---------------------------------------------------------------

        [Fact]
        public void RunningPatcherTwiceProducesIdenticalEngineFbt()
        {
            var (root, enginePath, _) = StageEaeProject();
            var r1 = new DeployResult();
            InvokeRecipePatcher(root, r1);
            var afterFirst = File.ReadAllBytes(enginePath);

            var r2 = new DeployResult();
            InvokeRecipePatcher(root, r2);
            var afterSecond = File.ReadAllBytes(enginePath);

            Assert.Equal(afterFirst, afterSecond);
            Assert.Empty(r2.PatchesApplied); // second pass should be a no-op
        }

        [Fact]
        public void RunningPatcherTwiceProducesIdenticalCompositeFbt()
        {
            var (root, _, compositePath) = StageEaeProject();
            InvokeRecipePatcher(root, new DeployResult());
            var afterFirst = File.ReadAllBytes(compositePath);

            InvokeRecipePatcher(root, new DeployResult());
            var afterSecond = File.ReadAllBytes(compositePath);

            Assert.Equal(afterFirst, afterSecond);
        }
    }
}
