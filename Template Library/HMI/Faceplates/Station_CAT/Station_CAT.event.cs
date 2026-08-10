/*
 * Created by EcoStruxure Automation Expert.
 * User: Evans_A
 * Date: 7/31/2025
 * Time: 9:10 AM
 * 
 */
using System;
using NxtControl.GuiFramework;
using NxtControl.Services;

#region Definitions;
#region #Station_CAT_HMI;

namespace HMI.Main.Symbols.Station_CAT
{

  public class SREQEventArgs : System.EventArgs
  {
    IHMIAccessorService accessorService;
    int channelId;
    int cookie; 
    int eventIndex;

    public SREQEventArgs(int channelId, int cookie, int eventIndex)
    {
      this.accessorService = (IHMIAccessorService)ServiceProvider.GetService(typeof(IHMIAccessorService));
      this.channelId = channelId;
      this.cookie = cookie;
      this.eventIndex = eventIndex;
    }
    public bool Get_StationName(ref System.String value)
    {
      if (accessorService == null)
        return false;
      string var = null;
      bool ret = accessorService.GetStringValue(channelId, cookie, eventIndex, true,0, ref var);
      if (ret) value = (System.String) var;
      return ret;
    }

    public System.String StationName
    { get {
      if (accessorService == null)
        return null;
      string var = null;
      bool ret = accessorService.GetStringValue(channelId, cookie, eventIndex, true,0, ref var);
      if (!ret) return null;
      return (System.String) var;
    }  }

    public bool Get_ParentName(ref System.String value)
    {
      if (accessorService == null)
        return false;
      string var = null;
      bool ret = accessorService.GetStringValue(channelId, cookie, eventIndex, true,1, ref var);
      if (ret) value = (System.String) var;
      return ret;
    }

    public System.String ParentName
    { get {
      if (accessorService == null)
        return null;
      string var = null;
      bool ret = accessorService.GetStringValue(channelId, cookie, eventIndex, true,1, ref var);
      if (!ret) return null;
      return (System.String) var;
    }  }


  }

  public class LMREQEventArgs : System.EventArgs
  {
    IHMIAccessorService accessorService;
    int channelId;
    int cookie; 
    int eventIndex;

    public LMREQEventArgs(int channelId, int cookie, int eventIndex)
    {
      this.accessorService = (IHMIAccessorService)ServiceProvider.GetService(typeof(IHMIAccessorService));
      this.channelId = channelId;
      this.cookie = cookie;
      this.eventIndex = eventIndex;
    }
    public bool Get_LocalMode(ref System.Int16 value)
    {
      if (accessorService == null)
        return false;
      System.Int64 var = 0;
      bool ret = accessorService.GetInt64Value(channelId, cookie, eventIndex, true,0, ref var);
      if (ret) value = (System.Int16) var;
      return ret;
    }

    public System.Int16? LocalMode
    { get {
      if (accessorService == null)
        return null;
      System.Int64 var = 0;
      bool ret = accessorService.GetInt64Value(channelId, cookie, eventIndex, true,0, ref var);
      if (!ret) return null;
      return (System.Int16) var;
    }  }


  }

  public class LCTREQEventArgs : System.EventArgs
  {
    IHMIAccessorService accessorService;
    int channelId;
    int cookie; 
    int eventIndex;

    public LCTREQEventArgs(int channelId, int cookie, int eventIndex)
    {
      this.accessorService = (IHMIAccessorService)ServiceProvider.GetService(typeof(IHMIAccessorService));
      this.channelId = channelId;
      this.cookie = cookie;
      this.eventIndex = eventIndex;
    }
    public bool Get_LocalCycleType(ref System.Int16 value)
    {
      if (accessorService == null)
        return false;
      System.Int64 var = 0;
      bool ret = accessorService.GetInt64Value(channelId, cookie, eventIndex, true,0, ref var);
      if (ret) value = (System.Int16) var;
      return ret;
    }

    public System.Int16? LocalCycleType
    { get {
      if (accessorService == null)
        return null;
      System.Int64 var = 0;
      bool ret = accessorService.GetInt64Value(channelId, cookie, eventIndex, true,0, ref var);
      if (!ret) return null;
      return (System.Int16) var;
    }  }


  }

  public class LLFREQEventArgs : System.EventArgs
  {
    IHMIAccessorService accessorService;
    int channelId;
    int cookie; 
    int eventIndex;

    public LLFREQEventArgs(int channelId, int cookie, int eventIndex)
    {
      this.accessorService = (IHMIAccessorService)ServiceProvider.GetService(typeof(IHMIAccessorService));
      this.channelId = channelId;
      this.cookie = cookie;
      this.eventIndex = eventIndex;
    }
    public bool Get_LL_Fault(ref System.Boolean value)
    {
      if (accessorService == null)
        return false;
      bool var = false;
      bool ret = accessorService.GetBoolValue(channelId, cookie, eventIndex, true,0, ref var);
      if (ret) value = (System.Boolean) var;
      return ret;
    }

