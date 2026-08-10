/*
 * Created by EcoStruxure Automation Expert.
 * User: Evans_A
 * Date: 7/23/2025
 * Time: 3:09 PM
 * 
 */
using System;
using NxtControl.GuiFramework;
using NxtControl.Services;

#region Definitions;
#region #Area_CAT_HMI;

namespace HMI.Main.Symbols.Area_CAT
{

  public class MSTSEventArgs : System.EventArgs
  {
    IHMIAccessorService accessorService;
    int channelId;
    int cookie; 
    int eventIndex;

    public MSTSEventArgs(int channelId, int cookie, int eventIndex)
    {
      this.accessorService = (IHMIAccessorService)ServiceProvider.GetService(typeof(IHMIAccessorService));
      this.channelId = channelId;
      this.cookie = cookie;
      this.eventIndex = eventIndex;
    }
    public bool Get_System_Mode(ref System.Int16 value)
    {
      if (accessorService == null)
        return false;
      System.Int64 var = 0;
      bool ret = accessorService.GetInt64Value(channelId, cookie, eventIndex, true,0, ref var);
      if (ret) value = (System.Int16) var;
      return ret;
    }

    public System.Int16? System_Mode
    { get {
      if (accessorService == null)
        return null;
      System.Int64 var = 0;
      bool ret = accessorService.GetInt64Value(channelId, cookie, eventIndex, true,0, ref var);
      if (!ret) return null;
      return (System.Int16) var;
    }  }


  }

  public class CTSTSEventArgs : System.EventArgs
  {
    IHMIAccessorService accessorService;
    int channelId;
    int cookie; 
    int eventIndex;

    public CTSTSEventArgs(int channelId, int cookie, int eventIndex)
    {
      this.accessorService = (IHMIAccessorService)ServiceProvider.GetService(typeof(IHMIAccessorService));
      this.channelId = channelId;
      this.cookie = cookie;
      this.eventIndex = eventIndex;
    }
    public bool Get_System_Cycle_Type(ref System.Int16 value)
    {
      if (accessorService == null)
        return false;
      System.Int64 var = 0;
      bool ret = accessorService.GetInt64Value(channelId, cookie, eventIndex, true,0, ref var);
      if (ret) value = (System.Int16) var;
      return ret;
    }

    public System.Int16? System_Cycle_Type
    { get {
      if (accessorService == null)
        return null;
      System.Int64 var = 0;
      bool ret = accessorService.GetInt64Value(channelId, cookie, eventIndex, true,0, ref var);
      if (!ret) return null;
      return (System.Int16) var;
    }  }


  }

  public class LLFSTSEventArgs : System.EventArgs
  {
    IHMIAccessorService accessorService;
    int channelId;
    int cookie; 
    int eventIndex;

    public LLFSTSEventArgs(int channelId, int cookie, int eventIndex)
    {
      this.accessorService = (IHMIAccessorService)ServiceProvider.GetService(typeof(IHMIAccessorService));
      this.channelId = channelId;
      this.cookie = cookie;
      this.eventIndex = eventIndex;
    }
    public bool Get_LL_Fault_Status(ref System.Boolean value)
    {
      if (accessorService == null)
        return false;
      bool var = false;
      bool ret = accessorService.GetBoolValue(channelId, cookie, eventIndex, true,0, ref var);
      if (ret) value = (System.Boolean) var;
      return ret;
    }

    public System.Boolean? LL_Fault_Status
    { get {
      if (accessorService == null)
        return null;
      bool var = false;
      bool ret = accessorService.GetBoolValue(channelId, cookie, eventIndex, true,0, ref var);
      if (!ret) return null;
      return (System.Boolean) var;
    }  }


  }

  public class INIT_NAMEEventArgs : System.EventArgs
  {
    IHMIAccessorService accessorService;
    int channelId;
    int cookie; 
    int eventIndex;

