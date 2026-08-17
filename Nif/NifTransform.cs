using System.Numerics;

namespace SECmd.Nif
{
    /// <summary>
    /// A NIF node transform: a translation, a rotation matrix, and a single uniform
    /// scale.
    /// </summary>
    /// <remarks>
    /// NIF has no non-uniform scale, which is why <see cref="Scale"/> is one float
    /// rather than three. Anything non-uniform coming back from FBX has to be baked
    /// into the geometry instead.
    /// </remarks>
    public readonly struct NifTransform(NifVector3 translation, NifMatrix33 rotation, float scale)
    {
        public NifVector3 Translation { get; } = translation;

        public NifMatrix33 Rotation { get; } = rotation;

        public float Scale { get; } = scale;

        public static NifTransform Identity => new(new NifVector3(), NifMatrix33.Identity, 1f);

        /// <summary>Composes this transform with a parent one, parent applied last.</summary>
        public NifTransform ComposedWith(NifTransform parent)
        {
            Matrix4x4 combined = ToMatrix() * parent.ToMatrix();
            return FromMatrix(combined);
        }

        /// <summary>The transform as a row-vector matrix, matching NIF's convention.</summary>
        public Matrix4x4 ToMatrix()
        {
            NifMatrix33 r = Rotation;
            float s = Scale;

            return new Matrix4x4(
                r.M11 * s, r.M12 * s, r.M13 * s, 0f,
                r.M21 * s, r.M22 * s, r.M23 * s, 0f,
                r.M31 * s, r.M32 * s, r.M33 * s, 0f,
                Translation.X, Translation.Y, Translation.Z, 1f);
        }

        /// <summary>
        /// Decomposes a matrix back into a NIF transform, taking the scale as the
        /// mean of the three axis lengths.
        /// </summary>
        public static NifTransform FromMatrix(Matrix4x4 m)
        {
            var x = new Vector3(m.M11, m.M12, m.M13);
            var y = new Vector3(m.M21, m.M22, m.M23);
            var z = new Vector3(m.M31, m.M32, m.M33);

            float sx = x.Length();
            float sy = y.Length();
            float sz = z.Length();
            float scale = (sx + sy + sz) / 3f;

            if (sx > 0) x /= sx;
            if (sy > 0) y /= sy;
            if (sz > 0) z /= sz;

            var rotation = new NifMatrix33
            {
                M11 = x.X, M12 = x.Y, M13 = x.Z,
                M21 = y.X, M22 = y.Y, M23 = y.Z,
                M31 = z.X, M32 = z.Y, M33 = z.Z
            };

            return new NifTransform(new NifVector3(m.M41, m.M42, m.M43), rotation, scale);
        }

        /// <summary>Applies the transform to a point.</summary>
        public NifVector3 Apply(NifVector3 point)
        {
            NifMatrix33 r = Rotation;
            float s = Scale;

            return new NifVector3(
                (point.X * r.M11 + point.Y * r.M21 + point.Z * r.M31) * s + Translation.X,
                (point.X * r.M12 + point.Y * r.M22 + point.Z * r.M32) * s + Translation.Y,
                (point.X * r.M13 + point.Y * r.M23 + point.Z * r.M33) * s + Translation.Z);
        }

        /// <summary>Applies only the rotation, for normals and other directions.</summary>
        public NifVector3 ApplyDirection(NifVector3 direction)
        {
            NifMatrix33 r = Rotation;

            return new NifVector3(
                direction.X * r.M11 + direction.Y * r.M21 + direction.Z * r.M31,
                direction.X * r.M12 + direction.Y * r.M22 + direction.Z * r.M32,
                direction.X * r.M13 + direction.Y * r.M23 + direction.Z * r.M33);
        }

        /// <summary>The rotation as a quaternion.</summary>
        public NifQuat ToQuaternion()
        {
            NifMatrix33 m = Rotation;
            float trace = m.M11 + m.M22 + m.M33;
            float w, x, y, z;

            if (trace > 0f)
            {
                float s = MathF.Sqrt(trace + 1f) * 2f;
                w = 0.25f * s;
                x = (m.M23 - m.M32) / s;
                y = (m.M31 - m.M13) / s;
                z = (m.M12 - m.M21) / s;
            }
            else if (m.M11 > m.M22 && m.M11 > m.M33)
            {
                float s = MathF.Sqrt(1f + m.M11 - m.M22 - m.M33) * 2f;
                w = (m.M23 - m.M32) / s;
                x = 0.25f * s;
                y = (m.M21 + m.M12) / s;
                z = (m.M31 + m.M13) / s;
            }
            else if (m.M22 > m.M33)
            {
                float s = MathF.Sqrt(1f + m.M22 - m.M11 - m.M33) * 2f;
                w = (m.M31 - m.M13) / s;
                x = (m.M21 + m.M12) / s;
                y = 0.25f * s;
                z = (m.M32 + m.M23) / s;
            }
            else
            {
                float s = MathF.Sqrt(1f + m.M33 - m.M11 - m.M22) * 2f;
                w = (m.M12 - m.M21) / s;
                x = (m.M31 + m.M13) / s;
                y = (m.M32 + m.M23) / s;
                z = 0.25f * s;
            }

            return new NifQuat(w, x, y, z);
        }

        /// <summary>
        /// The rotation as Euler angles in **degrees**, in FBX's XYZ order.
        /// </summary>
        /// <remarks>
        /// FBXWrangler goes matrix to quaternion to Euler XYZ (<c>EulOrdXYZs</c>) and
        /// writes degrees into LclRotation. The order is load-bearing: a different
        /// one silently produces wrong rotations for anything but trivial cases.
        /// </remarks>
        public NifVector3 ToEulerDegrees()
        {
            NifMatrix33 m = Rotation;

            // Rz * Ry * Rx applied to a row vector, which is what EulOrdXYZs means
            // once NIF's row-major storage is accounted for.
            float sy = -m.M13;
            float x, y, z;

            if (sy is > 0.99999f or < -0.99999f)
            {
                // Gimbal lock: yaw is +/-90 degrees and roll folds into yaw.
                y = MathF.CopySign(MathF.PI / 2f, sy);
                x = MathF.Atan2(-m.M32, m.M22);
                z = 0f;
            }
            else
            {
                y = MathF.Asin(sy);
                x = MathF.Atan2(m.M23, m.M33);
                z = MathF.Atan2(m.M12, m.M11);
            }

            const float ToDegrees = 180f / MathF.PI;
            return new NifVector3(x * ToDegrees, y * ToDegrees, z * ToDegrees);
        }

        /// <summary>Builds a rotation matrix from Euler angles in degrees, XYZ order.</summary>
        public static NifMatrix33 RotationFromEulerDegrees(float x, float y, float z)
        {
            const float ToRadians = MathF.PI / 180f;

            float cx = MathF.Cos(x * ToRadians), sx = MathF.Sin(x * ToRadians);
            float cy = MathF.Cos(y * ToRadians), sy = MathF.Sin(y * ToRadians);
            float cz = MathF.Cos(z * ToRadians), sz = MathF.Sin(z * ToRadians);

            return new NifMatrix33
            {
                M11 = cy * cz,
                M12 = cy * sz,
                M13 = -sy,
                M21 = sx * sy * cz - cx * sz,
                M22 = sx * sy * sz + cx * cz,
                M23 = sx * cy,
                M31 = cx * sy * cz + sx * sz,
                M32 = cx * sy * sz - sx * cz,
                M33 = cx * cy
            };
        }

        public override string ToString() =>
            $"T{Translation} S{Scale:G6}";
    }
}
