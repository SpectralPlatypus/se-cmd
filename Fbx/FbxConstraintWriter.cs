using SECmd.Conversion;
using SECmd.Nif;

namespace SECmd.Fbx
{
    /// <summary>
    /// Writes Havok constraints into an FBX scene as tagged attachment points.
    /// </summary>
    /// <remarks>
    /// FBX has constraints of its own, but none of them mean what a Havok constraint
    /// means, so a constraint becomes what it can be honestly represented as: an
    /// empty node placed where the joint is, carrying the descriptor as properties
    /// (spec §4.10). Nothing reads it as a constraint; everything can see where the
    /// joint sits and what it was.
    ///
    /// The descriptor is written field by field, straight off the nif.xml
    /// definition, rather than through a case for each of the seven constraint
    /// types. Those cases would be seven near-identical lists of vectors and
    /// angles, and the ones a hand-written version leaves out are exactly the ones
    /// the corpus turns out to use — the two constraints in it are a stiff spring
    /// and a ball-and-socket chain, both of which FBXWrangler skips.
    /// </remarks>
    public static class FbxConstraintWriter
    {
        /// <summary>Marks the node as an attachment point, as FBXWrangler names it.</summary>
        public const string NameSuffix = "_attach_point";

        /// <summary>Separates the two body names in an attachment point's name.</summary>
        public const string NameSeparator = "_con_";

        /// <summary>The property naming which kind of constraint this was.</summary>
        public const string TypeProperty = "constraint_type";

        /// <summary>Prefix on every property carrying a descriptor field.</summary>
        public const string FieldPrefix = "hkc_";

        /// <summary>
        /// The descriptor axes that make up a B frame, in the orders the types use.
        /// </summary>
        /// <remarks>
        /// Ragdoll names its axes after what they do; the hinges name theirs after
        /// the rotation they permit. Pivot-only constraints — ball and socket, stiff
        /// spring — have no frame at all and leave the node unrotated, which is
        /// exactly true of them: they constrain a point, not an orientation.
        /// </remarks>
        private static readonly string[][] FrameAxes =
        [
            ["Twist B", "Plane B", "Motor B"],
            ["Axis B", "Perp Axis In B1", "Perp Axis In B2"]
        ];

        /// <summary>
        /// Writes one constraint, given the body nodes it joins.
        /// </summary>
        /// <returns>The node created, or null when neither body was converted.</returns>
        public static FbxObject? AddConstraint(
            FbxScene scene, NifModel model, NifItem constraint,
            IReadOnlyDictionary<NifItem, (FbxObject Node, string Name)> bodies)
        {
            NifItem? wrapper = WrapperOf(model, constraint);
            NifItem descriptor = DescriptorOf(model, constraint) ?? constraint;

            (NifItem? entityA, NifItem? entityB) = EntitiesOf(model, constraint);

            bodies.TryGetValue(entityA ?? constraint, out var a);
            bodies.TryGetValue(entityB ?? constraint, out var b);

            // Under the far body, since the frame written here is expressed in its
            // space. A constraint with only one entity -- Bethesda's breakable ones
            // often name just the one -- hangs off whichever it has.
            FbxObject? parent = b.Node ?? a.Node;

            if (parent is null)
                return null;

            string name = $"{a.Name}{NameSeparator}{b.Name}{NameSuffix}";

            FbxObject node = FbxMeshWriter.AddModel(scene, name, "Null", FrameOf(model, descriptor));
            scene.Connect(node, parent);

            node.Properties.SetUserString(TypeProperty, TypeNameOf(model, constraint, descriptor));

            WriteFields(model, descriptor, node, string.Empty);

            // The wrapper's own settings sit outside the descriptor: how much force
            // breaks it, and whether breaking removes it. The wrapper itself is
            // skipped, since its live arm is what was just written.
            if (wrapper is not null)
                WriteFields(model, constraint, node, string.Empty, skip: wrapper);

            return node;
        }

        /// <summary>The union holding a wrapped constraint's parameters, if it has one.</summary>
        /// <remarks>
        /// A constraint block names its type one of two ways. Most are a class per
        /// type with the descriptor inline; the wrapped ones — breakable, malleable
        /// — carry a type number and a union, of which exactly one arm is live.
        /// </remarks>
        private static NifItem? WrapperOf(NifModel model, NifItem constraint) =>
            model.FindItem(constraint, "Constraint Data") ?? model.FindItem(constraint, "Constraint");

        /// <summary>The sub-item holding the constraint's parameters.</summary>
        public static NifItem? DescriptorOf(NifModel model, NifItem constraint)
        {
            if (WrapperOf(model, constraint) is not { } wrapped)
                return null;

            // A union arm is picked out by its condition, so the live one is the
            // only compound child whose condition holds.
            NifItem? arm = wrapped.Children.FirstOrDefault(
                c => c.Children.Count > 0 && c.Name != "Constraint Info" && model.EvalCondition(c));

            return arm ?? wrapped;
        }

