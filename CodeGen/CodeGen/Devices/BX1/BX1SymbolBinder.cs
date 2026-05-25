using CodeGen.Configuration;
using CodeGen.Translation;

namespace CodeGen.Devices.BX1
{
    /// <summary>
    /// BX1 hardware-config symbol binding. Currently deferred — see
    /// <see cref="BindBX1"/>. BX1 is EtherNet/IP, so its <c>.hcf</c> carries
    /// <i>word</i> channels (<c>EIP_Input_Word_1</c>) that only a
    /// <c>PLC_RW_BX1</c> broker's internal <c>WordToBits</c> can unpack — a
    /// direct bit-level CAT cannot consume a word. That needs the broker design
    /// and is left untouched here by request.
    /// </summary>
    public static class BX1SymbolBinder
    {
        /// <summary>
        /// BX1 is intentionally deferred. Its EtherNet/IP <c>.hcf</c> carries
        /// word-level channels (<c>EIP_Input_Word_1</c>) that only a
        /// <c>PLC_RW_BX1</c> broker's internal <c>WordToBits</c>/<c>BitsToWord</c>
        /// can unpack; the Mapper's direct bit-level CATs (athome/atwork) cannot
        /// consume a word. Binding it correctly needs the broker design, so this
        /// only records the deferral and leaves the BX1 <c>.hcf</c> untouched.
        /// Tag <c>[HcfBind][BX1]</c>.
        /// </summary>
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
