using System.Globalization;

namespace SECmd.Nif
{
    /// <summary>
    /// Conversions between the dotted version strings nif.xml uses and the packed
    /// uint32 stored in the file, where each component occupies one byte.
    /// </summary>
    public static class NifVersion
    {
        /// <summary>
        /// Parses a version string such as "20.2.0.7" into 0x14020007.
        /// </summary>
        /// <remarks>
        /// Two-component strings are the pre-4.0 style, where the digits after the
        /// dot are taken one at a time: "4.123" means 4.1.2.3. A bare number is
        /// treated as already packed, with 0xFFFFFFFF meaning "unset". Anything
        /// unparseable yields 0, matching NifSkope rather than throwing, because
        /// nif.xml is full of version attributes that are legitimately empty.
        /// </remarks>
        public static uint FromString(string? s)
        {
            if (string.IsNullOrEmpty(s))
                return 0;

            if (s.Contains('.'))
            {
                string[] parts = s.Split('.');

                if (parts.Length > 4)
                    return 0;

                uint v = 0;

                if (parts.Length == 2)
                {
                    // Old style: the major version, then one digit per byte.
                    v += ParseByte(parts[0]) << (3 * 8);

                    if (parts[1].Length >= 1)
                        v += ParseByte(parts[1].Substring(0, 1)) << (2 * 8);

                    if (parts[1].Length >= 2)
                        v += ParseByte(parts[1].Substring(1, 1)) << (1 * 8);

                    if (parts[1].Length >= 3)
                        v += ParseByte(parts[1][2..]);

                    return v;
                }

                for (int i = 0; i < 4 && i < parts.Length; i++)
                    v += ParseByte(parts[i]) << ((3 - i) * 8);

                return v;
            }

            if (uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint packed))
                return packed == 0xFFFFFFFF ? 0 : packed;

            return 0;
        }

        private static uint ParseByte(string s) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? unchecked((uint)i) : 0u;

        /// <summary>Formats a packed version as "20.2.0.7".</summary>
        public static string ToVersionString(uint version) =>
            $"{(version >> 24) & 0xFF}.{(version >> 16) & 0xFF}.{(version >> 8) & 0xFF}.{version & 0xFF}";

        /// <summary>True when the string looks like a four-component version literal.</summary>
        public static bool IsVersionLiteral(string s)
        {
            int dots = 0;
            bool digitInGroup = false;

            foreach (char c in s)
            {
                if (c == '.')
                {
                    if (!digitInGroup)
                        return false;

                    dots++;
                    digitInGroup = false;
                }
                else if (char.IsAsciiDigit(c))
                {
                    digitInGroup = true;
                }
                else
                {
                    return false;
                }
            }

            return dots == 3 && digitInGroup;
        }
    }
}
