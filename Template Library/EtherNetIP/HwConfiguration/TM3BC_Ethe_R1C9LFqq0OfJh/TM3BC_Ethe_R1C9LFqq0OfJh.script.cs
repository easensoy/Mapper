using System;
using System.Collections.Generic;
using NxtControl.ComponentModel.LibraryElements;
using NxtControl.Automation.DeviceConfigurator;
using Standard.IoEtherNetIP.MasterConfig.EIPSCANNER;

namespace HwConfiguration
{
	/// <summary>
	/// Description of SlaveType.
	/// </summary>
	public partial class TM3BC_Ethe_R1C9LFqq0OfJh : NxtControl.Automation.DeviceConfigurator.HWCAT
	{
		public override object Execute( HWCAT caller, object userData)
		{ 
			var ci = new connectinfo();
			ci.devicenumber = this.connectinfo.devicenumber;
			ci.ipaddress = this.connectinfo.ipaddress;
			var buscoupler = new buscoupler();
			buscoupler.name = this.InstanceName;
			buscoupler.connectinfo = ci;
			var busdevicelist = new List<busdevice>();

			//------------------------------------------------------------------
			// EIPConnectionInput0
			var busdeviceInput0 = new busdevice();
			busdeviceInput0.name = "EIPCONNECTION" + 0  + "_" + this.InstanceName + "_IN";
			busdeviceInput0.parameter = new parameter[1];
			
			// EIPConnectionOutput0
			var busdeviceOutput0 = new busdevice();
			busdeviceOutput0.name = "EIPCONNECTION" + 0 + "_" + this.InstanceName + "_OUT";
			busdeviceOutput0.parameter = new parameter[1];
			
			//--------------------
			// Inputs 0
			var parameterIn0 = new parameter();
			parameterIn0.objectid = this.input0.objectid;
			parameterIn0.length = this.input0.MemoryRequested;

			parameterIn0.datatype = parameterDatatype.input;
			parameterIn0.ioevent = parameterIoevent.cyclic;
			busdeviceInput0.parameter[0] = parameterIn0;
			
			//--------------------
			// Outputs 0
			var parameterOut0 = new parameter();
			parameterOut0.objectid = this.output0.objectid;
			parameterOut0.length = this.output0.MemoryRequested;
			parameterOut0.datatype = parameterDatatype.output;
			parameterOut0.ioevent = parameterIoevent.requestwrite;
			busdeviceOutput0.parameter[0] = parameterOut0;
			
			//-------------------
			// Add bus devices input & output 0
			busdevicelist.Add(busdeviceInput0);
			busdevicelist.Add(busdeviceOutput0); 
			//------------------------------------------------------------------
			
			//update bus coupler
			buscoupler.busdevice = busdevicelist.ToArray();
			return buscoupler;
		}

		public override FBNetwork GetMyCRDNetwork()
		{
			uint nInputBits = 0;
			uint nOutputBits = 0;
			
			nInputBits += this.input0.MemoryRequested * 8;
			nOutputBits += this.output0.MemoryRequested * 8;

			return GenericSenderReceiverHelper.GenerateFbNetwork(this.InstanceName, Convert.ToInt32(nInputBits), Convert.ToInt32(nOutputBits), true);
		}
	}
}
