using System.IO;
using System.Text;

namespace CodeGen.Hmi
{
    // <CAT>.cfg is the only live link between an IEC 61499 CAT and its HMI faceplate: it names the
    // HMI interface FB (IThis) and every symbol/faceplate canvas EAE must compile for that type.
    internal static class HmiCatCfgEmitter
    {
        // Emits a .cfg for every deployed CAT that has a faceplate template.
        internal static int EmitAll(string eaeProjectDir, string templateLibraryPath)
        {
            if (string.IsNullOrWhiteSpace(templateLibraryPath)) return 0;

            var written = 0;
            foreach (var tpl in HmiTemplateLibrary.Load(templateLibraryPath))
            {
                if (!Directory.Exists(Path.Combine(eaeProjectDir, "IEC61499", tpl.CatType))) continue;
                Emit(eaeProjectDir, tpl);
                written++;
            }
            return written;
        }

        internal static void Emit(string eaeProjectDir, HmiCatTemplate tpl)
        {
            var cat = tpl.CatType;
            var catDir = Path.Combine(eaeProjectDir, "IEC61499", cat);
            if (!Directory.Exists(catDir)) return;

            var hmi = cat + "_HMI";
            var b = new StringBuilder();
            b.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n");
            b.Append("<CAT xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" ");
            b.Append($"Name=\"{cat}\" CATFile=\"{cat}\\{cat}.fbt\" ");
            b.Append($"SymbolDefFile=\"..\\HMI\\{cat}\\{cat}.def.cs\" ");
            b.Append($"SymbolEventFile=\"..\\HMI\\{cat}\\{cat}.event.cs\" ");
            b.Append($"DesignFile=\"..\\HMI\\{cat}\\{cat}.Design.resx\" ");
            b.Append("xmlns=\"http://www.nxtcontrol.com/IEC61499.xsd\">\r\n");
            b.Append($"  <HMIInterface Name=\"IThis\" FileName=\"{cat}\\{hmi}.fbt\" UsedInCAT=\"true\" Usage=\"Private\">\r\n");

            // Placeable symbols first, then pop-up faceplates - the order EAE itself writes.
            foreach (var sym in tpl.Symbols.OrderBy(s => s.IsFaceplate).ThenBy(s => s.Name, StringComparer.Ordinal))
            {
                // Setup canvases exist only to drive the plant; a monitoring HMI never registers them.
                if (HmiNames.IsCommandSymbol(sym.Name)) continue;

                var stem = $"..\\HMI\\{cat}\\{cat}_{sym.Name}";
                var faceplate = sym.IsFaceplate ? " IsFaceplate=\"true\"" : string.Empty;
                b.Append($"    <Symbol Name=\"{sym.Name}\" FileName=\"{stem}.cnv.cs\"{faceplate}>\r\n");
                b.Append($"      <DependentFiles>{stem}.cnv.Designer.cs</DependentFiles>\r\n");
                b.Append($"      <DependentFiles>{stem}.cnv.resx</DependentFiles>\r\n");
                // A pop-up faceplate inherits its connection from the symbol that opened it and has no contract.
                if (!sym.IsFaceplate && sym.HasContract)
                    b.Append($"      <DependentFiles>{stem}.cnv.xml</DependentFiles>\r\n");
                b.Append("    </Symbol>\r\n");
            }

            b.Append("  </HMIInterface>\r\n");
            b.Append(Plugin("OfflineParametrizationEditor", "CAT_OFFLINE", $"{cat}\\{cat}_CAT.offline.xml"));
            b.Append(Plugin("OPCUAConfigurator", "CAT_OPCUA", $"{cat}\\{cat}_CAT.opcua.xml"));
            b.Append(Plugin("OfflineParametrizationEditor", "CAT_OFFLINE", $"{cat}\\{hmi}.offline.xml"));
            b.Append(Plugin("OPCUAConfigurator", "CAT_OPCUA", $"{cat}\\{hmi}.opcua.xml"));
            b.Append("  <HWConfiguration xsi:nil=\"true\" />\r\n");
            b.Append("</CAT>");

            File.WriteAllText(Path.Combine(catDir, $"{cat}.cfg"), b.ToString(), new UTF8Encoding(false));

            // EAE expects the sidecar to exist even when empty.
            var meta = Path.Combine(catDir, $"{hmi}.meta.xml");
            if (!File.Exists(meta)) File.WriteAllBytes(meta, System.Array.Empty<byte>());
        }

        private static string Plugin(string plugin, string type, string value) =>
            $"  <Plugin Name=\"Plugin={plugin};IEC61499Type={type};$ItemType$=None\" Project=\"IEC61499\" Value=\"{value}\" />\r\n";
    }
}
