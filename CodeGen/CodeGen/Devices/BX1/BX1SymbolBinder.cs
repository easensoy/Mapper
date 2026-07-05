using CodeGen.Configuration;
using CodeGen.Translation;

namespace CodeGen.Devices.BX1
{
    // BX1 hardware-config symbol binding is deferred: BX1 is EtherNet/IP, so its .hcf carries
    // word channels (EIP_Input_Word_1) that only a PLC_RW_BX1 broker's WordToBits can unpack —
    // a direct bit-level CAT cannot consume a word. Left untouched pending the broker design.
    public static class BX1SymbolBinder
    {
        public static void BindBX1(MapperConfig? config,
            SystemInjector.BindingApplicationReport report)
        {
            report.Missing.Add(
                "[HcfBind][BX1] deferred — BX1 is EtherNet/IP (word channels EIP_Input_Word_1/" +
                "EIP_Output_Word_1). Resolving them needs a PLC_RW_BX1 broker FB on the BX1 " +
                "resource to unpack word->bits (WordToBits); the direct bit-level CATs cannot " +
                "consume a word. Left the BX1 .hcf untouched pending the broker decision.");
        }
    }
}
