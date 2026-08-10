/*
 * Created by EcoStruxure Automation Expert.
 * User:  
 * Date: 10/7/2025
 * Time: 10:13 AM
 * 
 */
using System;
using NxtControl.GuiFramework;
using NxtControl.Services;

#region Definitions;
#region #Seven_State_Actuator_Centre_Home_CAT_HMI;

namespace HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT
{

  public class output_eventEventArgs : System.EventArgs
  {
    IHMIAccessorService accessorService;
    int channelId;
    int cookie; 
    int eventIndex;

    public output_eventEventArgs(int channelId, int cookie, int eventIndex)
    {
      this.accessorService = (IHMIAccessorService)ServiceProvider.GetService(typeof(IHMIAccessorService));
      this.channelId = channelId;
      this.cookie = cookie;
      this.eventIndex = eventIndex;
    }
    public bool Get_toWork2PLC(ref System.Boolean value)
    {
      if (accessorService == null)
        return false;
      bool var = false;
      bool ret = accessorService.GetBoolValue(channelId, cookie, eventIndex, true,0, ref var);
      if (ret) value = (System.Boolean) var;
      return ret;
    }

    public System.Boolean? toWork2PLC
    { get {
      if (accessorService == null)
        return null;
      bool var = false;
      bool ret = accessorService.GetBoolValue(channelId, cookie, eventIndex, true,0, ref var);
      if (!ret) return null;
      return (System.Boolean) var;
    }  }

    public bool Get_toWork1PLC(ref System.Boolean value)
    {
      if (accessorService == null)
        return false;
      bool var = false;
      bool ret = accessorService.GetBoolValue(channelId, cookie, eventIndex, true,1, ref var);
      if (ret) value = (System.Boolean) var;
      return ret;
    }

    public System.Boolean? toWork1PLC
    { get {
      if (accessorService == null)
        return null;
      bool var = false;
      bool ret = accessorService.GetBoolValue(channelId, cookie, eventIndex, true,1, ref var);
      if (!ret) return null;
      return (System.Boolean) var;
    }  }


  }

  public class pst_eventEventArgs : System.EventArgs
  {
    IHMIAccessorService accessorService;
    int channelId;
    int cookie; 
    int eventIndex;

    public pst_eventEventArgs(int channelId, int cookie, int eventIndex)
    {
      this.accessorService = (IHMIAccessorService)ServiceProvider.GetService(typeof(IHMIAccessorService));
      this.channelId = channelId;
      this.cookie = cookie;
      this.eventIndex = eventIndex;
    }
    public bool Get_current_state_to_process(ref System.Int16 value)
    {
      if (accessorService == null)
        return false;
      System.Int64 var = 0;
      bool ret = accessorService.GetInt64Value(channelId, cookie, eventIndex, true,0, ref var);
      if (ret) value = (System.Int16) var;
      return ret;
    }

    public System.Int16? current_state_to_process
    { get {
      if (accessorService == null)
        return null;
      System.Int64 var = 0;
      bool ret = accessorService.GetInt64Value(channelId, cookie, eventIndex, true,0, ref var);
      if (!ret) return null;
      return (System.Int16) var;
    }  }


  }

  public class input_eventEventArgs : System.EventArgs
  {
    IHMIAccessorService accessorService;
    int channelId;
    int cookie; 
    int eventIndex;

    public input_eventEventArgs(int channelId, int cookie, int eventIndex)
    {
      this.accessorService = (IHMIAccessorService)ServiceProvider.GetService(typeof(IHMIAccessorService));
      this.channelId = channelId;
      this.cookie = cookie;
      this.eventIndex = eventIndex;
    }
    public bool Get_atHome(ref System.Boolean value)
    {
      if (accessorService == null)
        return false;
      bool var = false;
      bool ret = accessorService.GetBoolValue(channelId, cookie, eventIndex, true,0, ref var);
      if (ret) value = (System.Boolean) var;
      return ret;
    }

