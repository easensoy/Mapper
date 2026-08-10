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

namespace HMI.Main.Symbols.Five_State_Actuator_CAT
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
			System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(sDefault));
			this.current_state_to_process = new System.HMI.Symbols.Base.FreeText<short>();
			this.rectangle1 = new NxtControl.GuiFramework.Rectangle();
			this.toWorkPLC = new System.HMI.Symbols.Base.Led<bool>();
			this.atHome = new System.HMI.Symbols.Base.Led<bool>();
			this.atWork = new System.HMI.Symbols.Base.Led<bool>();
			this.freeText1 = new NxtControl.GuiFramework.FreeText();
			this.freeText2 = new NxtControl.GuiFramework.FreeText();
			this.freeText3 = new NxtControl.GuiFramework.FreeText();
			this.toHomePLC = new System.HMI.Symbols.Base.Led<bool>();
			this.freeText4 = new NxtControl.GuiFramework.FreeText();
			this.fault_code = new System.HMI.Symbols.Base.TextBox<short>();
			this.freeText5 = new NxtControl.GuiFramework.FreeText();
			this.component_name = new System.HMI.Symbols.Base.FreeText();
			this.freeText6 = new NxtControl.GuiFramework.FreeText();
			this.freeText7 = new NxtControl.GuiFramework.FreeText();
			this.Work1Interlock = new System.HMI.Symbols.Base.Led<bool>();
			this.HomeInterlock = new System.HMI.Symbols.Base.Led<bool>();
			this.component_name_1 = new System.HMI.Symbols.Base.FreeText();
			this.component_name_2 = new System.HMI.Symbols.Base.Label();
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
			// toWorkPLC
			// 
			this.toWorkPLC.BeginInit();
			this.toWorkPLC.ColorFrame = new NxtControl.Drawing.Color("LedFrameColor");
			this.toWorkPLC.DesignMatrix = new NxtControl.Drawing.Matrix2D(2D, 0D, 0D, 2D, 124D, 96D);
			this.toWorkPLC.FrameSize = 33F;
			this.toWorkPLC.IsOnlyInput = true;
			this.toWorkPLC.Name = "toWorkPLC";
			propertyDictionary8.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			propertyDictionary9.Add("Color", new NxtControl.Drawing.Color("LedTrueColor"));
			this.toWorkPLC.Ranges.Clear();
			this.toWorkPLC.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(false, propertyDictionary8));
			this.toWorkPLC.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(true, propertyDictionary9));
			propertyDictionary7.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			this.toWorkPLC.Ranges.DefaultPropertyValues = propertyDictionary7;
			this.toWorkPLC.TagName = "toWorkPLC";
			this.toWorkPLC.EndInit();
			// 
			// atHome
			// 
			this.atHome.BeginInit();
			this.atHome.ColorFrame = new NxtControl.Drawing.Color("LedFrameColor");
			this.atHome.DesignMatrix = new NxtControl.Drawing.Matrix2D(2D, 0D, 0D, 2D, 268D, 96D);
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
			// atWork
			// 
			this.atWork.BeginInit();
			this.atWork.ColorFrame = new NxtControl.Drawing.Color("LedFrameColor");
			this.atWork.DesignMatrix = new NxtControl.Drawing.Matrix2D(2D, 0D, 0D, 2D, 268D, 124D);
			this.atWork.FrameSize = 33F;
			this.atWork.IsOnlyInput = true;
			this.atWork.Name = "atWork";
			propertyDictionary14.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			propertyDictionary15.Add("Color", new NxtControl.Drawing.Color("LedTrueColor"));
			this.atWork.Ranges.Clear();
			this.atWork.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(false, propertyDictionary14));
			this.atWork.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(true, propertyDictionary15));
			propertyDictionary13.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			this.atWork.Ranges.DefaultPropertyValues = propertyDictionary13;
			this.atWork.TagName = "atWork";
			this.atWork.EndInit();
			// 
			// freeText1
			// 
			this.freeText1.Color = new NxtControl.Drawing.Color(((byte)(0)), ((byte)(0)), ((byte)(0)));
			this.freeText1.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.freeText1.Location = new NxtControl.Drawing.PointF(16D, 82D);
			this.freeText1.Name = "freeText1";
			this.freeText1.Text = "To Work";
			// 
			// freeText2
			// 
			this.freeText2.Color = new NxtControl.Drawing.Color(((byte)(0)), ((byte)(0)), ((byte)(0)));
			this.freeText2.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.freeText2.Location = new NxtControl.Drawing.PointF(156D, 82D);
			this.freeText2.Name = "freeText2";
			this.freeText2.Text = "At Home";
			// 
			// freeText3
			// 
			this.freeText3.Color = new NxtControl.Drawing.Color(((byte)(0)), ((byte)(0)), ((byte)(0)));
			this.freeText3.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.freeText3.Location = new NxtControl.Drawing.PointF(156D, 111D);
			this.freeText3.Name = "freeText3";
			this.freeText3.Text = "At Work";
			// 
			// toHomePLC
			// 
			this.toHomePLC.BeginInit();
			this.toHomePLC.ColorFrame = new NxtControl.Drawing.Color("LedFrameColor");
			this.toHomePLC.DesignMatrix = new NxtControl.Drawing.Matrix2D(2D, 0D, 0D, 2D, 124D, 124D);
			this.toHomePLC.FrameSize = 33F;
			this.toHomePLC.IsOnlyInput = true;
			this.toHomePLC.Name = "toHomePLC";
			propertyDictionary17.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			propertyDictionary18.Add("Color", new NxtControl.Drawing.Color("LedTrueColor"));
			this.toHomePLC.Ranges.Clear();
			this.toHomePLC.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(false, propertyDictionary17));
			this.toHomePLC.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(true, propertyDictionary18));
			propertyDictionary16.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			this.toHomePLC.Ranges.DefaultPropertyValues = propertyDictionary16;
			this.toHomePLC.TagName = "toHomePLC";
			this.toHomePLC.EndInit();
			// 
			// freeText4
			// 
			this.freeText4.Color = new NxtControl.Drawing.Color(((byte)(0)), ((byte)(0)), ((byte)(0)));
			this.freeText4.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.freeText4.Location = new NxtControl.Drawing.PointF(16D, 112D);
			this.freeText4.Name = "freeText4";
			this.freeText4.Text = "To Home";
			// 
			// fault_code
			// 
			this.fault_code.BeginInit();
			this.fault_code.Brush = new NxtControl.Drawing.Brush(new NxtControl.Drawing.Color(((byte)(234)), ((byte)(22)), ((byte)(30))));
			this.fault_code.DecimalPlacesCount = ((uint)(0u));
			this.fault_code.DesignMatrix = new NxtControl.Drawing.Matrix2D(0.48D, 0D, 0D, 1D, 216D, 0D);
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
			this.freeText5.Location = new NxtControl.Drawing.PointF(24D, 144D);
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
			this.freeText6.Location = new NxtControl.Drawing.PointF(16D, 144D);
			this.freeText6.Name = "freeText6";
			this.freeText6.Text = "To Work Blocked";
			// 
			// freeText7
			// 
			this.freeText7.Color = new NxtControl.Drawing.Color(((byte)(0)), ((byte)(0)), ((byte)(0)));
			this.freeText7.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.freeText7.Location = new NxtControl.Drawing.PointF(16D, 176D);
			this.freeText7.Name = "freeText7";
			this.freeText7.Text = "To Home Blocked";
			// 
			// Work1Interlock
			// 
			this.Work1Interlock.BeginInit();
			this.Work1Interlock.ColorFrame = new NxtControl.Drawing.Color("LedFrameColor");
			this.Work1Interlock.DesignMatrix = new NxtControl.Drawing.Matrix2D(2D, 0D, 0D, 2D, 204D, 156D);
			this.Work1Interlock.FrameSize = 33F;
			this.Work1Interlock.IsOnlyInput = true;
			this.Work1Interlock.Name = "Work1Interlock";
			propertyDictionary20.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			propertyDictionary21.Add("Color", new NxtControl.Drawing.Color("LedTrueColor"));
			this.Work1Interlock.Ranges.Clear();
			this.Work1Interlock.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(false, propertyDictionary20));
			this.Work1Interlock.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(true, propertyDictionary21));
			propertyDictionary19.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			this.Work1Interlock.Ranges.DefaultPropertyValues = propertyDictionary19;
			this.Work1Interlock.TagName = "Work1Interlock";
			this.Work1Interlock.EndInit();
			// 
			// HomeInterlock
			// 
			this.HomeInterlock.BeginInit();
			this.HomeInterlock.ColorFrame = new NxtControl.Drawing.Color("LedFrameColor");
			this.HomeInterlock.DesignMatrix = new NxtControl.Drawing.Matrix2D(2D, 0D, 0D, 2D, 204D, 188D);
			this.HomeInterlock.FrameSize = 33F;
			this.HomeInterlock.IsOnlyInput = true;
			this.HomeInterlock.Name = "HomeInterlock";
			propertyDictionary23.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			propertyDictionary24.Add("Color", new NxtControl.Drawing.Color("LedTrueColor"));
			this.HomeInterlock.Ranges.Clear();
			this.HomeInterlock.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(false, propertyDictionary23));
			this.HomeInterlock.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(true, propertyDictionary24));
			propertyDictionary22.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			this.HomeInterlock.Ranges.DefaultPropertyValues = propertyDictionary22;
			this.HomeInterlock.TagName = "HomeInterlock";
			this.HomeInterlock.EndInit();
			// 
			// component_name_1
			// 
			this.component_name_1.BeginInit();
			this.component_name_1.DesignMatrix = new NxtControl.Drawing.Matrix2D(23.5D, 0D, 0D, 1D, 16D, 8D);
			this.component_name_1.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.component_name_1.IsOnlyInput = true;
			this.component_name_1.Name = "component_name_1";
			this.component_name_1.TagName = "component_name";
			this.component_name_1.TextAngle = 0F;
			this.component_name_1.EndInit();
			// 
			// component_name_2
			// 
			this.component_name_2.BeginInit();
			this.component_name_2.BorderStyle = System.Windows.Forms.BorderStyle.None;
			this.component_name_2.Brush = new NxtControl.Drawing.Brush("ComboBoxBrush");
			this.component_name_2.DesignMatrix = new NxtControl.Drawing.Matrix2D(1.0666666666666667D, 0D, 0D, 1.1428571428571428D, 0D, 8D);
			this.component_name_2.FontScale = false;
			this.component_name_2.IsOnlyInput = true;
			this.component_name_2.Name = "component_name_2";
			this.component_name_2.Pen = new NxtControl.Drawing.Pen("LabelPen");
			this.component_name_2.TagName = "component_name";
			this.component_name_2.EndInit();
			// 
			// sDefault
			// 
			this.Shapes.AddRange(new System.ComponentModel.IComponent[] {
			this.rectangle1,
			this.current_state_to_process,
			this.toWorkPLC,
			this.atHome,
			this.atWork,
			this.freeText1,
			this.freeText2,
			this.freeText3,
			this.toHomePLC,
			this.freeText4,
			this.fault_code,
			this.freeText5,
			this.component_name,
			this.freeText6,
			this.freeText7,
			this.Work1Interlock,
			this.HomeInterlock,
			this.component_name_1,
			this.component_name_2});
			this.SymbolSize = new System.Drawing.Size(300, 204);

		}
		private System.HMI.Symbols.Base.FreeText<short> current_state_to_process;
		private NxtControl.GuiFramework.Rectangle rectangle1;
		private System.HMI.Symbols.Base.Led<bool> toWorkPLC;
		private System.HMI.Symbols.Base.Led<bool> atHome;
		private System.HMI.Symbols.Base.Led<bool> atWork;
		private NxtControl.GuiFramework.FreeText freeText1;
		private NxtControl.GuiFramework.FreeText freeText2;
		private NxtControl.GuiFramework.FreeText freeText3;
		private System.HMI.Symbols.Base.Led<bool> toHomePLC;
		private NxtControl.GuiFramework.FreeText freeText4;
		private System.HMI.Symbols.Base.TextBox<short> fault_code;
		private NxtControl.GuiFramework.FreeText freeText5;
		private System.HMI.Symbols.Base.FreeText component_name;
		private NxtControl.GuiFramework.FreeText freeText6;
		private NxtControl.GuiFramework.FreeText freeText7;
		private System.HMI.Symbols.Base.Led<bool> Work1Interlock;
		private System.HMI.Symbols.Base.Led<bool> HomeInterlock;
		private System.HMI.Symbols.Base.FreeText component_name_1;
		private System.HMI.Symbols.Base.Label component_name_2;
		#endregion
	}
}
