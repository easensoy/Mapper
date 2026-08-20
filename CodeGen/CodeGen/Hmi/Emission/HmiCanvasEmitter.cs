using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace CodeGen.Hmi
{
    // Writes the three files EAE requires per canvas: the partial class, the designer, and the resx.
    internal static class HmiCanvasEmitter
    {
        private static string Banner(HmiDefinition def) =>
            "/*\r\n * " + def.Deployment.GeneratedBanner + "\r\n */\r\n";

        internal static IReadOnlyList<string> Emit(
            HmiScreen screen, string hmiDir, string libraryNamespace, string screenResxTemplate, HmiDefinition def)
        {
            var written = new List<string>();
            void Write(string suffix, string body)
            {
                var path = Path.Combine(hmiDir, screen.Name + suffix);
                File.WriteAllText(path, body, Utf8Bom);
                written.Add(screen.Name + suffix);
            }

            Write(".cnv.cs", Code(screen, libraryNamespace, def));
            Write(".cnv.Designer.cs", Designer(screen, libraryNamespace, def));
            Write(".cnv.resx", Resx(screen, screenResxTemplate));
            return written;
        }

        private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

        private static string Code(HmiScreen s, string ns, HmiDefinition def) =>
            Banner(def) +
            "using System;\r\n" +
            "using NxtControl.GuiFramework;\r\n\r\n" +
            $"namespace HMI.{ns}.{def.Deployment.CanvasNamespaceSuffix}\r\n" +
            "{\r\n" +
            $"\tpublic partial class {s.Name} : NxtControl.GuiFramework.HMICanvas\r\n" +
            "\t{\r\n" +
            $"\t\tpublic {s.Name}()\r\n" +
            "\t\t{\r\n" +
            "\t\t\tInitializeComponent();\r\n" +
            "\t\t}\r\n" +
            "\t}\r\n" +
            "}\r\n";

        private static string Designer(HmiScreen s, string ns, HmiDefinition def)
        {
            var g = def.Geometry;
            var st = def.Style;
            var b = new StringBuilder();
            b.Append(Banner(def));
            b.Append("using System;\r\nusing System.ComponentModel;\r\nusing System.Collections;\r\nusing System.Diagnostics;\r\n\r\n");
            b.Append("using NxtControl.GuiFramework;\r\n\r\n");
            b.Append($"namespace HMI.{ns}.{def.Deployment.CanvasNamespaceSuffix}\r\n{{\r\n");
            b.Append($"\tpartial class {s.Name}\r\n\t{{\r\n");
            b.Append("\t\t#region Component Designer generated code\r\n");
            b.Append("\t\t/// <summary>\r\n\t\t/// Required method for Designer support - do not modify\r\n");
            b.Append("\t\t/// the contents of this method with the code editor.\r\n\t\t/// </summary>\r\n");
            b.Append("\t\tprivate void InitializeComponent()\r\n\t\t{\r\n");

            foreach (var c in s.Captions)
                b.Append($"\t\t\tthis.{c.Name} = new NxtControl.GuiFramework.FreeText();\r\n");
            foreach (var it in s.Items)
                b.Append($"\t\t\tthis.{it.Name} = new HMI.{ns}.{def.Deployment.SymbolNamespaceSuffix}.{it.CatType}.{it.Symbol.Name}();\r\n");
            foreach (var n in s.Buttons)
                b.Append($"\t\t\tthis.{n.Name} = new NxtControl.GuiFramework.ChangeCanvasButton();\r\n");

            // Captions are literal text with no TagName: they cannot read from or write to the PLC.
            foreach (var c in s.Captions)
            {
                b.Append($"\t\t\t// \r\n\t\t\t// {c.Name}\r\n\t\t\t// \r\n");
                var col = c.Emphasis ? st.EmphasisColor : st.CaptionColor;
                var font = st.CaptionFont;
                b.Append($"\t\t\tthis.{c.Name}.Color = new NxtControl.Drawing.Color(((byte)({col.R})), ((byte)({col.G})), ((byte)({col.B})));\r\n");
                b.Append($"\t\t\tthis.{c.Name}.Font = {Font(font)};\r\n");
                b.Append($"\t\t\tthis.{c.Name}.Location = new NxtControl.Drawing.PointF({c.X}D, {c.Y}D);\r\n");
                b.Append($"\t\t\tthis.{c.Name}.Name = \"{c.Name}\";\r\n");
                b.Append($"\t\t\tthis.{c.Name}.Text = \"{Escape(c.Text)}\";\r\n");
            }

            foreach (var it in s.Items)
            {
                b.Append($"\t\t\t// \r\n\t\t\t// {it.Name}\r\n\t\t\t// \r\n");
                b.Append($"\t\t\tthis.{it.Name}.BeginInit();\r\n");
                b.Append($"\t\t\tthis.{it.Name}.DesignMatrix = new NxtControl.Drawing.Matrix2D(1D, 0D, 0D, 1D, {it.X}D, {it.Y}D);\r\n");
                b.Append($"\t\t\tthis.{it.Name}.Name = \"{it.Name}\";\r\n");
                b.Append($"\t\t\tthis.{it.Name}.SecurityToken = ((uint)(4294967295u));\r\n");
                b.Append($"\t\t\tthis.{it.Name}.TagName = \"{it.TagName}\";\r\n");
                b.Append($"\t\t\tthis.{it.Name}.EndInit();\r\n");
            }

            foreach (var n in s.Buttons)
            {
                b.Append($"\t\t\t// \r\n\t\t\t// {n.Name}\r\n\t\t\t// \r\n");
                b.Append($"\t\t\tthis.{n.Name}.Bounds = new NxtControl.Drawing.RectF({F(n.X)}, {F(n.Y)}, {F(g.ButtonWidth)}, {F(g.ButtonHeight)});\r\n");
                b.Append($"\t\t\tthis.{n.Name}.Brush = new NxtControl.Drawing.Brush(\"{st.ButtonBrush}\");\r\n");
                b.Append($"\t\t\tthis.{n.Name}.CanvasName = \"{n.CanvasName}\";\r\n");
                b.Append($"\t\t\tthis.{n.Name}.Font = {Font(st.ButtonFont)};\r\n");
                b.Append($"\t\t\tthis.{n.Name}.Name = \"{n.Name}\";\r\n");
                b.Append($"\t\t\tthis.{n.Name}.Text = \"{Escape(n.Text)}\";\r\n");
                b.Append($"\t\t\tthis.{n.Name}.TextColor = new NxtControl.Drawing.Color(((byte)({st.ButtonTextColor.R})), ((byte)({st.ButtonTextColor.G})), ((byte)({st.ButtonTextColor.B})));\r\n");
            }

            b.Append($"\t\t\t// \r\n\t\t\t// {s.Name}\r\n\t\t\t// \r\n");
            b.Append($"\t\t\tthis.Bounds = new NxtControl.Drawing.RectF({F(0)}, {F(0)}, {F(g.CanvasWidth)}, {F(g.WorkHeight)});\r\n");
            b.Append($"\t\t\tthis.Brush = new NxtControl.Drawing.Brush(\"{st.CanvasBrush}\");\r\n");

            var shapes = Shapes(s).ToList();
            if (shapes.Count > 0)
            {
                b.Append("\t\t\tthis.Shapes.AddRange(new System.ComponentModel.IComponent[] {\r\n");
                b.Append(string.Join(",\r\n", shapes.Select(n => $"\t\t\tthis.{n}")));
                b.Append("});\r\n");
            }
            b.Append($"\t\t\tthis.Size = new System.Drawing.Size({g.CanvasWidth}, {g.WorkHeight});\r\n");
            b.Append("\r\n\t\t}\r\n");

            foreach (var c in s.Captions)
                b.Append($"\t\tprivate NxtControl.GuiFramework.FreeText {c.Name};\r\n");
            foreach (var it in s.Items)
                b.Append($"\t\tprivate HMI.{ns}.{def.Deployment.SymbolNamespaceSuffix}.{it.CatType}.{it.Symbol.Name} {it.Name};\r\n");
            foreach (var n in s.Buttons)
                b.Append($"\t\tprivate NxtControl.GuiFramework.ChangeCanvasButton {n.Name};\r\n");

            b.Append("\t\t#endregion\r\n\t}\r\n}\r\n");
            return b.ToString();
        }

        private static IEnumerable<string> Shapes(HmiScreen s) =>
            s.Captions.Select(c => c.Name)
                .Concat(s.Items.Select(i => i.Name))
                .Concat(s.Buttons.Select(n => n.Name));

        // EAE accepts either a named theme font or an explicit family/size/style triple.
        private static string Font(HmiFont f) =>
            f.Size <= 0
                ? $"new NxtControl.Drawing.Font(\"{f.Family}\")"
                : $"new NxtControl.Drawing.Font(\"{f.Family}\", {f.Size.ToString(System.Globalization.CultureInfo.InvariantCulture)}F, " +
                  $"System.Drawing.FontStyle.{(f.Bold ? "Bold" : "Regular")})";

        private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");

        // EAE serialises floats as a double literal cast to float.
        private static string F(int v) => $"((float)({v}D))";

        // The ResX declaration, schema and resheader block are reused verbatim from the template;
        // only the designer metadata (one entry per shape plus the editor settings) is regenerated.
        private static string Resx(HmiScreen s, string templatePath)
        {
            var text = File.ReadAllText(templatePath);
            var first = text.IndexOf("  <metadata", StringComparison.Ordinal);
            var close = text.LastIndexOf("</root>", StringComparison.Ordinal);
            if (first < 0 || close < 0 || first > close)
                throw new InvalidOperationException($"Screen resx template is not in the expected shape: {templatePath}");

            var b = new StringBuilder();
            foreach (var name in Shapes(s))
                b.Append(Meta(name + ".Name", null, name));
            b.Append(Meta("$this.GridSize", "System.Drawing.Size, System.Drawing, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a", "8, 8"));
            b.Append(Meta("$this.SnapToGrid", Bool, "True"));
            b.Append(Meta("$this.ShowGrid", Bool, "True"));
            b.Append(Meta("$this.LayerSelectable", null, Sixteen));
            b.Append(Meta("$this.LayerVisible", null, Sixteen));
            b.Append(Meta("$this.LayerActive", "System.Int32, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089", "0"));

            return text.Substring(0, first) + b + text.Substring(close);
        }

        private const string Bool = "System.Boolean, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";
        private const string Sixteen = "1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1";

        private static string Meta(string name, string? type, string value)
        {
            var attr = type != null ? $" type=\"{type}\"" : " xml:space=\"preserve\"";
            return $"  <metadata name=\"{name}\"{attr}>\r\n    <value>{value}</value>\r\n  </metadata>\r\n";
        }
    }
}
