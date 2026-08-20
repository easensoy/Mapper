/*
 * Created by EcoStruxure Automation Expert.
 * User:
 * Date: 10/8/2025
 * Time: 11:54 AM
 *
 */
using System;
using System.ComponentModel;
using System.Collections;
using NxtControl.GuiFramework;

namespace HMI.Main.Symbols.Robot_Task_CAT
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
			this.Pulse = new System.HMI.Symbols.Base.Led<bool>();
			this.TaskState = new System.HMI.Symbols.Base.TextBox<int>();
			//
			// Pulse
			//
			this.Pulse.BeginInit();
			this.Pulse.ColorFrame = new NxtControl.Drawing.Color("LedFrameColor");
			this.Pulse.DesignMatrix = new NxtControl.Drawing.Matrix2D(3D, 0D, 0D, 3D, 26D, 26D);
			this.Pulse.FrameSize = 33F;
			this.Pulse.IsOnlyInput = true;
			this.Pulse.Name = "Pulse";
			propertyDictionary2.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			propertyDictionary3.Add("Color", new NxtControl.Drawing.Color("LedTrueColor"));
			this.Pulse.Ranges.Clear();
			this.Pulse.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(false, propertyDictionary2));
			this.Pulse.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(true, propertyDictionary3));
			propertyDictionary1.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			this.Pulse.Ranges.DefaultPropertyValues = propertyDictionary1;
			this.Pulse.TagName = "PulseActive";
			this.Pulse.EndInit();
			//
			// TaskState
			//
			this.TaskState.BeginInit();
			this.TaskState.DesignMatrix = new NxtControl.Drawing.Matrix2D(1D, 0D, 0D, 1D, 56D, 13D);
			this.TaskState.MaximumTag = null;
			this.TaskState.MinimumTag = null;
			this.TaskState.Name = "TaskState";
			this.TaskState.NumberBase = NxtControl.GuiFramework.NumberBase.Decimal;
			this.TaskState.Pen = new NxtControl.Drawing.Pen("TextBoxPen");
			this.TaskState.SetColor = new NxtControl.Drawing.Color("Yellow");
			this.TaskState.TagName = "current_state_to_process";
			this.TaskState.TextAlignment = NxtControl.Drawing.ContentAlignment.MiddleLeft;
			this.TaskState.Value = null;
			this.TaskState.EndInit();
			//
			// sDefault
			//
			this.Shapes.AddRange(new System.ComponentModel.IComponent[] {
			this.Pulse,
			this.TaskState});
			this.SymbolSize = new System.Drawing.Size(240, 96);

		}
		private System.HMI.Symbols.Base.Led<bool> Pulse;
		private System.HMI.Symbols.Base.TextBox<int> TaskState;
		#endregion
	}
}
