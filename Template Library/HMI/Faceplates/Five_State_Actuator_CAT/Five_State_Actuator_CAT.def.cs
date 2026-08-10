using System;
using NxtControl.GuiFramework;
using NxtControl.Services;


#region Definitions;
#region Five_State_Actuator_CAT_HMI;

namespace HMI.Main.Symbols.Five_State_Actuator_CAT
{
  partial class sDefault
  {

    private HMI.Main.Faceplates.Five_State_Actuator_CAT.sFault sFault
    {
      get
      { 
        if (IsOpenFaceplateSecure() == false)
          return null;

        HMI.Main.Faceplates.Five_State_Actuator_CAT.sFault faceplate = null;
        
        IHMIManagementService hmiManagementService = (IHMIManagementService)ServiceProvider.GetService(typeof(IHMIManagementService));
        if (hmiManagementService != null)
          faceplate = (HMI.Main.Faceplates.Five_State_Actuator_CAT.sFault)hmiManagementService.GetRegisteredHMIFaceplate(MapPath, typeof(HMI.Main.Faceplates.Five_State_Actuator_CAT.sFault));
        
        if (faceplate == null)
        {
          faceplate = new HMI.Main.Faceplates.Five_State_Actuator_CAT.sFault();

          faceplate.SetConnectionInfo(this.TagName, this.SymbolPath, this.ChannelId, GetType());

          if (hmiManagementService != null)
            hmiManagementService.RegisterHMIFaceplate(faceplate);
        }
        return faceplate;
      }
    }
     
