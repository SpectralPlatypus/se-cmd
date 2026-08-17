using SECmd.Nif;

namespace SECmd.Havok
{
    /// <summary>
    /// Builds Havok MOPP bounding-volume trees for mesh collision shapes.
    /// </summary>
    /// <remarks>
    /// MOPP generation is the one part of the conversion that genuinely needs Havok,
    /// and Havok's licence forbids redistributing it as part of a tool. So se-cmd
    /// contains no Havok code and instead calls out to a binary the user supplies —
    /// the same posture NifSkope takes.
    ///
    /// Two backends implement this: <see cref="NifMoppGenerator"/> (in-process
    /// P/Invoke into NifMopp.dll, Windows only) and
    /// <see cref="MopperProcessGenerator"/> (out-of-process mopper.exe, which runs
    /// under Wine and is therefore the portable option).
    /// </remarks>
    public interface IMoppGenerator
    {
        /// <summary>True when this backend can actually run.</summary>
        bool IsAvailable { get; }

        /// <summary>Why the backend is unusable, for reporting.</summary>
        string? UnavailableReason { get; }

        /// <summary>
        /// Builds MOPP code for a triangle mesh, as needed by a
        /// <c>bhkMoppBvTreeShape</c> wrapping a simple mesh shape.
        /// </summary>
        /// <returns>The code, or null when generation was not possible.</returns>
        MoppResult? GenerateSimpleMesh(IReadOnlyList<NifVector3> vertices, IReadOnlyList<NifTriangle> triangles);
    }

    /// <summary>
    /// A generated MOPP tree, with the quantisation it was built against and the
    /// per-triangle welding info Havok computed alongside it.
    /// </summary>
    /// <param name="Code">The MOPP bytecode, stored verbatim in the NIF.</param>
    /// <param name="Origin">Offset mapping world space into the tree's integer space.</param>
    /// <param name="Scale">Scale mapping world space into the tree's integer space.</param>
    /// <param name="WeldingInfo">
    /// Per-triangle welding info, empty when the backend does not report it.
    /// </param>
    public sealed record MoppResult(
        byte[] Code,
        NifVector3 Origin,
        float Scale,
        IReadOnlyList<ushort> WeldingInfo);
}
