using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace CodeGen.Devices.Core
{
    // Shared primitives for a SYMLINKMULTIVAR{SRC,DST} bridge between a PLC_RW word broker and the CAT
    // symlinks (the pattern PLC_RW_BX1/BX1_IO uses for the covers, and PLC_RW_REVPI/RevPI_IO uses for the
    // Feed actuators). EAE generates SYMLINKMULTIVAR{SRC,DST}_<hash> per BOOL arity; the hash is GUI-computed
    // (not derivable), and SRC/DST of one arity share it. Only these arities exist — pick the smallest that
    // covers the value count (surplus VALUEs are inert). (Bx1IoBrokerInjector keeps its own copy: it is the
    // rig-proven path and must not be disturbed.)
    internal static class SymlinkBridge
    {
        static readonly (int Arity, string Hash)[] BoolSymlinkTypes =
        {
            (1, "1559B0FF8170C9BA0"), (2, "277E97BEC1451D2C"), (3, "151ACB50A2F8223B2"),
            (4, "19628BFC3C74F1AB1"), (7, "238520AAD20108C65"), (10, "2183AAEC3B58E76C9"),
            (15, "2217C9CA39686140D"),
        };

        // The SYMLINKMULTIVAR{sd}_<hash> type + its arity, for the smallest arity >= need. sd is "SRC" or "DST".
        internal static (int Arity, string Type) Pick(string sd, int need)
        {
            var t = BoolSymlinkTypes.Where(x => x.Arity >= need).OrderBy(x => x.Arity).First();
            return (t.Arity, $"SYMLINKMULTIVAR{sd}_{t.Hash}");
        }

        internal static string Iface(int n) =>
            "Runtime.System#I:=" + n + ";VALUE${I}:" + string.Join(",", Enumerable.Repeat("BOOL", n));

        // A SYMLINKMULTIVAR bridge FB (QI=TRUE + one NAME<i> per symlink target). `symlinkNames[i]` is the
        // absolute symlink each VALUE binds to; unused VALUEs (arity > names) get an inert spare name.
        internal static XElement BuildFb(string ns, int id, int uid, string name, string type, int arity,
            IReadOnlyList<string> symlinkNames, int x, int y)
        {
            XName N(string n) => XName.Get(n, ns);
            var fb = new XElement(N("FB"),
                new XAttribute("ID", id), new XAttribute("UID", uid),
                new XAttribute("Name", name), new XAttribute("Type", type),
                new XAttribute("x", x.ToString()), new XAttribute("y", y.ToString()),
                new XAttribute("Namespace", "Main"),
                new XElement(N("Attribute"),
                    new XAttribute("Name", "Configuration.GenericFBType.InterfaceParams"),
                    new XAttribute("Value", Iface(arity))),
                new XElement(N("Parameter"), new XAttribute("Name", "QI"), new XAttribute("Value", "TRUE")));
            for (int i = 0; i < arity; i++)
            {
                var sym = i < symlinkNames.Count ? symlinkNames[i] : $"{name}.Spare{i + 1}";
                fb.Add(new XElement(N("Parameter"), new XAttribute("Name", $"NAME{i + 1}"),
                    new XAttribute("Value", $"'{sym}'")));
            }
            return fb;
        }
    }
}
