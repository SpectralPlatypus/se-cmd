using MeshIO.Formats.Fbx;
using SECmd.Conversion;
using SECmd.Nif;

namespace SECmd.Fbx
{
    /// <summary>
    /// Writes a mesh into an FBX scene as a <c>Geometry</c> object, and the
    /// <c>Model</c> nodes that carry them.
    /// </summary>
    /// <remarks>
    /// Emits the layout FBXWrangler produces: attributes mapped
    /// <c>ByControlPoint</c> / <c>Direct</c>, one triangle per polygon, and the UV
    /// element named <c>"UV Map"</c>. That name is not cosmetic — Blender will not
    /// merge UV maps across meshes unless they share a name.
    /// </remarks>
    public static class FbxMeshWriter
    {
        /// <summary>The UV element name that lets Blender merge UV maps across meshes.</summary>
        public const string UvElementName = "UV Map";

        private const int GeometryVersion = 124;
        private const int LayerElementVersion = 101;
        private const int LayerVersion = 100;
        private const int ModelVersion = 232;

        /// <summary>
        /// Adds a mesh as a <c>Geometry</c> object. UVs are expected already in FBX
        /// convention, i.e. with V flipped relative to NIF.
        /// </summary>
        public static FbxObject AddGeometry(FbxScene scene, string name, MeshGeometry mesh)
        {
            FbxObject geometry = scene.AddObject("Geometry", name, "Mesh");
            FbxNode node = geometry.Node;

            node.Nodes.Add(new FbxNode("GeometryVersion", GeometryVersion));

            // Control points, flattened.
            var vertices = new double[mesh.Vertices.Count * 3];

            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                NifVector3 v = mesh.Vertices[i];
                vertices[i * 3] = v.X;
                vertices[i * 3 + 1] = v.Y;
                vertices[i * 3 + 2] = v.Z;
            }

            node.Nodes.Add(new FbxNode("Vertices", vertices));

            // Polygons. FBX marks the last corner of each polygon by storing its
            // bitwise complement, which is how a flat index list encodes polygon
            // boundaries without a separate size array.
            var indices = new int[mesh.Triangles.Count * 3];

            for (int i = 0; i < mesh.Triangles.Count; i++)
            {
                NifTriangle t = mesh.Triangles[i];
                indices[i * 3] = t.V1;
                indices[i * 3 + 1] = t.V2;
                indices[i * 3 + 2] = ~t.V3;
            }

            node.Nodes.Add(new FbxNode("PolygonVertexIndex", indices));

            var layerElements = new List<string>();

            if (mesh.HasNormals)
            {
                node.Nodes.Add(BuildVector3Element("LayerElementNormal", "Normals", string.Empty, mesh.Normals));
                layerElements.Add("LayerElementNormal");
            }

            if (mesh.HasUvs)
            {
                var uv = new double[mesh.Uvs.Count * 2];

                for (int i = 0; i < mesh.Uvs.Count; i++)
                {
                    uv[i * 2] = mesh.Uvs[i].X;
                    uv[i * 2 + 1] = mesh.Uvs[i].Y;
                }

                var element = new FbxNode("LayerElementUV", 0);
                element.Nodes.Add(new FbxNode("Version", LayerElementVersion));
                element.Nodes.Add(new FbxNode("Name", UvElementName));
                element.Nodes.Add(new FbxNode("MappingInformationType", "ByControlPoint"));
                element.Nodes.Add(new FbxNode("ReferenceInformationType", "Direct"));
                element.Nodes.Add(new FbxNode("UV", uv));

                node.Nodes.Add(element);
                layerElements.Add("LayerElementUV");
            }

            if (mesh.HasColors)
            {
                var colors = new double[mesh.Colors.Count * 4];

                for (int i = 0; i < mesh.Colors.Count; i++)
                {
                    NifColor4 c = mesh.Colors[i];
                    colors[i * 4] = c.R;
                    colors[i * 4 + 1] = c.G;
                    colors[i * 4 + 2] = c.B;
                    colors[i * 4 + 3] = c.A;
                }

                var element = new FbxNode("LayerElementColor", 0);
                element.Nodes.Add(new FbxNode("Version", LayerElementVersion));
                element.Nodes.Add(new FbxNode("Name", "VertexColor"));
                element.Nodes.Add(new FbxNode("MappingInformationType", "ByControlPoint"));
                element.Nodes.Add(new FbxNode("ReferenceInformationType", "Direct"));
                element.Nodes.Add(new FbxNode("Colors", colors));

                node.Nodes.Add(element);
                layerElements.Add("LayerElementColor");
            }

            node.Nodes.Add(BuildLayer(layerElements));

