using System.Text;

namespace SECmd.Conversion
{
    /// <summary>
    /// Escapes and unescapes node names between NIF and FBX.
    /// </summary>
    /// <remarks>
    /// FBX node names cannot carry arbitrary characters, so FBXWrangler substitutes
    /// four of them (see <c>MathHelper.cpp</c>). We reproduce the substitution
    /// exactly, because names are the only thing tying an FBX node back to the NIF
    /// block it came from — and, in the FBX to NIF direction, the only thing marking
    /// a node as a rigid body or a constraint.
    ///
    /// The mapping is deliberately not injective: a name containing a literal
    /// <c>_s_</c> decodes back to a space. That is a defect in the original, but
    /// changing it would break every FBX already exported by ck-cmd, so it stands.
    /// </remarks>
    public static class NameEncoding
    {
        private static readonly (string Character, string Escape)[] Substitutions =
        [
            (" ", "_s_"),
            ("[", "_ob_"),
            ("]", "_cb_"),
            (":", "_dd_")
        ];

        /// <summary>Encodes a NIF name for use as an FBX node name.</summary>
        public static string Sanitize(string name)
        {
            if (name.Length == 0)
                return name;

            var text = new StringBuilder(name);

            foreach ((string character, string escape) in Substitutions)
                text.Replace(character, escape);

            return text.ToString();
        }

        /// <summary>Decodes an FBX node name back to a NIF name.</summary>
        public static string Unsanitize(string name)
        {
            if (name.Length == 0)
                return name;

            var text = new StringBuilder(name);

            foreach ((string character, string escape) in Substitutions)
                text.Replace(escape, character);

            return text.ToString();
        }
    }
}
