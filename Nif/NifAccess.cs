namespace SECmd.Nif
{
    /// <summary>
    /// Typed reads over the item tree for the blocks the converter cares about.
    /// </summary>
    /// <remarks>
    /// The tree is data-driven, so everything is reachable by name already. These
    /// helpers exist to keep field names and their quirks in one place instead of
    /// scattering string literals through the conversion code.
    /// </remarks>
    public static class NifAccess
    {
        // --- NiAVObject -------------------------------------------------------

        /// <summary>Reads a node's local transform.</summary>
        public static NifTransform GetTransform(this NifModel model, NifItem block)
        {
            NifVector3 translation = model.FindItem(block, "Translation")?.Value.Get<NifVector3>() ?? new NifVector3();
            NifMatrix33 rotation = model.FindItem(block, "Rotation")?.Value.Get<NifMatrix33>() ?? NifMatrix33.Identity;
            NifItem? scaleItem = model.FindItem(block, "Scale");
            float scale = scaleItem is null ? 1f : scaleItem.Value.ToFloat();

            return new NifTransform(translation, rotation, scale);
        }

        /// <summary>Writes a node's local transform.</summary>
        public static void SetTransform(this NifModel model, NifItem block, NifTransform transform)
        {
            model.FindItem(block, "Translation")?.Value.Set(transform.Translation);
            model.FindItem(block, "Rotation")?.Value.Set(transform.Rotation);

            NifItem? scale = model.FindItem(block, "Scale");
            scale?.Value.SetFloat(transform.Scale);
        }

        /// <summary>The block's name, resolved through the header string table.</summary>
        public static string GetName(this NifModel model, NifItem block)
        {
            NifItem? name = model.FindItem(block, "Name");
            return name is null ? string.Empty : model.ResolveString(name);
        }

        /// <summary>The blocks a <c>NiNode</c> lists as children, skipping null links.</summary>
        public static IEnumerable<NifItem> GetChildren(this NifModel model, NifItem node)
        {
            NifItem? children = model.FindItem(node, "Children");

            if (children is null)
                yield break;

            foreach (NifItem link in children.Children)
            {
                if (model.GetBlock(link) is { } child)
                    yield return child;
            }
        }

        /// <summary>Follows a single reference field, or null when unset.</summary>
        public static NifItem? GetRef(this NifModel model, NifItem block, string field)
        {
            NifItem? link = model.FindItem(block, field);
            return link is null ? null : model.GetBlock(link);
        }

        /// <summary>The blocks listed by an array-of-references field.</summary>
        public static IEnumerable<NifItem> GetRefArray(this NifModel model, NifItem block, string field)
        {
            NifItem? array = model.FindItem(block, field);

            if (array is null)
                yield break;

            foreach (NifItem link in array.Children)
            {
                if (model.GetBlock(link) is { } target)
                    yield return target;
            }
        }

        // --- geometry ---------------------------------------------------------

        /// <summary>Reads the vertex positions of a geometry data block.</summary>
        public static List<NifVector3> GetVertices(this NifModel model, NifItem data)
        {
            var result = new List<NifVector3>();

            if (model.FindItem(data, "Vertices") is not { } vertices)
                return result;

            foreach (NifItem item in vertices.Children)
                result.Add(item.Value.Get<NifVector3>());

            return result;
        }

        /// <summary>Reads the normals, or an empty list when the block has none.</summary>
        public static List<NifVector3> GetNormals(this NifModel model, NifItem data)
        {
            var result = new List<NifVector3>();

            if (model.FindItem(data, "Normals") is not { } normals)
                return result;

            foreach (NifItem item in normals.Children)
                result.Add(item.Value.Get<NifVector3>());

            return result;
        }

        public static List<NifVector3> GetTangents(this NifModel model, NifItem data) =>
            ReadVector3Array(model, data, "Tangents");

        public static List<NifVector3> GetBitangents(this NifModel model, NifItem data) =>
            ReadVector3Array(model, data, "Bitangents");

        private static List<NifVector3> ReadVector3Array(NifModel model, NifItem data, string field)
        {
            var result = new List<NifVector3>();

            if (model.FindItem(data, field) is not { } array)
                return result;

            foreach (NifItem item in array.Children)
                result.Add(item.Value.Get<NifVector3>());

            return result;
        }

        /// <summary>Reads the vertex colours, or an empty list.</summary>
        public static List<NifColor4> GetVertexColors(this NifModel model, NifItem data)
        {
            var result = new List<NifColor4>();

            if (model.FindItem(data, "Vertex Colors") is not { } colors)
                return result;

            foreach (NifItem item in colors.Children)
                result.Add(item.Value.Get<NifColor4>());

            return result;
        }

        /// <summary>
        /// Reads one UV set. <c>UV Sets</c> is a two-dimensional array, outer index
        /// the set and inner the vertex, so set 0 is the one meshes normally use.
        /// </summary>
        public static List<NifVector2> GetUvSet(this NifModel model, NifItem data, int set = 0)
        {
            var result = new List<NifVector2>();

            if (model.FindItem(data, "UV Sets") is not { } sets)
                return result;

            if (sets.Child(set) is not { } uvs)
                return result;

            foreach (NifItem item in uvs.Children)
                result.Add(item.Value.Get<NifVector2>());

            return result;
        }

        /// <summary>Reads the triangle list of a <c>NiTriShapeData</c>.</summary>
        public static List<NifTriangle> GetTriangles(this NifModel model, NifItem data)
        {
            var result = new List<NifTriangle>();

            if (model.FindItem(data, "Triangles") is not { } triangles)
                return result;

            foreach (NifItem item in triangles.Children)
                result.Add(item.Value.Get<NifTriangle>());

            return result;
        }

        /// <summary>
        /// Reads a <c>NiTriStripsData</c>'s strips and flattens them to triangles.
        /// </summary>
        /// <remarks>
        /// Strips alternate winding, and degenerate triangles (a repeated index,
        /// used to stitch strips together) are dropped rather than emitted.
        /// </remarks>
        public static List<NifTriangle> GetTrianglesFromStrips(this NifModel model, NifItem data)
        {
            var result = new List<NifTriangle>();

            if (model.FindItem(data, "Points") is not { } strips)
                return result;

            foreach (NifItem strip in strips.Children)
            {
                var points = new List<ushort>(strip.Children.Count);

                foreach (NifItem point in strip.Children)
                    points.Add((ushort)point.Value.ToUInt());

                for (int i = 0; i + 2 < points.Count; i++)
                {
                    ushort a = points[i];
                    ushort b = points[i + 1];
                    ushort c = points[i + 2];

                    if (a == b || b == c || a == c)
                        continue;

                    result.Add((i & 1) == 0
                        ? new NifTriangle(a, b, c)
                        : new NifTriangle(a, c, b));
                }
            }

            return result;
        }

        /// <summary>
        /// The triangles of any tri-based geometry data block, whether it stores a
        /// triangle list or strips.
        /// </summary>
        public static List<NifTriangle> GetGeometryTriangles(this NifModel model, NifItem data) =>
            data.Name == "NiTriStripsData"
                ? model.GetTrianglesFromStrips(data)
                : model.GetTriangles(data);
    }
}