    public System.Boolean? atHome
    { get {
      if (accessorService == null)
        return null;
      bool var = false;
      bool ret = accessorService.GetBoolValue(channelId, cookie, eventIndex, true,0, ref var);
      if (!ret) return null;
      return (System.Boolean) var;
    }  }

    public bool Get_atWork1(ref System.Boolean value)
    {
      if (accessorService == null)
        return false;
      bool var = false;
      bool ret = accessorService.GetBoolValue(channelId, cookie, eventIndex, true,1, ref var);
      if (ret) value = (System.Boolean) var;
      return ret;
    }

    public System.Boolean? atWork1
    { get {
      if (accessorService == null)
        return null;
      bool var = false;
      bool ret = accessorService.GetBoolValue(channelId, cookie, eventIndex, true,1, ref var);
      if (!ret) return null;
      return (System.Boolean) var;
    }  }

    public bool Get_atWork2(ref System.Boolean value)
    {
      if (accessorService == null)
        return false;
      bool var = false;
      bool ret = accessorService.GetBoolValue(channelId, cookie, eventIndex, true,2, ref var);
      if (ret) value = (System.Boolean) var;
      return ret;
    }

    public System.Boolean? atWork2
    { get {
      if (accessorService == null)
        return null;
      bool var = false;
      bool ret = accessorService.GetBoolValue(channelId, cookie, eventIndex, true,2, ref var);
      if (!ret) return null;
      return (System.Boolean) var;
    }  }


  }

  public class FAULT_EVENTEventArgs : System.EventArgs
  {
    IHMIAccessorService accessorService;
    int channelId;
    int cookie; 
    int eventIndex;

    public FAULT_EVENTEventArgs(int channelId, int cookie, int eventIndex)
    {
      this.accessorService = (IHMIAccessorService)ServiceProvider.GetService(typeof(IHMIAccessorService));
      this.channelId = channelId;
      this.cookie = cookie;
      this.eventIndex = eventIndex;
    }
    public bool Get_fault_active(ref System.Boolean value)
    {
      if (accessorService == null)
        return false;
      bool var = false;
      bool ret = accessorService.GetBoolValue(channelId, cookie, eventIndex, true,0, ref var);
      if (ret) value = (System.Boolean) var;
      return ret;
    }

    public System.Boolean? fault_active
    { get {
      if (accessorService == null)
        return null;
      bool var = false;
      bool ret = accessorService.GetBoolValue(channelId, cookie, eventIndex, true,0, ref var);
      if (!ret) return null;
      return (System.Boolean) var;
    }  }

    public bool Get_fault_code(ref System.Int16 value)
    {
      if (accessorService == null)
        return false;
      System.Int64 var = 0;
      bool ret = accessorService.GetInt64Value(channelId, cookie, eventIndex, true,1, ref var);
      if (ret) value = (System.Int16) var;
      return ret;
    }

    public System.Int16? fault_code
    { get {
      if (accessorService == null)
        return null;
      System.Int64 var = 0;
      bool ret = accessorService.GetInt64Value(channelId, cookie, eventIndex, true,1, ref var);
      if (!ret) return null;
      return (System.Int16) var;
    }  }


  }

  public class name_eventEventArgs : System.EventArgs
  {
    IHMIAccessorService accessorService;
    int channelId;
    int cookie; 
    int eventIndex;

    public name_eventEventArgs(int channelId, int cookie, int eventIndex)
    {
      this.accessorService = (IHMIAccessorService)ServiceProvider.GetService(typeof(IHMIAccessorService));
      this.channelId = channelId;
      this.cookie = cookie;
      this.eventIndex = eventIndex;
    }
    public bool Get_component_name(ref System.String value)
    {
      if (accessorService == null)
        return false;
      string var = null;
      bool ret = accessorService.GetStringValue(channelId, cookie, eventIndex, true,0, ref var);
      if (ret) value = (System.String) var;
      return ret;
    }

