/*
 * Created by EcoStruxure Automation Expert.
 * User:  
 * Date: 2/2/2026
 * Time: 4:04 PM
 * 
 */
using System;
using System.ComponentModel;
using System.Collections;
using NxtControl.GuiFramework;

namespace HMI.Main.Symbols.Sensor_Bool_CAT
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
			this.State = new System.HMI.Symbols.Base.Led<bool>();
			this.name_1 = new System.HMI.Symbols.Base.TextBox<string>();
			// 
			// State
			// 
			this.State.BeginInit();
			this.State.ColorFrame = new NxtControl.Drawing.Color("LedFrameColor");
			this.State.DesignMatrix = new NxtControl.Drawing.Matrix2D(3D, 0D, 0D, 3D, 26D, 26D);
			this.State.FrameSize = 33F;
			this.State.IsOnlyInput = true;
			this.State.Name = "State";
			propertyDictionary2.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			propertyDictionary3.Add("Color", new NxtControl.Drawing.Color("LedTrueColor"));
			this.State.Ranges.Clear();
			this.State.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(false, propertyDictionary2));
			this.State.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(true, propertyDictionary3));
			propertyDictionary1.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			this.State.Ranges.DefaultPropertyValues = propertyDictionary1;
			this.State.TagName = "State";
			this.State.ValueChanged += new System.EventHandler<NxtControl.GuiFramework.ValueChangedEventArgs>(this.StateValueChanged);
			this.State.EndInit();
			// 
			// name_1
			// 
			this.name_1.BeginInit();
			this.name_1.DesignMatrix = new NxtControl.Drawing.Matrix2D(1D, 0D, 0D, 1D, 56D, 13D);
			this.name_1.MaximumTag = null;
			this.name_1.MinimumTag = null;
			this.name_1.Name = "name_1";
			this.name_1.NumberBase = NxtControl.GuiFramework.NumberBase.Decimal;
			this.name_1.Pen = new NxtControl.Drawing.Pen("TextBoxPen");
			this.name_1.SetColor = new NxtControl.Drawing.Color("Yellow");
			this.name_1.TagName = "";
			this.name_1.TextAlignment = NxtControl.Drawing.ContentAlignment.MiddleLeft;
			this.name_1.Value = null;
			this.name_1.EndInit();
			// 
			// sDefault
			// 
			this.Shapes.AddRange(new System.ComponentModel.IComponent[] {
			this.State,
			this.name_1});
			this.SymbolSize = new System.Drawing.Size(240, 96);

		}
		private System.HMI.Symbols.Base.Led<bool> State;
		private System.HMI.Symbols.Base.TextBox<string> name_1;
		#endregion
	}
}
