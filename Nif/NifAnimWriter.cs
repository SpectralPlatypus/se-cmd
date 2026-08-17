using SECmd.Conversion;

namespace SECmd.Nif
{
    /// <summary>
    /// Builds the blocks that hold a model's animation.
    /// </summary>
    /// <remarks>
    /// A NIF does not attach animation to the nodes it moves. It hangs a
    /// <c>NiControllerManager</c> off the root, holding one
    /// <c>NiControllerSequence</c> per animation, each listing the nodes it drives
    /// by name. Two more blocks make that indirection work: a
    /// <c>NiMultiTargetTransformController</c> naming every node any sequence
    /// touches, and a <c>NiDefaultAVObjectPalette</c> mapping those names back to
    /// blocks so the engine can resolve a sequence without walking the tree.
    ///
    /// Follows the spec's §5.6, which is FBXWrangler's shape for this.
    /// </remarks>
    public static class NifAnimWriter
    {
        /// <summary>Manager flags: active, and driven by the animation system.</summary>
        private const uint ManagerFlags = 12;

        /// <summary>Transform controller flags, as FBXWrangler writes them.</summary>
        private const uint TransformControllerFlags = 44;

        /// <summary>Play once and hold, which is what an exported take means.</summary>
        private const uint CycleClamp = 0;

        /// <summary>Rotation stored as three separate axis groups.</summary>
        private const uint XyzRotationKey = 4;

        /// <summary>
        /// The value Gamebryo reads as "no base transform", so the interpolator falls
        /// back to the node's own.
        /// </summary>
        /// <remarks>
        /// Not a number anyone computed: it is <c>-FLT_MAX</c>, whose bit pattern
        /// <c>0xFF7FFFFF</c> is what the files show. Writing a real transform here
        /// instead would override the node's rest pose on every channel the keys do
        /// not cover.
        /// </remarks>
        private const float UnsetTransform = float.MinValue;

        /// <summary>
        /// Writes every sequence, returning the manager, or null when there is
        /// nothing to write.
        /// </summary>
        /// <param name="nodes">The blocks a track can name, by name.</param>
        /// <param name="warnings">Collects tracks whose node does not exist.</param>
        public static NifItem? WriteAnimations(
            this NifModel model,
            NifItem root,
            IReadOnlyList<AnimSequence> sequences,
            IReadOnlyDictionary<string, NifItem> nodes,
            List<string> warnings)
        {
            // Resolve first: a sequence with no resolvable target is a sequence with
            // nothing to write, and the manager should not exist for it.
            var resolved = new List<(AnimSequence Sequence, List<(AnimTrack Track, NifItem Node)> Tracks)>();
            var targets = new List<NifItem>();

            foreach (AnimSequence sequence in sequences)
            {
                var tracks = new List<(AnimTrack, NifItem)>();

                foreach (AnimTrack track in sequence.Tracks)
                {
                    if (!nodes.TryGetValue(track.NodeName, out NifItem? node))
                    {
                        warnings.Add(
                            $"{sequence.Name}: no node named \"{track.NodeName}\", its animation is dropped");
                        continue;
                    }

                    tracks.Add((track, node));

                    // Only nodes whose transform moves belong in the target list: it
                    // is what the transform controller drives, and a node listed
                    // there without transform keys would be driven to nothing.
                    if (track.Curves.Any(c => c.HasKeys) && !targets.Contains(node))
                        targets.Add(node);
                }

                if (tracks.Count > 0)
                    resolved.Add((sequence, tracks));
            }

            if (resolved.Count == 0)
                return null;

            NifItem manager = model.InsertBlock("NiControllerManager");
            model.SetRef(manager, "Target", root);
            model.FindItem(manager, "Flags")?.Value.SetCount(ManagerFlags);
            model.FindItem(manager, "Frequency")?.Value.SetFloat(1f);
            model.FindItem(manager, "Phase")?.Value.SetFloat(0f);

            NifItem controller = WriteMultiTargetController(model, root, targets);
            model.SetRef(manager, "Next Controller", controller);

            model.SetRef(manager, "Object Palette", WritePalette(model, root, targets));

            // The manager is reached through the root's controller chain, which is
            // the only thing that makes it part of the file rather than a loose block.
            model.SetRef(root, "Controller", manager);

            var built = new List<NifItem>();

            foreach ((AnimSequence sequence, var tracks) in resolved)
                built.Add(WriteSequence(model, manager, sequence, tracks));

            if (model.SetArraySize(manager, "Num Controller Sequences", "Controller Sequences", built.Count)
                is { } list)
            {
                for (int i = 0; i < built.Count && i < list.Children.Count; i++)
                    list.Children[i].Value.SetLink(model.IndexOf(built[i]));
            }

            return manager;
        }

