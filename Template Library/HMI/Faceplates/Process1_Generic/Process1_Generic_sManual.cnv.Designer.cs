/*
 * Created by EcoStruxure Automation Expert.
 * User:  
 * Date: 7/14/2026
 * Time: 1:48 PM
 * 
 */
using System;
using System.ComponentModel;
using System.Collections;
using NxtControl.GuiFramework;

namespace HMI.Main.Symbols.Process1_Generic
{
	/// <summary>
	/// Summary description for Symbol1.
	/// </summary>
	partial class sManual
	{

		#region Component Designer generated code
		/// <summary>
		/// Required method for Designer support - do not modify
		/// the contents of this method with the code editor.
		/// </summary>
		private void InitializeComponent()
		{
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary1 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary2 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary4 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary5 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary3 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary7 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary8 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary6 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary10 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary11 = new NxtControl.GuiFramework.PropertyDictionary();
			NxtControl.GuiFramework.PropertyDictionary propertyDictionary9 = new NxtControl.GuiFramework.PropertyDictionary();
			this.ProcessName = new System.HMI.Symbols.Base.Label();
			this.ModeCMD = new System.HMI.Symbols.Base.Label<short>();
			this.PreviousStepText = new System.HMI.Symbols.Base.Label();
			this.ThisStepText = new System.HMI.Symbols.Base.Label();
			this.NextStepText = new System.HMI.Symbols.Base.Label();
			this.ActuatorName = new System.HMI.Symbols.Base.Label();
			this.StateValue = new System.HMI.Symbols.Base.Label<short>();
			this.roundedRectangle1 = new NxtControl.GuiFramework.RoundedRectangle();
			this.ManualStepReady = new System.HMI.Symbols.Base.Led<bool>();
			this.ManualStepComplete = new System.HMI.Symbols.Base.Led<bool>();
			this.ProcessComplete = new System.HMI.Symbols.Base.Led<bool>();
			this.roundedRectangle3 = new NxtControl.GuiFramework.RoundedRectangle();
			this.chkManualExecuteStep = new System.HMI.Symbols.Base.CheckButton();
			this.ManualNextStep = new System.HMI.Symbols.Base.CheckButton();
			this.OperatorInstruction = new System.HMI.Symbols.Base.Label();
			this.freeText1 = new NxtControl.GuiFramework.FreeText();
			this.freeText3 = new NxtControl.GuiFramework.FreeText();
			this.freeText4 = new NxtControl.GuiFramework.FreeText();
			this.freeText5 = new NxtControl.GuiFramework.FreeText();
			this.freeText6 = new NxtControl.GuiFramework.FreeText();
			this.freeText7 = new NxtControl.GuiFramework.FreeText();
			this.freeText8 = new NxtControl.GuiFramework.FreeText();
			this.freeText9 = new NxtControl.GuiFramework.FreeText();
			this.freeText10 = new NxtControl.GuiFramework.FreeText();
			this.freeText11 = new NxtControl.GuiFramework.FreeText();
			this.freeText12 = new NxtControl.GuiFramework.FreeText();
			this.freeText13 = new NxtControl.GuiFramework.FreeText();
			// 
			// ProcessName
			// 
			this.ProcessName.BeginInit();
			this.ProcessName.BorderStyle = System.Windows.Forms.BorderStyle.None;
			this.ProcessName.Brush = new NxtControl.Drawing.Brush("ComboBoxBrush");
			this.ProcessName.DesignMatrix = new NxtControl.Drawing.Matrix2D(1.3866666666666667D, 0D, 0D, 1D, 112D, 16D);
			this.ProcessName.Font = new NxtControl.Drawing.Font("ButtonFont");
			this.ProcessName.FontScale = false;
			this.ProcessName.IsOnlyInput = true;
			this.ProcessName.Name = "ProcessName";
			this.ProcessName.Pen = new NxtControl.Drawing.Pen("LabelPen");
			this.ProcessName.TagName = "ProcessName";
			this.ProcessName.EndInit();
			// 
			// ModeCMD
			// 
			this.ModeCMD.BeginInit();
			this.ModeCMD.BorderStyle = System.Windows.Forms.BorderStyle.None;
			this.ModeCMD.DecimalPlacesCount = ((uint)(2u));
			this.ModeCMD.DesignMatrix = new NxtControl.Drawing.Matrix2D(0.26666666666666666D, 0D, 0D, 1D, 336D, 120D);
			this.ModeCMD.Font = new NxtControl.Drawing.Font("ButtonFont");
			this.ModeCMD.FontScale = false;
			this.ModeCMD.IsOnlyInput = true;
			this.ModeCMD.LeadingZeros = ((uint)(0u));
			this.ModeCMD.Name = "ModeCMD";
			this.ModeCMD.NumberBase = NxtControl.GuiFramework.NumberBase.Decimal;
			propertyDictionary1.Add("Text", "${Value}");
			propertyDictionary1.Add("TextColor", new NxtControl.Drawing.Color("LabelTextColor"));
			propertyDictionary1.Add("Brush", new NxtControl.Drawing.Brush(new NxtControl.Drawing.Color(((byte)(255)), ((byte)(255)), ((byte)(255)))));
			propertyDictionary1.Add("Pen", new NxtControl.Drawing.Pen("LabelPen"));
			this.ModeCMD.Ranges.DefaultPropertyValues = propertyDictionary1;
			this.ModeCMD.TagName = "ModeCMD";
			this.ModeCMD.EndInit();
			// 
			// PreviousStepText
			// 
			this.PreviousStepText.BeginInit();
			this.PreviousStepText.BorderStyle = System.Windows.Forms.BorderStyle.None;
			this.PreviousStepText.Brush = new NxtControl.Drawing.Brush("ComboBoxBrush");
			this.PreviousStepText.DesignMatrix = new NxtControl.Drawing.Matrix2D(1D, 0D, 0D, 1D, 112D, 48D);
			this.PreviousStepText.FontScale = false;
			this.PreviousStepText.IsOnlyInput = true;
			this.PreviousStepText.Name = "PreviousStepText";
			this.PreviousStepText.Pen = new NxtControl.Drawing.Pen("LabelPen");
			this.PreviousStepText.TagName = "PreviousStepText";
			this.PreviousStepText.EndInit();
			// 
			// ThisStepText
			// 
			this.ThisStepText.BeginInit();
			this.ThisStepText.BorderStyle = System.Windows.Forms.BorderStyle.None;
			this.ThisStepText.Brush = new NxtControl.Drawing.Brush("ComboBoxBrush");
			this.ThisStepText.DesignMatrix = new NxtControl.Drawing.Matrix2D(1D, 0D, 0D, 1D, 112D, 88D);
			this.ThisStepText.FontScale = false;
			this.ThisStepText.IsOnlyInput = true;
			this.ThisStepText.Name = "ThisStepText";
			this.ThisStepText.Pen = new NxtControl.Drawing.Pen("LabelPen");
			this.ThisStepText.TagName = "ThisStepText";
			this.ThisStepText.EndInit();
			// 
			// NextStepText
			// 
			this.NextStepText.BeginInit();
			this.NextStepText.BorderStyle = System.Windows.Forms.BorderStyle.None;
			this.NextStepText.Brush = new NxtControl.Drawing.Brush("ComboBoxBrush");
			this.NextStepText.DesignMatrix = new NxtControl.Drawing.Matrix2D(1D, 0D, 0D, 1D, 112D, 120D);
			this.NextStepText.FontScale = false;
			this.NextStepText.IsOnlyInput = true;
			this.NextStepText.Name = "NextStepText";
			this.NextStepText.Pen = new NxtControl.Drawing.Pen("LabelPen");
			this.NextStepText.TagName = "NextStepText";
			this.NextStepText.EndInit();
			// 
			// ActuatorName
			// 
			this.ActuatorName.BeginInit();
			this.ActuatorName.BorderStyle = System.Windows.Forms.BorderStyle.None;
			this.ActuatorName.Brush = new NxtControl.Drawing.Brush("TextBoxReadOnlyBrush");
			this.ActuatorName.DesignMatrix = new NxtControl.Drawing.Matrix2D(1D, 0D, 0D, 1D, 176D, 192D);
			this.ActuatorName.FontScale = false;
			this.ActuatorName.IsOnlyInput = true;
			this.ActuatorName.Name = "ActuatorName";
			this.ActuatorName.Pen = new NxtControl.Drawing.Pen("LabelPen");
			this.ActuatorName.TagName = "ActuatorName";
			this.ActuatorName.EndInit();
			// 
			// StateValue
			// 
			this.StateValue.BeginInit();
			this.StateValue.BorderStyle = System.Windows.Forms.BorderStyle.None;
			this.StateValue.DecimalPlacesCount = ((uint)(2u));
			this.StateValue.DesignMatrix = new NxtControl.Drawing.Matrix2D(0.26666666666666666D, 0D, 0D, 1D, 184D, 224D);
			this.StateValue.FontScale = false;
			this.StateValue.IsOnlyInput = true;
			this.StateValue.LeadingZeros = ((uint)(0u));
			this.StateValue.Name = "StateValue";
			this.StateValue.NumberBase = NxtControl.GuiFramework.NumberBase.Decimal;
			propertyDictionary2.Add("Text", "${Value}");
			propertyDictionary2.Add("TextColor", new NxtControl.Drawing.Color("LabelTextColor"));
			propertyDictionary2.Add("Brush", new NxtControl.Drawing.Brush("ComboBoxBrush"));
			propertyDictionary2.Add("Pen", new NxtControl.Drawing.Pen("LabelPen"));
			this.StateValue.Ranges.DefaultPropertyValues = propertyDictionary2;
			this.StateValue.TagName = "StateValue";
			this.StateValue.EndInit();
			// 
			// roundedRectangle1
			// 
			this.roundedRectangle1.Bounds = new NxtControl.Drawing.RectF(((float)(8D)), ((float)(152D)), ((float)(416D)), ((float)(104D)));
			this.roundedRectangle1.Font = new NxtControl.Drawing.Font("HMI Sans Serif", 9F, System.Drawing.FontStyle.Regular);
			this.roundedRectangle1.Name = "roundedRectangle1";
			// 
			// ManualStepReady
			// 
			this.ManualStepReady.BeginInit();
			this.ManualStepReady.ColorFrame = new NxtControl.Drawing.Color("LedFrameColor");
			this.ManualStepReady.DesignMatrix = new NxtControl.Drawing.Matrix2D(1.3333333333333333D, 0D, 0D, 1.3333333333333333D, 184D, 328D);
			this.ManualStepReady.FrameSize = 33F;
			this.ManualStepReady.IsOnlyInput = true;
			this.ManualStepReady.Name = "ManualStepReady";
			propertyDictionary4.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			propertyDictionary5.Add("Color", new NxtControl.Drawing.Color("LedTrueColor"));
			this.ManualStepReady.Ranges.Clear();
			this.ManualStepReady.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(false, propertyDictionary4));
			this.ManualStepReady.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(true, propertyDictionary5));
			propertyDictionary3.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			this.ManualStepReady.Ranges.DefaultPropertyValues = propertyDictionary3;
			this.ManualStepReady.TagName = "ManualStepReady";
			this.ManualStepReady.EndInit();
			// 
			// ManualStepComplete
			// 
			this.ManualStepComplete.BeginInit();
			this.ManualStepComplete.ColorFrame = new NxtControl.Drawing.Color("LedFrameColor");
			this.ManualStepComplete.DesignMatrix = new NxtControl.Drawing.Matrix2D(1.3333333333333333D, 0D, 0D, 1.3333333333333333D, 360D, 328D);
			this.ManualStepComplete.FrameSize = 33F;
			this.ManualStepComplete.IsOnlyInput = true;
			this.ManualStepComplete.Name = "ManualStepComplete";
			propertyDictionary7.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			propertyDictionary8.Add("Color", new NxtControl.Drawing.Color("LedTrueColor"));
			this.ManualStepComplete.Ranges.Clear();
			this.ManualStepComplete.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(false, propertyDictionary7));
			this.ManualStepComplete.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(true, propertyDictionary8));
			propertyDictionary6.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			this.ManualStepComplete.Ranges.DefaultPropertyValues = propertyDictionary6;
			this.ManualStepComplete.TagName = "ManualStepComplete";
			this.ManualStepComplete.EndInit();
			// 
			// ProcessComplete
			// 
			this.ProcessComplete.BeginInit();
			this.ProcessComplete.ColorFrame = new NxtControl.Drawing.Color("LedFrameColor");
			this.ProcessComplete.DesignMatrix = new NxtControl.Drawing.Matrix2D(1.3333333333333333D, 0D, 0D, 1.3333333333333333D, 248D, 408D);
			this.ProcessComplete.FrameSize = 33F;
			this.ProcessComplete.IsOnlyInput = true;
			this.ProcessComplete.Name = "ProcessComplete";
			propertyDictionary10.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			propertyDictionary11.Add("Color", new NxtControl.Drawing.Color("LedTrueColor"));
			this.ProcessComplete.Ranges.Clear();
			this.ProcessComplete.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(false, propertyDictionary10));
			this.ProcessComplete.Ranges.Add(new NxtControl.GuiFramework.Range<bool>(true, propertyDictionary11));
			propertyDictionary9.Add("Color", new NxtControl.Drawing.Color("LedFalseColor"));
			this.ProcessComplete.Ranges.DefaultPropertyValues = propertyDictionary9;
			this.ProcessComplete.TagName = "ProcessComplete";
			this.ProcessComplete.EndInit();
			// 
			// roundedRectangle3
			// 
			this.roundedRectangle3.Bounds = new NxtControl.Drawing.RectF(((float)(8D)), ((float)(272D)), ((float)(424D)), ((float)(160D)));
			this.roundedRectangle3.Font = new NxtControl.Drawing.Font("HMI Sans Serif", 9F, System.Drawing.FontStyle.Regular);
			this.roundedRectangle3.Name = "roundedRectangle3";
			this.roundedRectangle3.Pen = new NxtControl.Drawing.Pen(new NxtControl.Drawing.Color(((byte)(26)), ((byte)(170)), ((byte)(66))), 1F, NxtControl.Drawing.DashStyle.Solid);
			// 
			// chkManualExecuteStep
			// 
			this.chkManualExecuteStep.BeginInit();
			this.chkManualExecuteStep.DesignMatrix = new NxtControl.Drawing.Matrix2D(1.575D, 0D, 0D, 1D, 48D, 352D);
			this.chkManualExecuteStep.FalseImage = new NxtControl.Drawing.ImageHolder();
			this.chkManualExecuteStep.FalseImageDisabled = new NxtControl.Drawing.ImageHolder();
			this.chkManualExecuteStep.FalseText = "Execute Step";
			this.chkManualExecuteStep.Font = new NxtControl.Drawing.Font("ButtonFont");
			this.chkManualExecuteStep.FontScale = false;
			this.chkManualExecuteStep.Name = "chkManualExecuteStep";
			this.chkManualExecuteStep.TagName = "ManualExecuteStep";
			this.chkManualExecuteStep.TrueImage = new NxtControl.Drawing.ImageHolder();
			this.chkManualExecuteStep.TrueImageDisabled = new NxtControl.Drawing.ImageHolder();
			this.chkManualExecuteStep.Value = false;
			this.chkManualExecuteStep.MouseDown += new System.Windows.Forms.MouseEventHandler(this.ManualExecuteStepMouseDown);
			this.chkManualExecuteStep.MouseUp += new System.Windows.Forms.MouseEventHandler(this.ManualExecuteStepMouseUp);
			this.chkManualExecuteStep.MouseLeave += new System.EventHandler(this.ManualExecuteStepMouseLeave);
			this.chkManualExecuteStep.Click += new System.EventHandler(this.ChkManualExecuteStepClick);
			this.chkManualExecuteStep.EndInit();
			// 
			// ManualNextStep
			// 
			this.ManualNextStep.BeginInit();
			this.ManualNextStep.DesignMatrix = new NxtControl.Drawing.Matrix2D(1.575D, 0D, 0D, 1D, 240D, 352D);
			this.ManualNextStep.FalseImage = new NxtControl.Drawing.ImageHolder();
			this.ManualNextStep.FalseImageDisabled = new NxtControl.Drawing.ImageHolder();
			this.ManualNextStep.FalseText = "Next Step";
			this.ManualNextStep.Font = new NxtControl.Drawing.Font("ButtonFont");
			this.ManualNextStep.FontScale = false;
			this.ManualNextStep.Name = "ManualNextStep";
			this.ManualNextStep.TagName = "ManualNextStep";
			this.ManualNextStep.TrueImage = new NxtControl.Drawing.ImageHolder();
			this.ManualNextStep.TrueImageDisabled = new NxtControl.Drawing.ImageHolder();
			this.ManualNextStep.Value = false;
			this.ManualNextStep.MouseDown += new System.Windows.Forms.MouseEventHandler(this.ManualNextStepMouseDown);
			this.ManualNextStep.EndInit();
			// 
			// OperatorInstruction
			// 
			this.OperatorInstruction.BeginInit();
			this.OperatorInstruction.BorderStyle = System.Windows.Forms.BorderStyle.None;
			this.OperatorInstruction.Brush = new NxtControl.Drawing.Brush("TextBoxReadOnlyBrush");
			this.OperatorInstruction.DesignMatrix = new NxtControl.Drawing.Matrix2D(1.7066666666666666D, 0D, 0D, 1D, 168D, 280D);
			this.OperatorInstruction.Font = new NxtControl.Drawing.Font("ButtonFont");
			this.OperatorInstruction.FontScale = false;
			this.OperatorInstruction.IsOnlyInput = true;
			this.OperatorInstruction.Name = "OperatorInstruction";
			this.OperatorInstruction.Pen = new NxtControl.Drawing.Pen("LabelPen");
			this.OperatorInstruction.TagName = "OperatorInstruction";
			this.OperatorInstruction.EndInit();
			// 
			// freeText1
			// 
			this.freeText1.Color = new NxtControl.Drawing.Color("Ele24dc");
			this.freeText1.Font = new NxtControl.Drawing.Font("HMI Sans Serif", 12F, System.Drawing.FontStyle.Regular);
			this.freeText1.Location = new NxtControl.Drawing.PointF(0D, 16D);
			this.freeText1.Name = "freeText1";
			this.freeText1.Text = "Process Name:";
			// 
			// freeText3
			// 
			this.freeText3.Color = new NxtControl.Drawing.Color("Ele24dc");
			this.freeText3.Font = new NxtControl.Drawing.Font("HMI Sans Serif", 12F, System.Drawing.FontStyle.Regular);
			this.freeText3.Location = new NxtControl.Drawing.PointF(280D, 120D);
			this.freeText3.Name = "freeText3";
			this.freeText3.Text = "Mode:";
			// 
			// freeText4
			// 
			this.freeText4.Color = new NxtControl.Drawing.Color("Ele24dc");
			this.freeText4.Font = new NxtControl.Drawing.Font("HMI Sans Serif", 12F, System.Drawing.FontStyle.Regular);
			this.freeText4.Location = new NxtControl.Drawing.PointF(5D, 56D);
			this.freeText4.Name = "freeText4";
			this.freeText4.Text = "Previous Step:";
			// 
			// freeText5
			// 
			this.freeText5.Color = new NxtControl.Drawing.Color("Ele24dc");
			this.freeText5.Font = new NxtControl.Drawing.Font("HMI Sans Serif", 12F, System.Drawing.FontStyle.Regular);
			this.freeText5.Location = new NxtControl.Drawing.PointF(12D, 88D);
			this.freeText5.Name = "freeText5";
			this.freeText5.Text = "Current Step:";
			// 
			// freeText6
			// 
			this.freeText6.Color = new NxtControl.Drawing.Color("Ele24dc");
			this.freeText6.Font = new NxtControl.Drawing.Font("HMI Sans Serif", 12F, System.Drawing.FontStyle.Regular);
			this.freeText6.Location = new NxtControl.Drawing.PointF(20D, 120D);
			this.freeText6.Name = "freeText6";
			this.freeText6.Text = "Next Action:";
			// 
			// freeText7
			// 
			this.freeText7.Color = new NxtControl.Drawing.Color("Ele24dc");
			this.freeText7.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.freeText7.Location = new NxtControl.Drawing.PointF(96D, 160D);
			this.freeText7.Name = "freeText7";
			this.freeText7.Text = "CURRENT COMMAND";
			// 
			// freeText8
			// 
			this.freeText8.Color = new NxtControl.Drawing.Color("Ele24dc");
			this.freeText8.Font = new NxtControl.Drawing.Font("HMI Sans Serif", 12F, System.Drawing.FontStyle.Regular);
			this.freeText8.Location = new NxtControl.Drawing.PointF(104D, 192D);
			this.freeText8.Name = "freeText8";
			this.freeText8.Text = "Target:";
			// 
			// freeText9
			// 
			this.freeText9.Color = new NxtControl.Drawing.Color("Ele24dc");
			this.freeText9.Font = new NxtControl.Drawing.Font("HMI Sans Serif", 12F, System.Drawing.FontStyle.Regular);
			this.freeText9.Location = new NxtControl.Drawing.PointF(32D, 224D);
			this.freeText9.Name = "freeText9";
			this.freeText9.Text = "Requested State:";
			// 
			// freeText10
			// 
			this.freeText10.Color = new NxtControl.Drawing.Color("Ele24dc");
			this.freeText10.Font = new NxtControl.Drawing.Font("HMI Sans Serif", 12F, System.Drawing.FontStyle.Regular);
			this.freeText10.Location = new NxtControl.Drawing.PointF(8D, 280D);
			this.freeText10.Name = "freeText10";
			this.freeText10.Text = "Operator Instruction:";
			// 
			// freeText11
			// 
			this.freeText11.Color = new NxtControl.Drawing.Color("Ele24dc");
			this.freeText11.Font = new NxtControl.Drawing.Font("HMI Sans Serif", 12F, System.Drawing.FontStyle.Regular);
			this.freeText11.Location = new NxtControl.Drawing.PointF(32D, 320D);
			this.freeText11.Name = "freeText11";
			this.freeText11.Text = "Ready to Execute:";
			// 
			// freeText12
			// 
			this.freeText12.Color = new NxtControl.Drawing.Color("Ele24dc");
			this.freeText12.Font = new NxtControl.Drawing.Font("HMI Sans Serif", 12F, System.Drawing.FontStyle.Regular);
			this.freeText12.Location = new NxtControl.Drawing.PointF(232D, 320D);
			this.freeText12.Name = "freeText12";
			this.freeText12.Text = "Step Complete:";
			// 
			// freeText13
			// 
			this.freeText13.Color = new NxtControl.Drawing.Color("Ele24dc");
			this.freeText13.Font = new NxtControl.Drawing.Font("HMI Sans Serif", 12F, System.Drawing.FontStyle.Regular);
			this.freeText13.Location = new NxtControl.Drawing.PointF(88D, 400D);
			this.freeText13.Name = "freeText13";
			this.freeText13.Text = "Process Complete:";
			// 
			// Symbol1
			// 
			this.Shapes.AddRange(new System.ComponentModel.IComponent[] {
			this.roundedRectangle1,
			this.ProcessName,
			this.ModeCMD,
			this.PreviousStepText,
			this.ThisStepText,
			this.NextStepText,
			this.ActuatorName,
			this.StateValue,
			this.roundedRectangle3,
			this.chkManualExecuteStep,
			this.ManualNextStep,
			this.ManualStepReady,
			this.ManualStepComplete,
			this.ProcessComplete,
			this.OperatorInstruction,
			this.freeText1,
			this.freeText3,
			this.freeText4,
			this.freeText5,
			this.freeText6,
			this.freeText7,
			this.freeText8,
			this.freeText9,
			this.freeText10,
			this.freeText11,
			this.freeText12,
			this.freeText13});
			this.SymbolSize = new System.Drawing.Size(432, 448);

		}
		private System.HMI.Symbols.Base.Label ProcessName;
		private System.HMI.Symbols.Base.Label<short> ModeCMD;
		private System.HMI.Symbols.Base.Label PreviousStepText;
		private System.HMI.Symbols.Base.Label ThisStepText;
		private System.HMI.Symbols.Base.Label NextStepText;
		private System.HMI.Symbols.Base.Label ActuatorName;
		private System.HMI.Symbols.Base.Label<short> StateValue;
		private NxtControl.GuiFramework.RoundedRectangle roundedRectangle1;
		private System.HMI.Symbols.Base.Led<bool> ManualStepReady;
		private System.HMI.Symbols.Base.Led<bool> ManualStepComplete;
		private System.HMI.Symbols.Base.Led<bool> ProcessComplete;
		private NxtControl.GuiFramework.RoundedRectangle roundedRectangle3;
		private System.HMI.Symbols.Base.CheckButton chkManualExecuteStep;
		private System.HMI.Symbols.Base.CheckButton ManualNextStep;
		private System.HMI.Symbols.Base.Label OperatorInstruction;
		private NxtControl.GuiFramework.FreeText freeText1;
		private NxtControl.GuiFramework.FreeText freeText3;
		private NxtControl.GuiFramework.FreeText freeText4;
		private NxtControl.GuiFramework.FreeText freeText5;
		private NxtControl.GuiFramework.FreeText freeText6;
		private NxtControl.GuiFramework.FreeText freeText7;
		private NxtControl.GuiFramework.FreeText freeText8;
		private NxtControl.GuiFramework.FreeText freeText9;
		private NxtControl.GuiFramework.FreeText freeText10;
		private NxtControl.GuiFramework.FreeText freeText11;
		private NxtControl.GuiFramework.FreeText freeText12;
		private NxtControl.GuiFramework.FreeText freeText13;
		#endregion
	}
}
