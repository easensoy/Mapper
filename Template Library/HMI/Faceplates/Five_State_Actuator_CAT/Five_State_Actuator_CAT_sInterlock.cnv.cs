/*
 * Created by EcoStruxure Automation Expert.
 * User:    
 * Date: 7/21/2026
 * Time: 10:40 AM
 * 
 */

using System;
using NxtControl.GuiFramework;

namespace HMI.Main.Faceplates.Five_State_Actuator_CAT
{
    public partial class sInterlock :
        NxtControl.GuiFramework.HMIFaceplate
    {
        private string currentComponentName = "Actuator";

        public sInterlock()
        {
        	InitializeComponent();

            name_event_Fired += (sender, e) =>
            {
                currentComponentName = e.component_name;
                txtComponentName.Text = currentComponentName;
            };

            interlock_event_Fired += (sender, e) =>
            {
                bool workBlocked =
                    e.Work1Interlock.Value;

                bool homeBlocked =
                    e.HomeInterlock.Value;

                int blockedState =
                    e.ActiveBlockedState.Value;

                UpdateInterlockDetails(
                    currentComponentName,
                    workBlocked,
                    homeBlocked,
                    blockedState);
            };

            ShowNoActiveInterlock();
        }

        private void BtnCloseClick(
            object sender,
            EventArgs e)
        {
            Close();
        }

        private void UpdateInterlockDetails(
            string componentName,
            bool workBlocked,
            bool homeBlocked,
            int blockedState)
        {
            string blockedMovement;

            if (workBlocked)
            {
                blockedMovement = "To Work";
            }
            else if (homeBlocked)
            {
                blockedMovement = "To Home";
            }
            else
            {
                ShowNoActiveInterlock();
                return;
            }

            string requiredPosition =
                GetRequiredPositionText(blockedState);

            txtComponentName.Text =
                componentName;

            txtBlockedMovement.Text =
                blockedMovement;

            txtCurrentState.Text =
                GetStateText(blockedState);

            txtReason.Text =
                componentName +
                " is not " +
                requiredPosition +
                ".";

            txtRequiredAction.Text =
                "Move " +
                componentName +
                " to " +
                requiredPosition +
                " and confirm the position feedback.";
        }

        private string GetStateText(int state)
        {
            switch (state)
            {
                case 0:
                    return "At Home Initial";

                case 1:
                    return "Moving to Work";

                case 2:
                    return "At Work";

                case 3:
                    return "Moving to Home";

                case 4:
                    return "At Home";

                default:
                    return "Unknown State";
            }
        }

        private string GetRequiredPositionText(int state)
        {
            switch (state)
            {
                case 0:
                case 3:
                case 4:
                    return "At Home";

                case 1:
                case 2:
                    return "At Work";

                default:
                    return "the required position";
            }
        }

        private void ShowNoActiveInterlock()
        {
            txtComponentName.Text =
                currentComponentName;

            txtBlockedMovement.Text =
                "No Movement Blocked";

            txtCurrentState.Text =
                "";

            txtReason.Text =
                "No active interlock is currently blocking movement.";

            txtRequiredAction.Text =
                "No operator action is required.";
        }
    }
}