        /// <summary>
        /// One controller naming every node any sequence moves.
        /// </summary>
        /// <remarks>
        /// The engine binds a sequence's tracks through this list rather than through
        /// each node's own controller chain, so a node missing from it stays still
        /// however many keys name it.
        /// </remarks>
        private static NifItem WriteMultiTargetController(
            NifModel model, NifItem root, List<NifItem> targets)
        {
            NifItem controller = model.InsertBlock("NiMultiTargetTransformController");

            model.SetRef(controller, "Target", root);
            model.FindItem(controller, "Flags")?.Value.SetCount(TransformControllerFlags);
            model.FindItem(controller, "Frequency")?.Value.SetFloat(1f);
            model.FindItem(controller, "Phase")?.Value.SetFloat(0f);
            model.FindItem(controller, "Start Time")?.Value.SetFloat(0f);
            model.FindItem(controller, "Stop Time")?.Value.SetFloat(0f);

            if (model.SetArraySize(controller, "Num Extra Targets", "Extra Targets", targets.Count)
                is { } extra)
            {
                for (int i = 0; i < targets.Count && i < extra.Children.Count; i++)
                    extra.Children[i].Value.SetLink(model.IndexOf(targets[i]));
            }

            return controller;
        }

        /// <summary>The name-to-block table the engine resolves sequences through.</summary>
        private static NifItem WritePalette(NifModel model, NifItem root, List<NifItem> targets)
        {
            NifItem palette = model.InsertBlock("NiDefaultAVObjectPalette");
            model.SetRef(palette, "Scene", root);

            // The root is in the palette too, because a sequence may name it as its
            // accumulation root.
            var all = new List<NifItem> { root };
            all.AddRange(targets.Where(t => t != root));

            if (model.SetArraySize(palette, "Num Objs", "Objs", all.Count) is not { } objects)
                return palette;

            for (int i = 0; i < all.Count && i < objects.Children.Count; i++)
            {
                NifItem entry = objects.Children[i];

                // A SizedString, not a table index: the palette is meant to be
                // readable without the header.
                model.FindItem(entry, "Name")?.Value.Set(model.GetName(all[i]));
                model.FindItem(entry, "AV Object")?.Value.SetLink(model.IndexOf(all[i]));
            }

            return palette;
        }

