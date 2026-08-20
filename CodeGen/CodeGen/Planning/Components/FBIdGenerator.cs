using System;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Mapping;

namespace CodeGen.Translation
{
    public static class FBIdGenerator
    {
        public static string GenerateFBId(string seed)
        {
            if (seed == null) throw new ArgumentNullException(nameof(seed));
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(seed));
            var sb = new StringBuilder(16);
            for (int i = 0; i < 8; i++)
                sb.Append(hash[i].ToString("X2"));
            return sb.ToString();
        }
    }
}
