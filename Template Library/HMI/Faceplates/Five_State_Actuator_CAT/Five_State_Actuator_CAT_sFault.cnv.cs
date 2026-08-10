/*
 * Created by EcoStruxure Automation Expert.
 * User:
 * Date: 7/20/2026
 * Time: 3:12 PM
 */

using System;
using NxtControl.GuiFramework;

namespace HMI.Main.Faceplates.Five_State_Actuator_CAT
{
   public partial class sFault :
    NxtControl.GuiFramework.HMIFaceplate
{
    public sFault()
    {
        InitializeComponent();
        FAULT_EVENT_Fired += (sender, e) =>
        {
            bool faultActive = e.fault_active.Value;
            int faultCode = e.fault_code.Value;

            UpdateFaultDetails(faultActive, faultCode);
        };

        ShowNoActiveFault();
    }
	
    private void UpdateFaultDetails(
        bool faultActive,
        int faultCode)
    {
        if (!faultActive)
        {
            ShowNoActiveFault();
            return;
        }

        switch (faultCode)
        {
            case 1001:
                ShowAtWorkTimeout();
                break;

            case 1002:
                ShowAtHomeTimeout();
                break;

            case 1003:
                ShowSensorFault();
                break;

            default:
                ShowUnknownFault(faultCode);
                break;
        }
    }

        private void ShowNoActiveFault()
        {
            txtFaultType.Text = "No Active Fault";

            txtFaultDescription.Text =
                "No active actuator fault is currently present.";

            txtOperatorChecks.Text =
                "No operator action is required.";
        }

        private void ShowAtWorkTimeout()
        {
            txtFaultType.Text = "At Work Timeout";

            txtFaultDescription.Text =
                "The actuator did not reach the At Work position " +
                "within the configured timeout.";

            txtOperatorChecks.Text =
                "Check the air supply, mechanical obstruction, " +
                "At Work sensor and sensor wiring.";
        }

        private void ShowAtHomeTimeout()
        {
            txtFaultType.Text = "At Home Timeout";

            txtFaultDescription.Text =
                "The actuator did not reach the At Home position " +
                "within the configured timeout.";

            txtOperatorChecks.Text =
                "Check the air supply, mechanical obstruction, " +
                "At Home sensor and sensor wiring.";
        }

        private void ShowSensorFault()
        {
            txtFaultType.Text = "Sensor Fault";

            txtFaultDescription.Text =
                "The actuator position sensor signals are invalid " +
                "or inconsistent.";

            txtOperatorChecks.Text =
                "Check the Home and Work sensors, sensor alignment, " +
                "wiring and input status.";
        }

        private void ShowUnknownFault(int faultCode)
        {
            txtFaultType.Text = "Unknown Fault";

            txtFaultDescription.Text =
                "The actuator reported an unrecognised fault code: " +
                faultCode.ToString();

            txtOperatorChecks.Text =
                "Check the actuator diagnostics and fault logic.";
        }
    }
}
