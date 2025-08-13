using System.Text;

namespace SECmd.Utils
{
    internal static class HKCrc
    {
        private static uint[]? table = null;

        public static string Compute(string input)
        {
            const uint POLYNOM = 0xEDB88320;
            
            if (table == null)
            {
                table = new uint[256];
                for(uint i = 0; i <  table.Length; i++)
                {
                    uint entry = i;
                    for(int j = 0; j < 8; ++j)
                    {
                        if ((entry & 0x1) != 0)
                            entry = entry >> 1 ^ POLYNOM;
                        else
                            entry >>= 1;
                    }
                    table[i] = entry;
                }
            }

            var bytes = Encoding.ASCII.GetBytes(input);
            uint hash = 0x0; // init val
            for(int i = 0; i < bytes.Length; i++)
            {
                hash = table[(hash ^ bytes[i]) & 0xFF] ^ hash >> 8;
            }

            return hash.ToString("X");
        }
    }
}
