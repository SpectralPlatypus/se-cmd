using SECmd.Nif;

namespace SECmd.Fbx
{
    /// <summary>
    /// Carries the controllers on a block that animate nothing.
    /// </summary>
    /// <remarks>
    /// The animation layer recognises a controller by what its interpolator drives
    /// (§5A.4). A controller that holds no interpolator drives nothing that layer can
    /// see, so it is invisible to it — and there is nothing else in the file that would
    /// bring it back.
    ///
    /// These are not animation. <c>NiPSysUpdateCtlr</c> is the switch that makes a
    /// particle system run at all; <c>BSLagBoneController</c> makes a bone trail behind
    /// the one above it by a fixed amount, which is a property of the skeleton rather
    /// than of a timeline. Both say something about the thing they hang on, so they
    /// travel with it, as properties on its node.
    ///
    /// Controllers that *do* hold an interpolator are left alone: they are animation
    /// and go the other way, and carrying them here as well would rebuild them twice.
    /// </remarks>
    public static class FbxNodeControllers
    {
        /// <summary>The property counting the block's structural controllers.</summary>
        public const string CountProperty = "particle_controllers";

        /// <summary>Prefix on one structural controller's fields, before its index.</summary>
        public const string Prefix = "npc_";

        /// <summary>
        /// Whether a controller says something about the block rather than about a
        /// timeline.
        /// </summary>
        /// <remarks>
        /// Two things disqualify one. Holding an interpolator — in either slot, since
        /// an emitter's on/off track lives in the second — makes it animation, which
        /// travels by its own route; carrying it here as well would rebuild it twice.
        ///
        /// And the sequence machinery is not a controller on a node in the sense that
        /// matters. A <c>NiControllerManager</c> holds no interpolator of its own, but
        /// it *is* the animation layer, rebuilt from the sequences — carrying it here
        /// put a manager back into a file whose animation had been turned off.
        /// </remarks>
        /// And a controller a *sequence* names is rebuilt from that sequence, which is
        /// the <paramref name="sequenced"/> set the caller passes in. Holding no
        /// interpolator of its own is not enough to be structural:
        /// <c>BSProceduralLightningController</c> holds nine, none of them called
        /// `Interpolator`, and every one of them is driven from a sequence.
        private static bool IsStructural(NifModel model, NifItem controller) =>
            model.GetRef(controller, "Interpolator") is null
            && model.GetRef(controller, "Visibility Interpolator") is null
            && !model.BlockInherits(controller, "NiControllerManager")
            && !model.BlockInherits(controller, "NiMultiTargetTransformController");

        /// <summary>Fields rebuilt from the chain rather than carried.</summary>
        private static bool Rebuilt(NifItem child) => child.Name is "Next Controller" or "Target";

        /// <summary>Records the controllers on a block that hold no interpolator.</summary>
        /// <param name="sequenced">
        /// Controllers a sequence names, which the animation route rebuilds. Passing
        /// none carries every structural controller, which is right only for a file
        /// with no sequences.
        /// </param>
        public static void Write(
            FbxObject node, NifModel model, NifItem block, IReadOnlySet<NifItem>? sequenced = null)
        {
            var controllers = new List<NifItem>();

            for (NifItem? controller = model.GetRef(block, "Controller");
                 controller is not null;
                 controller = model.GetRef(controller, "Next Controller"))
            {
                if (IsStructural(model, controller) && sequenced?.Contains(controller) != true)
                    controllers.Add(controller);
            }

            if (controllers.Count == 0)
                return;

            node.Properties.SetUserString(
                CountProperty,
                controllers.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));

            for (int i = 0; i < controllers.Count; i++)
            {
                string prefix = $"{Prefix}{i}_";

                node.Properties.SetUserString($"{prefix}type", controllers[i].Name);

                NifFieldCodec.Write(
                    model, controllers[i], prefix,
                    (name, value) => node.Properties.SetUserString(name, value),
                    Rebuilt);
            }
        }

        /// <summary>Rebuilds the controllers that animate nothing, onto the block.</summary>
        public static void Read(FbxObject node, NifModel model, NifItem block, List<string> warnings)
        {
            string text = node.Properties.GetString(CountProperty);

            if (!int.TryParse(text, System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.InvariantCulture, out int count))
            {
                return;
            }

            for (int i = 0; i < count; i++)
            {
                string prefix = $"{Prefix}{i}_";
                string type = node.Properties.GetString($"{prefix}type");

                if (type.Length == 0)
                    continue;

                if (!model.KnowsBlock(type) || !model.Database.Inherits(type, "NiTimeController"))
                {
                    warnings.Add(
                        $"{model.GetName(block)}: \"{type}\" is not a controller this build knows, "
                        + "it is dropped");

                    continue;
                }

                NifItem controller = model.InsertBlock(type);

                NifFieldCodec.Read(
                    model, controller, prefix,
                    name => node.Properties.GetString(name) is { Length: > 0 } value ? value : null,
                    Rebuilt);

                model.SetRef(controller, "Target", block);

                Attach(model, block, controller);
            }
        }

        /// <summary>Adds a controller to the end of a block's chain.</summary>
        /// <remarks>
        /// The end rather than the front, so controllers keep the order they were read
        /// in: a chain is walked in order and two on one block can disagree.
        /// </remarks>
        private static void Attach(NifModel model, NifItem host, NifItem controller)
        {
            if (model.GetRef(host, "Controller") is not { } first)
            {
                model.SetRef(host, "Controller", controller);
                return;
            }

            NifItem last = first;

            while (model.GetRef(last, "Next Controller") is { } next)
                last = next;

            model.SetRef(last, "Next Controller", controller);
        }
    }
}
