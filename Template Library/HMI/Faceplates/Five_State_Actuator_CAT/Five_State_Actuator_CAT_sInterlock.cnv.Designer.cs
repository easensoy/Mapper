/*
 * Created by EcoStruxure Automation Expert.
 * User:    
 * Date: 7/21/2026
 * Time: 10:40 AM
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
	/// Summary description for sInterlock.
	/// </summary>
	partial class sInterlock
	{

		#region Component Designer generated code
		/// <summary>
		/// Required method for Designer support - do not modify
		/// the contents of this method with the code editor.
		/// </summary>
		private void InitializeComponent()
		{
			this.freeText1 = new NxtControl.GuiFramework.FreeText();
			this.freeText2 = new NxtControl.GuiFramework.FreeText();
			this.freeText3 = new NxtControl.GuiFramework.FreeText();
			this.freeText4 = new NxtControl.GuiFramework.FreeText();
			this.freeText5 = new NxtControl.GuiFramework.FreeText();
			this.txtComponentName = new NxtControl.GuiFramework.FreeText();
			this.txtBlockedMovement = new NxtControl.GuiFramework.FreeText();
			this.txtCurrentState = new NxtControl.GuiFramework.FreeText();
			this.txtReason = new NxtControl.GuiFramework.FreeText();
			this.txtRequiredAction = new NxtControl.GuiFramework.FreeText();
			this.freeText11 = new NxtControl.GuiFramework.FreeText();
			// 
			// freeText1
			// 
			this.freeText1.Color = new NxtControl.Drawing.Color(((byte)(0)), ((byte)(0)), ((byte)(0)));
			this.freeText1.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.freeText1.Location = new NxtControl.Drawing.PointF(112D, 40D);
			this.freeText1.Name = "freeText1";
			this.freeText1.Text = "Actuator:";
			// 
			// freeText2
			// 
			this.freeText2.Color = new NxtControl.Drawing.Color(((byte)(0)), ((byte)(0)), ((byte)(0)));
			this.freeText2.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.freeText2.Location = new NxtControl.Drawing.PointF(7D, 80D);
			this.freeText2.Name = "freeText2";
			this.freeText2.Text = "Blocked Movement:";
			// 
			// freeText3
			// 
			this.freeText3.Color = new NxtControl.Drawing.Color(((byte)(0)), ((byte)(0)), ((byte)(0)));
			this.freeText3.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.freeText3.Location = new NxtControl.Drawing.PointF(65D, 120D);
			this.freeText3.Name = "freeText3";
			this.freeText3.Text = "Current State:";
			// 
			// freeText4
			// 
			this.freeText4.Color = new NxtControl.Drawing.Color(((byte)(0)), ((byte)(0)), ((byte)(0)));
			this.freeText4.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.freeText4.Location = new NxtControl.Drawing.PointF(125D, 160D);
			this.freeText4.Name = "freeText4";
			this.freeText4.Text = "Reason:";
			// 
			// freeText5
			// 
			this.freeText5.Color = new NxtControl.Drawing.Color(((byte)(0)), ((byte)(0)), ((byte)(0)));
			this.freeText5.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.freeText5.Location = new NxtControl.Drawing.PointF(39D, 200D);
			this.freeText5.Name = "freeText5";
			this.freeText5.Text = "Required Action:";
			// 
			// txtComponentName
			// 
			this.txtComponentName.Color = new NxtControl.Drawing.Color("LabelTextColor");
			this.txtComponentName.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.txtComponentName.Location = new NxtControl.Drawing.PointF(216D, 40D);
			this.txtComponentName.Name = "txtComponentName";
			this.txtComponentName.Text = "Text";
			// 
			// txtBlockedMovement
			// 
			this.txtBlockedMovement.Color = new NxtControl.Drawing.Color("LabelTextColor");
			this.txtBlockedMovement.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.txtBlockedMovement.Location = new NxtControl.Drawing.PointF(216D, 80D);
			this.txtBlockedMovement.Name = "txtBlockedMovement";
			this.txtBlockedMovement.Text = "Text";
			// 
			// txtCurrentState
			// 
			this.txtCurrentState.Color = new NxtControl.Drawing.Color("LabelTextColor");
			this.txtCurrentState.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.txtCurrentState.Location = new NxtControl.Drawing.PointF(216D, 120D);
			this.txtCurrentState.Name = "txtCurrentState";
			this.txtCurrentState.Text = "Text";
			// 
			// txtReason
			// 
			this.txtReason.Color = new NxtControl.Drawing.Color("LabelTextColor");
			this.txtReason.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.txtReason.Location = new NxtControl.Drawing.PointF(216D, 160D);
			this.txtReason.Name = "txtReason";
			this.txtReason.Text = "Text";
			// 
			// txtRequiredAction
			// 
			this.txtRequiredAction.Color = new NxtControl.Drawing.Color("LabelTextColor");
			this.txtRequiredAction.Font = new NxtControl.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Bold);
			this.txtRequiredAction.Location = new NxtControl.Drawing.PointF(216D, 200D);
			this.txtRequiredAction.Name = "txtRequiredAction";
			this.txtRequiredAction.Text = "Text";
			// 
			// freeText11
			// 
			this.freeText11.Color = new NxtControl.Drawing.Color(((byte)(0)), ((byte)(0)), ((byte)(0)));
			this.freeText11.Font = new NxtControl.Drawing.Font("BigCanvasTopologyButtonFont");
			this.freeText11.Location = new NxtControl.Drawing.PointF(152D, 8D);
			this.freeText11.Name = "freeText11";
			this.freeText11.Text = "Interlock Check";
			// 
			// sInterlock
			// 
			this.Bounds = new NxtControl.Drawing.RectF(((float)(0D)), ((float)(0D)), ((float)(736D)), ((float)(256D)));
			this.Brush = new NxtControl.Drawing.Brush("FaceplateBrush");
			this.Shapes.AddRange(new System.ComponentModel.IComponent[] {
			this.freeText1,
			this.freeText2,
			this.freeText3,
			this.freeText4,
			this.freeText5,
			this.txtComponentName,
			this.txtBlockedMovement,
			this.txtCurrentState,
			this.txtReason,
			this.txtRequiredAction,
			this.freeText11});
			this.Size = new System.Drawing.Size(736, 256);

		}
		private NxtControl.GuiFramework.FreeText freeText1;
		private NxtControl.GuiFramework.FreeText freeText2;
		private NxtControl.GuiFramework.FreeText freeText3;
		private NxtControl.GuiFramework.FreeText freeText4;
		private NxtControl.GuiFramework.FreeText freeText5;
		private NxtControl.GuiFramework.FreeText txtComponentName;
		private NxtControl.GuiFramework.FreeText txtBlockedMovement;
		private NxtControl.GuiFramework.FreeText txtCurrentState;
		private NxtControl.GuiFramework.FreeText txtReason;
		private NxtControl.GuiFramework.FreeText txtRequiredAction;
		private NxtControl.GuiFramework.FreeText freeText11;
		#endregion
	}
}