        private static NifItem WriteSequence(
            NifModel model, NifItem manager, AnimSequence sequence,
            List<(AnimTrack Track, NifItem Node)> tracks)
        {
            NifItem block = model.InsertBlock("NiControllerSequence");

            model.SetString(block, "Name", sequence.Name);
            model.SetRef(block, "Manager", manager);
            model.SetString(block, "Accum Root Name", model.GetName(model.Blocks[0]));

            // Sequences play from zero; where they sat on the source timeline is not
            // something the engine has any use for.
            float length = MathF.Max(sequence.Stop - sequence.Start, 0f);

            model.FindItem(block, "Start Time")?.Value.SetFloat(0f);
            model.FindItem(block, "Stop Time")?.Value.SetFloat(length);
            model.FindItem(block, "Frequency")?.Value.SetFloat(1f);
            model.FindItem(block, "Weight")?.Value.SetFloat(1f);
            model.FindItem(block, "Cycle Type")?.Value.SetCount(CycleClamp);

            model.SetRef(block, "Text Keys", WriteTextKeys(model, length));

            // A node's transform and each of its properties are separate blocks
            // here, though they arrived as one track.
            var entries = new List<(AnimTrack Track, NifItem Node, AnimProperty? Property)>();

            foreach ((AnimTrack track, NifItem node) in tracks)
            {
                if (track.Curves.Any(c => c.HasKeys))
                    entries.Add((track, node, null));

                foreach (AnimProperty property in track.Properties.Where(p => p.Curve.HasKeys))
                    entries.Add((track, node, property));
            }

            if (model.SetArraySize(block, "Num Controlled Blocks", "Controlled Blocks", entries.Count)
                is not { } controlled)
            {
                return block;
            }

            for (int i = 0; i < entries.Count && i < controlled.Children.Count; i++)
            {
                (AnimTrack track, NifItem node, AnimProperty? property) = entries[i];
                NifItem entry = controlled.Children[i];

                model.SetString(entry, "Node Name", model.GetName(node));

                if (property is null)
                {
                    model.SetRef(entry, "Interpolator", WriteInterpolator(model, track, sequence.Start));
                    model.SetString(entry, "Controller Type", "NiTransformController");
                    continue;
                }

                model.SetRef(entry, "Interpolator",
                    WriteScalarInterpolator(model, property, sequence.Start));

                // The four strings that say which controller on which sub-object
                // this drives. Without them the keys exist but belong to nothing.
                model.SetString(entry, "Controller Type", property.ControllerType);
                model.SetString(entry, "Controller ID", property.ControllerId);
                model.SetString(entry, "Interpolator ID", property.InterpolatorId);
                model.SetString(entry, "Property Type", property.PropertyType);
            }

            return block;
        }

        /// <summary>Writes a named scalar track as a float or boolean interpolator.</summary>
        /// <remarks>
        /// The two are the same shape — an interpolator pointing at a data block
        /// holding one key group — and differ only in whether the values are floats
        /// or bytes. Writing a boolean track as floats would leave the engine reading
        /// four bytes per key where it expects one.
        /// </remarks>
        private static NifItem WriteScalarInterpolator(NifModel model, AnimProperty property, float offset)
        {
            NifItem data = model.InsertBlock(property.IsBoolean ? "NiBoolData" : "NiFloatData");

            NifItem keys = SizeGroup(model, data, "Data", property.Curve.Keys.Count,
                KeyTypeOf([property.Curve]));

            for (int i = 0; i < property.Curve.Keys.Count && i < keys.Children.Count; i++)
            {
                AnimKey key = property.Curve.Keys[i];

                model.FindItem(keys.Children[i], "Time")?.Value.SetFloat(key.Time - offset);

                NifItem? value = model.FindItem(keys.Children[i], "Value");

                if (property.IsBoolean)
                    value?.Value.SetCount(key.Value != 0f ? 1u : 0u);
                else
                    value?.Value.SetFloat(key.Value);
            }

            NifItem interpolator = model.InsertBlock(
                property.IsBoolean ? "NiBoolInterpolator" : "NiFloatInterpolator");

            model.SetRef(interpolator, "Data", data);
            return interpolator;
        }

        /// <summary>
        /// The start and end markers every sequence needs.
        /// </summary>
        /// <remarks>
        /// Skyrim looks these up by name to know where a sequence begins and ends;
        /// a sequence without them is loaded but never plays.
        /// </remarks>
        private static NifItem WriteTextKeys(NifModel model, float length)
        {
            NifItem keys = model.InsertBlock("NiTextKeyExtraData");

            if (model.SetArraySize(keys, "Num Text Keys", "Text Keys", 2) is not { } list
                || list.Children.Count < 2)
            {
                return keys;
            }

            model.FindItem(list.Children[0], "Time")?.Value.SetFloat(0f);
            model.SetString(list.Children[0], "Value", "start");

            model.FindItem(list.Children[1], "Time")?.Value.SetFloat(length);
            model.SetString(list.Children[1], "Value", "end");

            return keys;
        }

