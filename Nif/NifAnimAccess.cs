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
        /// <summary>
        /// The name given to the sequence holding controllers that belong to no
        /// sequence of their own.
        /// </summary>
        public const string DefaultSequenceName = "Take 001";

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

            ReadStandaloneControllers(model, sequences);
            return sequences;
        }

        /// <summary>
        /// Picks up property controllers attached straight to a node.
        /// </summary>
        /// <remarks>
        /// A controller does not have to belong to a sequence: one hung off a node's
        /// controller chain plays for as long as the model is loaded. FBX has no
        /// equivalent of that, so they are gathered into one sequence named
        /// <see cref="DefaultSequenceName"/> — which is what FBXWrangler calls the
        /// stack it invents for the same reason (spec §4.7.3).
        ///
        /// Controllers a sequence already drives are left alone. In a file like
        /// Bethesda's animated effects the same controller block is both attached to
        /// its target and named by every sequence, and reading it twice would play it
        /// twice.
        /// </remarks>
        private static void ReadStandaloneControllers(NifModel model, List<AnimSequence> sequences)
        {
            var claimed = new HashSet<NifItem>();

            foreach (NifItem block in model.Blocks.Where(b => model.BlockInherits(b, "NiSequence")))
            {
                if (model.FindItem(block, "Controlled Blocks") is not { } controlled)
                    continue;

                foreach (NifItem entry in controlled.Children)
                {
                    if (model.GetRef(entry, "Controller") is { } c)
                        claimed.Add(c);
                }
            }

            var tracks = new Dictionary<string, AnimTrack>(StringComparer.Ordinal);

            foreach (NifItem block in model.Blocks)
            {
                if (!model.BlockInherits(block, "NiAVObject"))
                    continue;

                string name = model.GetName(block);

                if (name.Length == 0)
                    continue;

                for (NifItem? controller = model.GetRef(block, "Controller");
                     controller is not null;
                     controller = model.GetRef(controller, "Next Controller"))
                {
                    if (claimed.Contains(controller))
                        continue;

                    if (ReadStandaloneController(model, controller) is { } property)
                        TrackFor(tracks, name).Properties.Add(property);
                }
            }

            var keyed = tracks.Values.Where(t => t.HasKeys).ToList();

            if (keyed.Count == 0)
                return;

            var sequence = new AnimSequence { Name = DefaultSequenceName };
            sequence.Tracks.AddRange(keyed);
            (sequence.Start, sequence.Stop) = sequence.KeySpan();

            sequences.Add(sequence);
        }

        /// <summary>
        /// One node-attached controller, or null when it is not a kind this reads.
        /// </summary>
        /// <remarks>
        /// The two the spec names. Others exist — transform, colour, texture — but
        /// they drive things FBX has no property for, and inventing one would export
        /// a number no importer could act on.
        /// </remarks>
        private static AnimProperty? ReadStandaloneController(NifModel model, NifItem controller)
        {
            bool visibility = model.BlockInherits(controller, "NiVisController");

            if (!visibility && !model.BlockInherits(controller, "NiFloatExtraDataController"))
                return null;

            if (model.GetRef(controller, "Interpolator") is not { } interpolator)
                return null;

            // An extra data controller names its target through the extra data's own
            // name, which is also the id a sequence would identify it by.
            string id = visibility ? string.Empty : model.GetString(controller, "Extra Data Name");

            var property = new AnimProperty
            {
                Name = AnimProperty.ToPropertyName(controller.Name, id, string.Empty, string.Empty),
                IsBoolean = visibility,
                ControllerType = controller.Name,
                ControllerId = id
            };

            return ReadScalarKeys(model, interpolator, property.Curve) ? property : null;
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

            // One track per node, however many controlled blocks turn out to name it:
            // a node's transform and its properties are separate blocks here and one
            // track there.
            var tracks = new Dictionary<string, AnimTrack>(StringComparer.Ordinal);

            if (model.FindItem(block, "Controlled Blocks") is { } controlled)
            {
                foreach (NifItem entry in controlled.Children)
                    ReadControlledBlock(model, entry, tracks);
            }

            sequence.Tracks.AddRange(tracks.Values.Where(t => t.HasKeys));

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

        /// <summary>
        /// Files one controlled block under the node it targets.
        /// </summary>
        /// <remarks>
        /// The interpolator's type is what says which kind of track this is. A
        /// transform interpolator drives the node itself; a float or boolean one
        /// drives some named scalar on it, and the four identifying strings beside it
        /// are the only record of which.
        /// </remarks>
        private static void ReadControlledBlock(
            NifModel model, NifItem controlled, Dictionary<string, AnimTrack> tracks)
        {
            NifItem? interpolator = model.GetRef(controlled, "Interpolator");

            if (interpolator is null)
                return;

            string name = ReadTargetName(model, controlled);

            if (name.Length == 0)
                return;

            if (model.BlockInherits(interpolator, "NiTransformInterpolator"))
            {
                if (model.GetRef(interpolator, "Data") is { } data)
                    ReadTransformTrack(model, data, TrackFor(tracks, name));

                return;
            }

            bool boolean = model.BlockInherits(interpolator, "NiBoolInterpolator");

            if (!boolean && !model.BlockInherits(interpolator, "NiFloatInterpolator"))
                return;

            var property = new AnimProperty
            {
                Name = AnimProperty.ToPropertyName(
                    model.GetString(controlled, "Controller Type"),
                    model.GetString(controlled, "Controller ID"),
                    model.GetString(controlled, "Interpolator ID"),
                    model.GetString(controlled, "Property Type")),
                IsBoolean = boolean,
                ControllerType = model.GetString(controlled, "Controller Type"),
                ControllerId = model.GetString(controlled, "Controller ID"),
                InterpolatorId = model.GetString(controlled, "Interpolator ID"),
                PropertyType = model.GetString(controlled, "Property Type")
            };

            if (ReadScalarKeys(model, interpolator, property.Curve))
                TrackFor(tracks, name).Properties.Add(property);
        }

        private static AnimTrack TrackFor(Dictionary<string, AnimTrack> tracks, string name)
        {
            if (!tracks.TryGetValue(name, out AnimTrack? track))
                tracks[name] = track = new AnimTrack { NodeName = name };

            return track;
        }

        private static void ReadTransformTrack(NifModel model, NifItem data, AnimTrack track)
        {
            ReadTranslations(model, data, track);
            ReadRotations(model, data, track);
            ReadScales(model, data, track);
        }

        /// <summary>
        /// Reads a float or boolean interpolator's keys.
        /// </summary>
        /// <remarks>
        /// Both store their keys the same way — a single key group two blocks down —
        /// so the only difference is that boolean values arrive as bytes. They are
        /// read as the zero and one they stand for, which is what an FBX curve can
        /// carry anyway.
        /// </remarks>
        private static bool ReadScalarKeys(NifModel model, NifItem interpolator, AnimCurve curve)
        {
            if (model.GetRef(interpolator, "Data") is not { } block
                || model.FindItem(block, "Data") is not { } group)
            {
                return false;
            }

            AnimInterpolation interpolation = InterpolationOf(model, group);

            foreach (NifItem key in KeysOf(model, group))
            {
                NifItem? value = model.FindItem(key, "Value");

                curve.Keys.Add(new AnimKey(
                    FloatOf(model, key, "Time"),
                    value?.Value.ToFloat() ?? 0f,
                    interpolation));
            }

            return curve.HasKeys;
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
