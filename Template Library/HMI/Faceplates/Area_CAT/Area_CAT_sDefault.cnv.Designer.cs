/*
 * Created by EcoStruxure Automation Expert.
 * User: Evans_A
 * Date: 7/23/2025
 * Time: 3:09 PM
 * 
 */
using System;
using System.ComponentModel;
using System.Collections;
using NxtControl.GuiFramework;

namespace HMI.Main.Symbols.Area_CAT
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
			this.rectangle1 = new NxtControl.GuiFramework.Rectangle();
			this.LL_Fault_Status = new System.HMI.Symbols.Base.Led<bool>();
			this.drawnTextBox1 = new NxtControl.GuiFramework.DrawnTextBox();
			this.freeText3 = new NxtControl.GuiFramework.FreeText();
			this.freeText4 = new NxtControl.GuiFramework.FreeText();
			this.System_Mode = new System.HMI.Symbols.Base.FreeText<short>();
			this.System_Cycle_Type = new System.HMI.Symbols.Base.FreeText<short>();
			this.Area_Name_1 = new System.HMI.Symbols.Base.Label();
			// 
			// rectangle1
			// 
			this.rectangle1.Bounds = new NxtControl.Drawing.RectF(((float)(0D)), ((float)(0D)), ((float)(1000D)), ((float)(182D)));
			this.rectangle1.Font = new NxtControl.Drawing.Font("HMI Sans Serif", 9F, System.Drawing.FontStyle.Regular);
			this.rectangle1.Name = "rectangle1";
			// 
			// LL_Fault_Status
			// 
			this.LL_Fault_Status.BeginInit();
			this.LL_Fault_Status.ColorFrame = new NxtControl.Drawing.Color("LedFrameColor");
			this.LL_Fault_Status.DesignMatrix = new NxtControl.Drawing.Matrix2D(2.0833333333333335D, 0D, 0D, 2.1666666666666665D, 762.5D, 25D);
			this.LL_Fault_Status.FrameSize = 33F;
			this.LL_Fault_Status.IsOnlyInput = true;
			this.LL_Fault_Status.Name = "LL_Fault_Status";
			propertyDictionary2.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			propertyDictionary3.Add("Color", new NxtControl.Drawing.Color("Red"));
			this.LL_Fault_Status.Ranges.Clear();
			this.LL_Fault_Status.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(false, propertyDictionary2));
			this.LL_Fault_Status.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(true, propertyDictionary3));
			propertyDictionary1.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			this.LL_Fault_Status.Ranges.DefaultPropertyValues = propertyDictionary1;
			this.LL_Fault_Status.TagName = "LL_Fault_Status";
			this.LL_Fault_Status.Visible = false;
			this.LL_Fault_Status.EndInit();
			// 
			// drawnTextBox1
			// 
			this.drawnTextBox1.BorderStyle = System.Windows.Forms.BorderStyle.None;
			this.drawnTextBox1.Bounds = new NxtControl.Drawing.RectF(((float)(632D)), ((float)(14D)), ((float)(112D)), ((float)(25D)));
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
			this.drawnTextBox1.Visible = false;
			// 
			// freeText3
			// 
			this.freeText3.Color = new NxtControl.Drawing.Color("LabelTextColor");
			this.freeText3.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.freeText3.Location = new NxtControl.Drawing.PointF(804D, 44D);
			this.freeText3.Name = "freeText3";
			this.freeText3.Text = "System Mode";
			// 
			// freeText4
			// 
			this.freeText4.Color = new NxtControl.Drawing.Color("LabelTextColor");
			this.freeText4.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.freeText4.Location = new NxtControl.Drawing.PointF(804D, 112D);
			this.freeText4.Name = "freeText4";
			this.freeText4.Text = "System Cycle Type";
			// 
			// System_Mode
			// 
			this.System_Mode.BeginInit();
			this.System_Mode.DecimalPlacesCount = ((uint)(2u));
			this.System_Mode.DesignMatrix = new NxtControl.Drawing.Matrix2D(1D, 0D, 0D, 1D, 798D, 74D);
			this.System_Mode.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.System_Mode.IsOnlyInput = true;
			this.System_Mode.Name = "System_Mode";
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
			this.System_Mode.Ranges.Clear();
			this.System_Mode.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(0)), propertyDictionary5));
			this.System_Mode.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(1)), propertyDictionary6));
			this.System_Mode.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(2)), propertyDictionary7));
			this.System_Mode.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(3)), propertyDictionary8));
			this.System_Mode.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(9)), propertyDictionary9));
			this.System_Mode.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(100)), propertyDictionary10));
			propertyDictionary4.Add("Text", "${Value}");
			propertyDictionary4.Add("TextColor", new NxtControl.Drawing.Color("LabelTextColor"));
			this.System_Mode.Ranges.DefaultPropertyValues = propertyDictionary4;
			this.System_Mode.TagName = "System_Mode";
			this.System_Mode.TextAngle = 0F;
			this.System_Mode.EndInit();
			// 
			// System_Cycle_Type
			// 
			this.System_Cycle_Type.BeginInit();
			this.System_Cycle_Type.DecimalPlacesCount = ((uint)(2u));
			this.System_Cycle_Type.DesignMatrix = new NxtControl.Drawing.Matrix2D(1D, 0D, 0D, 1D, 796D, 144D);
			this.System_Cycle_Type.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.System_Cycle_Type.IsOnlyInput = true;
			this.System_Cycle_Type.Name = "System_Cycle_Type";
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
			this.System_Cycle_Type.Ranges.Clear();
			this.System_Cycle_Type.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(0)), propertyDictionary12));
			this.System_Cycle_Type.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(1)), propertyDictionary13));
			this.System_Cycle_Type.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(2)), propertyDictionary14));
			this.System_Cycle_Type.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(3)), propertyDictionary15));
			this.System_Cycle_Type.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(4)), propertyDictionary16));
			this.System_Cycle_Type.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(100)), propertyDictionary17));
			propertyDictionary11.Add("Text", "${Value}");
			propertyDictionary11.Add("TextColor", new NxtControl.Drawing.Color("LabelTextColor"));
			this.System_Cycle_Type.Ranges.DefaultPropertyValues = propertyDictionary11;
			this.System_Cycle_Type.TagName = "System_Cycle_Type";
			this.System_Cycle_Type.TextAngle = 0F;
			this.System_Cycle_Type.EndInit();
			// 
			// Area_Name_1
			// 
			this.Area_Name_1.BeginInit();
			this.Area_Name_1.BorderStyle = System.Windows.Forms.BorderStyle.None;
			this.Area_Name_1.DesignMatrix = new NxtControl.Drawing.Matrix2D(0.85333333333333339D, 0D, 0D, 1.5238095238095237D, 14D, 10D);
			this.Area_Name_1.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.Area_Name_1.FontScale = true;
			this.Area_Name_1.IsOnlyInput = true;
			this.Area_Name_1.Name = "Area_Name_1";
			this.Area_Name_1.Pen = new NxtControl.Drawing.Pen("LabelPen");
			this.Area_Name_1.TagName = "Area_Name";
			this.Area_Name_1.TextColor = new NxtControl.Drawing.Color("MedAir");
			this.Area_Name_1.EndInit();
			// 
			// sDefault
			// 
			this.Shapes.AddRange(new System.ComponentModel.IComponent[] {
			this.rectangle1,
			this.LL_Fault_Status,
			this.drawnTextBox1,
			this.freeText3,
			this.freeText4,
			this.System_Mode,
			this.System_Cycle_Type,
			this.Area_Name_1});
			this.SymbolSize = new System.Drawing.Size(1000, 190);

		}
		private NxtControl.GuiFramework.Rectangle rectangle1;
		private System.HMI.Symbols.Base.Led<bool> LL_Fault_Status;
		private NxtControl.GuiFramework.DrawnTextBox drawnTextBox1;
		private NxtControl.GuiFramework.FreeText freeText3;
		private NxtControl.GuiFramework.FreeText freeText4;
		private System.HMI.Symbols.Base.FreeText<short> System_Mode;
		private System.HMI.Symbols.Base.FreeText<short> System_Cycle_Type;
		private System.HMI.Symbols.Base.Label Area_Name_1;
		#endregion
	}
}