            return geometry;
        }

        /// <summary>
        /// Adds the material element that assigns a single material to the whole
        /// mesh, which is the only case NIF has: one shape, one material.
        /// </summary>
        public static void AddSingleMaterialElement(FbxObject geometry)
        {
            var element = new FbxNode("LayerElementMaterial", 0);
            element.Nodes.Add(new FbxNode("Version", LayerElementVersion));
            element.Nodes.Add(new FbxNode("Name", string.Empty));
            element.Nodes.Add(new FbxNode("MappingInformationType", "AllSame"));
            element.Nodes.Add(new FbxNode("ReferenceInformationType", "IndexToDirect"));
            element.Nodes.Add(new FbxNode("Materials", new[] { 0 }));

            // Insert before the Layer record so the layer can reference it.
            FbxNode? layer = geometry.Node.Nodes.FirstOrDefault(n => n.Name == "Layer");
            int at = layer is null ? geometry.Node.Nodes.Count : geometry.Node.Nodes.IndexOf(layer);
            geometry.Node.Nodes.Insert(at, element);

            if (layer is not null)
                AddLayerElement(layer, "LayerElementMaterial");
        }

        private static FbxNode BuildVector3Element(
            string elementName, string arrayName, string name, IReadOnlyList<NifVector3> values)
        {
            var data = new double[values.Count * 3];

            for (int i = 0; i < values.Count; i++)
            {
                data[i * 3] = values[i].X;
                data[i * 3 + 1] = values[i].Y;
                data[i * 3 + 2] = values[i].Z;
            }

            var element = new FbxNode(elementName, 0);
            element.Nodes.Add(new FbxNode("Version", LayerElementVersion));
            element.Nodes.Add(new FbxNode("Name", name));
            element.Nodes.Add(new FbxNode("MappingInformationType", "ByControlPoint"));
            element.Nodes.Add(new FbxNode("ReferenceInformationType", "Direct"));
            element.Nodes.Add(new FbxNode(arrayName, data));

            return element;
        }

        private static FbxNode BuildLayer(IEnumerable<string> elementTypes)
        {
            var layer = new FbxNode("Layer", 0);
            layer.Nodes.Add(new FbxNode("Version", LayerVersion));

            foreach (string type in elementTypes)
                AddLayerElement(layer, type);

            return layer;
        }

        private static void AddLayerElement(FbxNode layer, string type)
        {
            var entry = new FbxNode("LayerElement");
            entry.Nodes.Add(new FbxNode("Type", type));
            entry.Nodes.Add(new FbxNode("TypedIndex", 0));
            layer.Nodes.Add(entry);
        }

        /// <summary>
        /// Adds a <c>Model</c> node with a transform.
        /// </summary>
        /// <param name="subClass">
        /// "Mesh" for a node carrying geometry, "Null" for a plain transform,
        /// "LimbNode" for a skeleton joint.
        /// </param>
        public static FbxObject AddModel(FbxScene scene, string name, string subClass, NifTransform transform)
        {
            FbxObject model = scene.AddObject("Model", name, subClass);
            FbxNode node = model.Node;

            node.Nodes.Add(new FbxNode("Version", ModelVersion));

            NifVector3 t = transform.Translation;
            NifVector3 r = transform.ToEulerDegrees();
            float s = transform.Scale;

            // Only write channels that differ from the default, keeping files close
            // to what other exporters produce.
            if (t.X != 0 || t.Y != 0 || t.Z != 0)
                model.Properties.Set("Lcl Translation", "Lcl Translation", "", "A", (double)t.X, (double)t.Y, (double)t.Z);

            if (r.X != 0 || r.Y != 0 || r.Z != 0)
                model.Properties.Set("Lcl Rotation", "Lcl Rotation", "", "A", (double)r.X, (double)r.Y, (double)r.Z);

            if (Math.Abs(s - 1f) > 1e-6f)
                model.Properties.Set("Lcl Scaling", "Lcl Scaling", "", "A", (double)s, (double)s, (double)s);

            // Scale is inherited normally; NIF has no other mode.
            model.Properties.Set("InheritType", "enum", "", "", 1);

            node.Nodes.Add(new FbxNode("MultiLayer", 0));
            node.Nodes.Add(new FbxNode("MultiTake", 0));

            // FBX's one-byte boolean is property type 'C', which MeshIO models as a
            // char in both directions. Passing a bool here writes nothing MeshIO can
            // serialise and the save fails.
            node.Nodes.Add(new FbxNode("Shading", (char)1));

            node.Nodes.Add(new FbxNode("Culling", "CullingOff"));

            return model;
        }
    }
}