    public INIT_NAMEEventArgs(int channelId, int cookie, int eventIndex)
    {
      this.accessorService = (IHMIAccessorService)ServiceProvider.GetService(typeof(IHMIAccessorService));
      this.channelId = channelId;
      this.cookie = cookie;
      this.eventIndex = eventIndex;
    }
    public bool Get_Area_Name(ref System.String value)
    {
      if (accessorService == null)
        return false;
      string var = null;
      bool ret = accessorService.GetStringValue(channelId, cookie, eventIndex, true,0, ref var);
      if (ret) value = (System.String) var;
      return ret;
    }

    public System.String Area_Name
    { get {
      if (accessorService == null)
        return null;
      string var = null;
      bool ret = accessorService.GetStringValue(channelId, cookie, eventIndex, true,0, ref var);
      if (!ret) return null;
      return (System.String) var;
    }  }


  }

}

namespace HMI.Main.Symbols.Area_CAT
{

  public class MCNFEventArgs : System.EventArgs
  {
    public MCNFEventArgs()
    {
    }
    private System.Int16? Mode_field = null;
    public System.Int16? Mode
    {
       get { return Mode_field; }
       set { Mode_field = value; }
    }

  }

  public class CTCNFEventArgs : System.EventArgs
  {
    public CTCNFEventArgs()
    {
    }
    private System.Int16? CycleType_field = null;
    public System.Int16? CycleType
    {
       get { return CycleType_field; }
       set { CycleType_field = value; }
    }

  }

  public class FRCNFEventArgs : System.EventArgs
  {
    public FRCNFEventArgs()
    {
    }
    private System.Boolean? Fault_Reset_field = null;
    public System.Boolean? Fault_Reset
    {
       get { return Fault_Reset_field; }
       set { Fault_Reset_field = value; }
    }

  }

}

namespace HMI.Main.Symbols.Area_CAT
{
  partial class sDefault
  {

    private event EventHandler<HMI.Main.Symbols.Area_CAT.MSTSEventArgs> MSTS_Fired;

    private event EventHandler<HMI.Main.Symbols.Area_CAT.CTSTSEventArgs> CTSTS_Fired;

    private event EventHandler<HMI.Main.Symbols.Area_CAT.LLFSTSEventArgs> LLFSTS_Fired;

    private event EventHandler<HMI.Main.Symbols.Area_CAT.INIT_NAMEEventArgs> INIT_NAME_Fired;

    protected override void OnEndInit()
    {
      if (MSTS_Fired != null)
        AttachEventInput(0);
      if (CTSTS_Fired != null)
        AttachEventInput(1);
      if (LLFSTS_Fired != null)
        AttachEventInput(2);
      if (INIT_NAME_Fired != null)
        AttachEventInput(3);

    }