    public System.Boolean? LL_Fault
    { get {
      if (accessorService == null)
        return null;
      bool var = false;
      bool ret = accessorService.GetBoolValue(channelId, cookie, eventIndex, true,0, ref var);
      if (!ret) return null;
      return (System.Boolean) var;
    }  }


  }

  public class LLMREQEventArgs : System.EventArgs
  {
    IHMIAccessorService accessorService;
    int channelId;
    int cookie; 
    int eventIndex;

    public LLMREQEventArgs(int channelId, int cookie, int eventIndex)
    {
      this.accessorService = (IHMIAccessorService)ServiceProvider.GetService(typeof(IHMIAccessorService));
      this.channelId = channelId;
      this.cookie = cookie;
      this.eventIndex = eventIndex;
    }
    public bool Get_LL_Mode(ref System.Int16 value)
    {
      if (accessorService == null)
        return false;
      System.Int64 var = 0;
      bool ret = accessorService.GetInt64Value(channelId, cookie, eventIndex, true,0, ref var);
      if (ret) value = (System.Int16) var;
      return ret;
    }

    public System.Int16? LL_Mode
    { get {
      if (accessorService == null)
        return null;
      System.Int64 var = 0;
      bool ret = accessorService.GetInt64Value(channelId, cookie, eventIndex, true,0, ref var);
      if (!ret) return null;
      return (System.Int16) var;
    }  }


  }

  public class LLCTREQEventArgs : System.EventArgs
  {
    IHMIAccessorService accessorService;
    int channelId;
    int cookie; 
    int eventIndex;

    public LLCTREQEventArgs(int channelId, int cookie, int eventIndex)
    {
      this.accessorService = (IHMIAccessorService)ServiceProvider.GetService(typeof(IHMIAccessorService));
      this.channelId = channelId;
      this.cookie = cookie;
      this.eventIndex = eventIndex;
    }
    public bool Get_LL_CycleType(ref System.Int16 value)
    {
      if (accessorService == null)
        return false;
      System.Int64 var = 0;
      bool ret = accessorService.GetInt64Value(channelId, cookie, eventIndex, true,0, ref var);
      if (ret) value = (System.Int16) var;
      return ret;
    }

    public System.Int16? LL_CycleType
    { get {
      if (accessorService == null)
        return null;
      System.Int64 var = 0;
      bool ret = accessorService.GetInt64Value(channelId, cookie, eventIndex, true,0, ref var);
      if (!ret) return null;
      return (System.Int16) var;
    }  }


  }

}

namespace HMI.Main.Symbols.Station_CAT
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
    private System.Boolean? FaultReset_field = null;
    public System.Boolean? FaultReset
    {
       get { return FaultReset_field; }
       set { FaultReset_field = value; }
    }

  }

}

namespace HMI.Main.Symbols.Station_CAT
{
  partial class sDefault
  {

    private event EventHandler<HMI.Main.Symbols.Station_CAT.SREQEventArgs> SREQ_Fired;

    private event EventHandler<HMI.Main.Symbols.Station_CAT.LMREQEventArgs> LMREQ_Fired;

    private event EventHandler<HMI.Main.Symbols.Station_CAT.LCTREQEventArgs> LCTREQ_Fired;

    private event EventHandler<HMI.Main.Symbols.Station_CAT.LLFREQEventArgs> LLFREQ_Fired;

    private event EventHandler<HMI.Main.Symbols.Station_CAT.LLMREQEventArgs> LLMREQ_Fired;

    private event EventHandler<HMI.Main.Symbols.Station_CAT.LLCTREQEventArgs> LLCTREQ_Fired;

    protected override void OnEndInit()
    {
      if (SREQ_Fired != null)
        AttachEventInput(0);
      if (LMREQ_Fired != null)
        AttachEventInput(1);
      if (LCTREQ_Fired != null)
        AttachEventInput(2);
      if (LLFREQ_Fired != null)
        AttachEventInput(3);
      if (LLMREQ_Fired != null)
        AttachEventInput(4);
      if (LLCTREQ_Fired != null)
        AttachEventInput(5);

    }