    public System.String component_name
    { get {
      if (accessorService == null)
        return null;
      string var = null;
      bool ret = accessorService.GetStringValue(channelId, cookie, eventIndex, true,0, ref var);
      if (!ret) return null;
      return (System.String) var;
    }  }

    public bool Get_ModeCMD(ref System.Int16 value)
    {
      if (accessorService == null)
        return false;
      System.Int64 var = 0;
      bool ret = accessorService.GetInt64Value(channelId, cookie, eventIndex, true,1, ref var);
      if (ret) value = (System.Int16) var;
      return ret;
    }

    public System.Int16? ModeCMD
    { get {
      if (accessorService == null)
        return null;
      System.Int64 var = 0;
      bool ret = accessorService.GetInt64Value(channelId, cookie, eventIndex, true,1, ref var);
      if (!ret) return null;
      return (System.Int16) var;
    }  }


  }

  public class mode_eventEventArgs : System.EventArgs
  {
    IHMIAccessorService accessorService;
    int channelId;
    int cookie; 
    int eventIndex;

    public mode_eventEventArgs(int channelId, int cookie, int eventIndex)
    {
      this.accessorService = (IHMIAccessorService)ServiceProvider.GetService(typeof(IHMIAccessorService));
      this.channelId = channelId;
      this.cookie = cookie;
      this.eventIndex = eventIndex;
    }
    public bool Get_ModeCMD(ref System.Int16 value)
    {
      if (accessorService == null)
        return false;
      System.Int64 var = 0;
      bool ret = accessorService.GetInt64Value(channelId, cookie, eventIndex, true,0, ref var);
      if (ret) value = (System.Int16) var;
      return ret;
    }

    public System.Int16? ModeCMD
    { get {
      if (accessorService == null)
        return null;
      System.Int64 var = 0;
      bool ret = accessorService.GetInt64Value(channelId, cookie, eventIndex, true,0, ref var);
      if (!ret) return null;
      return (System.Int16) var;
    }  }


  }

  public class interlock_eventEventArgs : System.EventArgs
  {
    IHMIAccessorService accessorService;
    int channelId;
    int cookie; 
    int eventIndex;

    public interlock_eventEventArgs(int channelId, int cookie, int eventIndex)
    {
      this.accessorService = (IHMIAccessorService)ServiceProvider.GetService(typeof(IHMIAccessorService));
      this.channelId = channelId;
      this.cookie = cookie;
      this.eventIndex = eventIndex;
    }
    public bool Get_ActiveRuleIndex(ref System.Int16 value)
    {
      if (accessorService == null)
        return false;
      System.Int64 var = 0;
      bool ret = accessorService.GetInt64Value(channelId, cookie, eventIndex, true,0, ref var);
      if (ret) value = (System.Int16) var;
      return ret;
    }

    public System.Int16? ActiveRuleIndex
    { get {
      if (accessorService == null)
        return null;
      System.Int64 var = 0;
      bool ret = accessorService.GetInt64Value(channelId, cookie, eventIndex, true,0, ref var);
      if (!ret) return null;
      return (System.Int16) var;
    }  }

    public bool Get_ActiveSourceID(ref System.Int16 value)
    {
      if (accessorService == null)
        return false;
      System.Int64 var = 0;
      bool ret = accessorService.GetInt64Value(channelId, cookie, eventIndex, true,1, ref var);
      if (ret) value = (System.Int16) var;
      return ret;
    }

    public System.Int16? ActiveSourceID
    { get {
      if (accessorService == null)
        return null;
      System.Int64 var = 0;
      bool ret = accessorService.GetInt64Value(channelId, cookie, eventIndex, true,1, ref var);
      if (!ret) return null;
      return (System.Int16) var;
    }  }

    public bool Get_ActiveBlockedState(ref System.Int16 value)
    {
      if (accessorService == null)
        return false;
      System.Int64 var = 0;
      bool ret = accessorService.GetInt64Value(channelId, cookie, eventIndex, true,2, ref var);
      if (ret) value = (System.Int16) var;
      return ret;
    }

