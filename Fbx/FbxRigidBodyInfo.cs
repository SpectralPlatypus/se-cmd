using System.Globalization;
using SECmd.Nif;

namespace SECmd.Fbx
{
    /// <summary>
    /// Carries a rigid body's mass and collision layer through FBX.
    /// </summary>
    /// <remarks>
    /// These two are carried and the inertia tensor is not, because they are different
    /// kinds of fact. The mass is authored — ck-cmd's own example files give a box and
    /// a sphere of different sizes the same mass, which no density can produce — while
    /// the tensor follows from the mass and the shape, and is computed on import by
    /// <see cref="Conversion.HavokInertia"/>.
    ///
    /// The layer travels because it decides everything else: ck-cmd picks a body's
    /// motion system, quality and solver deactivation from it, and a static body is
    /// given a zero mass whatever it was carrying. A static with a mass is treated as
    /// movable, which is how a piece of scenery ends up falling through the world.
    /// </remarks>
    public static class FbxRigidBodyInfo
    {
        /// <summary>The property the mass travels in.</summary>
        public const string MassProperty = "nif_rb_mass";

        /// <summary>The property the collision layer travels in.</summary>
        public const string LayerProperty = "nif_rb_layer";

        /// <summary>The layer assumed for a body that arrives without one.</summary>
        public const string DefaultLayer = "SKYL_STATIC";

        /// <summary>Records a body's mass and layer on the node standing for it.</summary>
        public static void Write(FbxObject bodyNode, NifModel model, NifItem body)
        {
            if (model.FindItem(body, @"Rigid Body Info\Mass") is { } mass)
            {
                bodyNode.Properties.SetUserString(
                    MassProperty, mass.Value.ToFloat().ToString("R", CultureInfo.InvariantCulture));
            }

            bodyNode.Properties.SetUserString(LayerProperty, FbxCollisionMaterial.LayerOf(model, body));
        }

        /// <summary>The mass carried with a node, or null when none was.</summary>
        public static float? MassOf(FbxObject bodyNode)
        {
            string text = bodyNode.Properties.GetString(MassProperty);

            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float mass)
                ? mass
                : null;
        }

        /// <summary>The collision layer carried with a node, or the static default.</summary>
        public static string LayerOf(FbxObject bodyNode)
        {
            string layer = bodyNode.Properties.GetString(LayerProperty);

            return layer.Length > 0 ? layer : DefaultLayer;
        }

        /// <summary>
        /// Whether a layer is one Havok never moves.
        /// </summary>
        /// <remarks>
        /// ck-cmd's division: animated and biped bodies get box inertia, clutter gets
        /// full dynamics, and everything else is static and loses its mass.
        /// </remarks>
        public static bool IsStatic(string layer) =>
            layer is not ("SKYL_ANIMSTATIC" or "SKYL_BIPED" or "SKYL_CLUTTER");
    }
}
