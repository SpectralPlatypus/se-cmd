using MeshIO.Formats;
using MeshIO.Formats.Fbx;
using MeshIO.Formats.Fbx.Parsers;
using MeshIO.Formats.Fbx.Writers;

namespace SECmd.Fbx
{
    /// <summary>
    /// An FBX file as its raw node tree, which is all an FBX physically is: a
    /// recursive list of named records, each carrying typed properties.
    /// </summary>
    /// <remarks>
    /// Reading and writing the tree is MeshIO's job; everything above it is ours.
    /// This type exists because MeshIO exposes raw *reading* publicly but keeps its
    /// raw writer internal, so the two directions are not symmetric through its
    /// public API. Compiling MeshIO's raw layer into this assembly (see
    /// se-cmd.csproj) makes both reachable, and this wraps them into one place.
    ///
    /// Deliberately no scene semantics here: Model/Geometry/Deformer/AnimationCurve
    /// and the rest live above, because the FBX features this project needs
    /// (skin clusters, bind poses, animation curves, custom properties) are exactly
    /// the ones MeshIO's own scene model does not cover.
    /// </remarks>
    public sealed class FbxDocument
    {
        /// <summary>The document's top-level records, in order.</summary>
        public List<FbxNode> Nodes { get; } = [];

        /// <summary>The FBX version to write. 7400 is FBX 2014/2015, which Blender and Max both read.</summary>
        public FbxVersion Version { get; set; } = FbxVersion.v7400;

        /// <summary>Whether the file was read as binary or ASCII.</summary>
        public ContentType ContentType { get; set; } = ContentType.Binary;

        /// <summary>Reads an FBX from a file, detecting binary or ASCII.</summary>
        public static FbxDocument Load(string path)
        {
            using FileStream stream = File.OpenRead(path);
            return Load(stream);
        }

        /// <summary>Reads an FBX from a stream, detecting binary or ASCII.</summary>
        public static FbxDocument Load(Stream stream)
        {
            ContentType contentType = DetectContentType(stream);

            // MeshIO's parsers close the stream they are given; the caller owns it.
            var guarded = new NonClosingStream(stream);

            IFbxParser parser = contentType == ContentType.Binary
                ? new FbxBinaryParser(guarded)
                : new FbxAsciiParser(guarded);

            using (parser)
            {
                FbxRootNode root = parser.Parse();

                var document = new FbxDocument
                {
                    Version = root.Version,
                    ContentType = contentType
                };

                document.Nodes.AddRange(root.Nodes);
                return document;
            }
        }

        /// <summary>Writes the document to a file.</summary>
        public void Save(string path, ContentType? contentType = null)
        {
            using FileStream stream = File.Create(path);
            Save(stream, contentType);
        }

        /// <summary>Writes the document to a stream.</summary>
        /// <remarks>
        /// Binary only. MeshIO's ASCII writer emits boolean properties as a raw
        /// <c>\x01</c> escape, which is not valid FBX ASCII and which its own parser
        /// rejects, so an ASCII save would produce a file nothing can open. Reading
        /// ASCII works and stays supported.
        /// </remarks>
        public void Save(Stream stream, ContentType? contentType = null)
        {
            if ((contentType ?? ContentType) == ContentType.ASCII)
            {
                throw new NotSupportedException(
                    "writing FBX as ASCII is not supported: the underlying writer emits "
                    + "invalid escapes for boolean properties. Save as binary instead.");
            }

            var root = new FbxRootNode { Version = Version };

            foreach (FbxNode node in Nodes)
                root.Nodes.Add(node);

            // MeshIO's writers close the stream they are given; the caller owns it.
            using var writer = new FbxBinaryWriter(root, new NonClosingStream(stream));
            writer.Write();
        }

        /// <summary>
        /// Binary FBX files open with a fixed magic string; anything else is ASCII.
        /// </summary>
        private static ContentType DetectContentType(Stream stream)
        {
            const string BinaryMagic = "Kaydara FBX Binary  \0";

            long start = stream.Position;
            byte[] header = new byte[BinaryMagic.Length];

            int read = stream.Read(header, 0, header.Length);
            stream.Position = start;

            if (read < header.Length)
                return ContentType.ASCII;

            for (int i = 0; i < BinaryMagic.Length; i++)
            {
                if (header[i] != (byte)BinaryMagic[i])
                    return ContentType.ASCII;
            }

            return ContentType.Binary;
        }

        /// <summary>The first top-level record with this name, or null.</summary>
        public FbxNode? this[string name] => Nodes.FirstOrDefault(n => n.Name == name);
    }
}
