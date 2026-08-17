namespace SECmd.Conversion
{
    /// <summary>How a key blends into the next one.</summary>
    /// <remarks>
    /// The three both formats agree on. NIF's TBC keys carry tension, bias and
    /// continuity that FBX expresses differently, so they arrive here as
    /// <see cref="Cubic"/> — the shape of the curve is preserved, the authoring
    /// handles are not.
    /// </remarks>
    public enum AnimInterpolation
    {
        /// <summary>Hold the value until the next key.</summary>
        Constant,

        /// <summary>Straight line to the next key.</summary>
        Linear,

        /// <summary>Smooth, with tangents.</summary>
        Cubic
    }

    /// <summary>One key on one curve. Time is in seconds.</summary>
    public readonly record struct AnimKey(float Time, float Value, AnimInterpolation Interpolation);

    /// <summary>A single animated scalar over time.</summary>
    public sealed class AnimCurve
    {
        public List<AnimKey> Keys { get; } = [];

        public bool HasKeys => Keys.Count > 0;

        /// <summary>The first and last key times, or zero when empty.</summary>
        public (float Start, float Stop) Span =>
            Keys.Count == 0 ? (0f, 0f) : (Keys[0].Time, Keys[^1].Time);
    }

    /// <summary>
    /// One node's animation, as the nine curves FBX addresses separately.
    /// </summary>
    /// <remarks>
    /// NIF groups a transform's keys by component — a translation key is one
    /// Vector3, a rotation key one quaternion — while FBX animates X, Y and Z as
    /// three independent curves. Splitting on the way in means the two directions
    /// only have to agree about scalars.
    ///
    /// Rotation is in **degrees**, Euler XYZ, matching what a node's static
    /// <c>Lcl Rotation</c> carries. Anything else and an animated node would jump
    /// the moment its first key took effect.
    /// </remarks>
    public sealed class AnimTrack
    {
        public required string NodeName { get; init; }

        public AnimCurve[] Translation { get; } = [new(), new(), new()];

        public AnimCurve[] Rotation { get; } = [new(), new(), new()];

        public AnimCurve[] Scale { get; } = [new(), new(), new()];

        public IEnumerable<AnimCurve> Curves => Translation.Concat(Rotation).Concat(Scale);

        public bool HasKeys => Curves.Any(c => c.HasKeys);
    }

    /// <summary>
    /// One animation: a named span of time and the nodes it moves.
    /// </summary>
    /// <remarks>
    /// A NIF's <c>NiControllerSequence</c> and an FBX's animation stack are the same
    /// idea under different names, so this is what both convert through.
    /// </remarks>
    public sealed class AnimSequence
    {
        public required string Name { get; init; }

        /// <summary>When the sequence begins, in seconds.</summary>
        public float Start { get; set; }

        /// <summary>When it ends, in seconds.</summary>
        public float Stop { get; set; }

        public List<AnimTrack> Tracks { get; } = [];

        /// <summary>
        /// The span the keys actually cover, for when the sequence does not say.
        /// </summary>
        /// <remarks>
        /// A sequence whose declared span is empty or inverted — Bethesda's files
        /// leave the float sentinels in place often enough — would otherwise import
        /// as an animation of zero length.
        /// </remarks>
        public (float Start, float Stop) KeySpan()
        {
            float start = float.MaxValue;
            float stop = float.MinValue;

            foreach (AnimCurve curve in Tracks.SelectMany(t => t.Curves).Where(c => c.HasKeys))
            {
                (float first, float last) = curve.Span;
                start = MathF.Min(start, first);
                stop = MathF.Max(stop, last);
            }

            return start > stop ? (0f, 0f) : (start, stop);
        }
    }
}
