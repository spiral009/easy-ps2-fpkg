using System;
using System.IO;
using CHDReaderTest;

namespace Ps2Fpkg
{
    /// <summary>Decompress a (DVD-type) CHD to a raw .iso, using the vendored managed CHD reader.</summary>
    public static class ChdExtract
    {
        public static void ToIso(string chdPath, string isoPath, Action<string> log)
        {
            using var s = File.OpenRead(chdPath);
            if (!CHDVersion.CheckHeader(s, out _, out uint version))
                throw new Exception("Not a valid CHD file.");
            if (version != 5)
                throw new Exception($"CHD v{version} is not supported here — re-create with a modern chdman (v5) or extract to .iso with chdman.");
            using var outFs = File.Create(isoPath);
            CHDV5.Extract(s, outFs, log);
        }
    }
}
