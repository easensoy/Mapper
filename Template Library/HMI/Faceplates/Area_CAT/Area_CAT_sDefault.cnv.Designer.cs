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
			this.Fault_Reset = new System.HMI.Symbols.Base.CheckButton();
			this.LL_Fault_Status = new System.HMI.Symbols.Base.Led<bool>();
			this.drawnTextBox1 = new NxtControl.GuiFramework.DrawnTextBox();
			this.freeText3 = new NxtControl.GuiFramework.FreeText();
			this.freeText4 = new NxtControl.GuiFramework.FreeText();
			this.System_Mode = new System.HMI.Symbols.Base.FreeText<short>();
			this.System_Cycle_Type = new System.HMI.Symbols.Base.FreeText<short>();
			this.Area_Name_1 = new System.HMI.Symbols.Base.Label();
			this.AutoButton = new NxtControl.GuiFramework.DrawnButton();
			this.ManualButton = new NxtControl.GuiFramework.DrawnButton();
			this.SetupButton = new NxtControl.GuiFramework.DrawnButton();
			this.InitialPositionButton = new NxtControl.GuiFramework.DrawnButton();
			this.StopButton = new NxtControl.GuiFramework.DrawnButton();
			this.RunContiuouslyButton = new NxtControl.GuiFramework.DrawnButton();
			this.StopAtEndOfCycleButton = new NxtControl.GuiFramework.DrawnButton();
			this.SingleCycleRunButton = new NxtControl.GuiFramework.DrawnButton();
			this.OpenManualScreenButton = new NxtControl.GuiFramework.ChangeCanvasButton();
			this.OpenSetupScreenButton = new NxtControl.GuiFramework.ChangeCanvasButton();
			// 
			// rectangle1
			// 
			this.rectangle1.Bounds = new NxtControl.Drawing.RectF(((float)(0D)), ((float)(0D)), ((float)(1000D)), ((float)(182D)));
			this.rectangle1.Font = new NxtControl.Drawing.Font("HMI Sans Serif", 9F, System.Drawing.FontStyle.Regular);
			this.rectangle1.Name = "rectangle1";
			// 
			// Fault_Reset
			// 
			this.Fault_Reset.BeginInit();
			this.Fault_Reset.DesignMatrix = new NxtControl.Drawing.Matrix2D(1.8500000000000003D, 0D, 0D, 1.9333333333333333D, 632D, 48D);
			this.Fault_Reset.FalseImage = new NxtControl.Drawing.ImageHolder();
			this.Fault_Reset.FalseImageDisabled = new NxtControl.Drawing.ImageHolder();
			this.Fault_Reset.FalseText = "RESET";
			this.Fault_Reset.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.Fault_Reset.FontScale = true;
			this.Fault_Reset.Name = "Fault_Reset";
			this.Fault_Reset.TagName = "Fault_Reset";
			this.Fault_Reset.TrueImage = new NxtControl.Drawing.ImageHolder();
			this.Fault_Reset.TrueImageDisabled = new NxtControl.Drawing.ImageHolder();
			this.Fault_Reset.TrueText = "RESET";
			this.Fault_Reset.Value = false;
			this.Fault_Reset.Visible = false;
			this.Fault_Reset.EndInit();
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
			// AutoButton
			// 
			this.AutoButton.Bounds = new NxtControl.Drawing.RectF(((float)(16D)), ((float)(48D)), ((float)(148D)), ((float)(57D)));
			this.AutoButton.Brush = new NxtControl.Drawing.Brush("ButtonBrush");
			this.AutoButton.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.AutoButton.FontScale = true;
			this.AutoButton.InnerBorderColor = new NxtControl.Drawing.Color("ButtonInnerBorderColor");
			this.AutoButton.Name = "AutoButton";
			this.AutoButton.Pen = new NxtControl.Drawing.Pen("ButtonPen");
			this.AutoButton.Radius = 4D;
			this.AutoButton.Text = "AUTO";
			this.AutoButton.TextColor = new NxtControl.Drawing.Color("ButtonTextColor");
			this.AutoButton.TextColorMouseDown = new NxtControl.Drawing.Color("ButtonTextColorMouseDown");
			this.AutoButton.Use3DEffect = false;
			this.AutoButton.Click += new System.EventHandler(this.AutoButtonClick);
			// 
			// ManualButton
			// 
			this.ManualButton.Bounds = new NxtControl.Drawing.RectF(((float)(171D)), ((float)(48D)), ((float)(148D)), ((float)(57D)));
			this.ManualButton.Brush = new NxtControl.Drawing.Brush("ButtonBrush");
			this.ManualButton.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.ManualButton.FontScale = true;
			this.ManualButton.InnerBorderColor = new NxtControl.Drawing.Color("ButtonInnerBorderColor");
			this.ManualButton.Name = "ManualButton";
			this.ManualButton.Pen = new NxtControl.Drawing.Pen("ButtonPen");
			this.ManualButton.Radius = 4D;
			this.ManualButton.Text = "MANUAL";
			this.ManualButton.TextColor = new NxtControl.Drawing.Color("ButtonTextColor");
			this.ManualButton.TextColorMouseDown = new NxtControl.Drawing.Color("ButtonTextColorMouseDown");
			this.ManualButton.Use3DEffect = false;
			this.ManualButton.Click += new System.EventHandler(this.ManualButtonClick);
			// 
			// SetupButton
			// 
			this.SetupButton.Bounds = new NxtControl.Drawing.RectF(((float)(326D)), ((float)(48D)), ((float)(148D)), ((float)(57D)));
			this.SetupButton.Brush = new NxtControl.Drawing.Brush("ButtonBrush");
			this.SetupButton.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.SetupButton.FontScale = true;
			this.SetupButton.InnerBorderColor = new NxtControl.Drawing.Color("ButtonInnerBorderColor");
			this.SetupButton.Name = "SetupButton";
			this.SetupButton.Pen = new NxtControl.Drawing.Pen("ButtonPen");
			this.SetupButton.Radius = 4D;
			this.SetupButton.Text = "SETUP";
			this.SetupButton.TextColor = new NxtControl.Drawing.Color("ButtonTextColor");
			this.SetupButton.TextColorMouseDown = new NxtControl.Drawing.Color("ButtonTextColorMouseDown");
			this.SetupButton.Use3DEffect = false;
			this.SetupButton.Click += new System.EventHandler(this.SetupButtonClick);
			// 
			// InitialPositionButton
			// 
			this.InitialPositionButton.Bounds = new NxtControl.Drawing.RectF(((float)(480D)), ((float)(48D)), ((float)(148D)), ((float)(57D)));
			this.InitialPositionButton.Brush = new NxtControl.Drawing.Brush("ButtonBrush");
			this.InitialPositionButton.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.InitialPositionButton.FontScale = true;
			this.InitialPositionButton.InnerBorderColor = new NxtControl.Drawing.Color("ButtonInnerBorderColor");
			this.InitialPositionButton.Name = "InitialPositionButton";
			this.InitialPositionButton.Pen = new NxtControl.Drawing.Pen("ButtonPen");
			this.InitialPositionButton.Radius = 4D;
			this.InitialPositionButton.Text = "HOME";
			this.InitialPositionButton.TextColor = new NxtControl.Drawing.Color("ButtonTextColor");
			this.InitialPositionButton.TextColorMouseDown = new NxtControl.Drawing.Color("ButtonTextColorMouseDown");
			this.InitialPositionButton.Use3DEffect = false;
			this.InitialPositionButton.Click += new System.EventHandler(this.InitialPositionButtonClick);
			// 
			// StopButton
			// 
			this.StopButton.Bounds = new NxtControl.Drawing.RectF(((float)(16D)), ((float)(114D)), ((float)(148D)), ((float)(57D)));
			this.StopButton.Brush = new NxtControl.Drawing.Brush("ButtonBrush");
			this.StopButton.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.StopButton.FontScale = true;
			this.StopButton.InnerBorderColor = new NxtControl.Drawing.Color("ButtonInnerBorderColor");
			this.StopButton.Name = "StopButton";
			this.StopButton.Pen = new NxtControl.Drawing.Pen("ButtonPen");
			this.StopButton.Radius = 4D;
			this.StopButton.Text = "STOP";
			this.StopButton.TextColor = new NxtControl.Drawing.Color("ButtonTextColor");
			this.StopButton.TextColorMouseDown = new NxtControl.Drawing.Color("ButtonTextColorMouseDown");
			this.StopButton.Use3DEffect = false;
			this.StopButton.Click += new System.EventHandler(this.StopButtonClick);
			// 
			// RunContiuouslyButton
			// 
			this.RunContiuouslyButton.Bounds = new NxtControl.Drawing.RectF(((float)(170D)), ((float)(114D)), ((float)(148D)), ((float)(57D)));
			this.RunContiuouslyButton.Brush = new NxtControl.Drawing.Brush("ButtonBrush");
			this.RunContiuouslyButton.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.RunContiuouslyButton.FontScale = true;
			this.RunContiuouslyButton.InnerBorderColor = new NxtControl.Drawing.Color("ButtonInnerBorderColor");
			this.RunContiuouslyButton.Name = "RunContiuouslyButton";
			this.RunContiuouslyButton.Pen = new NxtControl.Drawing.Pen("ButtonPen");
			this.RunContiuouslyButton.Radius = 4D;
			this.RunContiuouslyButton.Text = "RUN";
			this.RunContiuouslyButton.TextColor = new NxtControl.Drawing.Color("ButtonTextColor");
			this.RunContiuouslyButton.TextColorMouseDown = new NxtControl.Drawing.Color("ButtonTextColorMouseDown");
			this.RunContiuouslyButton.Use3DEffect = false;
			this.RunContiuouslyButton.Click += new System.EventHandler(this.RunContiuouslyButtonClick);
			// 
			// StopAtEndOfCycleButton
			// 
			this.StopAtEndOfCycleButton.Bounds = new NxtControl.Drawing.RectF(((float)(324D)), ((float)(114D)), ((float)(148D)), ((float)(57D)));
			this.StopAtEndOfCycleButton.Brush = new NxtControl.Drawing.Brush("ButtonBrush");
			this.StopAtEndOfCycleButton.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.StopAtEndOfCycleButton.FontScale = true;
			this.StopAtEndOfCycleButton.InnerBorderColor = new NxtControl.Drawing.Color("ButtonInnerBorderColor");
			this.StopAtEndOfCycleButton.Name = "StopAtEndOfCycleButton";
			this.StopAtEndOfCycleButton.Pen = new NxtControl.Drawing.Pen("ButtonPen");
			this.StopAtEndOfCycleButton.Radius = 4D;
			this.StopAtEndOfCycleButton.Text = "STOP AT END";
			this.StopAtEndOfCycleButton.TextColor = new NxtControl.Drawing.Color("ButtonTextColor");
			this.StopAtEndOfCycleButton.TextColorMouseDown = new NxtControl.Drawing.Color("ButtonTextColorMouseDown");
			this.StopAtEndOfCycleButton.Use3DEffect = false;
			this.StopAtEndOfCycleButton.Click += new System.EventHandler(this.StopAtEndOfCycleButtonClick);
			// 
			// SingleCycleRunButton
			// 
			this.SingleCycleRunButton.Bounds = new NxtControl.Drawing.RectF(((float)(478D)), ((float)(114D)), ((float)(148D)), ((float)(57D)));
			this.SingleCycleRunButton.Brush = new NxtControl.Drawing.Brush("ButtonBrush");
			this.SingleCycleRunButton.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.SingleCycleRunButton.FontScale = true;
			this.SingleCycleRunButton.InnerBorderColor = new NxtControl.Drawing.Color("ButtonInnerBorderColor");
			this.SingleCycleRunButton.Name = "SingleCycleRunButton";
			this.SingleCycleRunButton.Pen = new NxtControl.Drawing.Pen("ButtonPen");
			this.SingleCycleRunButton.Radius = 4D;
			this.SingleCycleRunButton.Text = "SINGLE RUN";
			this.SingleCycleRunButton.TextColor = new NxtControl.Drawing.Color("ButtonTextColor");
			this.SingleCycleRunButton.TextColorMouseDown = new NxtControl.Drawing.Color("ButtonTextColorMouseDown");
			this.SingleCycleRunButton.Use3DEffect = false;
			this.SingleCycleRunButton.Click += new System.EventHandler(this.SingleCycleRunButtonClick);
			// 
			// OpenManualScreenButton
			// 
			this.OpenManualScreenButton.Bounds = new NxtControl.Drawing.RectF(((float)(632D)), ((float)(46D)), ((float)(148D)), ((float)(62D)));
			this.OpenManualScreenButton.Brush = new NxtControl.Drawing.Brush("ButtonFalseBrush");
			this.OpenManualScreenButton.CanvasName = "ManualScreen";
			this.OpenManualScreenButton.Enabled = false;
			this.OpenManualScreenButton.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.OpenManualScreenButton.Name = "OpenManualScreenButton";
			this.OpenManualScreenButton.Pen = new NxtControl.Drawing.Pen(new NxtControl.Drawing.Color(((byte)(255)), ((byte)(255)), ((byte)(255))), 1F, NxtControl.Drawing.DashStyle.Dash);
			this.OpenManualScreenButton.Text = "Open Manual Control";
			this.OpenManualScreenButton.TextColor = new NxtControl.Drawing.Color(((byte)(255)), ((byte)(255)), ((byte)(255)));
			this.OpenManualScreenButton.TextColorMouseDown = new NxtControl.Drawing.Color(((byte)(255)), ((byte)(255)), ((byte)(255)));
			this.OpenManualScreenButton.Visible = false;
			// 
			// OpenSetupScreenButton
			// 
			this.OpenSetupScreenButton.Bounds = new NxtControl.Drawing.RectF(((float)(630D)), ((float)(112D)), ((float)(148D)), ((float)(62D)));
			this.OpenSetupScreenButton.Brush = new NxtControl.Drawing.Brush("ButtonFalseBrush");
			this.OpenSetupScreenButton.CanvasName = "SetupScreen";
			this.OpenSetupScreenButton.Enabled = false;
			this.OpenSetupScreenButton.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.OpenSetupScreenButton.Name = "OpenSetupScreenButton";
			this.OpenSetupScreenButton.Pen = new NxtControl.Drawing.Pen(new NxtControl.Drawing.Color(((byte)(255)), ((byte)(255)), ((byte)(255))), 1F, NxtControl.Drawing.DashStyle.Dash);
			this.OpenSetupScreenButton.Text = "Open Setup Control";
			this.OpenSetupScreenButton.TextColor = new NxtControl.Drawing.Color(((byte)(255)), ((byte)(255)), ((byte)(255)));
			this.OpenSetupScreenButton.TextColorMouseDown = new NxtControl.Drawing.Color(((byte)(255)), ((byte)(255)), ((byte)(255)));
			this.OpenSetupScreenButton.Visible = false;
			// 
			// sDefault
			// 
			this.Shapes.AddRange(new System.ComponentModel.IComponent[] {
			this.rectangle1,
			this.Fault_Reset,
			this.LL_Fault_Status,
			this.drawnTextBox1,
			this.freeText3,
			this.freeText4,
			this.System_Mode,
			this.System_Cycle_Type,
			this.Area_Name_1,
			this.AutoButton,
			this.ManualButton,
			this.SetupButton,
			this.InitialPositionButton,
			this.StopButton,
			this.RunContiuouslyButton,
			this.StopAtEndOfCycleButton,
			this.SingleCycleRunButton,
			this.OpenManualScreenButton,
			this.OpenSetupScreenButton});
			this.SymbolSize = new System.Drawing.Size(1000, 190);

		}
		private NxtControl.GuiFramework.Rectangle rectangle1;
		private System.HMI.Symbols.Base.CheckButton Fault_Reset;
		private System.HMI.Symbols.Base.Led<bool> LL_Fault_Status;
		private NxtControl.GuiFramework.DrawnTextBox drawnTextBox1;
		private NxtControl.GuiFramework.FreeText freeText3;
		private NxtControl.GuiFramework.FreeText freeText4;
		private System.HMI.Symbols.Base.FreeText<short> System_Mode;
		private System.HMI.Symbols.Base.FreeText<short> System_Cycle_Type;
		private System.HMI.Symbols.Base.Label Area_Name_1;
		private NxtControl.GuiFramework.DrawnButton AutoButton;
		private NxtControl.GuiFramework.DrawnButton ManualButton;
		private NxtControl.GuiFramework.DrawnButton SetupButton;
		private NxtControl.GuiFramework.DrawnButton InitialPositionButton;
		private NxtControl.GuiFramework.DrawnButton StopButton;
		private NxtControl.GuiFramework.DrawnButton RunContiuouslyButton;
		private NxtControl.GuiFramework.DrawnButton StopAtEndOfCycleButton;
		private NxtControl.GuiFramework.DrawnButton SingleCycleRunButton;
		private NxtControl.GuiFramework.ChangeCanvasButton OpenManualScreenButton;
		private NxtControl.GuiFramework.ChangeCanvasButton OpenSetupScreenButton;
		#endregion
	}
}
