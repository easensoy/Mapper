/*
 * Created by EcoStruxure Automation Expert.
 * User:  
 * Date: 10/7/2025
 * Time: 10:13 AM
 * 
 */
using System;
using System.ComponentModel;
using System.Collections;
using NxtControl.GuiFramework;

namespace HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT
{
	/// <summary>
	/// Summary description for sSetup.
	/// </summary>
	partial class sSetup
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
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary4 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary5 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary6 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary1 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary8 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary9 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary7 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary11 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary12 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary10 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary14 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary15 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary13 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary17 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary18 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary16 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary20 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary21 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary19 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary23 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary24 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary22 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary26 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary27 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary25 = new NxtControl.GuiFramework.PropertyDictionary();
			this.current_state_to_process = new System.HMI.Symbols.Base.FreeText<short>();
			this.rectangle1 = new NxtControl.GuiFramework.Rectangle();
			this.toWork1PLC = new System.HMI.Symbols.Base.Led<bool>();
			this.atHome = new System.HMI.Symbols.Base.Led<bool>();
			this.atWork1 = new System.HMI.Symbols.Base.Led<bool>();
			this.freeText1 = new NxtControl.GuiFramework.FreeText();
			this.freeText2 = new NxtControl.GuiFramework.FreeText();
			this.freeText3 = new NxtControl.GuiFramework.FreeText();
			this.toWork2PLC = new System.HMI.Symbols.Base.Led<bool>();
			this.freeText4 = new NxtControl.GuiFramework.FreeText();
			this.fault_code = new System.HMI.Symbols.Base.TextBox<short>();
			this.freeText5 = new NxtControl.GuiFramework.FreeText();
			this.component_name = new System.HMI.Symbols.Base.FreeText();
			this.freeText6 = new NxtControl.GuiFramework.FreeText();
			this.freeText7 = new NxtControl.GuiFramework.FreeText();
			this.freeText8 = new NxtControl.GuiFramework.FreeText();
			this.atWork2 = new System.HMI.Symbols.Base.Led<bool>();
			this.freeText9 = new NxtControl.GuiFramework.FreeText();
			this.Work1Interlock = new System.HMI.Symbols.Base.Led<bool>();
			this.Work2Interlock = new System.HMI.Symbols.Base.Led<bool>();
			this.toWork1 = new System.HMI.Symbols.Base.CheckButton();
			this.toHome = new System.HMI.Symbols.Base.CheckButton();
			this.toWork2 = new System.HMI.Symbols.Base.CheckButton();
			// 
			// current_state_to_process
			// 
			this.current_state_to_process.BeginInit();
			this.current_state_to_process.DecimalPlacesCount = ((uint)(2u));
			this.current_state_to_process.DesignMatrix = new NxtControl.Drawing.Matrix2D(1D, 0D, 0D, 1D, 72D, 48D);
			this.current_state_to_process.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.current_state_to_process.IsOnlyInput = true;
			this.current_state_to_process.Name = "current_state_to_process";
			propertyDictionary2.Add("Text", "At Home Initial");
			propertyDictionary2.Add("TextColor", new NxtControl.Drawing.Color(((byte)(0)), ((byte)(0)), ((byte)(0))));
			propertyDictionary3.Add("Text", "Moving To Work");
			propertyDictionary3.Add("TextColor", new NxtControl.Drawing.Color(((byte)(178)), ((byte)(14)), ((byte)(18))));
			propertyDictionary4.Add("Text", "   At Work");
			propertyDictionary4.Add("TextColor", new NxtControl.Drawing.Color(((byte)(26)), ((byte)(170)), ((byte)(66))));
			propertyDictionary5.Add("Text", "Moving To Home");
			propertyDictionary5.Add("TextColor", new NxtControl.Drawing.Color(((byte)(178)), ((byte)(14)), ((byte)(18))));
			propertyDictionary6.Add("Text", "  At Home End");
			propertyDictionary6.Add("TextColor", new NxtControl.Drawing.Color(((byte)(26)), ((byte)(170)), ((byte)(66))));
			this.current_state_to_process.Ranges.Clear();
			this.current_state_to_process.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(0)), propertyDictionary2));
			this.current_state_to_process.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(1)), propertyDictionary3));
			this.current_state_to_process.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(2)), propertyDictionary4));
			this.current_state_to_process.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(3)), propertyDictionary5));
			this.current_state_to_process.Ranges.Add(new NxtControl.GuiFramework.Range<short>(((short)(4)), propertyDictionary6));
			propertyDictionary1.Add("Text", "${Value}");
			propertyDictionary1.Add("TextColor", new NxtControl.Drawing.Color("AlarmControlForeCellColor"));
			this.current_state_to_process.Ranges.DefaultPropertyValues = propertyDictionary1;
			this.current_state_to_process.TagName = "current_state_to_process";
			this.current_state_to_process.TextAngle = 0F;
			this.current_state_to_process.EndInit();
			// 
			// rectangle1
			// 
			this.rectangle1.Bounds = new NxtControl.Drawing.RectF(((float)(0D)), ((float)(40D)), ((float)(300D)), ((float)(40D)));
			this.rectangle1.Font = new NxtControl.Drawing.Font("HMI Sans Serif", 9F, System.Drawing.FontStyle.Regular);
			this.rectangle1.Name = "rectangle1";
			// 
			// toWork1PLC
			// 
			this.toWork1PLC.BeginInit();
			this.toWork1PLC.ColorFrame = new NxtControl.Drawing.Color("LedFrameColor");
			this.toWork1PLC.DesignMatrix = new NxtControl.Drawing.Matrix2D(2D, 0D, 0D, 2D, 123D, 97D);
			this.toWork1PLC.FrameSize = 33F;
			this.toWork1PLC.IsOnlyInput = true;
			this.toWork1PLC.Name = "toWork1PLC";
			propertyDictionary8.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			propertyDictionary9.Add("Color", new NxtControl.Drawing.Color("LedTrueColor"));
			this.toWork1PLC.Ranges.Clear();
			this.toWork1PLC.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(false, propertyDictionary8));
			this.toWork1PLC.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(true, propertyDictionary9));
			propertyDictionary7.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			this.toWork1PLC.Ranges.DefaultPropertyValues = propertyDictionary7;
			this.toWork1PLC.TagName = "toWork1PLC";
			this.toWork1PLC.EndInit();
			// 
			// atHome
			// 
			this.atHome.BeginInit();
			this.atHome.ColorFrame = new NxtControl.Drawing.Color("LedFrameColor");
			this.atHome.DesignMatrix = new NxtControl.Drawing.Matrix2D(2D, 0D, 0D, 2D, 268D, 97D);
			this.atHome.FrameSize = 33F;
			this.atHome.IsOnlyInput = true;
			this.atHome.Name = "atHome";
			propertyDictionary11.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			propertyDictionary12.Add("Color", new NxtControl.Drawing.Color("LedTrueColor"));
			this.atHome.Ranges.Clear();
			this.atHome.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(false, propertyDictionary11));
			this.atHome.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(true, propertyDictionary12));
			propertyDictionary10.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			this.atHome.Ranges.DefaultPropertyValues = propertyDictionary10;
			this.atHome.TagName = "atHome";
			this.atHome.EndInit();
			// 
			// atWork1
			// 
			this.atWork1.BeginInit();
			this.atWork1.ColorFrame = new NxtControl.Drawing.Color("LedFrameColor");
			this.atWork1.DesignMatrix = new NxtControl.Drawing.Matrix2D(2D, 0D, 0D, 2D, 268D, 125D);
			this.atWork1.FrameSize = 33F;
			this.atWork1.IsOnlyInput = true;
			this.atWork1.Name = "atWork1";
			propertyDictionary14.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			propertyDictionary15.Add("Color", new NxtControl.Drawing.Color("LedTrueColor"));
			this.atWork1.Ranges.Clear();
			this.atWork1.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(false, propertyDictionary14));
			this.atWork1.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(true, propertyDictionary15));
			propertyDictionary13.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			this.atWork1.Ranges.DefaultPropertyValues = propertyDictionary13;
			this.atWork1.TagName = "atWork1";
			this.atWork1.EndInit();
			// 
			// freeText1
			// 
			this.freeText1.Color = new NxtControl.Drawing.Color(((byte)(0)), ((byte)(0)), ((byte)(0)));
			this.freeText1.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.freeText1.Location = new NxtControl.Drawing.PointF(8D, 83D);
			this.freeText1.Name = "freeText1";
			this.freeText1.Text = "To Work1";
			// 
			// freeText2
			// 
			this.freeText2.Color = new NxtControl.Drawing.Color(((byte)(0)), ((byte)(0)), ((byte)(0)));
			this.freeText2.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.freeText2.Location = new NxtControl.Drawing.PointF(156D, 83D);
			this.freeText2.Name = "freeText2";
			this.freeText2.Text = "At Home";
			// 
			// freeText3
			// 
			this.freeText3.Color = new NxtControl.Drawing.Color(((byte)(0)), ((byte)(0)), ((byte)(0)));
			this.freeText3.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.freeText3.Location = new NxtControl.Drawing.PointF(156D, 111D);
			this.freeText3.Name = "freeText3";
			this.freeText3.Text = "At Work1";
			// 
			// toWork2PLC
			// 
			this.toWork2PLC.BeginInit();
			this.toWork2PLC.ColorFrame = new NxtControl.Drawing.Color("LedFrameColor");
			this.toWork2PLC.DesignMatrix = new NxtControl.Drawing.Matrix2D(2D, 0D, 0D, 2D, 124D, 125D);
			this.toWork2PLC.FrameSize = 33F;
			this.toWork2PLC.IsOnlyInput = true;
			this.toWork2PLC.Name = "toWork2PLC";
			propertyDictionary17.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			propertyDictionary18.Add("Color", new NxtControl.Drawing.Color("LedTrueColor"));
			this.toWork2PLC.Ranges.Clear();
			this.toWork2PLC.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(false, propertyDictionary17));
			this.toWork2PLC.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(true, propertyDictionary18));
			propertyDictionary16.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			this.toWork2PLC.Ranges.DefaultPropertyValues = propertyDictionary16;
			this.toWork2PLC.TagName = "toWork2PLC";
			this.toWork2PLC.EndInit();
			// 
			// freeText4
			// 
			this.freeText4.Color = new NxtControl.Drawing.Color(((byte)(0)), ((byte)(0)), ((byte)(0)));
			this.freeText4.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.freeText4.Location = new NxtControl.Drawing.PointF(8D, 111D);
			this.freeText4.Name = "freeText4";
			this.freeText4.Text = "To Work2";
			// 
			// fault_code
			// 
			this.fault_code.BeginInit();
			this.fault_code.Brush = new NxtControl.Drawing.Brush(new NxtControl.Drawing.Color(((byte)(234)), ((byte)(22)), ((byte)(30))));
			this.fault_code.DecimalPlacesCount = ((uint)(0u));
			this.fault_code.DesignMatrix = new NxtControl.Drawing.Matrix2D(0.48D, 0D, 0D, 1D, 224D, 0D);
			this.fault_code.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.fault_code.IsOnlyInput = true;
			this.fault_code.MaximumTag = null;
			this.fault_code.MinimumTag = null;
			this.fault_code.Name = "fault_code";
			this.fault_code.NumberBase = NxtControl.GuiFramework.NumberBase.Decimal;
			this.fault_code.Pen = new NxtControl.Drawing.Pen("TextBoxPen");
			this.fault_code.Prefix = "Fault";
			this.fault_code.SetColor = new NxtControl.Drawing.Color("Yellow");
			this.fault_code.TagName = "fault_code";
			this.fault_code.TextAlignment = NxtControl.Drawing.ContentAlignment.MiddleLeft;
			this.fault_code.TextColor = new NxtControl.Drawing.Color(((byte)(0)), ((byte)(0)), ((byte)(0)));
			this.fault_code.Value = ((short)(0));
			this.fault_code.EndInit();
			// 
			// freeText5
			// 
			this.freeText5.Color = new NxtControl.Drawing.Color("LabelTextColor");
			this.freeText5.Font = new NxtControl.Drawing.Font("LabelFont");
			this.freeText5.Location = new NxtControl.Drawing.PointF(24D, 168D);
			this.freeText5.Name = "freeText5";
			// 
			// component_name
			// 
			this.component_name.BeginInit();
			this.component_name.DesignMatrix = new NxtControl.Drawing.Matrix2D(1D, 0D, 0D, 1D, 8D, 8D);
			this.component_name.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.component_name.IsOnlyInput = true;
			this.component_name.Name = "component_name";
			this.component_name.TagName = "component_name";
			this.component_name.TextAngle = 0F;
			this.component_name.TextColor = new NxtControl.Drawing.Color("Black");
			this.component_name.EndInit();
			// 
			// freeText6
			// 
			this.freeText6.Color = new NxtControl.Drawing.Color(((byte)(0)), ((byte)(0)), ((byte)(0)));
			this.freeText6.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.freeText6.Location = new NxtControl.Drawing.PointF(8D, 216D);
			this.freeText6.Name = "freeText6";
			this.freeText6.Text = "Home <> Work1 Blocked";
			// 
			// freeText7
			// 
			this.freeText7.Color = new NxtControl.Drawing.Color(((byte)(0)), ((byte)(0)), ((byte)(0)));
			this.freeText7.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.freeText7.Location = new NxtControl.Drawing.PointF(8D, 244D);
			this.freeText7.Name = "freeText7";
			this.freeText7.Text = "Home <> Work2 Blocked";
			// 
			// freeText8
			// 
			this.freeText8.Color = new NxtControl.Drawing.Color("LabelTextColor");
			this.freeText8.Font = new NxtControl.Drawing.Font("LabelFont");
			this.freeText8.Location = new NxtControl.Drawing.PointF(24D, 168D);
			this.freeText8.Name = "freeText8";
			// 
			// atWork2
			// 
			this.atWork2.BeginInit();
			this.atWork2.ColorFrame = new NxtControl.Drawing.Color("LedFrameColor");
			this.atWork2.DesignMatrix = new NxtControl.Drawing.Matrix2D(2D, 0D, 0D, 2D, 268D, 152D);
			this.atWork2.FrameSize = 33F;
			this.atWork2.IsOnlyInput = true;
			this.atWork2.Name = "atWork2";
			propertyDictionary20.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			propertyDictionary21.Add("Color", new NxtControl.Drawing.Color("LedTrueColor"));
			this.atWork2.Ranges.Clear();
			this.atWork2.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(false, propertyDictionary20));
			this.atWork2.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(true, propertyDictionary21));
			propertyDictionary19.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			this.atWork2.Ranges.DefaultPropertyValues = propertyDictionary19;
			this.atWork2.TagName = "atWork2";
			this.atWork2.EndInit();
			// 
			// freeText9
			// 
			this.freeText9.Color = new NxtControl.Drawing.Color(((byte)(0)), ((byte)(0)), ((byte)(0)));
			this.freeText9.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.freeText9.Location = new NxtControl.Drawing.PointF(156D, 138D);
			this.freeText9.Name = "freeText9";
			this.freeText9.Text = "At Work2";
			// 
			// Work1Interlock
			// 
			this.Work1Interlock.BeginInit();
			this.Work1Interlock.ColorFrame = new NxtControl.Drawing.Color("LedFrameColor");
			this.Work1Interlock.DesignMatrix = new NxtControl.Drawing.Matrix2D(2D, 0D, 0D, 2D, 269D, 229D);
			this.Work1Interlock.FrameSize = 33F;
			this.Work1Interlock.IsOnlyInput = true;
			this.Work1Interlock.Name = "Work1Interlock";
			propertyDictionary23.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			propertyDictionary24.Add("Color", new NxtControl.Drawing.Color("LedTrueColor"));
			this.Work1Interlock.Ranges.Clear();
			this.Work1Interlock.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(false, propertyDictionary23));
			this.Work1Interlock.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(true, propertyDictionary24));
			propertyDictionary22.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			this.Work1Interlock.Ranges.DefaultPropertyValues = propertyDictionary22;
			this.Work1Interlock.TagName = "Work1Interlock";
			this.Work1Interlock.EndInit();
			// 
			// Work2Interlock
			// 
			this.Work2Interlock.BeginInit();
			this.Work2Interlock.ColorFrame = new NxtControl.Drawing.Color("LedFrameColor");
			this.Work2Interlock.DesignMatrix = new NxtControl.Drawing.Matrix2D(2D, 0D, 0D, 2D, 269D, 258D);
			this.Work2Interlock.FrameSize = 33F;
			this.Work2Interlock.IsOnlyInput = true;
			this.Work2Interlock.Name = "Work2Interlock";
			propertyDictionary26.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			propertyDictionary27.Add("Color", new NxtControl.Drawing.Color("LedTrueColor"));
			this.Work2Interlock.Ranges.Clear();
			this.Work2Interlock.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(false, propertyDictionary26));
			this.Work2Interlock.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(true, propertyDictionary27));
			propertyDictionary25.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			this.Work2Interlock.Ranges.DefaultPropertyValues = propertyDictionary25;
			this.Work2Interlock.TagName = "Work2Interlock";
			this.Work2Interlock.EndInit();
			//
			// toWork1
			//
			this.toWork1.BeginInit();
			this.toWork1.DesignMatrix = new NxtControl.Drawing.Matrix2D(1D, 0D, 0D, 1D, 8D, 296D);
			this.toWork1.FalseImage = new NxtControl.Drawing.ImageHolder();
			this.toWork1.FalseImageDisabled = new NxtControl.Drawing.ImageHolder();
			this.toWork1.FalseText = "To Work 1";
			this.toWork1.Font = new NxtControl.Drawing.Font("ButtonFont");
			this.toWork1.FontScale = false;
			this.toWork1.Name = "toWork1";
			this.toWork1.TagName = "toWork1";
			this.toWork1.TrueImage = new NxtControl.Drawing.ImageHolder();
			this.toWork1.TrueImageDisabled = new NxtControl.Drawing.ImageHolder();
			this.toWork1.Value = false;
			this.toWork1.EndInit();
			//
			// toHome
			//
			this.toHome.BeginInit();
			this.toHome.DesignMatrix = new NxtControl.Drawing.Matrix2D(1D, 0D, 0D, 1D, 118D, 296D);
			this.toHome.FalseImage = new NxtControl.Drawing.ImageHolder();
			this.toHome.FalseImageDisabled = new NxtControl.Drawing.ImageHolder();
			this.toHome.FalseText = "To Home";
			this.toHome.Font = new NxtControl.Drawing.Font("ButtonFont");
			this.toHome.FontScale = false;
			this.toHome.Name = "toHome";
			this.toHome.TagName = "toHome";
			this.toHome.TrueImage = new NxtControl.Drawing.ImageHolder();
			this.toHome.TrueImageDisabled = new NxtControl.Drawing.ImageHolder();
			this.toHome.Value = false;
			this.toHome.EndInit();
			//
			// toWork2
			//
			this.toWork2.BeginInit();
			this.toWork2.DesignMatrix = new NxtControl.Drawing.Matrix2D(1D, 0D, 0D, 1D, 228D, 296D);
			this.toWork2.FalseImage = new NxtControl.Drawing.ImageHolder();
			this.toWork2.FalseImageDisabled = new NxtControl.Drawing.ImageHolder();
			this.toWork2.FalseText = "To Work 2";
			this.toWork2.Font = new NxtControl.Drawing.Font("ButtonFont");
			this.toWork2.FontScale = false;
			this.toWork2.Name = "toWork2";
			this.toWork2.TagName = "toWork2";
			this.toWork2.TrueImage = new NxtControl.Drawing.ImageHolder();
			this.toWork2.TrueImageDisabled = new NxtControl.Drawing.ImageHolder();
			this.toWork2.Value = false;
			this.toWork2.EndInit();
			// 
			// sSetup
			// 
			this.Shapes.AddRange(new System.ComponentModel.IComponent[] {
			this.rectangle1,
			this.current_state_to_process,
			this.toWork1PLC,
			this.atHome,
			this.atWork1,
			this.freeText1,
			this.freeText2,
			this.freeText3,
			this.toWork2PLC,
			this.freeText4,
			this.fault_code,
			this.freeText5,
			this.component_name,
			this.freeText6,
			this.freeText7,
			this.freeText8,
			this.atWork2,
			this.freeText9,
			this.Work1Interlock,
			this.Work2Interlock,
			this.toWork1,
			this.toHome,
			this.toWork2});
			this.SymbolSize = new System.Drawing.Size(340, 360);

		}
		private System.HMI.Symbols.Base.FreeText<short> current_state_to_process;
		private NxtControl.GuiFramework.Rectangle rectangle1;
		private System.HMI.Symbols.Base.Led<bool> toWork1PLC;
		private System.HMI.Symbols.Base.Led<bool> atHome;
		private System.HMI.Symbols.Base.Led<bool> atWork1;
		private NxtControl.GuiFramework.FreeText freeText1;
		private NxtControl.GuiFramework.FreeText freeText2;
		private NxtControl.GuiFramework.FreeText freeText3;
		private System.HMI.Symbols.Base.Led<bool> toWork2PLC;
		private NxtControl.GuiFramework.FreeText freeText4;
		private System.HMI.Symbols.Base.TextBox<short> fault_code;
		private NxtControl.GuiFramework.FreeText freeText5;
		private System.HMI.Symbols.Base.FreeText component_name;
		private NxtControl.GuiFramework.FreeText freeText6;
		private NxtControl.GuiFramework.FreeText freeText7;
		private NxtControl.GuiFramework.FreeText freeText8;
		private System.HMI.Symbols.Base.Led<bool> atWork2;
		private NxtControl.GuiFramework.FreeText freeText9;
		private System.HMI.Symbols.Base.Led<bool> Work1Interlock;
		private System.HMI.Symbols.Base.Led<bool> Work2Interlock;
		private System.HMI.Symbols.Base.CheckButton toWork1;
		private System.HMI.Symbols.Base.CheckButton toHome;
		private System.HMI.Symbols.Base.CheckButton toWork2;
		#endregion
	}
}
