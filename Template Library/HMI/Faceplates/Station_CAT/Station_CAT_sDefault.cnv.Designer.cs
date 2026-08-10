/*
 * Created by EcoStruxure Automation Expert.
 * User: Evans_A
 * Date: 7/31/2025
 * Time: 9:10 AM
 * 
 */
using System;
using System.ComponentModel;
using System.Collections;
using NxtControl.GuiFramework;

namespace HMI.Main.Symbols.Station_CAT
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
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary2 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary3 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary1 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary5 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary6 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary7 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary8 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary9 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary10 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary4 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary12 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary13 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary14 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary15 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary16 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary17 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary11 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary19 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary20 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary21 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary22 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary23 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary24 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary18 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary26 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary27 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary28 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary29 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary30 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary31 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary25 = new NxtControl.GuiFramework.PropertyDictionary();
			this.LL_Fault = new System.HMI.Symbols.Base.Led<bool>();
			this.StationName = new System.HMI.Symbols.Base.Label();
			this.ParentName = new System.HMI.Symbols.Base.Label();
			this.rectangle1 = new NxtControl.GuiFramework.Rectangle();
			this.drawnTextBox1 = new NxtControl.GuiFramework.DrawnTextBox();
			this.drawnTextBox2 = new NxtControl.GuiFramework.DrawnTextBox();
			this.freeText5 = new NxtControl.GuiFramework.FreeText();
			this.freeText6 = new NxtControl.GuiFramework.FreeText();
			this.freeText_11 = new System.HMI.Symbols.Base.FreeText<short>();
			this.freeText_12 = new System.HMI.Symbols.Base.FreeText<short>();
			this.freeText7 = new NxtControl.GuiFramework.FreeText();
			this.freeText8 = new NxtControl.GuiFramework.FreeText();
			this.freeText_13 = new System.HMI.Symbols.Base.FreeText<short>();
			this.freeText_14 = new System.HMI.Symbols.Base.FreeText<short>();
			// 
			// LL_Fault
			// 
			this.LL_Fault.BeginInit();
			this.LL_Fault.ColorFrame = new NxtControl.Drawing.Color("LedFrameColor");
			this.LL_Fault.DesignMatrix = new NxtControl.Drawing.Matrix2D(2D, 0D, 0D, 2D, 172D, 260D);
			this.LL_Fault.FrameSize = 33F;
			this.LL_Fault.IsOnlyInput = true;
			this.LL_Fault.Name = "LL_Fault";
			propertyDictionary2.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			propertyDictionary3.Add("Color", new NxtControl.Drawing.Color("Red"));
			this.LL_Fault.Ranges.Clear();
			this.LL_Fault.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(false, propertyDictionary2));
			this.LL_Fault.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(true, propertyDictionary3));
			propertyDictionary1.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			this.LL_Fault.Ranges.DefaultPropertyValues = propertyDictionary1;
			this.LL_Fault.TagName = "LL_Fault";
			this.LL_Fault.EndInit();
			// 
			// StationName
			// 
			this.StationName.BeginInit();
			this.StationName.BorderStyle = System.Windows.Forms.BorderStyle.None;
			this.StationName.DesignMatrix = new NxtControl.Drawing.Matrix2D(1D, 0D, 0D, 1.9047619047619047D, 16D, 16D);
			this.StationName.Font = new NxtControl.Drawing.Font("HMI Monospace", 16F, System.Drawing.FontStyle.Regular);
			this.StationName.FontScale = false;
			this.StationName.IsOnlyInput = true;
			this.StationName.Name = "StationName";
			this.StationName.Pen = new NxtControl.Drawing.Pen("LabelPen");
			this.StationName.TagName = "StationName";
			this.StationName.TextColor = new NxtControl.Drawing.Color("Blue");
			this.StationName.EndInit();
			// 
			// ParentName
			// 
			this.ParentName.BeginInit();
			this.ParentName.BorderStyle = System.Windows.Forms.BorderStyle.None;
			this.ParentName.DesignMatrix = new NxtControl.Drawing.Matrix2D(1D, 0D, 0D, 1D, 640D, 16D);
			this.ParentName.FontScale = false;
			this.ParentName.IsOnlyInput = true;
			this.ParentName.Name = "ParentName";
			this.ParentName.Pen = new NxtControl.Drawing.Pen("LabelPen");
			this.ParentName.Prefix = "Parent: ";
			this.ParentName.TagName = "ParentName";
			this.ParentName.EndInit();
			// 
			// rectangle1
			// 
			this.rectangle1.Bounds = new NxtControl.Drawing.RectF(((float)(0D)), ((float)(8D)), ((float)(800D)), ((float)(280D)));
			this.rectangle1.Font = new NxtControl.Drawing.Font("HMI Sans Serif", 9F, System.Drawing.FontStyle.Regular);
			this.rectangle1.Name = "rectangle1";
			// 
			// drawnTextBox1
			// 
			this.drawnTextBox1.BorderStyle = System.Windows.Forms.BorderStyle.None;
			this.drawnTextBox1.Bounds = new NxtControl.Drawing.RectF(((float)(40D)), ((float)(248D)), ((float)(160D)), ((float)(25D)));
			this.drawnTextBox1.Brush = new NxtControl.Drawing.Brush("Transparent");
			this.drawnTextBox1.Font = new NxtControl.Drawing.Font("TextBoxFont");
			this.drawnTextBox1.FontScale = true;
			this.drawnTextBox1.Maximum = 100D;
			this.drawnTextBox1.Minimum = 0D;
			this.drawnTextBox1.Name = "drawnTextBox1";
			this.drawnTextBox1.Pen = new NxtControl.Drawing.Pen("TextBoxPen");
			this.drawnTextBox1.Text = "Lower Level Fault";
			this.drawnTextBox1.TextAutoSizeHorizontalOffset = 10;
			this.drawnTextBox1.TextAutoSizeVerticalOffset = 2;
			this.drawnTextBox1.TextPadding = new NxtControl.Drawing.Padding(2);
			// 
			// drawnTextBox2
			// 
			this.drawnTextBox2.BorderStyle = System.Windows.Forms.BorderStyle.None;
			this.drawnTextBox2.Bounds = new NxtControl.Drawing.RectF(((float)(40D)), ((float)(248D)), ((float)(160D)), ((float)(25D)));
			this.drawnTextBox2.Brush = new NxtControl.Drawing.Brush("Transparent");
			this.drawnTextBox2.Font = new NxtControl.Drawing.Font("TextBoxFont");
			this.drawnTextBox2.FontScale = true;
			this.drawnTextBox2.Maximum = 100D;
			this.drawnTextBox2.Minimum = 0D;
			this.drawnTextBox2.Name = "drawnTextBox2";
			this.drawnTextBox2.Pen = new NxtControl.Drawing.Pen("TextBoxPen");
			this.drawnTextBox2.Text = "Lower Level Fault";
			this.drawnTextBox2.TextAutoSizeHorizontalOffset = 10;
			this.drawnTextBox2.TextAutoSizeVerticalOffset = 2;
			this.drawnTextBox2.TextPadding = new NxtControl.Drawing.Padding(2);
			// 
			// freeText5
			// 
			this.freeText5.Color = new NxtControl.Drawing.Color("LabelTextColor");
			this.freeText5.Font = new NxtControl.Drawing.Font("LabelFont");
			this.freeText5.Location = new NxtControl.Drawing.PointF(616D, 56D);
			this.freeText5.Name = "freeText5";
			this.freeText5.Text = "Station Mode";
			// 
			// freeText6
			// 
			this.freeText6.Color = new NxtControl.Drawing.Color("LabelTextColor");
			this.freeText6.Font = new NxtControl.Drawing.Font("LabelFont");
			this.freeText6.Location = new NxtControl.Drawing.PointF(616D, 104D);
			this.freeText6.Name = "freeText6";
			this.freeText6.Text = "Station Cycle Type";
			// 
			// freeText_11
			// 
			this.freeText_11.BeginInit();
			this.freeText_11.DecimalPlacesCount = ((uint)(2u));
			this.freeText_11.DesignMatrix = new NxtControl.Drawing.Matrix2D(1D, 0D, 0D, 1D, 616D, 80D);
			this.freeText_11.IsOnlyInput = true;
			this.freeText_11.Name = "freeText_11";
			propertyDictionary5.Add("Text", "No Mode Selected");
			propertyDictionary5.Add("TextColor", new NxtControl.Drawing.Color("LabelTextColor"));
			propertyDictionary6.Add("Text", "AUTO");
			propertyDictionary6.Add("TextColor", new NxtControl.Drawing.Color("Green"));
			propertyDictionary7.Add("Text", "MANUAL");
			propertyDictionary7.Add("TextColor", new NxtControl.Drawing.Color("LedTrueColor"));
			propertyDictionary8.Add("Text", "SETUP");
			propertyDictionary8.Add("TextColor", new NxtControl.Drawing.Color("Yellow"));
			propertyDictionary9.Add("Text", "HOMING");
			propertyDictionary9.Add("TextColor", new NxtControl.Drawing.BlinkColor("DarkYellowWhite"));
			propertyDictionary10.Add("Text", "UNDETERMINED");
			propertyDictionary10.Add("TextColor", new NxtControl.Drawing.Color("AlarmCame"));
			this.freeText_11.Ranges.Clear();
			this.freeText_11.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(0)), propertyDictionary5));
			this.freeText_11.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(1)), propertyDictionary6));
			this.freeText_11.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(2)), propertyDictionary7));
			this.freeText_11.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(3)), propertyDictionary8));
			this.freeText_11.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(9)), propertyDictionary9));
			this.freeText_11.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(100)), propertyDictionary10));
			propertyDictionary4.Add("Text", "${Value}");
			propertyDictionary4.Add("TextColor", new NxtControl.Drawing.Color("LabelTextColor"));
			this.freeText_11.Ranges.DefaultPropertyValues = propertyDictionary4;
			this.freeText_11.TagName = "LocalMode";
			this.freeText_11.TextAngle = 0F;
			this.freeText_11.EndInit();
			// 
			// freeText_12
			// 
			this.freeText_12.BeginInit();
			this.freeText_12.DecimalPlacesCount = ((uint)(2u));
			this.freeText_12.DesignMatrix = new NxtControl.Drawing.Matrix2D(1D, 0D, 0D, 1D, 616D, 128D);
			this.freeText_12.IsOnlyInput = true;
			this.freeText_12.Name = "freeText_12";
			propertyDictionary12.Add("Text", "STOPPED");
			propertyDictionary12.Add("TextColor", new NxtControl.Drawing.Color("Red"));
			propertyDictionary13.Add("Text", "RUNNING");
			propertyDictionary13.Add("TextColor", new NxtControl.Drawing.Color("Green"));
			propertyDictionary14.Add("Text", "STOP AT END");
			propertyDictionary14.Add("TextColor", new NxtControl.Drawing.Color("Green"));
			propertyDictionary15.Add("Text", "SINGLE CYCLE");
			propertyDictionary15.Add("TextColor", new NxtControl.Drawing.Color("Green"));
			propertyDictionary16.Add("Text", "DRY CYCLE");
			propertyDictionary16.Add("TextColor", new NxtControl.Drawing.Color("DarkYellow"));
			propertyDictionary17.Add("Text", "UNDETERMINED");
			propertyDictionary17.Add("TextColor", new NxtControl.Drawing.Color("Red"));
			this.freeText_12.Ranges.Clear();
			this.freeText_12.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(0)), propertyDictionary12));
			this.freeText_12.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(1)), propertyDictionary13));
			this.freeText_12.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(2)), propertyDictionary14));
			this.freeText_12.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(3)), propertyDictionary15));
			this.freeText_12.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(4)), propertyDictionary16));
			this.freeText_12.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(100)), propertyDictionary17));
			propertyDictionary11.Add("Text", "${Value}");
			propertyDictionary11.Add("TextColor", new NxtControl.Drawing.Color("LabelTextColor"));
			this.freeText_12.Ranges.DefaultPropertyValues = propertyDictionary11;
			this.freeText_12.TagName = "LocalCycleType";
			this.freeText_12.TextAngle = 0F;
			this.freeText_12.EndInit();
			// 
			// freeText7
			// 
			this.freeText7.Color = new NxtControl.Drawing.Color("LabelTextColor");
			this.freeText7.Font = new NxtControl.Drawing.Font("LabelFont");
			this.freeText7.Location = new NxtControl.Drawing.PointF(616D, 184D);
			this.freeText7.Name = "freeText7";
			this.freeText7.Text = "Lower Level Mode";
			// 
			// freeText8
			// 
			this.freeText8.Color = new NxtControl.Drawing.Color("LabelTextColor");
			this.freeText8.Font = new NxtControl.Drawing.Font("LabelFont");
			this.freeText8.Location = new NxtControl.Drawing.PointF(616D, 232D);
			this.freeText8.Name = "freeText8";
			this.freeText8.Text = "Lower Level Cycle Type";
			// 
			// freeText_13
			// 
			this.freeText_13.BeginInit();
			this.freeText_13.DecimalPlacesCount = ((uint)(2u));
			this.freeText_13.DesignMatrix = new NxtControl.Drawing.Matrix2D(1D, 0D, 0D, 1D, 616D, 208D);
			this.freeText_13.IsOnlyInput = true;
			this.freeText_13.Name = "freeText_13";
			propertyDictionary19.Add("Text", "No Mode Selected");
			propertyDictionary19.Add("TextColor", new NxtControl.Drawing.Color("LabelTextColor"));
			propertyDictionary20.Add("Text", "AUTO");
			propertyDictionary20.Add("TextColor", new NxtControl.Drawing.Color("Green"));
			propertyDictionary21.Add("Text", "MANUAL");
			propertyDictionary21.Add("TextColor", new NxtControl.Drawing.Color("LedTrueColor"));
			propertyDictionary22.Add("Text", "SETUP");
			propertyDictionary22.Add("TextColor", new NxtControl.Drawing.Color("Yellow"));
			propertyDictionary23.Add("Text", "HOMING");
			propertyDictionary23.Add("TextColor", new NxtControl.Drawing.BlinkColor("DarkYellowWhite"));
			propertyDictionary24.Add("Text", "UNDETERMINED");
			propertyDictionary24.Add("TextColor", new NxtControl.Drawing.Color("AlarmCame"));
			this.freeText_13.Ranges.Clear();
			this.freeText_13.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(0)), propertyDictionary19));
			this.freeText_13.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(1)), propertyDictionary20));
			this.freeText_13.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(2)), propertyDictionary21));
			this.freeText_13.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(3)), propertyDictionary22));
			this.freeText_13.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(9)), propertyDictionary23));
			this.freeText_13.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(100)), propertyDictionary24));
			propertyDictionary18.Add("Text", "${Value}");
			propertyDictionary18.Add("TextColor", new NxtControl.Drawing.Color("LabelTextColor"));
			this.freeText_13.Ranges.DefaultPropertyValues = propertyDictionary18;
			this.freeText_13.TagName = "LL_Mode";
			this.freeText_13.TextAngle = 0F;
			this.freeText_13.EndInit();
			// 
			// freeText_14
			// 
			this.freeText_14.BeginInit();
			this.freeText_14.DecimalPlacesCount = ((uint)(2u));
			this.freeText_14.DesignMatrix = new NxtControl.Drawing.Matrix2D(1D, 0D, 0D, 1D, 616D, 256D);
			this.freeText_14.IsOnlyInput = true;
			this.freeText_14.Name = "freeText_14";
			propertyDictionary26.Add("Text", "STOPPED");
			propertyDictionary26.Add("TextColor", new NxtControl.Drawing.Color("Red"));
			propertyDictionary27.Add("Text", "RUNNING");
			propertyDictionary27.Add("TextColor", new NxtControl.Drawing.Color("Green"));
			propertyDictionary28.Add("Text", "STOP AT END");
			propertyDictionary28.Add("TextColor", new NxtControl.Drawing.Color("Green"));
			propertyDictionary29.Add("Text", "SINGLE CYCLE");
			propertyDictionary29.Add("TextColor", new NxtControl.Drawing.Color("Green"));
			propertyDictionary30.Add("Text", "DRY CYCLE");
			propertyDictionary30.Add("TextColor", new NxtControl.Drawing.Color("DarkYellow"));
			propertyDictionary31.Add("Text", "UNDETERMINED");
			propertyDictionary31.Add("TextColor", new NxtControl.Drawing.Color("Red"));
			this.freeText_14.Ranges.Clear();
			this.freeText_14.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(0)), propertyDictionary26));
			this.freeText_14.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(1)), propertyDictionary27));
			this.freeText_14.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(2)), propertyDictionary28));
			this.freeText_14.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(3)), propertyDictionary29));
			this.freeText_14.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(4)), propertyDictionary30));
			this.freeText_14.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(100)), propertyDictionary31));
			propertyDictionary25.Add("Text", "${Value}");
			propertyDictionary25.Add("TextColor", new NxtControl.Drawing.Color("LabelTextColor"));
			this.freeText_14.Ranges.DefaultPropertyValues = propertyDictionary25;
			this.freeText_14.TagName = "LL_CycleType";
			this.freeText_14.TextAngle = 0F;
			this.freeText_14.EndInit();
			// 
			// sDefault
			// 
			this.Shapes.AddRange(new System.ComponentModel.IComponent[] {
			this.rectangle1,
			this.LL_Fault,
			this.StationName,
			this.ParentName,
			this.drawnTextBox1,
			this.drawnTextBox2,
			this.freeText5,
			this.freeText6,
			this.freeText_11,
			this.freeText_12,
			this.freeText7,
			this.freeText8,
			this.freeText_13,
			this.freeText_14});
			this.SymbolSize = new System.Drawing.Size(832, 400);

		}
		private System.HMI.Symbols.Base.Led<bool> LL_Fault;
		private System.HMI.Symbols.Base.Label StationName;
		private System.HMI.Symbols.Base.Label ParentName;
		private NxtControl.GuiFramework.Rectangle rectangle1;
		private NxtControl.GuiFramework.DrawnTextBox drawnTextBox1;
		private NxtControl.GuiFramework.DrawnTextBox drawnTextBox2;
		private NxtControl.GuiFramework.FreeText freeText5;
		private NxtControl.GuiFramework.FreeText freeText6;
		private System.HMI.Symbols.Base.FreeText<short> freeText_11;
		private System.HMI.Symbols.Base.FreeText<short> freeText_12;
		private NxtControl.GuiFramework.FreeText freeText7;
		private NxtControl.GuiFramework.FreeText freeText8;
		private System.HMI.Symbols.Base.FreeText<short> freeText_13;
		private System.HMI.Symbols.Base.FreeText<short> freeText_14;
		#endregion
	}
}
