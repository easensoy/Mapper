/*
 * Created by EcoStruxure Automation Expert.
 * User:  
 * Date: 7/14/2026
 * Time: 1:48 PM
 * 
 */

using System;
using NxtControl.GuiFramework;

namespace HMI.Main.Symbols.Process1_Generic
{
	/// <summary>
	/// Description of Symbol1.
	/// </summary>
	public partial class sManual : NxtControl.GuiFramework.HMISymbol
	{
		public sManual()
		{
			//
			// The InitializeComponent() call is required for Windows Forms designer support.
			//
			InitializeComponent();
		}

		void ManualExecuteStepMouseDown(object sender, System.Windows.Forms.MouseEventArgs e)
		{
			chkManualExecuteStep.Value = true; // TODO: Implement ManualExecuteStepMouseDown
			FireEvent_MREQO(true);
		}


		void ManualExecuteStepMouseUp(object sender, System.Windows.Forms.MouseEventArgs e)
		{
			chkManualExecuteStep.Value = false; // TODO: Implement ManualExecuteStepMouseUp
		}

		void ManualExecuteStepMouseLeave(object sender, EventArgs e)
		{
			chkManualExecuteStep.Value = false; // TODO: Implement ManualExecuteStepMouseLeave
		}

		void ChkManualExecuteStepClick(object sender, EventArgs e)
		{
			 chkManualExecuteStep.Value = false; // TODO: Implement ChkManualExecuteStepClick
		}

		void ManualNextStepMouseDown(object sender, System.Windows.Forms.MouseEventArgs e)
		{
			FireEvent_NSREQO(true); // TODO: Implement ManualNextStepMouseDown
		}
	}
}