        private static NifItem WriteInterpolator(NifModel model, AnimTrack track, float offset)
        {
            NifItem data = model.InsertBlock("NiTransformData");

            WriteTranslations(model, data, track, offset);
            WriteRotations(model, data, track, offset);
            WriteScales(model, data, track, offset);

            NifItem interpolator = model.InsertBlock("NiTransformInterpolator");
            model.SetRef(interpolator, "Data", data);

            // The base transform is left unset so the node's own is used for whatever
            // the keys do not drive.
            model.FindItem(interpolator, @"Transform\Translation")?.Value
                .Set(new NifVector3(UnsetTransform, UnsetTransform, UnsetTransform));

            model.FindItem(interpolator, @"Transform\Rotation")?.Value
                .Set(new NifQuat(UnsetTransform, UnsetTransform, UnsetTransform, UnsetTransform));

            model.FindItem(interpolator, @"Transform\Scale")?.Value.SetFloat(UnsetTransform);

            return interpolator;
        }

        private static void WriteTranslations(NifModel model, NifItem data, AnimTrack track, float offset)
        {
            // FBX keys each axis independently; a NIF translation key is one vector,
            // so the axes have to be sampled onto one shared set of times.
            var times = MergedTimes(track.Translation);

            if (times.Count == 0)
                return;

            NifItem keys = SizeGroup(model, data, "Translations", times.Count,
                KeyTypeOf(track.Translation));

            for (int i = 0; i < times.Count && i < keys.Children.Count; i++)
            {
                model.FindItem(keys.Children[i], "Time")?.Value.SetFloat(times[i] - offset);

                model.FindItem(keys.Children[i], "Value")?.Value.Set(new NifVector3(
                    Sample(track.Translation[0], times[i]),
                    Sample(track.Translation[1], times[i]),
                    Sample(track.Translation[2], times[i])));
            }
        }

        /// <summary>
        /// Rotation keys, written as three separate axis groups.
        /// </summary>
        /// <remarks>
        /// The XYZ form is used rather than quaternions because it is the one that
        /// survives the trip: FBX keys Euler axes independently and at different
        /// times, and packing those into quaternions would force every axis onto a
        /// shared timeline and lose any winding past a half turn.
        /// </remarks>
        private static void WriteRotations(NifModel model, NifItem data, AnimTrack track, float offset)
        {
            const float ToRadians = MathF.PI / 180f;

            if (!track.Rotation.Any(c => c.HasKeys))
                return;

            // The count field must say one for the XYZ form; the real counts live in
            // the groups themselves.
            model.FindItem(data, "Num Rotation Keys")?.Value.SetCount(1);
            data.InvalidateConditionsRecursive();

            model.FindItem(data, "Rotation Type")?.Value.SetCount(XyzRotationKey);
            data.InvalidateConditionsRecursive();

            if (model.FindItem(data, "XYZ Rotations") is not { } groups)
                return;

            model.UpdateArraySize(groups);

            for (int axis = 0; axis < 3 && axis < groups.Children.Count; axis++)
            {
                AnimCurve curve = track.Rotation[axis];
                NifItem group = groups.Children[axis];

                NifItem keys = SizeGroup(model, group, string.Empty, curve.Keys.Count,
                    KeyTypeOf([curve]));

                for (int i = 0; i < curve.Keys.Count && i < keys.Children.Count; i++)
                {
                    model.FindItem(keys.Children[i], "Time")?.Value.SetFloat(curve.Keys[i].Time - offset);
                    model.FindItem(keys.Children[i], "Value")?.Value.SetFloat(curve.Keys[i].Value * ToRadians);
                }
            }
        }

