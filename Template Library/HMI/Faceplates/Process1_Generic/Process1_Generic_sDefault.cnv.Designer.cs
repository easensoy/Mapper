/*
 * Created by EcoStruxure Automation Expert.
 * User:  
 * Date: 9/29/2025
 * Time: 10:14 AM
 * 
 */
using System;
using System.ComponentModel;
using System.Collections;
using NxtControl.GuiFramework;

namespace HMI.Main.Symbols.Process1_Generic
{
	/// <summary>
	/// Summary description for sDefault.
	/// </summary>
	partial class sDefault
	{

		#region Component Designer generated code
		/// <summary>
		/// Required method for Designer support - do not modify
		/// the contents of this method with the code editor.
		/// </summary>
		private void InitializeComponent()
		{
			this.freeText1 = new NxtControl.GuiFramework.FreeText();
			this.rectangle1 = new NxtControl.GuiFramework.Rectangle();
			this.ThisStepText_1 = new System.HMI.Symbols.Base.Label();
			// 
			// freeText1
			// 
			this.freeText1.Color = new NxtControl.Drawing.Color("LabelTextColor");
			this.freeText1.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.freeText1.Location = new NxtControl.Drawing.PointF(8D, 8D);
			this.freeText1.Name = "freeText1";
			this.freeText1.Text = "Feed Process";
			// 
			// rectangle1
			// 
			this.rectangle1.Bounds = new NxtControl.Drawing.RectF(((float)(0D)), ((float)(0D)), ((float)(330D)), ((float)(85D)));
			this.rectangle1.Font = new NxtControl.Drawing.Font("HMI Sans Serif", 9F, System.Drawing.FontStyle.Regular);
			this.rectangle1.Name = "rectangle1";
			// 
			// ThisStepText_1
			// 
			this.ThisStepText_1.BeginInit();
			this.ThisStepText_1.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
			this.ThisStepText_1.DesignMatrix = new NxtControl.Drawing.Matrix2D(2.1333333333333333D, 0D, 0D, 1.9047619047619047D, 5D, 40D);
			this.ThisStepText_1.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.ThisStepText_1.FontScale = false;
			this.ThisStepText_1.IsOnlyInput = true;
			this.ThisStepText_1.Name = "ThisStepText_1";
			this.ThisStepText_1.Pen = new NxtControl.Drawing.Pen("Black");
			this.ThisStepText_1.TagName = "ThisStepText";
			this.ThisStepText_1.TextAlignment = NxtControl.Drawing.ContentAlignment.MiddleLeft;
			this.ThisStepText_1.TextColor = new NxtControl.Drawing.Color(((byte)(0)), ((byte)(0)), ((byte)(0)));
			this.ThisStepText_1.EndInit();
			// 
			// sDefault
			// 
			this.Shapes.AddRange(new System.ComponentModel.IComponent[] {
			this.rectangle1,
			this.freeText1,
			this.ThisStepText_1});
			this.SymbolSize = new System.Drawing.Size(330, 85);

		}
		private NxtControl.GuiFramework.FreeText freeText1;
		private NxtControl.GuiFramework.Rectangle rectangle1;
		private System.HMI.Symbols.Base.Label ThisStepText_1;
		#endregion
	}
}
