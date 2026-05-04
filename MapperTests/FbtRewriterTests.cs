using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Translation;
using Xunit;

namespace MapperTests
{
    public class FbtRewriterTests
    {
        const string FbtTemplate = """
<?xml version="1.0" encoding="utf-8"?>
<FBType xmlns="http://www.holobloc.com/xml/LibraryElement.xsd" Name="ProcessRuntime_Generic_v1">
  <BasicFB>
    <ECC>
      <ECState Name="START" />
    </ECC>
    <Algorithm Name="initializeinit" Comment="">
      <ST Text=""><![CDATA[ORIGINAL_BODY := TRUE;]]></ST>
    </Algorithm>
    <Algorithm Name="other" Comment="">
      <ST Text=""><![CDATA[OTHER := TRUE;]]></ST>
    </Algorithm>
  </BasicFB>
</FBType>
""";

        static string MakeTempFbt(out string folder)
        {
            folder = Path.Combine(Path.GetTempPath(), "FbtRw_" + Path.GetRandomFileName());
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "ProcessRuntime_Generic_v1.fbt");
            File.WriteAllText(path, FbtTemplate);
            return path;
        }

        [Fact]
        public void RewriteReplacesInitializeInitOnly()
        {
            var path = MakeTempFbt(out _);
            const string newBody = "PusherID := 5;\nStepType[0] := 1;\n";

            FbtRewriter.RewriteInitializeInit(path, newBody);

            var st = FbtRewriter.ReadInitializeInitSt(path);
            Assert.Equal(newBody, st);

            // The other algorithm must remain untouched.
            var doc = XDocument.Load(path);
            var other = doc.Descendants().First(e =>
                e.Name.LocalName == "Algorithm" &&
                (string?)e.Attribute("Name") == "other");
            var otherSt = other.Descendants().First(e => e.Name.LocalName == "ST");
            var otherCdata = string.Concat(otherSt.Nodes().OfType<XCData>().Select(c => c.Value));
            Assert.Equal("OTHER := TRUE;", otherCdata);
        }

        [Fact]
        public void RewriteCreatesOriginalBackupOnFirstRun()
        {
            var path = MakeTempFbt(out _);
            FbtRewriter.RewriteInitializeInit(path, "FIRST := 1;");

            var backup = path + ".original";
            Assert.True(File.Exists(backup));
            Assert.Contains("ORIGINAL_BODY := TRUE;", File.ReadAllText(backup));
        }

        [Fact]
        public void RewriteIsIdempotentAcrossMultipleRuns()
        {
            var path = MakeTempFbt(out _);
            FbtRewriter.RewriteInitializeInit(path, "FIRST := 1;");
            FbtRewriter.RewriteInitializeInit(path, "SECOND := 2;");
            FbtRewriter.RewriteInitializeInit(path, "THIRD := 3;");

            var st = FbtRewriter.ReadInitializeInitSt(path);
            Assert.Equal("THIRD := 3;", st);

            // Backup still contains the pristine baseline only.
            var backup = path + ".original";
            Assert.Contains("ORIGINAL_BODY := TRUE;", File.ReadAllText(backup));
            Assert.DoesNotContain("FIRST := 1;", File.ReadAllText(backup));
            Assert.DoesNotContain("SECOND := 2;", File.ReadAllText(backup));
        }

        [Fact]
        public void RewriteThrowsForMissingFbt()
        {
            var folder = Path.Combine(Path.GetTempPath(), "FbtRwMiss_" + Path.GetRandomFileName());
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "missing.fbt");

            Assert.Throws<FileNotFoundException>(() =>
                FbtRewriter.RewriteInitializeInit(path, "X := 1;"));
        }
    }
}