        /// <summary>The two rigid bodies a constraint joins.</summary>
        private static (NifItem? A, NifItem? B) EntitiesOf(NifModel model, NifItem constraint)
        {
            // On the constraint itself for the plain types, and on the chain's own
            // info block for the chained ones.
            foreach (string path in new[] { string.Empty, @"Constraint Chain Info\" })
            {
                NifItem? a = model.FindItem(constraint, $"{path}Entity A");
                NifItem? b = model.FindItem(constraint, $"{path}Entity B");

                if (a is not null || b is not null)
                    return (a is null ? null : model.GetBlock(a), b is null ? null : model.GetBlock(b));
            }

            return (null, null);
        }

        /// <summary>The name to record for the constraint's kind.</summary>
        private static string TypeNameOf(NifModel model, NifItem constraint, NifItem descriptor)
        {
            // A wrapped constraint's real type is the name of the live union arm;
            // an unwrapped one's is its own class, minus the Havok prefix.
            if (descriptor != constraint && descriptor.Name.Length > 0 && descriptor.Children.Count > 0)
                return descriptor.Name.Replace(" ", string.Empty);

            string name = constraint.Name;

            if (name.StartsWith("bhk", StringComparison.Ordinal))
                name = name[3..];

            return name.EndsWith("Constraint", StringComparison.Ordinal)
                ? name[..^"Constraint".Length]
                : name;
        }

        /// <summary>
        /// Where the joint sits, in the second body's space.
        /// </summary>
        /// <remarks>
        /// The axes are written as the matrix's rows rather than its columns because
        /// NIF applies its matrices to row vectors; the spec describes the same frame
        /// from the other convention.
        /// </remarks>
        private static NifTransform FrameOf(NifModel model, NifItem descriptor)
        {
            NifVector3 pivot = ScaledVector(model, descriptor, "Pivot B");
            NifMatrix33 rotation = NifMatrix33.Identity;

            foreach (string[] axes in FrameAxes)
            {
                if (!axes.All(a => model.FindItem(descriptor, a) is not null))
                    continue;

                NifVector3 x = Vector(model, descriptor, axes[0]);
                NifVector3 y = Vector(model, descriptor, axes[1]);
                NifVector3 z = Vector(model, descriptor, axes[2]);

                // Degenerate axes mean the file left the frame unset; identity is a
                // truer reading of that than a matrix that collapses space.
                if (Length(x) < 1e-6f || Length(y) < 1e-6f || Length(z) < 1e-6f)
                    break;

                rotation = new NifMatrix33
                {
                    M11 = x.X, M12 = x.Y, M13 = x.Z,
                    M21 = y.X, M22 = y.Y, M23 = y.Z,
                    M31 = z.X, M32 = z.Y, M33 = z.Z
                };

                break;
            }

            return new NifTransform(pivot, rotation, 1f);
        }

        /// <summary>
        /// Writes every live field of a descriptor as a string property.
        /// </summary>
        /// <remarks>
        /// Strings, following the spec, and because a Havok descriptor mixes vectors,
        /// angles, enums and flags: one representation that carries all of them
        /// without a schema beats four that each need one.
        ///
        /// Only live fields are written. A descriptor's definition lists the same
        /// field several times over for different Havok versions, and the conditions
        /// are what say which spelling this file uses.
        /// </remarks>
        private static void WriteFields(
            NifModel model, NifItem parent, FbxObject node, string prefix, NifItem? skip = null)
        {
            foreach (NifItem child in parent.Children)
            {
                if (child == skip || child.IsAbstract || !model.EvalCondition(child))
                    continue;

                // The entity links are the joint's two ends, already expressed by
                // where the node sits in the hierarchy.
                if (child.Name is "Entity A" or "Entity B" or "Constraint Info" or "Chained Entities")
                    continue;

                string name = $"{prefix}{child.Name.Replace(' ', '_').ToLowerInvariant()}";

                if (child.IsArray)
                {
                    // A chain's pivots are an array, and they are the whole of what
                    // the chain says: dropping them leaves a joint with a length and
                    // no idea where any of its links are.
                    for (int i = 0; i < child.Children.Count; i++)
                        WriteFields(model, child.Children[i], node, $"{name}_{i}_");

                    continue;
                }

                if (child.Children.Count > 0)
                {
                    WriteFields(model, child, node, $"{name}_");
                    continue;
                }

                node.Properties.SetUserString($"{FieldPrefix}{name}", Format(child));
            }
        }

        /// <summary>Formats one field's value for storage.</summary>
        private static string Format(NifItem item) => item.Value.Type switch
        {
            NifValueType.Vector4 => Format(item.Value.Get<NifVector4>()),
            NifValueType.Vector3 => Format(item.Value.Get<NifVector3>()),
            NifValueType.Float => item.Value.ToFloat().ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            _ => item.Value.ToUInt().ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        private static string Format(NifVector4 v) => Format(new NifVector3(v.X, v.Y, v.Z));

        private static string Format(NifVector3 v) => string.Join(' ',
            v.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            v.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            v.Z.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

        private static NifVector3 Vector(NifModel model, NifItem parent, string field)
        {
            if (model.FindItem(parent, field) is not { } item)
                return new NifVector3();

            NifVector4 v = item.Value.Get<NifVector4>();
            return new NifVector3(v.X, v.Y, v.Z);
        }

        /// <summary>A pivot, in Skyrim units rather than Havok's metres.</summary>
        private static NifVector3 ScaledVector(NifModel model, NifItem parent, string field)
        {
            NifVector3 v = Vector(model, parent, field);

            return new NifVector3(
                v.X * ShapeTessellator.BhkScaleFactor,
                v.Y * ShapeTessellator.BhkScaleFactor,
                v.Z * ShapeTessellator.BhkScaleFactor);
        }

        private static float Length(NifVector3 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
    }
}
