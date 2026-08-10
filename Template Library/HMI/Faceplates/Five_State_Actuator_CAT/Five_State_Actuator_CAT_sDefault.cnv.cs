/*
 * Created by EcoStruxure Automation Expert.
 * User:  
 * Date: 10/7/2025
 * Time: 10:13 AM
 * 
 */

using System;
using NxtControl.GuiFramework;

namespace HMI.Main.Symbols.Five_State_Actuator_CAT
{
	/// <summary>
	/// Description of sDefault.
	/// </summary>
	public partial class sDefault : NxtControl.GuiFramework.HMISymbol
	{
		public sDefault()
		{
			//
			// The InitializeComponent() call is required for Windows Forms designer support.
			//
			InitializeComponent();
			FAULT_EVENT_Fired += FaultStatusChanged;
			  // Direct commands disabled until Setup mode is confirmed
    		
		}
		
		void FaultStatusChanged(object sender, FAULT_EVENTEventArgs e)
		{
			if(e.fault_active.Value == true)
			{
				fault_code.Visible = true;
			}		
			else
			{
				fault_code.Visible = false;
			}
		}
		
		}

	}