        private static void WriteScales(NifModel model, NifItem data, AnimTrack track, float offset)
        {
            var times = MergedTimes(track.Scale);

            if (times.Count == 0)
                return;

            NifItem keys = SizeGroup(model, data, "Scales", times.Count, KeyTypeOf(track.Scale));

            for (int i = 0; i < times.Count && i < keys.Children.Count; i++)
            {
                model.FindItem(keys.Children[i], "Time")?.Value.SetFloat(times[i] - offset);

                // NIF scales uniformly. X is the axis a NIF-sourced file keyed all
                // three of, and the only sensible pick when they disagree.
                model.FindItem(keys.Children[i], "Value")?.Value.SetFloat(Sample(track.Scale[0], times[i]));
            }
        }

        /// <summary>
        /// Sizes a <c>KeyGroup</c> and states its interpolation.
        /// </summary>
        /// <remarks>
        /// The order is forced: the interpolation field does not exist until the
        /// count says there are keys, and the keys' own layout depends on the
        /// interpolation, since quadratic keys carry tangents and the others do not.
        /// </remarks>
        private static NifItem SizeGroup(
            NifModel model, NifItem parent, string field, int count, uint keyType)
        {
            string prefix = field.Length > 0 ? $@"{field}\" : string.Empty;

            model.FindItem(parent, $"{prefix}Num Keys")?.Value.SetCount((uint)count);
            parent.InvalidateConditionsRecursive();

            model.FindItem(parent, $"{prefix}Interpolation")?.Value.SetCount(keyType);
            parent.InvalidateConditionsRecursive();

            NifItem keys = model.FindItem(parent, $"{prefix}Keys")!;
            model.UpdateArraySize(keys);
            return keys;
        }

        /// <summary>
        /// The NIF key type for a channel, taking the smoothest its axes ask for.
        /// </summary>
        /// <remarks>
        /// A group has one interpolation for all its keys, so axes that disagree have
        /// to be reconciled. Taking the smoothest keeps a curve that was authored
        /// smooth from becoming a set of straight lines; the reverse would be visible.
        /// </remarks>
        private static uint KeyTypeOf(IReadOnlyList<AnimCurve> curves)
        {
            const uint Linear = 1, Quadratic = 2, Const = 5;

            uint best = Const;

            foreach (AnimKey key in curves.SelectMany(c => c.Keys))
            {
                uint type = key.Interpolation switch
                {
                    AnimInterpolation.Constant => Const,
                    AnimInterpolation.Linear => Linear,
                    _ => Quadratic
                };

                // Const is the coarsest and quadratic the smoothest, but the enum
                // does not order them that way, so rank explicitly.
                if (Rank(type) > Rank(best))
                    best = type;
            }

            return best;

            static int Rank(uint type) => type switch { Const => 0, Linear => 1, _ => 2 };
        }

        /// <summary>Every time any axis of a channel is keyed at, in order.</summary>
        private static List<float> MergedTimes(AnimCurve[] curves)
        {
            var times = new SortedSet<float>();

            foreach (AnimCurve curve in curves)
            {
                foreach (AnimKey key in curve.Keys)
                    times.Add(key.Time);
            }

            return [.. times];
        }

        /// <summary>
        /// A curve's value at a time, interpolating between the keys around it.
        /// </summary>
        /// <remarks>
        /// Needed because merging the axes onto shared times asks each axis for
        /// values at times it was not keyed at. Linear is the honest reading:
        /// inventing a smooth fit through points that were never authored would
        /// overshoot between them.
        /// </remarks>
        private static float Sample(AnimCurve curve, float time)
        {
            if (curve.Keys.Count == 0)
                return 0f;

            if (time <= curve.Keys[0].Time)
                return curve.Keys[0].Value;

            for (int i = 1; i < curve.Keys.Count; i++)
            {
                if (time > curve.Keys[i].Time)
                    continue;

                AnimKey before = curve.Keys[i - 1];
                AnimKey after = curve.Keys[i];

                if (before.Interpolation == AnimInterpolation.Constant)
                    return before.Value;

                float span = after.Time - before.Time;

                return span <= 0f
                    ? after.Value
                    : before.Value + (after.Value - before.Value) * ((time - before.Time) / span);
            }

            return curve.Keys[^1].Value;
        }
    }
}
