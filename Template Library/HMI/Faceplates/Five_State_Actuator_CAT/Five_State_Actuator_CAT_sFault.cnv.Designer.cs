/*
 * Created by EcoStruxure Automation Expert.
 * User:  
 * Date: 7/20/2026
 * Time: 3:12 PM
 * 
 */
using System;
using System.ComponentModel;
using System.Collections;
using System.Diagnostics;
using NxtControl.GuiFramework;

namespace HMI.Main.Faceplates.Five_State_Actuator_CAT
{
	/// <summary>
	/// Summary description for Faceplate1.
	/// </summary>
	partial class sFault
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
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary4 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary8 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary9 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary7 = new NxtControl.GuiFramework.PropertyDictionary();
			this.component_name = new System.HMI.Symbols.Base.Label();
			this.fault_active = new System.HMI.Symbols.Base.Led<bool>();
			this.freeText1 = new NxtControl.GuiFramework.FreeText();
			this.freeText3 = new NxtControl.GuiFramework.FreeText();
			this.freeText4 = new NxtControl.GuiFramework.FreeText();
			this.atHome = new System.HMI.Symbols.Base.Led<bool>();
			this.freeText5 = new NxtControl.GuiFramework.FreeText();
			this.freeText6 = new NxtControl.GuiFramework.FreeText();
			this.atWork = new System.HMI.Symbols.Base.Led<bool>();
			this.freeText7 = new NxtControl.GuiFramework.FreeText();
			this.txtFaultDescription = new NxtControl.GuiFramework.FreeText();
			this.freeText9 = new NxtControl.GuiFramework.FreeText();
			this.txtOperatorChecks = new NxtControl.GuiFramework.FreeText();
			this.txtFaultType = new NxtControl.GuiFramework.FreeText();
			this.workAreaControl1 = new NxtControl.GuiFramework.WorkAreaControl();
			// 
			// component_name
			// 
			this.component_name.BeginInit();
			this.component_name.BorderStyle = System.Windows.Forms.BorderStyle.None;
			this.component_name.Brush = new NxtControl.Drawing.Brush("ComboBoxBrush");
			this.component_name.DesignMatrix = new NxtControl.Drawing.Matrix2D(1.44D, 0D, 0D, 1D, 160D, 8D);
			this.component_name.FontScale = false;
			this.component_name.IsOnlyInput = true;
			this.component_name.Name = "component_name";
			this.component_name.Pen = new NxtControl.Drawing.Pen("LabelPen");
			this.component_name.TagName = "component_name";
			this.component_name.EndInit();
			// 
			// fault_active
			// 
			this.fault_active.BeginInit();
			this.fault_active.ColorFrame = new NxtControl.Drawing.Color("LedFrameColor");
			this.fault_active.DesignMatrix = new NxtControl.Drawing.Matrix2D(1.6666666666666667D, 0D, 0D, 1.6666666666666667D, 146D, 50D);
			this.fault_active.FrameSize = 33F;
			this.fault_active.IsOnlyInput = true;
			this.fault_active.Name = "fault_active";
			propertyDictionary2.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			propertyDictionary3.Add("Color", new NxtControl.Drawing.Color("LedTrueColor"));
			this.fault_active.Ranges.Clear();
			this.fault_active.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(false, propertyDictionary2));
			this.fault_active.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(true, propertyDictionary3));
			propertyDictionary1.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			this.fault_active.Ranges.DefaultPropertyValues = propertyDictionary1;
			this.fault_active.TagName = "fault_active";
			this.fault_active.EndInit();
			// 
			// freeText1
			// 
			this.freeText1.Color = new NxtControl.Drawing.Color(((byte)(0)), ((byte)(0)), ((byte)(0)));
			this.freeText1.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.freeText1.Location = new NxtControl.Drawing.PointF(8D, 40D);
			this.freeText1.Name = "freeText1";
			this.freeText1.Text = "Fault Status:";
			// 
			// freeText3
			// 
			this.freeText3.Color = new NxtControl.Drawing.Color(((byte)(0)), ((byte)(0)), ((byte)(0)));
			this.freeText3.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.freeText3.Location = new NxtControl.Drawing.PointF(8D, 72D);
			this.freeText3.Name = "freeText3";
			this.freeText3.Text = "Fault Type:";
			// 
			// freeText4
			// 
			this.freeText4.Color = new NxtControl.Drawing.Color(((byte)(0)), ((byte)(0)), ((byte)(0)));
			this.freeText4.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.freeText4.Location = new NxtControl.Drawing.PointF(8D, 104D);
			this.freeText4.Name = "freeText4";
			this.freeText4.Text = "Current Position:";
			// 
			// atHome
			// 
			this.atHome.BeginInit();
			this.atHome.ColorFrame = new NxtControl.Drawing.Color("LedFrameColor");
			this.atHome.DesignMatrix = new NxtControl.Drawing.Matrix2D(1.6666666666666667D, 0D, 0D, 1.6666666666666667D, 138D, 154D);
			this.atHome.FrameSize = 33F;
			this.atHome.IsOnlyInput = true;
			this.atHome.Name = "atHome";
			propertyDictionary5.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			propertyDictionary6.Add("Color", new NxtControl.Drawing.Color("LedTrueColor"));
			this.atHome.Ranges.Clear();
			this.atHome.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(false, propertyDictionary5));
			this.atHome.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(true, propertyDictionary6));
			propertyDictionary4.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			this.atHome.Ranges.DefaultPropertyValues = propertyDictionary4;
			this.atHome.TagName = "atHome";
			this.atHome.EndInit();
			// 
			// freeText5
			// 
			this.freeText5.Color = new NxtControl.Drawing.Color(((byte)(0)), ((byte)(0)), ((byte)(0)));
			this.freeText5.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.freeText5.Location = new NxtControl.Drawing.PointF(8D, 136D);
			this.freeText5.Name = "freeText5";
			this.freeText5.Text = "At Home:";
			// 
			// freeText6
			// 
			this.freeText6.Color = new NxtControl.Drawing.Color(((byte)(0)), ((byte)(0)), ((byte)(0)));
			this.freeText6.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.freeText6.Location = new NxtControl.Drawing.PointF(8D, 176D);
			this.freeText6.Name = "freeText6";
			this.freeText6.Text = "At Work:";
			// 
			// atWork
			// 
			this.atWork.BeginInit();
			this.atWork.ColorFrame = new NxtControl.Drawing.Color("LedFrameColor");
			this.atWork.DesignMatrix = new NxtControl.Drawing.Matrix2D(1.6666666666666667D, 0D, 0D, 1.6666666666666667D, 138D, 186D);
			this.atWork.FrameSize = 33F;
			this.atWork.IsOnlyInput = true;
			this.atWork.Name = "atWork";
			propertyDictionary8.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			propertyDictionary9.Add("Color", new NxtControl.Drawing.Color("LedTrueColor"));
			this.atWork.Ranges.Clear();
			this.atWork.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(false, propertyDictionary8));
			this.atWork.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(true, propertyDictionary9));
			propertyDictionary7.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			this.atWork.Ranges.DefaultPropertyValues = propertyDictionary7;
			this.atWork.TagName = "atWork";
			this.atWork.EndInit();
			// 
			// freeText7
			// 
			this.freeText7.Color = new NxtControl.Drawing.Color(((byte)(0)), ((byte)(0)), ((byte)(0)));
			this.freeText7.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.freeText7.Location = new NxtControl.Drawing.PointF(0D, 208D);
			this.freeText7.Name = "freeText7";
			this.freeText7.Text = "Description:";
			// 
			// txtFaultDescription
			// 
			this.txtFaultDescription.Color = new NxtControl.Drawing.Color(((byte)(0)), ((byte)(0)), ((byte)(0)));
			this.txtFaultDescription.Font = new NxtControl.Drawing.Font("HMI Sans Serif", 12F, System.Drawing.FontStyle.Regular);
			this.txtFaultDescription.Location = new NxtControl.Drawing.PointF(8D, 240D);
			this.txtFaultDescription.Name = "txtFaultDescription";
			this.txtFaultDescription.Text = "No active fault";
			// 
			// freeText9
			// 
			this.freeText9.Color = new NxtControl.Drawing.Color(((byte)(0)), ((byte)(0)), ((byte)(0)));
			this.freeText9.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.freeText9.Location = new NxtControl.Drawing.PointF(0D, 272D);
			this.freeText9.Name = "freeText9";
			this.freeText9.Text = "Operator Checks:";
			// 
			// txtOperatorChecks
			// 
			this.txtOperatorChecks.Color = new NxtControl.Drawing.Color(((byte)(0)), ((byte)(0)), ((byte)(0)));
			this.txtOperatorChecks.Font = new NxtControl.Drawing.Font("HMI Sans Serif", 12F, System.Drawing.FontStyle.Regular);
			this.txtOperatorChecks.Location = new NxtControl.Drawing.PointF(8D, 296D);
			this.txtOperatorChecks.Name = "txtOperatorChecks";
			this.txtOperatorChecks.Text = "No operator action required";
			// 
			// txtFaultType
			// 
			this.txtFaultType.Color = new NxtControl.Drawing.Color("LabelTextColor");
			this.txtFaultType.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.txtFaultType.Location = new NxtControl.Drawing.PointF(136D, 72D);
			this.txtFaultType.Name = "txtFaultType";
			this.txtFaultType.Text = "Text";
			// 
			// workAreaControl1
			// 
			this.workAreaControl1.Anchor = System.Windows.Forms.AnchorStyles.None;
			this.workAreaControl1.AutoScroll = true;
			this.workAreaControl1.AutoScrollPosition = new System.Drawing.Point(0, 0);
			this.workAreaControl1.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(230)))), ((int)(((byte)(230)))), ((int)(((byte)(230)))));
			this.workAreaControl1.Dock = System.Windows.Forms.DockStyle.None;
			this.workAreaControl1.Font = new NxtControl.Drawing.Font("Microsoft Sans Serif", 9F, System.Drawing.FontStyle.Regular);
			this.workAreaControl1.ForeColor = System.Drawing.SystemColors.ControlText;
			this.workAreaControl1.Location = new System.Drawing.Point(328, 304);
			this.workAreaControl1.Name = "workAreaControl1";
			this.workAreaControl1.Size = new System.Drawing.Size(9, 0);
			this.workAreaControl1.Text = "workAreaControl1";
			// 
			// sFault
			// 
			this.Bounds = new NxtControl.Drawing.RectF(((float)(0D)), ((float)(0D)), ((float)(592D)), ((float)(320D)));
			this.Brush = new NxtControl.Drawing.Brush("FaceplateBrush");
			this.Shapes.AddRange(new System.ComponentModel.IComponent[] {
			this.component_name,
			this.fault_active,
			this.freeText1,
			this.freeText3,
			this.freeText4,
			this.atHome,
			this.freeText5,
			this.freeText6,
			this.atWork,
			this.freeText7,
			this.txtFaultDescription,
			this.freeText9,
			this.txtOperatorChecks,
			this.txtFaultType,
			this.workAreaControl1});
			this.Size = new System.Drawing.Size(592, 320);

		}
		private System.HMI.Symbols.Base.Label component_name;
		private System.HMI.Symbols.Base.Led<bool> fault_active;
		private NxtControl.GuiFramework.FreeText freeText1;
		private NxtControl.GuiFramework.FreeText freeText3;
		private NxtControl.GuiFramework.FreeText freeText4;
		private System.HMI.Symbols.Base.Led<bool> atHome;
		private NxtControl.GuiFramework.FreeText freeText5;
		private NxtControl.GuiFramework.FreeText freeText6;
		private System.HMI.Symbols.Base.Led<bool> atWork;
		private NxtControl.GuiFramework.FreeText freeText7;
		private NxtControl.GuiFramework.FreeText txtFaultDescription;
		private NxtControl.GuiFramework.FreeText freeText9;
		private NxtControl.GuiFramework.FreeText txtOperatorChecks;
		private NxtControl.GuiFramework.FreeText txtFaultType;
		private NxtControl.GuiFramework.WorkAreaControl workAreaControl1;
		#endregion
	}
}
