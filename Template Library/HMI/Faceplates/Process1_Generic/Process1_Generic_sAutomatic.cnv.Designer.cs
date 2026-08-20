/*
 * Created by EcoStruxure Automation Expert.
 * User:  
 * Date: 7/14/2026
 * Time: 3:31 PM
 * 
 */
using System;
using System.ComponentModel;
using System.Collections;
using NxtControl.GuiFramework;

namespace HMI.Main.Symbols.Process1_Generic
{
	/// <summary>
	/// Summary description for Symbol3.
	/// </summary>
	partial class sAutomatic
	{

		#region Component Designer generated code
		/// <summary>
		/// Required method for Designer support - do not modify
		/// the contents of this method with the code editor.
		/// </summary>
		private void InitializeComponent()
		{
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary2 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary3 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary1 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary4 = new NxtControl.GuiFramework.PropertyDictionary();
			this.freeText1 = new NxtControl.GuiFramework.FreeText();
			this.ProcessName = new System.HMI.Symbols.Base.Label();
			this.freeText3 = new NxtControl.GuiFramework.FreeText();
			this.ProcessComplete = new System.HMI.Symbols.Base.Led<bool>();
			this.freeText4 = new NxtControl.GuiFramework.FreeText();
			this.ModeCMD = new System.HMI.Symbols.Base.Label<short>();
			this.freeText2 = new NxtControl.GuiFramework.FreeText();
			this.rectangle1 = new NxtControl.GuiFramework.Rectangle();
			this.ThisStepText = new System.HMI.Symbols.Base.Label();
			// 
			// freeText1
			// 
			this.freeText1.Color = new NxtControl.Drawing.Color("Ele24dc");
			this.freeText1.Font = new NxtControl.Drawing.Font("HMI Sans Serif", 10F, System.Drawing.FontStyle.Regular);
			this.freeText1.Location = new NxtControl.Drawing.PointF(16D, 56D);
			this.freeText1.Name = "freeText1";
			this.freeText1.Text = "Process Name:";
			// 
			// ProcessName
			// 
			this.ProcessName.BeginInit();
			this.ProcessName.BorderStyle = System.Windows.Forms.BorderStyle.None;
			this.ProcessName.Brush = new NxtControl.Drawing.Brush("TextBoxReadOnlyBrush");
			this.ProcessName.DesignMatrix = new NxtControl.Drawing.Matrix2D(1D, 0D, 0D, 1D, 112D, 48D);
			this.ProcessName.FontScale = false;
			this.ProcessName.IsOnlyInput = true;
			this.ProcessName.Name = "ProcessName";
			this.ProcessName.Pen = new NxtControl.Drawing.Pen("LabelPen");
			this.ProcessName.TagName = "ProcessName";
			this.ProcessName.EndInit();
			// 
			// freeText3
			// 
			this.freeText3.Color = new NxtControl.Drawing.Color("Ele24dc");
			this.freeText3.Font = new NxtControl.Drawing.Font("HMI Sans Serif", 10F, System.Drawing.FontStyle.Regular);
			this.freeText3.Location = new NxtControl.Drawing.PointF(16D, 96D);
			this.freeText3.Name = "freeText3";
			this.freeText3.Text = "Current Step:";
			// 
			// ProcessComplete
			// 
			this.ProcessComplete.BeginInit();
			this.ProcessComplete.ColorFrame = new NxtControl.Drawing.Color("LedFrameColor");
			this.ProcessComplete.DesignMatrix = new NxtControl.Drawing.Matrix2D(1.3333333333333333D, 0D, 0D, 1.3333333333333333D, 144D, 144D);
			this.ProcessComplete.FrameSize = 33F;
			this.ProcessComplete.IsOnlyInput = true;
			this.ProcessComplete.Name = "ProcessComplete";
			propertyDictionary2.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			propertyDictionary3.Add("Color", new NxtControl.Drawing.Color("LedTrueColor"));
			this.ProcessComplete.Ranges.Clear();
			this.ProcessComplete.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(false, propertyDictionary2));
			this.ProcessComplete.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(true, propertyDictionary3));
			propertyDictionary1.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			this.ProcessComplete.Ranges.DefaultPropertyValues = propertyDictionary1;
			this.ProcessComplete.TagName = "ProcessComplete";
			this.ProcessComplete.EndInit();
			// 
			// freeText4
			// 
			this.freeText4.Color = new NxtControl.Drawing.Color("Ele24dc");
			this.freeText4.Font = new NxtControl.Drawing.Font("HMI Sans Serif", 10F, System.Drawing.FontStyle.Regular);
			this.freeText4.Location = new NxtControl.Drawing.PointF(16D, 136D);
			this.freeText4.Name = "freeText4";
			this.freeText4.Text = "Process Complete:";
			// 
			// ModeCMD
			// 
			this.ModeCMD.BeginInit();
			this.ModeCMD.BorderStyle = System.Windows.Forms.BorderStyle.None;
			this.ModeCMD.DecimalPlacesCount = ((uint)(2u));
			this.ModeCMD.DesignMatrix = new NxtControl.Drawing.Matrix2D(1D, 0D, 0D, 1D, 112D, 16D);
			this.ModeCMD.FontScale = false;
			this.ModeCMD.IsOnlyInput = true;
			this.ModeCMD.LeadingZeros = ((uint)(0u));
			this.ModeCMD.Name = "ModeCMD";
			this.ModeCMD.NumberBase = NxtControl.GuiFramework.NumberBase.Decimal;
			propertyDictionary4.Add("Text", "${Value}");
			propertyDictionary4.Add("TextColor", new NxtControl.Drawing.Color("LabelTextColor"));
			propertyDictionary4.Add("Brush", new NxtControl.Drawing.Brush("TextBoxReadOnlyBrush"));
			propertyDictionary4.Add("Pen", new NxtControl.Drawing.Pen("LabelPen"));
			this.ModeCMD.Ranges.DefaultPropertyValues = propertyDictionary4;
			this.ModeCMD.TagName = "ModeCMD";
			this.ModeCMD.EndInit();
			// 
			// freeText2
			// 
			this.freeText2.Color = new NxtControl.Drawing.Color("Ele24dc");
			this.freeText2.Font = new NxtControl.Drawing.Font("HMI Sans Serif", 10F, System.Drawing.FontStyle.Regular);
			this.freeText2.Location = new NxtControl.Drawing.PointF(16D, 24D);
			this.freeText2.Name = "freeText2";
			this.freeText2.Text = "Mode:";
			// 
			// rectangle1
			// 
			this.rectangle1.Bounds = new NxtControl.Drawing.RectF(((float)(8D)), ((float)(8D)), ((float)(272D)), ((float)(152D)));
			this.rectangle1.Font = new NxtControl.Drawing.Font("HMI Sans Serif", 9F, System.Drawing.FontStyle.Regular);
			this.rectangle1.Name = "rectangle1";
			// 
			// ThisStepText
			// 
			this.ThisStepText.BeginInit();
			this.ThisStepText.BorderStyle = System.Windows.Forms.BorderStyle.None;
			this.ThisStepText.Brush = new NxtControl.Drawing.Brush("TextBoxReadOnlyBrush");
			this.ThisStepText.DesignMatrix = new NxtControl.Drawing.Matrix2D(1D, 0D, 0D, 1D, 112D, 88D);
			this.ThisStepText.FontScale = false;
			this.ThisStepText.IsOnlyInput = true;
			this.ThisStepText.Name = "ThisStepText";
			this.ThisStepText.Pen = new NxtControl.Drawing.Pen("LabelPen");
			this.ThisStepText.TagName = "ThisStepText";
			this.ThisStepText.EndInit();
			// 
			// Symbol3
			// 
			this.Shapes.AddRange(new System.ComponentModel.IComponent[] {
			this.rectangle1,
			this.freeText1,
			this.ProcessName,
			this.freeText3,
			this.ProcessComplete,
			this.freeText4,
			this.ModeCMD,
			this.freeText2,
			this.ThisStepText});
			this.SymbolSize = new System.Drawing.Size(288, 160);

		}
		private NxtControl.GuiFramework.FreeText freeText1;
		private System.HMI.Symbols.Base.Label ProcessName;
		private NxtControl.GuiFramework.FreeText freeText3;
		private System.HMI.Symbols.Base.Led<bool> ProcessComplete;
		private NxtControl.GuiFramework.FreeText freeText4;
		private System.HMI.Symbols.Base.Label<short> ModeCMD;
		private NxtControl.GuiFramework.FreeText freeText2;
		private NxtControl.GuiFramework.Rectangle rectangle1;
		private System.HMI.Symbols.Base.Label ThisStepText;
		#endregion
	}
}
