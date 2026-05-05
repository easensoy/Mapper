using System.IO;
using System.Linq;
using System.Xml.Linq;
using MapperUI.Services;
using Xunit;

namespace MapperTests
{
    public class DfbprojRegistrarTests
    {
        const string EmptyDfbproj = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <ItemGroup />
</Project>";

        static string MakeTempProj(out string folder)
        {
            folder = Path.Combine(Path.GetTempPath(), "Dfb_" + Path.GetRandomFileName());
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "IEC61499.dfbproj");
            File.WriteAllText(path, EmptyDfbproj);
            return path;
        }

        [Fact]
        public void RegisterDataType_AppendsCompileEntry()
        {
            var path = MakeTempProj(out _);
            int added = DfbprojRegistrar.RegisterDataType(path, @"DataType\Component_State.dt");

            Assert.Equal(1, added);
            var doc = XDocument.Load(path);
            var ns = doc.Root!.GetDefaultNamespace();
            var entry = doc.Descendants(ns + "Compile")
                .FirstOrDefault(e => (string?)e.Attribute("Include") == @"DataType\Component_State.dt");
            Assert.NotNull(entry);
            Assert.Equal("DataType", (string?)entry!.Element(ns + "IEC61499Type"));
        }

        [Fact]
        public void RegisterDataType_IsIdempotent()
        {
            var path = MakeTempProj(out _);
            int first = DfbprojRegistrar.RegisterDataType(path, @"DataType\Component_State.dt");
            int second = DfbprojRegistrar.RegisterDataType(path, @"DataType\Component_State.dt");
            Assert.Equal(1, first);
            Assert.Equal(0, second);
        }

        [Fact]
        public void SweepIec61499Folder_PicksUpDtAdpAndFbtFiles()
        {
            var path = MakeTempProj(out var projDir);
            var iec = Path.Combine(projDir, "IEC61499");
            var dtDir = Path.Combine(iec, "DataType");
            Directory.CreateDirectory(dtDir);

            File.WriteAllText(Path.Combine(dtDir, "Component_State.dt"), "<DataType/>");
            File.WriteAllText(Path.Combine(iec, "stateRptCmdAdptr.adp"), "<Adapter/>");
            File.WriteAllText(Path.Combine(iec, "MyBasic.fbt"), "<FBType/>");

            int added = DfbprojRegistrar.SweepIec61499Folder(path, iec);

            Assert.True(added >= 3, $"Expected >=3 entries added, got {added}");
            var doc = XDocument.Load(path);
            var ns = doc.Root!.GetDefaultNamespace();
            var includes = doc.Descendants(ns + "Compile")
                .Select(e => (string?)e.Attribute("Include"))
                .ToHashSet();

            Assert.Contains(@"DataType\Component_State.dt", includes);
            Assert.Contains("stateRptCmdAdptr.adp", includes);
            Assert.Contains("MyBasic.fbt", includes);
        }

        [Fact]
        public void SweepIec61499Folder_TreatsCompositeAsCompositeWhenOfflineXmlPresent()
        {
            var path = MakeTempProj(out var projDir);
            var iec = Path.Combine(projDir, "IEC61499");
            Directory.CreateDirectory(iec);
            File.WriteAllText(Path.Combine(iec, "Area.fbt"), "<FBType/>");
            File.WriteAllText(Path.Combine(iec, "Area.composite.offline.xml"), "<x/>");

            DfbprojRegistrar.SweepIec61499Folder(path, iec);

            var doc = XDocument.Load(path);
            var ns = doc.Root!.GetDefaultNamespace();
            var area = doc.Descendants(ns + "Compile")
                .First(e => (string?)e.Attribute("Include") == "Area.fbt");
            Assert.Equal("Composite", (string?)area.Element(ns + "IEC61499Type"));
        }
    }
}
