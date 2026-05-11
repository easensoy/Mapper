using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using MapperUI.Services;
using Xunit;
using Xunit.Abstractions;

namespace MapperTests
{
    /// <summary>
    /// Diagnostic — dumps the literal Parameter values emitted on the Process1 FB so the
    /// recipe can be eyeballed. Always passes; output is in test stdout.
    /// </summary>
    public class PhaseOneInspectionTests
    {
        readonly ITestOutputHelper _out;
        public PhaseOneInspectionTests(ITestOutputHelper output) { _out = output; }

        [Fact]
        public void DumpEmittedProcess1Parameters()
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "TestData", "Feed_Station_Fixture.xml");
            var folder = Path.Combine(Path.GetTempPath(), "MapperTests_Inspect_" + Path.GetRandomFileName());
            Directory.CreateDirectory(folder);
            var injector = new SystemInjector();
            var path = injector.GenerateFeedStationSyslay(fixture, folder);

            var doc = XDocument.Load(path);
            XNamespace ns = "https://www.se.com/LibraryElements";
            var process = doc.Descendants(ns + "FB")
                .Single(fb => (string?)fb.Attribute("Type") == "Process1_Generic");

            _out.WriteLine($"--- Process1 Parameter values from {path} ---");
            foreach (var p in process.Elements(ns + "Parameter"))
            {
                _out.WriteLine($"  {p.Attribute("Name")!.Value} = {p.Attribute("Value")!.Value}");
            }
        }
    }
}
