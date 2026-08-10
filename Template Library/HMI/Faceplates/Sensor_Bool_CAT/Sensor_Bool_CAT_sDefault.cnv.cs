/*
 * Created by EcoStruxure Automation Expert.
 * User:  
 * Date: 2/2/2026
 * Time: 4:04 PM
 * 
 */

using System;
using NxtControl.GuiFramework;

namespace HMI.Main.Symbols.Sensor_Bool_CAT
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
		}

		void StateValueChanged(object sender, ValueChangedEventArgs e)
		{
			// TODO: Implement StateValueChanged
		}
	}
}
