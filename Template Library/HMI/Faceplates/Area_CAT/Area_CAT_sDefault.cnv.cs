/*
 * Created by EcoStruxure Automation Expert.
 * User: Evans_A
 * Date: 7/23/2025
 * Time: 3:09 PM
 * 
 */

using System;
using NxtControl.GuiFramework;
using NxtControl.GuiFramework.Dialogs;

namespace HMI.Main.Symbols.Area_CAT
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
			modeButtonVisiblity(true);
			CycleTypeButtonVisibility(false);
			StopButtonVisibility(false);
			StopAtEndButtonVisibility(false);
			CTSTS_Fired += SystemCycleTypeChanged;
			MSTS_Fired += SystemModeChanged;
		}

		// Mode change buttons

		void AutoButtonClick(object sender, EventArgs e)
		{
			// TODO: Implement AutoButtonClick	 
			
		 	FireEvent_MCNF(1);
		 	CycleTypeButtonVisibility(true);
		 	
		 	OpenManualScreenButton.Visible = false;
		 	OpenManualScreenButton.Enabled = false;
		 	
		 	OpenSetupScreenButton.Visible = false;
    		OpenSetupScreenButton.Enabled = false;
		}
		
		void ManualButtonClick(object sender, EventArgs e)
		{
			// TODO: Implement ManualButtonClick
			
		 	FireEvent_MCNF(2);
		 	CycleTypeButtonVisibility(false);
		 	StopButtonVisibility(false);
		 	StopAtEndButtonVisibility(false);
		 	
		 	OpenManualScreenButton.Visible = true;
		 	OpenManualScreenButton.Enabled= true;
		 	
		 	OpenSetupScreenButton.Visible = false;
    		OpenSetupScreenButton.Enabled = false;
		}

		void SetupButtonClick(object sender, EventArgs e)
		{
			// TODO: Implement SetupButtonClick
					 	
		 	FireEvent_MCNF(3);
		 	CycleTypeButtonVisibility(false);
    		StopButtonVisibility(false);
    		StopAtEndButtonVisibility(false);

    		OpenManualScreenButton.Visible = false;
    		OpenManualScreenButton.Enabled = false;

   			OpenSetupScreenButton.Visible = true;
    		OpenSetupScreenButton.Enabled = true;
		}

		void InitialPositionButtonClick(object sender, EventArgs e)
		{
			// TODO: Implement InitialPositionButtonClick
		 	
		 	FireEvent_MCNF(9);
		 	CycleTypeButtonVisibility(false);
    		StopButtonVisibility(false);
    		StopAtEndButtonVisibility(false);

    		OpenManualScreenButton.Visible = false;
    		OpenManualScreenButton.Enabled = false;

    		OpenSetupScreenButton.Visible = false;
    		OpenSetupScreenButton.Enabled = false;
		}

		
		// CycleType change buttons

		void StopButtonClick(object sender, EventArgs e)
		{
			// TODO: Implement StopButtonClick
			FireEvent_CTCNF(0);
			modeButtonVisiblity(true);
		}

		void RunContiuouslyButtonClick(object sender, EventArgs e)
		{
			// TODO: Implement RunContiuouslyButtonClick
			FireEvent_CTCNF(1);
			modeButtonVisiblity(false);
		}

		void StopAtEndOfCycleButtonClick(object sender, EventArgs e)
		{
			// TODO: Implement StopAtEndOfCycleButtonClick
			FireEvent_CTCNF(2);
			modeButtonVisiblity(false);
		}

		void SingleCycleRunButtonClick(object sender, EventArgs e)
		{
			// TODO: Implement SingleCycleRunButtonClick
			FireEvent_CTCNF(3);
			modeButtonVisiblity(false);
		}

		// Button Visibility
		
		void modeButtonVisiblity(Boolean show)
		{
			ManualButton.Visible = show;
			AutoButton.Visible = show;
			SetupButton.Visible = show;
			InitialPositionButton.Visible = show;
		    
		}
		
		void CycleTypeButtonVisibility(Boolean show)
		{
			RunContiuouslyButton.Visible = show;			
			SingleCycleRunButton.Visible = show;
		}
		
		void StopButtonVisibility(Boolean show)
		{
			StopButton.Visible = show;
		}
		
		void StopAtEndButtonVisibility(Boolean show)
		{
			StopAtEndOfCycleButton.Visible = show;
		}
		
		void SystemCycleTypeChanged(object sender, CTSTSEventArgs e )
		{
			if(e.System_Cycle_Type.Value == 0) //stop
			{ 
				modeButtonVisiblity(true);
				CycleTypeButtonVisibility(true);
				StopButtonVisibility(false);
				StopAtEndButtonVisibility(false);
				
			}
			else
			{
				modeButtonVisiblity(false);
				StopButtonVisibility(true);
				if (e.System_Cycle_Type.Value == 1) // Running Continuously
				{
					StopAtEndButtonVisibility(true);
					CycleTypeButtonVisibility(false);
				}
				else
				{
					StopAtEndButtonVisibility(false);
					CycleTypeButtonVisibility(false);
				}
			}
		}
		
		void SystemModeChanged(object sender, MSTSEventArgs e)
		{
			 int mode = e.System_Mode.Value;

    		bool autoMode   = mode == 1;
    		bool manualMode = mode == 2;
    		bool setupMode  = mode == 3;
    		bool homeMode   = mode == 9;

		    // AUTO: show automatic cycle-selection buttons
		    CycleTypeButtonVisibility(autoMode);
		
		    // MANUAL: show button that opens ManualScreen
		    OpenManualScreenButton.Visible = manualMode;
		    OpenManualScreenButton.Enabled = manualMode;
		
		    // SETUP: show button that opens SetupScreen
		    OpenSetupScreenButton.Visible = setupMode;
		    OpenSetupScreenButton.Enabled = setupMode;
		
		    // Stop At End only becomes available through
		    // SystemCycleTypeChanged while Auto is running.
		    StopAtEndButtonVisibility(false);
		
		    // Home may need a Stop command.
		    // All other stopped modes hide Stop here.
		    StopButtonVisibility(homeMode);
								
		    }
		}
     
	}
	
			
	

		

	
