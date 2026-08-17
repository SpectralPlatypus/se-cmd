using SECmd.Conversion;

namespace SECmd.Nif
{
    /// <summary>
    /// Reads transform animation out of a model.
    /// </summary>
    /// <remarks>
    /// A NIF keeps its animations in <c>NiControllerSequence</c> blocks, each a list
    /// of <c>ControlledBlock</c>s pairing a target node with an interpolator. The
    /// interpolator's <c>NiTransformData</c> holds the keys, grouped by component:
    /// translations as Vector3 keys, scales as floats, and rotations either as
    /// quaternions or — when the rotation type says XYZ — as three separate float
    /// groups.
    ///
    /// Only transform tracks are read. Float and boolean controllers (shader
    /// properties, visibility, particle emitters) reach the same sequences through
    /// their own interpolator types and are skipped rather than mis-read.
    /// </remarks>
    public static class NifAnimAccess
    {
        /// <summary>Every animation in the model, in block order.</summary>
        public static List<AnimSequence> ReadAnimations(this NifModel model)
        {
            var sequences = new List<AnimSequence>();

            foreach (NifItem block in model.Blocks)
            {
                if (!model.BlockInherits(block, "NiSequence"))
                    continue;

                if (model.ReadSequence(block) is { } sequence)
                    sequences.Add(sequence);
            }

            return sequences;
        }

        /// <summary>One sequence, or null when it animates nothing this reads.</summary>
        public static AnimSequence? ReadSequence(this NifModel model, NifItem block)
        {
            var sequence = new AnimSequence
            {
                Name = model.GetString(block, "Name"),
                Start = FloatOf(model, block, "Start Time"),
                Stop = FloatOf(model, block, "Stop Time")
            };

            if (model.FindItem(block, "Controlled Blocks") is { } controlled)
            {
                foreach (NifItem entry in controlled.Children)
                {
                    if (ReadTrack(model, entry) is { } track)
                        sequence.Tracks.Add(track);
                }
            }

            if (sequence.Tracks.Count == 0)
                return null;

            // Bethesda's files often leave the float sentinels in the declared span,
            // which would import as an animation lasting no time at all.
            if (!(sequence.Stop > sequence.Start) || !float.IsFinite(sequence.Start)
                || !float.IsFinite(sequence.Stop) || MathF.Abs(sequence.Stop) > 1e9f)
            {
                (sequence.Start, sequence.Stop) = sequence.KeySpan();
            }

            return sequence;
        }

        private static AnimTrack? ReadTrack(NifModel model, NifItem controlled)
        {
            NifItem? interpolator = model.GetRef(controlled, "Interpolator");

            if (interpolator is null || !model.BlockInherits(interpolator, "NiTransformInterpolator"))
                return null;

            NifItem? data = model.GetRef(interpolator, "Data");

            if (data is null)
                return null;

            string name = ReadTargetName(model, controlled);

            if (name.Length == 0)
                return null;

            var track = new AnimTrack { NodeName = name };

            ReadTranslations(model, data, track);
            ReadRotations(model, data, track);
            ReadScales(model, data, track);

            return track.HasKeys ? track : null;
        }

        /// <summary>
        /// The name of the node a controlled block targets.
        /// </summary>
        /// <remarks>
        /// Three spellings, by version. Modern files store the name outright; files
        /// between 10.2 and 20.1 store an offset into a shared
        /// <c>NiStringPalette</c>, which is how a .kf keeps its target names in one
        /// place; older ones name the target directly in the block.
        /// </remarks>
        private static string ReadTargetName(NifModel model, NifItem controlled)
        {
            if (model.FindItem(controlled, "Node Name") is not null)
            {
                string direct = model.GetString(controlled, "Node Name");

                if (direct.Length > 0)
                    return direct;
            }

            if (model.GetRef(controlled, "String Palette") is { } palette
                && model.FindItem(controlled, "Node Name Offset") is { } offset)
            {
                return ReadFromPalette(model, palette, offset.Value.ToUInt());
            }

            return model.FindItem(controlled, "Target Name") is not null
                ? model.GetString(controlled, "Target Name")
                : string.Empty;
        }

        /// <summary>The NUL-terminated string at an offset into a string palette.</summary>
        private static string ReadFromPalette(NifModel model, NifItem palette, uint offset)
        {
            // The unset offset is all ones, not zero -- zero is a real string.
            if (offset == uint.MaxValue)
                return string.Empty;

            string all = model.GetString(palette, @"Palette\Palette");

            if (offset >= all.Length)
                return string.Empty;

            int end = all.IndexOf('\0', (int)offset);
            return end < 0 ? all[(int)offset..] : all[(int)offset..end];
        }