    protected override void FireEventCallback(int channelId, int cookie, int eventIndex)
    {
      switch(eventIndex)
      {
        default:
          break;
        case 0:
          if (MSTS_Fired != null)
          {
            try
            {
              MSTS_Fired(this, new HMI.Main.Symbols.Area_CAT.MSTSEventArgs(channelId, cookie, eventIndex));
            }
            catch (System.Exception e)
            {
              NxtControl.Services.LoggingService.ErrorFormatted(@"In Event Callback for event:'{0}' Type:'{1}' CAT:'{2}' came exception:{3}
stack Trace:
{4}","MSTS_Fired", this.GetType().Name, this.CATName, e.Message, e.StackTrace);
            }
          }
        break; 
        case 1:
          if (CTSTS_Fired != null)
          {
            try
            {
              CTSTS_Fired(this, new HMI.Main.Symbols.Area_CAT.CTSTSEventArgs(channelId, cookie, eventIndex));
            }
            catch (System.Exception e)
            {
              NxtControl.Services.LoggingService.ErrorFormatted(@"In Event Callback for event:'{0}' Type:'{1}' CAT:'{2}' came exception:{3}
stack Trace:
{4}","CTSTS_Fired", this.GetType().Name, this.CATName, e.Message, e.StackTrace);
            }
          }
        break; 
        case 2:
          if (LLFSTS_Fired != null)
          {
            try
            {
              LLFSTS_Fired(this, new HMI.Main.Symbols.Area_CAT.LLFSTSEventArgs(channelId, cookie, eventIndex));
            }
            catch (System.Exception e)
            {
              NxtControl.Services.LoggingService.ErrorFormatted(@"In Event Callback for event:'{0}' Type:'{1}' CAT:'{2}' came exception:{3}
stack Trace:
{4}","LLFSTS_Fired", this.GetType().Name, this.CATName, e.Message, e.StackTrace);
            }
          }
        break; 
        case 3:
          if (INIT_NAME_Fired != null)
          {
            try
            {
              INIT_NAME_Fired(this, new HMI.Main.Symbols.Area_CAT.INIT_NAMEEventArgs(channelId, cookie, eventIndex));
            }
            catch (System.Exception e)
            {
              NxtControl.Services.LoggingService.ErrorFormatted(@"In Event Callback for event:'{0}' Type:'{1}' CAT:'{2}' came exception:{3}
stack Trace:
{4}","INIT_NAME_Fired", this.GetType().Name, this.CATName, e.Message, e.StackTrace);
            }
          }
        break; 

      }
    }
    public bool FireEvent_MCNF(System.Int16 Mode)
    {
      return ((IHMIAccessorOutput)this).FireEvent(0, new object[] {Mode});
    }
    public bool FireEvent_MCNF(HMI.Main.Symbols.Area_CAT.MCNFEventArgs ea)
    {
      object[] _values_ = new object[1];
      if (ea.Mode.HasValue) _values_[0] = ea.Mode.Value;
      return ((IHMIAccessorOutput)this).FireEvent(0, _values_);
    }
    public bool FireEvent_MCNF(System.Int16 Mode, bool ignore_Mode)
    {
      object[] _values_ = new object[1];
      if (!ignore_Mode) _values_[0] = Mode;
      return ((IHMIAccessorOutput)this).FireEvent(0, _values_);
    }
    public bool FireEvent_CTCNF(System.Int16 CycleType)
    {
      return ((IHMIAccessorOutput)this).FireEvent(1, new object[] {CycleType});
    }
    public bool FireEvent_CTCNF(HMI.Main.Symbols.Area_CAT.CTCNFEventArgs ea)
    {
      object[] _values_ = new object[1];
      if (ea.CycleType.HasValue) _values_[0] = ea.CycleType.Value;
      return ((IHMIAccessorOutput)this).FireEvent(1, _values_);
    }
    public bool FireEvent_CTCNF(System.Int16 CycleType, bool ignore_CycleType)
    {
      object[] _values_ = new object[1];
      if (!ignore_CycleType) _values_[0] = CycleType;
      return ((IHMIAccessorOutput)this).FireEvent(1, _values_);
    }
    public bool FireEvent_FRCNF(System.Boolean Fault_Reset)
    {
      return ((IHMIAccessorOutput)this).FireEvent(2, new object[] {Fault_Reset});
    }
    public bool FireEvent_FRCNF(HMI.Main.Symbols.Area_CAT.FRCNFEventArgs ea)
    {
      object[] _values_ = new object[1];
      if (ea.Fault_Reset.HasValue) _values_[0] = ea.Fault_Reset.Value;
      return ((IHMIAccessorOutput)this).FireEvent(2, _values_);
    }
    public bool FireEvent_FRCNF(System.Boolean Fault_Reset, bool ignore_Fault_Reset)
    {
      object[] _values_ = new object[1];
      if (!ignore_Fault_Reset) _values_[0] = Fault_Reset;
      return ((IHMIAccessorOutput)this).FireEvent(2, _values_);
    }

  }
}
#endregion #Area_CAT_HMI;

#endregion Definitions;
