using System;
using NxtControl.GuiFramework;
using NxtControl.Services;


#region Definitions;
#region Seven_State_Actuator_Centre_Home_CAT_HMI;

namespace HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT
{
  partial class sDefault
  {

    private HMI.Main.Faceplates.Seven_State_Actuator_Centre_Home_CAT.setup setup
    {
      get
      { 
        if (IsOpenFaceplateSecure() == false)
          return null;

        HMI.Main.Faceplates.Seven_State_Actuator_Centre_Home_CAT.setup faceplate = null;
        
        IHMIManagementService hmiManagementService = (IHMIManagementService)ServiceProvider.GetService(typeof(IHMIManagementService));
        if (hmiManagementService != null)
          faceplate = (HMI.Main.Faceplates.Seven_State_Actuator_Centre_Home_CAT.setup)hmiManagementService.GetRegisteredHMIFaceplate(MapPath, typeof(HMI.Main.Faceplates.Seven_State_Actuator_Centre_Home_CAT.setup));
        
        if (faceplate == null)
        {
          faceplate = new HMI.Main.Faceplates.Seven_State_Actuator_Centre_Home_CAT.setup();

          faceplate.SetConnectionInfo(this.TagName, this.SymbolPath, this.ChannelId, GetType());

          if (hmiManagementService != null)
            hmiManagementService.RegisterHMIFaceplate(faceplate);
        }
        return faceplate;
      }
    }
     
    protected override void DoOpenFaceplate(OpenFaceplate openFaceplate)
    {
      NxtControl.GuiFramework.HMIFaceplate hmiFaceplate = null;

      if ("setup" == (string)openFaceplate.FaceplateType)
        hmiFaceplate = setup;

      if (hmiFaceplate != null)
      {
        if (hmiFaceplate.Initialized == true)
          hmiFaceplate.Activate();
        else
        {
          OnInitializeFaceplate(hmiFaceplate);
          hmiFaceplate.Show(this);
        }
      }
    }

    public override void DoOpenFaceplate(string openFaceplate)
    {
      NxtControl.GuiFramework.HMIFaceplate hmiFaceplate = null;

      if ("setup" == openFaceplate)
        hmiFaceplate = setup;

      if (hmiFaceplate != null)
      {
        if (hmiFaceplate.Initialized == true)
          hmiFaceplate.Activate();
        else
        {
          OnInitializeFaceplate(hmiFaceplate);
          hmiFaceplate.Show(this);
        }
      }
    }

  }
}
#endregion Seven_State_Actuator_Centre_Home_CAT_HMI;

#endregion Definitions;