        private static void ReadTranslations(NifModel model, NifItem data, AnimTrack track)
        {
            if (model.FindItem(data, "Translations") is not { } group)
                return;

            AnimInterpolation interpolation = InterpolationOf(model, group);

            foreach (NifItem key in KeysOf(model, group))
            {
                float time = FloatOf(model, key, "Time");
                NifVector3 value = model.FindItem(key, "Value")?.Value.Get<NifVector3>() ?? new NifVector3();

                track.Translation[0].Keys.Add(new AnimKey(time, value.X, interpolation));
                track.Translation[1].Keys.Add(new AnimKey(time, value.Y, interpolation));
                track.Translation[2].Keys.Add(new AnimKey(time, value.Z, interpolation));
            }
        }

        /// <summary>
        /// Rotation keys, which come one of two ways.
        /// </summary>
        /// <remarks>
        /// Rotation type 4 means the file already stores X, Y and Z as separate
        /// float groups — in radians — and the quaternion array is then empty
        /// regardless of what the key count says. Any other type means quaternion
        /// keys, which have to be decomposed into the same Euler XYZ degrees a
        /// node's static rotation uses.
        /// </remarks>
        private static void ReadRotations(NifModel model, NifItem data, AnimTrack track)
        {
            const uint XyzRotation = 4;
            const float ToDegrees = 180f / MathF.PI;

            if (model.GetUInt(data, "Rotation Type") == XyzRotation)
            {
                if (model.FindItem(data, "XYZ Rotations") is not { } groups)
                    return;

                for (int axis = 0; axis < 3 && axis < groups.Children.Count; axis++)
                {
                    NifItem group = groups.Children[axis];
                    AnimInterpolation interpolation = InterpolationOf(model, group);

                    foreach (NifItem key in KeysOf(model, group))
                    {
                        track.Rotation[axis].Keys.Add(new AnimKey(
                            FloatOf(model, key, "Time"),
                            FloatOf(model, key, "Value") * ToDegrees,
                            interpolation));
                    }
                }

                return;
            }

            if (model.FindItem(data, "Quaternion Keys") is not { } keys)
                return;

            foreach (NifItem key in keys.Children)
            {
                float time = FloatOf(model, key, "Time");
                NifQuat value = model.FindItem(key, "Value")?.Value.Get<NifQuat>() ?? NifQuat.Identity;

                NifVector3 euler = new NifTransform(
                    new NifVector3(), NifTransform.RotationFromQuaternion(value), 1f).ToEulerDegrees();

                // A quaternion carries no tangents, so the smooth reading is the
                // only one that reproduces the slerp it stood for.
                track.Rotation[0].Keys.Add(new AnimKey(time, euler.X, AnimInterpolation.Cubic));
                track.Rotation[1].Keys.Add(new AnimKey(time, euler.Y, AnimInterpolation.Cubic));
                track.Rotation[2].Keys.Add(new AnimKey(time, euler.Z, AnimInterpolation.Cubic));
            }
        }

        private static void ReadScales(NifModel model, NifItem data, AnimTrack track)
        {
            if (model.FindItem(data, "Scales") is not { } group)
                return;

            AnimInterpolation interpolation = InterpolationOf(model, group);

            foreach (NifItem key in KeysOf(model, group))
            {
                float time = FloatOf(model, key, "Time");
                float value = FloatOf(model, key, "Value");

                // NIF scales uniformly; FBX has three axes and wants all of them.
                for (int axis = 0; axis < 3; axis++)
                    track.Scale[axis].Keys.Add(new AnimKey(time, value, interpolation));
            }
        }

        private static IEnumerable<NifItem> KeysOf(NifModel model, NifItem group) =>
            model.FindItem(group, "Keys")?.Children ?? Enumerable.Empty<NifItem>();

        private static AnimInterpolation InterpolationOf(NifModel model, NifItem group) =>
            FromKeyType(model.GetUInt(group, "Interpolation"));

        /// <summary>Maps a NIF key type onto the interpolation FBX understands.</summary>
        private static AnimInterpolation FromKeyType(uint keyType) => keyType switch
        {
            1 => AnimInterpolation.Linear,
            2 => AnimInterpolation.Cubic,

            // Tension/bias/continuity keys are curves too; FBX just describes their
            // handles differently, so the curve survives and the handles do not.
            3 => AnimInterpolation.Cubic,
            5 => AnimInterpolation.Constant,
            _ => AnimInterpolation.Linear
        };

        private static float FloatOf(NifModel model, NifItem parent, string field) =>
            model.FindItem(parent, field)?.Value.ToFloat() ?? 0f;
    }
}