    private HMI.Main.Faceplates.Five_State_Actuator_CAT.sInterlock sInterlock
    {
      get
      { 
        if (IsOpenFaceplateSecure() == false)
          return null;

        HMI.Main.Faceplates.Five_State_Actuator_CAT.sInterlock faceplate = null;
        
        IHMIManagementService hmiManagementService = (IHMIManagementService)ServiceProvider.GetService(typeof(IHMIManagementService));
        if (hmiManagementService != null)
          faceplate = (HMI.Main.Faceplates.Five_State_Actuator_CAT.sInterlock)hmiManagementService.GetRegisteredHMIFaceplate(MapPath, typeof(HMI.Main.Faceplates.Five_State_Actuator_CAT.sInterlock));
        
        if (faceplate == null)
        {
          faceplate = new HMI.Main.Faceplates.Five_State_Actuator_CAT.sInterlock();

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

      if ("sFault" == (string)openFaceplate.FaceplateType)
        hmiFaceplate = sFault;

      if ("sInterlock" == (string)openFaceplate.FaceplateType)
        hmiFaceplate = sInterlock;

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

      if ("sFault" == openFaceplate)
        hmiFaceplate = sFault;

      if ("sInterlock" == openFaceplate)
        hmiFaceplate = sInterlock;

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

namespace HMI.Main.Symbols.Five_State_Actuator_CAT
{
  partial class sSetup
  {

    private HMI.Main.Faceplates.Five_State_Actuator_CAT.sFault sFault
    {
      get
      { 
        if (IsOpenFaceplateSecure() == false)
          return null;

        HMI.Main.Faceplates.Five_State_Actuator_CAT.sFault faceplate = null;
        
        IHMIManagementService hmiManagementService = (IHMIManagementService)ServiceProvider.GetService(typeof(IHMIManagementService));
        if (hmiManagementService != null)
          faceplate = (HMI.Main.Faceplates.Five_State_Actuator_CAT.sFault)hmiManagementService.GetRegisteredHMIFaceplate(MapPath, typeof(HMI.Main.Faceplates.Five_State_Actuator_CAT.sFault));
        
        if (faceplate == null)
        {
          faceplate = new HMI.Main.Faceplates.Five_State_Actuator_CAT.sFault();

          faceplate.SetConnectionInfo(this.TagName, this.SymbolPath, this.ChannelId, GetType());

          if (hmiManagementService != null)
            hmiManagementService.RegisterHMIFaceplate(faceplate);
        }
        return faceplate;
      }
    }
     
    private HMI.Main.Faceplates.Five_State_Actuator_CAT.sInterlock sInterlock
    {
      get
      { 
        if (IsOpenFaceplateSecure() == false)
          return null;

        HMI.Main.Faceplates.Five_State_Actuator_CAT.sInterlock faceplate = null;
        
        IHMIManagementService hmiManagementService = (IHMIManagementService)ServiceProvider.GetService(typeof(IHMIManagementService));
        if (hmiManagementService != null)
          faceplate = (HMI.Main.Faceplates.Five_State_Actuator_CAT.sInterlock)hmiManagementService.GetRegisteredHMIFaceplate(MapPath, typeof(HMI.Main.Faceplates.Five_State_Actuator_CAT.sInterlock));
        
        if (faceplate == null)
        {
          faceplate = new HMI.Main.Faceplates.Five_State_Actuator_CAT.sInterlock();

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

      if ("sFault" == (string)openFaceplate.FaceplateType)
        hmiFaceplate = sFault;

      if ("sInterlock" == (string)openFaceplate.FaceplateType)
        hmiFaceplate = sInterlock;

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

      if ("sFault" == openFaceplate)
        hmiFaceplate = sFault;

      if ("sInterlock" == openFaceplate)
        hmiFaceplate = sInterlock;

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

namespace HMI.Main.Faceplates.Five_State_Actuator_CAT
{
  partial class sFault
  {

    private HMI.Main.Faceplates.Five_State_Actuator_CAT.sInterlock sInterlock
    {
      get
      { 
        if (IsOpenFaceplateSecure() == false)
          return null;

        HMI.Main.Faceplates.Five_State_Actuator_CAT.sInterlock faceplate = null;
        
        IHMIManagementService hmiManagementService = (IHMIManagementService)ServiceProvider.GetService(typeof(IHMIManagementService));
        if (hmiManagementService != null)
          faceplate = (HMI.Main.Faceplates.Five_State_Actuator_CAT.sInterlock)hmiManagementService.GetRegisteredHMIFaceplate(MapPath, typeof(HMI.Main.Faceplates.Five_State_Actuator_CAT.sInterlock));
        
        if (faceplate == null)
        {
          faceplate = new HMI.Main.Faceplates.Five_State_Actuator_CAT.sInterlock();

          faceplate.SetConnectionInfo(this.TagName, this.ConnectionSymbolPath, this.ChannelId, this.ParentType);

          if (hmiManagementService != null)
            hmiManagementService.RegisterHMIFaceplate(faceplate);
        }
        return faceplate;
      }
    }
     
    protected override void DoOpenFaceplate(OpenFaceplate openFaceplate)
    {
      NxtControl.GuiFramework.HMIFaceplate hmiFaceplate = null;

      if ("sInterlock" == (string)openFaceplate.FaceplateType)
        hmiFaceplate = sInterlock;

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

      if ("sInterlock" == openFaceplate)
        hmiFaceplate = sInterlock;

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

namespace HMI.Main.Faceplates.Five_State_Actuator_CAT
{
  partial class sInterlock
  {

    private HMI.Main.Faceplates.Five_State_Actuator_CAT.sFault sFault
    {
      get
      { 
        if (IsOpenFaceplateSecure() == false)
          return null;

        HMI.Main.Faceplates.Five_State_Actuator_CAT.sFault faceplate = null;
        
        IHMIManagementService hmiManagementService = (IHMIManagementService)ServiceProvider.GetService(typeof(IHMIManagementService));
        if (hmiManagementService != null)
          faceplate = (HMI.Main.Faceplates.Five_State_Actuator_CAT.sFault)hmiManagementService.GetRegisteredHMIFaceplate(MapPath, typeof(HMI.Main.Faceplates.Five_State_Actuator_CAT.sFault));
        
        if (faceplate == null)
        {
          faceplate = new HMI.Main.Faceplates.Five_State_Actuator_CAT.sFault();

          faceplate.SetConnectionInfo(this.TagName, this.ConnectionSymbolPath, this.ChannelId, this.ParentType);

          if (hmiManagementService != null)
            hmiManagementService.RegisterHMIFaceplate(faceplate);
        }
        return faceplate;
      }
    }
     
    protected override void DoOpenFaceplate(OpenFaceplate openFaceplate)
    {
      NxtControl.GuiFramework.HMIFaceplate hmiFaceplate = null;

      if ("sFault" == (string)openFaceplate.FaceplateType)
        hmiFaceplate = sFault;

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

      if ("sFault" == openFaceplate)
        hmiFaceplate = sFault;

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
#endregion Five_State_Actuator_CAT_HMI;

#endregion Definitions;