    protected override void FireEventCallback(int channelId, int cookie, int eventIndex)
    {
      switch(eventIndex)
      {
        default:
          break;
        case 0:
          if (SREQ_Fired != null)
          {
            try
            {
              SREQ_Fired(this, new HMI.Main.Symbols.Station_CAT.SREQEventArgs(channelId, cookie, eventIndex));
            }
            catch (System.Exception e)
            {
              NxtControl.Services.LoggingService.ErrorFormatted(@"In Event Callback for event:'{0}' Type:'{1}' CAT:'{2}' came exception:{3}
stack Trace:
{4}","SREQ_Fired", this.GetType().Name, this.CATName, e.Message, e.StackTrace);
            }
          }
        break; 
        case 1:
          if (LMREQ_Fired != null)
          {
            try
            {
              LMREQ_Fired(this, new HMI.Main.Symbols.Station_CAT.LMREQEventArgs(channelId, cookie, eventIndex));
            }
            catch (System.Exception e)
            {
              NxtControl.Services.LoggingService.ErrorFormatted(@"In Event Callback for event:'{0}' Type:'{1}' CAT:'{2}' came exception:{3}
stack Trace:
{4}","LMREQ_Fired", this.GetType().Name, this.CATName, e.Message, e.StackTrace);
            }
          }
        break; 
        case 2:
          if (LCTREQ_Fired != null)
          {
            try
            {
              LCTREQ_Fired(this, new HMI.Main.Symbols.Station_CAT.LCTREQEventArgs(channelId, cookie, eventIndex));
            }
            catch (System.Exception e)
            {
              NxtControl.Services.LoggingService.ErrorFormatted(@"In Event Callback for event:'{0}' Type:'{1}' CAT:'{2}' came exception:{3}
stack Trace:
{4}","LCTREQ_Fired", this.GetType().Name, this.CATName, e.Message, e.StackTrace);
            }
          }
        break; 
        case 3:
          if (LLFREQ_Fired != null)
          {
            try
            {
              LLFREQ_Fired(this, new HMI.Main.Symbols.Station_CAT.LLFREQEventArgs(channelId, cookie, eventIndex));
            }
            catch (System.Exception e)
            {
              NxtControl.Services.LoggingService.ErrorFormatted(@"In Event Callback for event:'{0}' Type:'{1}' CAT:'{2}' came exception:{3}
stack Trace:
{4}","LLFREQ_Fired", this.GetType().Name, this.CATName, e.Message, e.StackTrace);
            }
          }
        break; 
        case 4:
          if (LLMREQ_Fired != null)
          {
            try
            {
              LLMREQ_Fired(this, new HMI.Main.Symbols.Station_CAT.LLMREQEventArgs(channelId, cookie, eventIndex));
            }
            catch (System.Exception e)
            {
              NxtControl.Services.LoggingService.ErrorFormatted(@"In Event Callback for event:'{0}' Type:'{1}' CAT:'{2}' came exception:{3}
stack Trace:
{4}","LLMREQ_Fired", this.GetType().Name, this.CATName, e.Message, e.StackTrace);
            }
          }
        break; 
        case 5:
          if (LLCTREQ_Fired != null)
          {
            try
            {
              LLCTREQ_Fired(this, new HMI.Main.Symbols.Station_CAT.LLCTREQEventArgs(channelId, cookie, eventIndex));
            }
            catch (System.Exception e)
            {
              NxtControl.Services.LoggingService.ErrorFormatted(@"In Event Callback for event:'{0}' Type:'{1}' CAT:'{2}' came exception:{3}
stack Trace:
{4}","LLCTREQ_Fired", this.GetType().Name, this.CATName, e.Message, e.StackTrace);
            }
          }
        break; 

      }
    }
    public bool FireEvent_MCNF(System.Int16 Mode)
    {
      return ((IHMIAccessorOutput)this).FireEvent(0, new object[] {Mode});
    }
    public bool FireEvent_MCNF(HMI.Main.Symbols.Station_CAT.MCNFEventArgs ea)
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
    public bool FireEvent_CTCNF(HMI.Main.Symbols.Station_CAT.CTCNFEventArgs ea)
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
    public bool FireEvent_FRCNF(System.Boolean FaultReset)
    {
      return ((IHMIAccessorOutput)this).FireEvent(2, new object[] {FaultReset});
    }
    public bool FireEvent_FRCNF(HMI.Main.Symbols.Station_CAT.FRCNFEventArgs ea)
    {
      object[] _values_ = new object[1];
      if (ea.FaultReset.HasValue) _values_[0] = ea.FaultReset.Value;
      return ((IHMIAccessorOutput)this).FireEvent(2, _values_);
    }
    public bool FireEvent_FRCNF(System.Boolean FaultReset, bool ignore_FaultReset)
    {
      object[] _values_ = new object[1];
      if (!ignore_FaultReset) _values_[0] = FaultReset;
      return ((IHMIAccessorOutput)this).FireEvent(2, _values_);
    }

  }
}
#endregion #Station_CAT_HMI;

#endregion Definitions;