    public System.Int16? ActiveBlockedState
    { get {
      if (accessorService == null)
        return null;
      System.Int64 var = 0;
      bool ret = accessorService.GetInt64Value(channelId, cookie, eventIndex, true,2, ref var);
      if (!ret) return null;
      return (System.Int16) var;
    }  }

    public bool Get_MoveAllowed(ref System.Boolean value)
    {
      if (accessorService == null)
        return false;
      bool var = false;
      bool ret = accessorService.GetBoolValue(channelId, cookie, eventIndex, true,3, ref var);
      if (ret) value = (System.Boolean) var;
      return ret;
    }

    public System.Boolean? MoveAllowed
    { get {
      if (accessorService == null)
        return null;
      bool var = false;
      bool ret = accessorService.GetBoolValue(channelId, cookie, eventIndex, true,3, ref var);
      if (!ret) return null;
      return (System.Boolean) var;
    }  }

    public bool Get_Work1Interlock(ref System.Boolean value)
    {
      if (accessorService == null)
        return false;
      bool var = false;
      bool ret = accessorService.GetBoolValue(channelId, cookie, eventIndex, true,4, ref var);
      if (ret) value = (System.Boolean) var;
      return ret;
    }

    public System.Boolean? Work1Interlock
    { get {
      if (accessorService == null)
        return null;
      bool var = false;
      bool ret = accessorService.GetBoolValue(channelId, cookie, eventIndex, true,4, ref var);
      if (!ret) return null;
      return (System.Boolean) var;
    }  }

    public bool Get_Work2Interlock(ref System.Boolean value)
    {
      if (accessorService == null)
        return false;
      bool var = false;
      bool ret = accessorService.GetBoolValue(channelId, cookie, eventIndex, true,5, ref var);
      if (ret) value = (System.Boolean) var;
      return ret;
    }

    public System.Boolean? Work2Interlock
    { get {
      if (accessorService == null)
        return null;
      bool var = false;
      bool ret = accessorService.GetBoolValue(channelId, cookie, eventIndex, true,5, ref var);
      if (!ret) return null;
      return (System.Boolean) var;
    }  }


  }

}

namespace HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT
{

  public class cmd_eventEventArgs : System.EventArgs
  {
    public cmd_eventEventArgs()
    {
    }
    private System.Boolean? toWork1_field = null;
    public System.Boolean? toWork1
    {
       get { return toWork1_field; }
       set { toWork1_field = value; }
    }
    private System.Boolean? toHome_field = null;
    public System.Boolean? toHome
    {
       get { return toHome_field; }
       set { toHome_field = value; }
    }
    private System.Boolean? toWork2_field = null;
    public System.Boolean? toWork2
    {
       get { return toWork2_field; }
       set { toWork2_field = value; }
    }

  }

}

namespace HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT
{
  partial class sDefault
  {

    private event EventHandler<HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT.output_eventEventArgs> output_event_Fired;

    private event EventHandler<HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT.pst_eventEventArgs> pst_event_Fired;

    private event EventHandler<HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT.input_eventEventArgs> input_event_Fired;

    private event EventHandler<HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT.FAULT_EVENTEventArgs> FAULT_EVENT_Fired;

    private event EventHandler<HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT.name_eventEventArgs> name_event_Fired;

    private event EventHandler<HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT.mode_eventEventArgs> mode_event_Fired;

    private event EventHandler<HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT.interlock_eventEventArgs> interlock_event_Fired;

    protected override void OnEndInit()
    {
      if (output_event_Fired != null)
        AttachEventInput(0);
      if (pst_event_Fired != null)
        AttachEventInput(1);
      if (input_event_Fired != null)
        AttachEventInput(2);
      if (FAULT_EVENT_Fired != null)
        AttachEventInput(3);
      if (name_event_Fired != null)
        AttachEventInput(4);
      if (mode_event_Fired != null)
        AttachEventInput(5);
      if (interlock_event_Fired != null)
        AttachEventInput(6);

    }

    protected override void FireEventCallback(int channelId, int cookie, int eventIndex)
    {
      switch(eventIndex)
      {
        default:
          break;
        case 0:
          if (output_event_Fired != null)
          {
            try
            {
              output_event_Fired(this, new HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT.output_eventEventArgs(channelId, cookie, eventIndex));
            }
            catch (System.Exception e)
            {
              NxtControl.Services.LoggingService.ErrorFormatted(@"In Event Callback for event:'{0}' Type:'{1}' CAT:'{2}' came exception:{3}
stack Trace:
{4}","output_event_Fired", this.GetType().Name, this.CATName, e.Message, e.StackTrace);
            }
          }
        break; 
        case 1:
          if (pst_event_Fired != null)
          {
            try
            {
              pst_event_Fired(this, new HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT.pst_eventEventArgs(channelId, cookie, eventIndex));
            }
            catch (System.Exception e)
            {
              NxtControl.Services.LoggingService.ErrorFormatted(@"In Event Callback for event:'{0}' Type:'{1}' CAT:'{2}' came exception:{3}
stack Trace:
{4}","pst_event_Fired", this.GetType().Name, this.CATName, e.Message, e.StackTrace);
            }
          }
        break; 
        case 2:
          if (input_event_Fired != null)
          {
            try
            {
              input_event_Fired(this, new HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT.input_eventEventArgs(channelId, cookie, eventIndex));
            }
            catch (System.Exception e)
            {
              NxtControl.Services.LoggingService.ErrorFormatted(@"In Event Callback for event:'{0}' Type:'{1}' CAT:'{2}' came exception:{3}
stack Trace:
{4}","input_event_Fired", this.GetType().Name, this.CATName, e.Message, e.StackTrace);
            }
          }
        break; 
        case 3:
          if (FAULT_EVENT_Fired != null)
          {
            try
            {
              FAULT_EVENT_Fired(this, new HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT.FAULT_EVENTEventArgs(channelId, cookie, eventIndex));
            }
            catch (System.Exception e)
            {
              NxtControl.Services.LoggingService.ErrorFormatted(@"In Event Callback for event:'{0}' Type:'{1}' CAT:'{2}' came exception:{3}
stack Trace:
{4}","FAULT_EVENT_Fired", this.GetType().Name, this.CATName, e.Message, e.StackTrace);
            }
          }
        break; 
        case 4:
          if (name_event_Fired != null)
          {
            try
            {
              name_event_Fired(this, new HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT.name_eventEventArgs(channelId, cookie, eventIndex));
            }
            catch (System.Exception e)
            {
              NxtControl.Services.LoggingService.ErrorFormatted(@"In Event Callback for event:'{0}' Type:'{1}' CAT:'{2}' came exception:{3}
stack Trace:
{4}","name_event_Fired", this.GetType().Name, this.CATName, e.Message, e.StackTrace);
            }
          }
        break; 
        case 5:
          if (mode_event_Fired != null)
          {
            try
            {
              mode_event_Fired(this, new HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT.mode_eventEventArgs(channelId, cookie, eventIndex));
            }
            catch (System.Exception e)
            {
              NxtControl.Services.LoggingService.ErrorFormatted(@"In Event Callback for event:'{0}' Type:'{1}' CAT:'{2}' came exception:{3}
stack Trace:
{4}","mode_event_Fired", this.GetType().Name, this.CATName, e.Message, e.StackTrace);
            }
          }
        break; 
        case 6:
          if (interlock_event_Fired != null)
          {
            try
            {
              interlock_event_Fired(this, new HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT.interlock_eventEventArgs(channelId, cookie, eventIndex));
            }
            catch (System.Exception e)
            {
              NxtControl.Services.LoggingService.ErrorFormatted(@"In Event Callback for event:'{0}' Type:'{1}' CAT:'{2}' came exception:{3}
stack Trace:
{4}","interlock_event_Fired", this.GetType().Name, this.CATName, e.Message, e.StackTrace);
            }
          }
        break; 

      }
    }
    public bool FireEvent_cmd_event(System.Boolean toWork1, System.Boolean toHome, System.Boolean toWork2)
    {
      return ((IHMIAccessorOutput)this).FireEvent(0, new object[] {toWork1, toHome, toWork2});
    }
    public bool FireEvent_cmd_event(HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT.cmd_eventEventArgs ea)
    {
      object[] _values_ = new object[3];
      if (ea.toWork1.HasValue) _values_[0] = ea.toWork1.Value;
      if (ea.toHome.HasValue) _values_[1] = ea.toHome.Value;
      if (ea.toWork2.HasValue) _values_[2] = ea.toWork2.Value;
      return ((IHMIAccessorOutput)this).FireEvent(0, _values_);
    }
    public bool FireEvent_cmd_event(System.Boolean toWork1, bool ignore_toWork1, System.Boolean toHome, bool ignore_toHome, System.Boolean toWork2, bool ignore_toWork2)
    {
      object[] _values_ = new object[3];
      if (!ignore_toWork1) _values_[0] = toWork1;
      if (!ignore_toHome) _values_[1] = toHome;
      if (!ignore_toWork2) _values_[2] = toWork2;
      return ((IHMIAccessorOutput)this).FireEvent(0, _values_);
    }

  }
}

namespace HMI.Main.Faceplates.Seven_State_Actuator_Centre_Home_CAT
{
  partial class setup
  {

    private event EventHandler<HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT.output_eventEventArgs> output_event_Fired;

    private event EventHandler<HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT.pst_eventEventArgs> pst_event_Fired;

    private event EventHandler<HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT.input_eventEventArgs> input_event_Fired;

    private event EventHandler<HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT.FAULT_EVENTEventArgs> FAULT_EVENT_Fired;

    private event EventHandler<HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT.name_eventEventArgs> name_event_Fired;

    private event EventHandler<HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT.mode_eventEventArgs> mode_event_Fired;

    private event EventHandler<HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT.interlock_eventEventArgs> interlock_event_Fired;

    protected override void OnEndInit()
    {
      if (output_event_Fired != null)
        AttachEventInput(0);
      if (pst_event_Fired != null)
        AttachEventInput(1);
      if (input_event_Fired != null)
        AttachEventInput(2);
      if (FAULT_EVENT_Fired != null)
        AttachEventInput(3);
      if (name_event_Fired != null)
        AttachEventInput(4);
      if (mode_event_Fired != null)
        AttachEventInput(5);
      if (interlock_event_Fired != null)
        AttachEventInput(6);

    }

    protected override void FireEventCallback(int channelId, int cookie, int eventIndex)
    {
      switch(eventIndex)
      {
        default:
          break;
        case 0:
          if (output_event_Fired != null)
          {
            try
            {
              output_event_Fired(this, new HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT.output_eventEventArgs(channelId, cookie, eventIndex));
            }
            catch (System.Exception e)
            {
              NxtControl.Services.LoggingService.ErrorFormatted(@"In Event Callback for event:'{0}' Type:'{1}' CAT:'{2}' came exception:{3}
stack Trace:
{4}","output_event_Fired", this.GetType().Name, this.CATName, e.Message, e.StackTrace);
            }
          }
        break; 
        case 1:
          if (pst_event_Fired != null)
          {
            try
            {
              pst_event_Fired(this, new HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT.pst_eventEventArgs(channelId, cookie, eventIndex));
            }
            catch (System.Exception e)
            {
              NxtControl.Services.LoggingService.ErrorFormatted(@"In Event Callback for event:'{0}' Type:'{1}' CAT:'{2}' came exception:{3}
stack Trace:
{4}","pst_event_Fired", this.GetType().Name, this.CATName, e.Message, e.StackTrace);
            }
          }
        break; 
        case 2:
          if (input_event_Fired != null)
          {
            try
            {
              input_event_Fired(this, new HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT.input_eventEventArgs(channelId, cookie, eventIndex));
            }
            catch (System.Exception e)
            {
              NxtControl.Services.LoggingService.ErrorFormatted(@"In Event Callback for event:'{0}' Type:'{1}' CAT:'{2}' came exception:{3}
stack Trace:
{4}","input_event_Fired", this.GetType().Name, this.CATName, e.Message, e.StackTrace);
            }
          }
        break; 
        case 3:
          if (FAULT_EVENT_Fired != null)
          {
            try
            {
              FAULT_EVENT_Fired(this, new HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT.FAULT_EVENTEventArgs(channelId, cookie, eventIndex));
            }
            catch (System.Exception e)
            {
              NxtControl.Services.LoggingService.ErrorFormatted(@"In Event Callback for event:'{0}' Type:'{1}' CAT:'{2}' came exception:{3}
stack Trace:
{4}","FAULT_EVENT_Fired", this.GetType().Name, this.CATName, e.Message, e.StackTrace);
            }
          }
        break; 
        case 4:
          if (name_event_Fired != null)
          {
            try
            {
              name_event_Fired(this, new HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT.name_eventEventArgs(channelId, cookie, eventIndex));
            }
            catch (System.Exception e)
            {
              NxtControl.Services.LoggingService.ErrorFormatted(@"In Event Callback for event:'{0}' Type:'{1}' CAT:'{2}' came exception:{3}
stack Trace:
{4}","name_event_Fired", this.GetType().Name, this.CATName, e.Message, e.StackTrace);
            }
          }
        break; 
        case 5:
          if (mode_event_Fired != null)
          {
            try
            {
              mode_event_Fired(this, new HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT.mode_eventEventArgs(channelId, cookie, eventIndex));
            }
            catch (System.Exception e)
            {
              NxtControl.Services.LoggingService.ErrorFormatted(@"In Event Callback for event:'{0}' Type:'{1}' CAT:'{2}' came exception:{3}
stack Trace:
{4}","mode_event_Fired", this.GetType().Name, this.CATName, e.Message, e.StackTrace);
            }
          }
        break; 
        case 6:
          if (interlock_event_Fired != null)
          {
            try
            {
              interlock_event_Fired(this, new HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT.interlock_eventEventArgs(channelId, cookie, eventIndex));
            }
            catch (System.Exception e)
            {
              NxtControl.Services.LoggingService.ErrorFormatted(@"In Event Callback for event:'{0}' Type:'{1}' CAT:'{2}' came exception:{3}
stack Trace:
{4}","interlock_event_Fired", this.GetType().Name, this.CATName, e.Message, e.StackTrace);
            }
          }
        break; 

      }
    }
    public bool FireEvent_cmd_event(System.Boolean toWork1, System.Boolean toHome, System.Boolean toWork2)
    {
      return ((IHMIAccessorOutput)this).FireEvent(0, new object[] {toWork1, toHome, toWork2});
    }
    public bool FireEvent_cmd_event(HMI.Main.Symbols.Seven_State_Actuator_Centre_Home_CAT.cmd_eventEventArgs ea)
    {
      object[] _values_ = new object[3];
      if (ea.toWork1.HasValue) _values_[0] = ea.toWork1.Value;
      if (ea.toHome.HasValue) _values_[1] = ea.toHome.Value;
      if (ea.toWork2.HasValue) _values_[2] = ea.toWork2.Value;
      return ((IHMIAccessorOutput)this).FireEvent(0, _values_);
    }
    public bool FireEvent_cmd_event(System.Boolean toWork1, bool ignore_toWork1, System.Boolean toHome, bool ignore_toHome, System.Boolean toWork2, bool ignore_toWork2)
    {
      object[] _values_ = new object[3];
      if (!ignore_toWork1) _values_[0] = toWork1;
      if (!ignore_toHome) _values_[1] = toHome;
      if (!ignore_toWork2) _values_[2] = toWork2;
      return ((IHMIAccessorOutput)this).FireEvent(0, _values_);
    }

  }
}
#endregion #Seven_State_Actuator_Centre_Home_CAT_HMI;

#endregion Definitions;
