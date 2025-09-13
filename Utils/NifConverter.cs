using DynamicData;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Skyrim;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using NiflySharp.Structs;
using Noggog;
using pxr;
using System.Numerics;
using static Mutagen.Bethesda.Skyrim.Furniture;
using Scene = USD.NET.Scene;

namespace SECmd.Utils
{
    struct AnimEntry
    {
        public AnimEntry(string name, int index) : this()
        {
            Name = name;
            Index = index;
        }

        public string Name { get; set; }
        public int Index { get; set; }
    }
    internal class NifConverter : IDisposable
    {
        Dictionary<string, string>? bonePaths = null;
        private NifFile nifFile = new();
        private Scene scene;

        private static readonly float HAVOK_SCALE = 69.9904f;

        private Dictionary<string, List<AnimEntry>> controllerTargets = [];

        public NifConverter(FileInfo inputFile)
        {
            /*
            if (skelFile != null)
            {
                // Parse skeleton first
                if(!skelFile.Exists)
                {
                    Console.WriteLine("Skeleton file doesn't exist!");
                    return;
                }

                Console.WriteLine("Parsing skeleton reference...");

                bonePaths = [];
                NifFile skelNif = new();

                skelNif.Load(skelFile.FullName);

                var skelRoot = skelNif.FindBlockByName<NiNode>(@"NPC Root [Root]");
                if (skelRoot == null)
                {
                    Console.WriteLine("Failed to find root bone!");
                    return;
                }

                RecurseBoneNode(skelNif, skelRoot, bonePaths);
            }
            */
            scene = Scene.Create();
            scene.UpAxis = Scene.UpAxes.Z;
            //scene.MetersPerUnit = 10;

            nifFile.Load(inputFile.FullName);

            // Parse controller blocks as this will affect node creation
            foreach(var manager in nifFile.Blocks.OfType<NiControllerManager>())
            {
                foreach (var ctrlSeq in manager.ControllerSequences.References)
                {
                    var ctrl = nifFile.GetBlock(ctrlSeq);
                    foreach (var block in ctrl.ControlledBlocks)
                    {
                        string blockId = "";
                        if (ctrl.StringPalette != null) blockId = block.ControllerID.String;
                        else blockId = block.NodeName.String;

                        if (!controllerTargets.ContainsKey(blockId)) controllerTargets[blockId] = [];
                        controllerTargets[blockId].Add(new(ctrl.Name.String, block.Interpolator.Index));
                    }
                }
            }
        }

        private UsdStageWeakPtr WeakPtr() => new(scene.Stage);

        public void Convert()
        { 
            var root = nifFile.GetRootNode();

            if (root is NiNode fadeNode)
            {
                var name = fadeNode.Name.String;
                if (name.IsNullOrEmpty()) name = fadeNode.GetType().Name;
                var primPath = new SdfPath($"/{USDUtils.SanitizeName(name)}");
                var prim = scene.Stage.DefinePrim(primPath, new TfToken("Xform"));
                if (!name.IsNullOrEmpty())
                    prim.GetPrim().CreateAttribute(new TfToken("nodeName"), SdfValueTypeNames.String).Set(fadeNode.Name.String);

                scene.Stage.SetDefaultPrim(prim);
                var modelApi = new UsdModelAPI(prim);
                modelApi.SetKind(KindTokens.group);

                var flags = prim.CreateAttribute(new TfToken("Flags"), SdfValueTypeNames.UInt);
                flags.Set((VtValue)fadeNode.Flags_ui);

                foreach (var childRef in root.Children.References)
                {
                    var child = nifFile.GetBlock(childRef);
                    if (child == null) continue;

                    if (child is NiTriShape shape)
                    {
                        CreatePolyMesh(primPath, shape);
                    }
                    else if (child is NiNode node)
                    {
                        RecurseNode(primPath, node);
                    }
                }

                var collisionRef = root.CollisionObject;
                if (collisionRef != null && nifFile.GetBlock(collisionRef) is bhkCollisionObject colObj)
                {
                    CreateCollision(primPath, colObj);
                }
            }

            scene.Stage.SetStartTimeCode(0.0);
            scene.Stage.SetEndTimeCode(Math.Round(0.866667*30));
            /*
            var xform = UsdGeomXform.Get(WeakPtr(), new("/UpperChest01/Chest/Lid"));
            var anim = xform.AddRotateXOp(UsdGeomXformOp.Precision.PrecisionDouble, new("Open"));
            anim.GetAttr().Set(0.0, 0.0);
            anim.GetAttr().Set(0.0, Math.Round(0.3 * 30));
            anim.GetAttr().Set(4.999973, Math.Round(0.466667 * 30));
            anim.GetAttr().Set(4.999973, Math.Round(0.866667 * 30));
            */
        }

        public void Save(FileInfo outputFile)
        {
            scene.SaveAs(Path.ChangeExtension(outputFile.FullName, "usda"));
        }

        private void RecurseNode(SdfPath path, NiNode node)
        {
            var prefix = node.Name.String.IsNullOrEmpty() ? USDUtils.GetPathName(nameof(NiNode)) : USDUtils.SanitizeName(node.Name.String);
            var primPath = path.AppendChild(prefix);
            UsdGeomXformable prim;
            if(controllerTargets.TryGetValue(node.Name.String, out var list))
            {
                prim = UsdSkelRoot.Define(new(scene.Stage), primPath);
                UsdSkelBindingAPI.Apply(prim.GetPrim());
                // Do skeleton stuff
                var mtx = CreateTransformMtx(node.Translation, node.Rotation, node.Scale);
                CreateKFSkeleton((UsdSkelRoot)prim, mtx, list);
            }
            else
            {
                prim = UsdGeomXform.Define(new(scene.Stage), primPath);
            }

            if (!node.Name.String.IsNullOrEmpty())
                prim.GetPrim().CreateAttribute(new TfToken("nodeName"), SdfValueTypeNames.String).Set(node.Name.String);

            CreateTransform(prim, node.Translation, node.Rotation, node.Scale);

            var collisionRef = node.CollisionObject;
            if (collisionRef != null && nifFile.GetBlock(collisionRef) is bhkCollisionObject colObj)
            {
                CreateCollision(primPath, colObj);
            }

            foreach (var childRef in node.Children.References)
            {
                var child = nifFile.GetBlock(childRef);
                if (child == null) continue;

                if (child is INiShape shape)
                {
                    if (child is BSTriShape triShape)
                    {
                        CreatePolyMesh(primPath, triShape);
                    }
                    else if (child is NiTriShape niTriShape)
                    {
                        CreatePolyMesh(primPath, niTriShape);
                    }
                }
                else if (child is NiNode niNode)
                {
                    RecurseNode(primPath, niNode);
                }
            }
        }

        private void CreateKFSkeleton(UsdSkelRoot skelRoot, GfMatrix4d transform, List<AnimEntry> list)
        {
            var skelPath = skelRoot.GetPath().AppendChild(new("Interpolator"));
            var skelPrim = UsdSkelSkeleton.Define(WeakPtr(), skelPath);
            UsdSkelBindingAPI.Apply(skelPrim.GetPrim());

            VtTokenArray boneNames = new();
            VtMatrix4dArray bindingPoses = new(1, new(1.0));

            boneNames.push_back(new("Root"));
            skelPrim.CreateJointsAttr(boneNames);
            skelPrim.CreateBindTransformsAttr(bindingPoses);
            skelPrim.CreateRestTransformsAttr(bindingPoses);

            var skelApi = UsdSkelBindingAPI.Get(WeakPtr(), skelRoot.GetPath());
            SdfPathVector skelPaths = [skelPath];
            // Global skeleton for all children
            skelApi.CreateSkeletonRel().SetTargets(skelPaths);

            var animPath = skelRoot.GetPath().AppendChild(new("Animations"));
            skelApi.CreateAnimationSourceRel().AddTarget(animPath);

            var anim = UsdSkelAnimation.Define(WeakPtr(), animPath);
            //UsdSkelBindingAPI.Apply(anim.GetPrim()).CreateJointsAttr(boneNames);
            anim.CreateJointsAttr(boneNames);

            float frameOffset = 0;
            foreach(var block in list)
            {
                var ctrl = nifFile.GetBlock<NiInterpolator>(block.Index);
                if(ctrl is NiTransformInterpolator tri)
                {
                    skelRoot.GetPrim().CreateAttribute(new TfToken("Anim:"+block.Name), SdfValueTypeNames.Int).Set(
                        (int)Math.Round(frameOffset*30));

                    CreateTransformAnim(anim, tri, ref frameOffset);
                }
            }
        }

        static readonly int FRAME_RATE = 30;
        void CreateTransformAnim(UsdSkelAnimation anim, NiTransformInterpolator node, ref float frameOffset)
        {
            var trData = nifFile.GetBlock(node.Data);
            var rotAttr = anim.CreateRotationsAttr();
            Dictionary<float, Vector3> entries = [];

            // Rotations are stored by axis, stitch them back together
            for (int i = 0; i < 3; i++)
            {
                foreach (var rot in trData.XYZRotations[i].Keys)
                {
                    var temp = entries.GetOrAdd(rot.Time);
                    temp[i] = rot.Value;
                    entries[rot.Time] = temp;
                }
            }

            if (entries.Count > 0)
            {
                foreach (var entry in entries)
                {
                    var qt = Quaternion.CreateFromYawPitchRoll(entry.Value.Y, entry.Value.X, entry.Value.Z);
                    rotAttr.Set(new VtQuatfArray(1, new GfQuatf(qt.W, qt.X, qt.Y, qt.Z)), ToFrameRate(entry.Key + frameOffset));
                }
            }
            else
            {
                rotAttr.Set(new VtQuatfArray(1, new GfQuatf(1)));
            }

            var transAttr = anim.CreateTranslationsAttr();
            if(trData.Translations.NumKeys > 0)
            {
                foreach(var key in trData.Translations.Keys)
                {
                    transAttr.Set(new VtVec3fArray(1, new GfVec3f(key.Value.X, key.Value.Y, key.Value.Z)), ToFrameRate(key.Time + frameOffset));

                }
            }
            else
            {
                transAttr.Set(new VtVec3fArray(1, new GfVec3f(0, 0, 0)));
            }

            var scaleAttr = anim.CreateScalesAttr();
            if (trData.Scales.NumKeys > 0)
            {
                foreach (var key in trData.Scales.Keys)
                {
                    scaleAttr.Set(new VtVec3hArray(1, new GfVec3h(new GfHalf(key.Value))), ToFrameRate(key.Time + frameOffset));
                }
            }
            else
            {
                scaleAttr.Set(new VtVec3fArray(1, new GfVec3f(0, 0, 0)));
            }

            frameOffset += entries.Keys.Order().Last() + 1;
        }

        static int ToFrameRate(float time)
        {
            return (int)Math.Round(time * FRAME_RATE);
        }

        void RecurseBoneNode(NifFile skelNif, NiNode parent, Dictionary<string, string> bonePaths)
        {
            var parentName = parent.Name.String;
            foreach (var nodeRef in parent.Children.References)
            {
                if (skelNif.GetBlock(nodeRef) is NiNode node)
                {
                    if (bonePaths.TryGetValue(parentName, out var bonePath))
                    {
                        bonePaths[node.Name.String] = bonePath + "/" + USDUtils.SanitizeName(node.Name.String);
                    }
                    else
                    {
                        bonePaths[node.Name.String] = USDUtils.SanitizeName(node.Name.String);
                    }

                    RecurseBoneNode(skelNif, node, bonePaths);
                }
            }
        }

        private void CreatePolyMesh(SdfPath path, NiTriShape shape)
        {
            string sanitizedName = USDUtils.SanitizeName(shape.Name.String);
            if (shape.HasSkinInstance)
            {
                path = path.AppendChild(new(sanitizedName + "_Armature"));
                var skelRoot = UsdSkelRoot.Define(new(scene.Stage), path);
                UsdSkelBindingAPI.Apply(skelRoot.GetPrim());
            }

            var extraPrim = path.AppendChild(new(sanitizedName + "_" + nameof(NiTriShape)));
            UsdGeomXform.Define(new(scene.Stage), extraPrim);

            pxr.VtVec3fArray points = new();
            pxr.VtIntArray faceVertexIndices = new();
            for (int i = 0; i < shape.GeometryData.Vertices.Count; i++)
            {
                points.push_back(new(shape.GeometryData.Vertices[i].X, shape.GeometryData.Vertices[i].Y, shape.GeometryData.Vertices[i].Z));
            }
            for (int i = 0; i < shape.GeometryData.Triangles.Count; i++)
            {
                faceVertexIndices.push_back(shape.GeometryData.Triangles[i].V1);
                faceVertexIndices.push_back(shape.GeometryData.Triangles[i].V2);
                faceVertexIndices.push_back(shape.GeometryData.Triangles[i].V3);
            }
            pxr.VtIntArray faceVertexCounts = new(faceVertexIndices.size() / 3, 3);

            if (!UsdGeomMesh.ValidateTopology(faceVertexIndices, faceVertexCounts, points.size(), out string reason))
            {
                throw new Exception(reason);
            }

            var primPath = extraPrim.AppendChild(new(nameof(NiTriShape)));
            UsdGeomMesh mesh = UsdGeomMesh.Define(new(scene.Stage), primPath);

            var prim = mesh.GetPrim();
            prim.SetSpecifier(SdfSpecifier.SdfSpecifierDef);

            GfMatrix4d transform = CreateTransformMtx(shape.Translation, shape.Rotation, shape.Scale);
            mesh.AddTransformOp(UsdGeomXformOp.Precision.PrecisionDouble).Set(transform);

            VtVec3fArray? normals = null;
            VtVec2fArray? uvs = null;
            VtVec3fArray? tangents = null;
            VtVec3fArray? colors = null;

            if (shape.HasNormals)
            {
                //TfTokenVector validInterpolations = [UsdGeomTokens.uniform, UsdGeomTokens.vertex, UsdGeomTokens.faceVarying];
                normals = new();
                for (int i = 0; i < shape.GeometryData.Normals.Count; i++)
                {
                    normals.push_back(new(shape.GeometryData.Normals[i].X, shape.GeometryData.Normals[i].Y, shape.GeometryData.Normals[i].Z));
                }
            }

            if (shape.HasUVs)
            {
                uvs = new();
                for (int i = 0; i < shape.GeometryData.UVSets.Count; i++)
                {
                    uvs.push_back(new(shape.GeometryData.UVSets[i].U, 1.0f - shape.GeometryData.UVSets[i].V));
                }
            }
            if (shape.HasTangents)
            {
                tangents = new();
                for (int i = 0; i < shape.GeometryData.Tangents.Count; i++)
                {
                    tangents.push_back(new(shape.GeometryData.Tangents[i].X, shape.GeometryData.Tangents[i].Y, shape.GeometryData.Tangents[i].Z));
                }
            }
            if (shape.HasVertexColors)
            {
                colors = new();
                foreach (var vColor in shape.GeometryData.VertexColors)
                {
                    colors.push_back(new GfVec3f(vColor.R, vColor.G, vColor.B));
                }
            }

            PopulateUsdMesh(mesh, points, faceVertexIndices, uvs, normals, colors, tangents);

            if (shape.HasShaderProperty)
            {
                var shaderProp = nifFile.GetBlock<INiShader>(shape.ShaderPropertyRef);
                if (shaderProp is BSLightingShaderProperty light)
                {
                    NiAlphaProperty? niAlphaProperty = null;
                    if (shape.HasAlphaProperty)
                    {
                        niAlphaProperty = nifFile.GetBlock(shape.AlphaPropertyRef);
                    }
                    var matlPath = CreateMaterial(extraPrim, light, niAlphaProperty);

                    UsdShadeMaterial matl = UsdShadeMaterial.Get(new(scene.Stage), matlPath);
                    UsdShadeMaterialBindingAPI.Apply(mesh.GetPrim()).Bind(matl);
                }
            }
            if (shape.HasSkinInstance)
            {
                var skinInstance = nifFile.GetBlock(shape.SkinInstanceRef);
                CreateSkeleton(mesh, skinInstance);
            }
            else if (UsdSkelRoot.Find(mesh.GetPrim()) != null)
            {
                // Found a skelRoot parent, this mesh must be bound to an interpolator
                // Add a SkelBinding and a single vertex group covering the entire mesh
                var api = UsdSkelBindingAPI.Apply(mesh.GetPrim());
                api.CreateJointWeightsPrimvar(true).GetAttr().Set(new VtFloatArray(1, 1.0f));
                api.CreateJointIndicesPrimvar(true).GetAttr().Set(new VtIntArray(1, 0));
            }
            
        }

        private void CreatePolyMesh(SdfPath path, BSTriShape shape)
        {
            var extraPrim = path.AppendChild(new(USDUtils.SanitizeName(shape.Name.String) + "TriShape"));
            UsdGeomXform.Define(new(scene.Stage), extraPrim);

            VtVec3fArray points = new();
            VtIntArray faceVertexIndices = new();
            VtVec2fArray? uvs = shape.HasUVs ? new() : null;
            VtVec3fArray? normals = shape.HasNormals ? new() : null;
            VtVec3fArray? tangents = shape.HasTangents ? new() : null;
            VtVec3fArray? colors = shape.HasVertexColors ? new() : null;

            for (int i = 0; i < shape.VertexDataSSE.Count; i++)
            {
                var vData = shape.VertexDataSSE[i];
                points.push_back(new(vData.Vertex.X, vData.Vertex.Y, vData.Vertex.Z));
                uvs?.push_back(new((float)vData.UV.U, 1.0f - (float)vData.UV.V));

                float nX = (vData.Normal.X * 2) / 255.0f - 1.0f;
                float nY = (vData.Normal.Y * 2) / 255.0f - 1.0f;
                float nZ = (vData.Normal.Z * 2) / 255.0f - 1.0f;
                normals?.push_back(new(nX, nY, nZ));

                float tX = (vData.Tangent.X * 2) / 255.0f - 1.0f;
                float tY = (vData.Tangent.Y * 2) / 255.0f - 1.0f;
                float tZ = (vData.Tangent.Z * 2) / 255.0f - 1.0f;
                tangents?.push_back(new(tX, tY, tZ));

                colors?.push_back(new(vData.VertexColors.R / 255.0f, vData.VertexColors.G / 255.0f, vData.VertexColors.B / 255.0f));
            }

            for (int i = 0; i < shape.Triangles.Count; i++)
            {
                faceVertexIndices.push_back(shape.Triangles[i].V1);
                faceVertexIndices.push_back(shape.Triangles[i].V2);
                faceVertexIndices.push_back(shape.Triangles[i].V3);
            }

            var primPath = extraPrim.AppendChild(new(nameof(BSTriShape)));
            UsdGeomMesh mesh = UsdGeomMesh.Define(WeakPtr(), primPath);

            var prim = mesh.GetPrim();
            prim.SetSpecifier(SdfSpecifier.SdfSpecifierDef);


            GfMatrix4d transform = new();
            transform.SetTranslateOnly(new(shape.Translation.X, shape.Translation.Y, shape.Translation.Z));
            transform.SetRotateOnly(NifToUsdMatrix3d(shape.Rotation));
            mesh.AddTransformOp(UsdGeomXformOp.Precision.PrecisionDouble).Set(transform);

            GfVec3d scaling = new(shape.Scale);
            mesh.AddScaleOp(UsdGeomXformOp.Precision.PrecisionDouble).GetAttr().Set(scaling);

            PopulateUsdMesh(mesh, points, faceVertexIndices, uvs, normals, colors, tangents);

            if (shape.HasShaderProperty)
            {
                var shaderProp = nifFile.GetBlock<INiShader>(shape.ShaderPropertyRef);
                if (shaderProp is BSLightingShaderProperty light)
                {
                    NiAlphaProperty? niAlphaProperty = null;
                    if (shape.HasAlphaProperty)
                    {
                        niAlphaProperty = nifFile.GetBlock(shape.AlphaPropertyRef);
                    }
                    var matlPath = CreateMaterial(extraPrim, light, niAlphaProperty);

                    UsdShadeMaterial matl = UsdShadeMaterial.Get(WeakPtr(), matlPath);
                    UsdShadeMaterialBindingAPI.Apply(mesh.GetPrim()).Bind(matl);
                }
            }
        }

        static void PopulateUsdMesh(UsdGeomMesh mesh,
            VtVec3fArray points,
            VtIntArray faceVertexIndices,
            VtVec2fArray? uvs = null,
            VtVec3fArray? normals = null,
            VtVec3fArray? vertexColors = null,
            VtVec3fArray? tangents = null)
        {
            VtIntArray faceVertexCounts = new(faceVertexIndices.size() / 3, 3);

            if (!UsdGeomMesh.ValidateTopology(faceVertexIndices, faceVertexCounts, points.size(), out string reason))
            {
                throw new Exception(reason);
            }
            mesh.CreateSubdivisionSchemeAttr(UsdGeomTokens.none);

            mesh.CreateFaceVertexCountsAttr().Set(faceVertexCounts);
            mesh.CreateFaceVertexIndicesAttr().Set(faceVertexIndices);
            mesh.CreatePointsAttr().Set(points);

            if (normals != null && normals.size() > 0)
            {
                mesh.CreateNormalsAttr(normals);
            }

            if (uvs != null)
            {
                var uvPrimVar = mesh.CreatePrimvar(UsdCs.UsdUtilsGetPrimaryUVSetName(), SdfValueTypeNames.TexCoord2fArray, UsdGeomTokens.vertex);
                uvPrimVar.GetAttr().Set(uvs);
            }

            if (vertexColors != null)
            {
                var primVar = mesh.CreateDisplayColorPrimvar(UsdGeomTokens.vertex);
                primVar.GetAttr().Set(vertexColors);
            }

            if (tangents != null)
            {
                UsdGeomPrimvar primVar = mesh.CreatePrimvar(UsdGeomTokens.tangents, SdfValueTypeNames.Normal3fArray, UsdGeomTokens.vertex);
                primVar.GetAttr().Set(tangents);
            }
        }

        SdfPath CreateMaterial(SdfPath path, BSLightingShaderProperty shaderProp, NiAlphaProperty? alphaProp = null)
        {
            var material = UsdShadeMaterial.Define(new(scene.Stage), path.AppendChild(new("Matl")));
            var shaderPrim = UsdShadeShader.Define(new(scene.Stage), material.GetPath().AppendChild(new("Shader")));

            // Add meta data (shader type, flags etc)
            var matlPrim = material.GetPrim();
            matlPrim.CreateAttribute(new TfToken("shaderType"), SdfValueTypeNames.String)
                .Set((VtValue)shaderProp.ShaderType.ToString());
            matlPrim.CreateAttribute(new TfToken("shaderFlags1"), SdfValueTypeNames.String)
                .Set(shaderProp.ShaderFlags_SSPF1.ToString());
            matlPrim.CreateAttribute(new TfToken("shaderFlags2"), SdfValueTypeNames.String)
                .Set(shaderProp.ShaderFlags_SSPF2.ToString());

            shaderPrim.CreateIdAttr(new TfToken("UsdPreviewSurface"));

            shaderPrim.CreateInput(new("useSpecularWorkflow"), SdfValueTypeNames.Int).Set(1);

            shaderPrim.CreateInput(new("specularColor"), SdfValueTypeNames.Color3f)
                .Set(new GfVec3f(shaderProp.SpecularColor.R, shaderProp.SpecularColor.G, shaderProp.SpecularColor.B));


            var emInput = shaderPrim.CreateInput(new("emissiveColor"), SdfValueTypeNames.Color4f);
            // Emissive multiple doesn't have a range, so this is really just making stuff up
            var alpha = Math.Clamp(shaderProp.EmissiveMultiple / 255, 0, 1.0);
            emInput.Set(new GfVec4f(shaderProp.EmissiveColor.R, shaderProp.EmissiveColor.G, shaderProp.EmissiveColor.B, (float)alpha));

            float roughness = 1.0f - shaderProp.Glossiness / 1000;
            shaderPrim.CreateInput(new TfToken("roughness"), SdfValueTypeNames.Float).Set(roughness);

            //shaderPrim.CreateInput(new TfToken("opacity"), SdfValueTypeNames.Float).Set(shaderProp.Alpha);
            //Add alpha values if present
            if (alphaProp != null)
            {
                float alphaThr = alphaProp.Threshold / 256.0f;
                shaderPrim.CreateInput(new TfToken("opacityMode"), SdfValueTypeNames.Token).Set(new TfToken("presence"));
                shaderPrim.CreateInput(new TfToken("opacityThreshold"), SdfValueTypeNames.Float).Set(alphaThr);
                matlPrim.CreateAttribute(new TfToken("alphaSrcBlendMode"), SdfValueTypeNames.String)
                .Set(Enum.GetName(alphaProp.Flags.SourceBlendMode));
                matlPrim.CreateAttribute(new TfToken("alphaDstBlendMode"), SdfValueTypeNames.String)
                .Set(Enum.GetName(alphaProp.Flags.DestinationBlendMode));
                matlPrim.CreateAttribute(new TfToken("alphaTestFunction"), SdfValueTypeNames.String)
                .Set(Enum.GetName(alphaProp.Flags.TestFunc));
                matlPrim.CreateAttribute(new TfToken("alphaEnableTesting"), SdfValueTypeNames.Bool)
                .Set(alphaProp.Flags.AlphaTest);
                matlPrim.CreateAttribute(new TfToken("alphaEnableBlending"), SdfValueTypeNames.Bool)
                .Set(alphaProp.Flags.AlphaBlend);
                matlPrim.CreateAttribute(new TfToken("alphaTestThreshold"), SdfValueTypeNames.Int)
                .Set((int)alphaProp.Threshold);
            }

            // read a UV reader 
            var stReader = UsdShadeShader.Define(WeakPtr(), material.GetPath().AppendChild(new("stReader")));
            stReader.CreateIdAttr(new TfToken("UsdPrimvarReader_float2"));

            var textureSet = nifFile.GetBlock(shaderProp.TextureSetRef);
            var env = GameEnvironment.Typical.Skyrim(SkyrimRelease.SkyrimSE).DataFolderPath;

            if (!string.IsNullOrEmpty(textureSet?.Textures[0].Content))
            {
                string filePath = Path.Combine(env.Path, textureSet.Textures[0].Content);
                if (!File.Exists(filePath))
                {
                    Console.WriteLine("Diffuse texture does not exist!");
                }
                var diffuseTex = UsdShadeShader.Define(WeakPtr(), material.GetPath().AppendChild(new("diffuse")));
                diffuseTex.CreateIdAttr(new TfToken("UsdUVTexture"));
                diffuseTex.CreateInput(new TfToken("file"), UsdCs.SdfGetValueTypeAsset()).Set(
                    new SdfAssetPath(filePath));
                diffuseTex.CreateInput(new("st"), SdfValueTypeNames.Float2).ConnectToSource(stReader.ConnectableAPI(), new("result"));
                //TODO: Texture clamp mode
                diffuseTex.CreateInput(new("wrapS"), SdfValueTypeNames.Token).Set(new TfToken("repeat"));
                diffuseTex.CreateInput(new("wrapT"), SdfValueTypeNames.Token).Set(new TfToken("repeat"));
                diffuseTex.CreateOutput(new("rgb"), SdfValueTypeNames.Float3);

                shaderPrim.CreateInput(new("diffuseColor"), SdfValueTypeNames.Color3f).ConnectToSource(diffuseTex.ConnectableAPI(), new("rgb"));

                if (alphaProp != null)
                {
                    //diffuseTex.CreateOutput(new("a"), SdfValueTypeNames.Float);
                    //shaderPrim.CreateInput(new("opacity"), SdfValueTypeNames.Float).ConnectToSource(diffuseTex.ConnectableAPI(), new("a"));
                }
            }

            if (!string.IsNullOrEmpty(textureSet?.Textures[1].Content))
            {
                string filePath = Path.Combine(env.Path, textureSet.Textures[1].Content);
                if (!File.Exists(filePath))
                {
                    Console.WriteLine("Diffuse texture does not exist!");
                }
                var normalTex = UsdShadeShader.Define(WeakPtr(), material.GetPath().AppendChild(new("normal")));
                normalTex.CreateIdAttr(new TfToken("UsdUVTexture"));
                normalTex.CreateInput(new TfToken("file"), UsdCs.SdfGetValueTypeAsset()).Set(
                    new SdfAssetPath(filePath));
                normalTex.CreateInput(new("st"), SdfValueTypeNames.Float2).ConnectToSource(stReader.ConnectableAPI(), new("result"));
                normalTex.CreateInput(new("wrapS"), SdfValueTypeNames.Token).Set(new TfToken("repeat"));
                normalTex.CreateInput(new("wrapT"), SdfValueTypeNames.Token).Set(new TfToken("repeat"));
                normalTex.CreateOutput(new("rgb"), SdfValueTypeNames.Float3);

                shaderPrim.CreateInput(new("normal"), SdfValueTypeNames.Color3f).ConnectToSource(normalTex.ConnectableAPI(), new("rgb"));
            }
            //Other texture maps
            var textureMap = matlPrim.GetPrim().CreateAttribute(new TfToken("textures"), SdfValueTypeNames.StringArray);
            var tfTokens = new VtStringArray((uint)textureSet!.Textures.Count);
            for (int i = 2; i < textureSet?.Textures.Count; i++)
            {
                var tex = textureSet.Textures[i].Content;
                tfTokens[i] = new(tex);
            }
            textureMap.Set(tfTokens);


            var stInput = material.CreateInput(new("frame:stPrimvarName"), SdfValueTypeNames.Token);
            stInput.Set(new TfToken("st"));

            stReader.CreateInput(new("varname"), SdfValueTypeNames.String).ConnectToSource(stInput);

            material.CreateOutput(new("surface"), SdfValueTypeNames.Token).ConnectToSource(shaderPrim.ConnectableAPI(), new("surface"));
            material.CreateOutput(new("displacement"), SdfValueTypeNames.Token).ConnectToSource(shaderPrim.ConnectableAPI(), new("displacement"));

            return material.GetPath();
        }

        private SdfPath CreateSkeleton(UsdGeomMesh mesh, NiSkinInstance skinInstance)
        {
            NiSkinData skinData = nifFile.GetBlock(skinInstance.Data);
            var skinParts = nifFile.GetBlock(skinInstance.SkinPartition);
            //var transform = skinData.SkinTransform;

            // Assume the path is already wrapped in UsdSkelRoot by this point
            SdfPath skelPath = mesh.GetPath().GetParentPath().GetParentPath().AppendChild(USDUtils.GetPathName("Skeleton"));
            var skelPrim = UsdSkelSkeleton.Define(WeakPtr(), skelPath);
            VtTokenArray boneNames = new();
            VtMatrix4dArray bindingPoses = new();
            VtMatrix4dArray restPoses = new();
            List<List<(uint, float)>> vertexWeights = new(skinParts.Partitions[0].NumVertices);
            var meshTransform = mesh.GetOrderedXformOps(out _)[0].GetOpTransform(UsdTimeCode.Default());
            var meshMat = meshTransform.ToMatrix4x4();

            for (int i = 0; i < skinParts.Partitions[0].NumVertices; i++)
            {
                vertexWeights.Add([]);
            }

            foreach (var part in skinParts.Partitions)
            {
                foreach (var bIdx in part.Bones)
                {
                    var boneRefIdx = skinInstance.Bones.GetBlockRef(bIdx);
                    var bone = nifFile.GetBlock<NiNode>(boneRefIdx);
                    boneNames.push_back(USDUtils.SanitizeName(bone.Name.String));

                    BoneData boneData = skinData.BoneList[bIdx];


                    var bindingPos =
                        CreateTransformMtx(boneData.SkinTransform.Translation, boneData.SkinTransform.Rotation, boneData.SkinTransform.Scale).GetInverse();
                    Matrix4x4.Invert(meshMat, out Matrix4x4 invMat);

                    var globalBinding = bindingPos.ToMatrix4x4();
                    globalBinding.Translation += meshMat.Translation;
                    globalBinding = Matrix4x4.Transform(globalBinding, Quaternion.CreateFromRotationMatrix(meshMat));

                    var restPos = CreateTransformMtx(bone.Translation, bone.Rotation, bone.Scale);

                    // Binding Pos uses world location
                    bindingPoses.push_back(globalBinding.ToGfMatrix4d());

                    // TODO: Resolve bone binding and set local pos
                    restPoses.push_back(restPos);

                    foreach (var bweight in boneData.VertexWeights)
                    {
                        vertexWeights[bweight.Index].Add((bIdx, bweight.Weight));
                    }
                }
            }

            skelPrim.CreateJointsAttr(boneNames);
            skelPrim.CreateBindTransformsAttr(bindingPoses);
            skelPrim.CreateRestTransformsAttr(restPoses);

            var skelApi = UsdSkelBindingAPI.Apply(mesh.GetPrim());
            SdfPathVector skelPaths = [skelPath];
            skelApi.CreateSkeletonRel().SetTargets(skelPaths);
            skelApi.CreateGeomBindTransformAttr().Set(meshTransform);

            int numWeightVertex = skinParts.Partitions[0].NumWeightsPerVertex;

            VtIntArray jointIdx = new((uint)(vertexWeights.Count * numWeightVertex), 0);
            VtFloatArray jointWeight = new((uint)(vertexWeights.Count * numWeightVertex), 0.0f);
            for (int i = 0; i < vertexWeights.Count; i++)
            {
                for (int j = 0; j < vertexWeights[i].Count; j++)
                {
                    jointIdx[i * numWeightVertex + j] = (int)vertexWeights[i][j].Item1;
                    jointWeight[i * numWeightVertex + j] = vertexWeights[i][j].Item2;
                }
            }

            skelApi.CreateJointIndicesPrimvar(false, skinParts.Partitions[0].NumWeightsPerVertex).GetAttr().Set(jointIdx);
            skelApi.CreateJointWeightsPrimvar(false, skinParts.Partitions[0].NumWeightsPerVertex).GetAttr().Set(jointWeight);

            return skelPath;
        }

        private void CreateCollision(SdfPath path, bhkCollisionObject collisionProp)
        {
            var extraPrim = path.AppendChild(USDUtils.GetPathName(nameof(bhkCollisionObject)));
            var bhkCollPrim = UsdGeomXform.Define(WeakPtr(), extraPrim);

            bhkCollPrim.GetPrim().CreateAttribute(new TfToken("Flags"), SdfValueTypeNames.String)
                .Set(collisionProp.Flags.ToString());

            _ = new UsdModelAPI(bhkCollPrim).SetKind(KindTokens.group);

            var collBody = collisionProp.Body;
            if (collBody != null)
            {
                if (nifFile.GetBlock(collBody) is bhkRigidBody body)
                {
                    GetCollisionShape(bhkCollPrim.GetPath(), body);
                }
            }
        }

        private void GetCollisionShape(SdfPath path, bhkRigidBody collisionProp)
        {
            // Export rotation and translation info even if a rigid body isn't a "T" variant
            UsdGeomXform rigidBodyPrim = UsdGeomXform.Define(WeakPtr(), path.AppendChild(new("rb")));
            var rbInfo = collisionProp.RigidBodyInfo_bRBCI2010;
            if (collisionProp is bhkRigidBodyT)
            {
                CreateTransform(rigidBodyPrim, rbInfo.Translation * HAVOK_SCALE, rbInfo.Rotation);
            }

            UsdPrim metaData = rigidBodyPrim.GetPrim();
            USDUtils.CreateStringAttribute(metaData, "CollisionResponse", Enum.GetName(collisionProp.RigidBodyInfo_bRBCI2010.CollisionResponse)!);
            USDUtils.CreateStringAttribute(metaData, "Layer", Enum.GetName(collisionProp.RigidBodyInfo_bRBCI2010.HavokFilter.Layer_SL)!);
            USDUtils.CreateStringAttribute(metaData, "MotionSystem", Enum.GetName(collisionProp.RigidBodyInfo_bRBCI2010.MotionSystem)!);
            USDUtils.CreateStringAttribute(metaData, "SolverDeactivation", Enum.GetName(collisionProp.RigidBodyInfo_bRBCI2010.SolverDeactivation)!);
            USDUtils.CreateStringAttribute(metaData, "QualityType", Enum.GetName(collisionProp.RigidBodyInfo_bRBCI2010.QualityType)!);

            // Rigid Body Data
            var rbApi = UsdPhysicsRigidBodyAPI.Apply(rigidBodyPrim.GetPrim());
            rbApi.CreateVelocityAttr(new GfVec3f(rbInfo.LinearVelocity.X, rbInfo.LinearVelocity.Y, rbInfo.LinearVelocity.Z));
            rbApi.CreateAngularVelocityAttr(new GfVec3f(rbInfo.AngularVelocity.X, rbInfo.AngularVelocity.Y, rbInfo.AngularVelocity.Z));

            var massApi = UsdPhysicsMassAPI.Apply(rigidBodyPrim.GetPrim());
            massApi.CreateCenterOfMassAttr(new GfVec3f(rbInfo.Center.X, rbInfo.Center.Y, rbInfo.Center.Z));
            massApi.CreateMassAttr(rbInfo.Mass);
            massApi.CreateDiagonalInertiaAttr(new GfVec3f(rbInfo.InertiaTensor.M11, rbInfo.InertiaTensor.M22, rbInfo.InertiaTensor.M33));
            var massPrim = massApi.GetPrim();
            USDUtils.CreateFloatAttribute(massPrim, "AngularDamping", rbInfo.AngularDamping);

            var shape = nifFile.GetBlock(collisionProp.Shape);
            if (shape != null)
            {
                GetShape(rigidBodyPrim.GetPath(), shape);
            }
        }

        private void GetShape(SdfPath path, bhkShape shape)
        {
            UsdGeomMesh? genMesh = null;
            if (shape is bhkListShape listShape)
            {
                UsdGeomXform rigidBodyPrim = UsdGeomXform.Define(WeakPtr(),
                    path.AppendChild(USDUtils.GetPathName(nameof(bhkListShape))));
                for (int i = 0; i < listShape.NumSubShapes; i++)
                {
                    var childShape = nifFile.GetBlock<bhkShape>(listShape.SubShapes.GetBlockRef(i));
                    GetShape(rigidBodyPrim.GetPath(), childShape);
                }
            }
            else if (shape is bhkConvexTransformShape transform)
            {
                UsdGeomXform collPrim = UsdGeomXform.Define(WeakPtr(),
                    path.AppendChild(USDUtils.GetPathName(nameof(bhkConvexTransformShape))));
                var trTuple = ToComponents(transform.Transform);

                GfMatrix3d rotMtx = new(trTuple.Item1);
                trTuple.Item2[0] *= HAVOK_SCALE;
                trTuple.Item2[1] *= HAVOK_SCALE;
                trTuple.Item2[2] *= HAVOK_SCALE;
                CreateTransform(collPrim, trTuple.Item2, rotMtx, trTuple.Item3);
                var childShape = nifFile.GetBlock(transform.Shape);
                GetShape(collPrim.GetPath(),childShape);
            }
            else if (shape is bhkListShape list)
            {

            }
            else if (shape is bhkBoxShape box)
            {
                genMesh = UsdGeomMesh.Define(WeakPtr(),
                    path.AppendChild(USDUtils.GetPathName(nameof(bhkBoxShape))));
                var extents = (box.Dimensions * HAVOK_SCALE);

                USDUtils.CreateBoxMesh(genMesh, extents);

                var matlPath = GetCollisionMaterial(box.Material.Material_SHM);
                UsdShadeMaterial matl = UsdShadeMaterial.Get(WeakPtr(), matlPath);
                UsdShadeMaterialBindingAPI.Apply(genMesh.GetPrim()).Bind(matl);
            }
            else if (shape is bhkConvexVerticesShape convex)
            {
                genMesh = UsdGeomMesh.Define(WeakPtr(),
                    path.AppendChild(USDUtils.GetPathName(nameof(bhkConvexVerticesShape))));
                var mcApi = UsdPhysicsMeshCollisionAPI.Apply(genMesh.GetPrim());
                mcApi.CreateApproximationAttr(UsdPhysicsTokens.convexDecomposition); // indicator for convex vertices shape

                GfVec3f scaling = new(69.9904f);
                genMesh.AddScaleOp(UsdGeomXformOp.Precision.PrecisionFloat).GetAttr().Set(scaling);
                var matApi = UsdPhysicsMaterialAPI.Apply(genMesh.GetPrim());


                List<Vector3> points = new();
                pxr.VtVec3fArray vPoints = new();
                pxr.VtIntArray faceVertexIndices = new();
                for (int i = 0; i < convex.Vertices.Count; i++)
                {
                    points.Add(new(convex.Vertices[i].X, convex.Vertices[i].Y, convex.Vertices[i].Z));
                    vPoints.push_back(new(convex.Vertices[i].X, convex.Vertices[i].Y, convex.Vertices[i].Z));
                }

                // Ported straight from Nifskope ._.
                Vector3 A, B, C;
                List<Vector3> tris = new();
                int prev; bool good = false;
                for (int i = 0; i < points.Count - 2; i++)
                {
                    A = points[i];
                    for (int j = i + 1; j < points.Count - 1; j++)
                    {
                        B = points[j];
                        for (int k = j + 1; k < points.Count; k++)
                        {
                            C = points[k];
                            prev = 0;
                            good = true;
                            var N = Vector3.Cross(B - A, C - A);
                            for (int p = 0; p < points.Count; p++)
                            {
                                var V = points[p];
                                if (V == A || V == B || V == C) continue;

                                var D = Vector3.Dot(V - A, N);
                                if (D == 0) continue;

                                int eps = Math.Sign(D);
                                if (eps + prev == 0)
                                {
                                    good = false;
                                    continue;
                                }
                                prev = eps;
                            }

                            if (good)
                            {
                                faceVertexIndices.push_back(i);
                                faceVertexIndices.push_back(j);
                                faceVertexIndices.push_back(k);
                            }
                        }
                    }
                }


                pxr.VtVec3fArray normals = new();
                pxr.VtIntArray normalsIndices = new();
                for (int i = 0; i < convex.Normals.Count; i++)
                {
                    normals.push_back(new(convex.Normals[i].X, convex.Normals[i].Y, convex.Normals[i].Z));
                    normalsIndices.push_back(i);
                }

                PopulateUsdMesh(genMesh, vPoints, faceVertexIndices, null, normals);

                var matlPath = GetCollisionMaterial(convex.Material.Material_SHM);
                UsdShadeMaterial matl = UsdShadeMaterial.Get(WeakPtr(), matlPath);
                UsdShadeMaterialBindingAPI.Apply(genMesh.GetPrim()).Bind(matl);
            }
            else if (shape is bhkMoppBvTreeShape mopp)
            {
                UsdGeomXform moppPrim = UsdGeomXform.Define(WeakPtr(), path.AppendChild(USDUtils.GetPathName("mopp")));
                var cms = nifFile.GetBlock(mopp.Shape);

                if (cms != null)
                {
                    GetShape(moppPrim.GetPath(), cms);
                }
            }
            else if (shape is bhkCompressedMeshShape cms)
            {
                var cmsScope = UsdGeomXform.Define(WeakPtr(), path.AppendChild(USDUtils.GetPathName(nameof(bhkCompressedMeshShape))));
                genMesh = UsdGeomMesh.Define(WeakPtr(), cmsScope.GetPath().AppendChild(USDUtils.GetPathName(nameof(bhkCompressedMeshShapeData))));
                genMesh.AddScaleOp(UsdGeomXformOp.Precision.PrecisionFloat)
                    .GetAttr().Set(new GfVec3f(cms.Scale.X, cms.Scale.Y, cms.Scale.Z));

                var cmsData = nifFile.GetBlock(cms.Data);
                List<List<int>> matlIndices = new(cmsData.ChunkMaterials.Count);
                for (int i = 0; i < cmsData.ChunkMaterials.Count; i++)
                {
                    matlIndices.Add([]);
                }

                pxr.VtVec3fArray points = new();
                pxr.VtIntArray faceVertexIndices = new();

                if (cmsData.BigVerts.Count > 0)
                {
                    foreach (var bigVert in cmsData.BigVerts)
                    {
                        var vec = bigVert * HAVOK_SCALE;
                        points.push_back(new(vec.X, vec.Y, vec.Z));
                    }
                    for (int i = 0; i < cmsData.BigTris.Count; i++)
                    {
                        faceVertexIndices.push_back(cmsData.BigTris[i].Triangle.V1);
                        faceVertexIndices.push_back(cmsData.BigTris[i].Triangle.V2);
                        faceVertexIndices.push_back(cmsData.BigTris[i].Triangle.V3);

                        matlIndices[(int)cmsData.BigTris[i].Material].Add(i);
                    }
                }


                foreach (var chunk in cmsData.Chunks)
                {
                    var chunkOrigin = new Vector3(chunk.Translation.X, chunk.Translation.Y, chunk.Translation.Z);
                    var matlIndex = chunk.MaterialIndex;
                    var rot = cmsData.ChunkTransforms[chunk.TransformIndex].Rotation;
                    var translate = cmsData.ChunkTransforms[chunk.TransformIndex].Translation;

                    var affineMtx = new GfMatrix4f();
                    affineMtx.SetTranslateOnly(new GfVec3f(translate.X, translate.Y, translate.Z));
                    affineMtx.SetRotateOnly(new GfQuatf(rot.W, rot.X, rot.Y, rot.Z));

                    int vertOffset = (int)points.size();

                    foreach (var chunkVert in chunk.Vertices)
                    {
                        Vector3 vec = chunkOrigin + (new Vector3(chunkVert.X, chunkVert.Y, chunkVert.Z) / 1000.0f);
                        vec *= HAVOK_SCALE;
                        GfVec3f gfVec3F = new GfVec3f(vec.X, vec.Y, vec.Z);
                        points.push_back(affineMtx.Transform(gfVec3F));
                    }

                    int triOffset = 0;
                    int faceVal = (int)faceVertexIndices.size() / 3;
                    for (int i = 0; i < chunk.Strips.Count; i++)
                    {
                        for (int j = 0; j < chunk.Strips[i] - 2; j++)
                        {
                            if ((j + 1) % 2 == 0)
                            {
                                faceVertexIndices.push_back(chunk.Indices[triOffset + j + 2] + vertOffset);
                                faceVertexIndices.push_back(chunk.Indices[triOffset + j + 1] + vertOffset);
                                faceVertexIndices.push_back(chunk.Indices[triOffset + j + 0] + vertOffset);
                            }
                            else
                            {
                                faceVertexIndices.push_back(chunk.Indices[triOffset + j + 0] + vertOffset);
                                faceVertexIndices.push_back(chunk.Indices[triOffset + j + 1] + vertOffset);
                                faceVertexIndices.push_back(chunk.Indices[triOffset + j + 2] + vertOffset);
                            }

                            matlIndices[(int)chunk.MaterialIndex].Add(faceVal++);
                        }
                        triOffset += chunk.Strips[i];
                    }

                    //Non-stripped tris
                    for (int i = 0; i < ((int)chunk.Indices.Count - triOffset); i += 3)
                    {
                        faceVertexIndices.push_back(chunk.Indices[triOffset + i + 0] + vertOffset);
                        faceVertexIndices.push_back(chunk.Indices[triOffset + i + 1] + vertOffset);
                        faceVertexIndices.push_back(chunk.Indices[triOffset + i + 2] + vertOffset);

                        matlIndices[(int)chunk.MaterialIndex].Add(faceVal++);
                    }
                }

                PopulateUsdMesh(genMesh, points, faceVertexIndices);

                List<SdfPath> materialPaths = [];
                foreach (var matlEntry in cmsData.ChunkMaterials)
                {
                    var matlPath = GetCollisionMaterial(matlEntry.Material, scene.Stage.GetPrimAtPath(cmsScope.GetPath()));
                    materialPaths.Add(matlPath);
                }

                for (int i = 0; i < matlIndices.Count; i++)
                {
                    VtIntArray subsetFaces = new((uint)matlIndices[i].Count);
                    subsetFaces.CopyFromArray([.. matlIndices[i]]);
                    var subset = UsdGeomSubset.CreateUniqueGeomSubset(genMesh, new("Subset" + i), UsdGeomTokens.face, subsetFaces);
                    UsdShadeMaterial matl = UsdShadeMaterial.Get(WeakPtr(), materialPaths[i]);
                    UsdShadeMaterialBindingAPI.Apply(subset.GetPrim()).Bind(matl);
                }
            }

            // If a mesh was generated, optionally add interpolator bone and vertex weight group
            if (genMesh != null && UsdSkelRoot.Find(genMesh.GetPrim()) != null)
            {
                // Found a skelRoot parent, this mesh must be bound to an interpolator
                // Add a SkelBinding and a single vertex group covering the entire mesh
                var api = UsdSkelBindingAPI.Apply(genMesh.GetPrim());
                api.CreateJointWeightsPrimvar(true).GetAttr().Set(new VtFloatArray(1, 1.0f));
                api.CreateJointIndicesPrimvar(true).GetAttr().Set(new VtIntArray(1, 0));
            }
        }

        private SdfPath GetCollisionMaterial(SkyrimHavokMaterial material, UsdPrim? parent = null)
        {
            SdfPath primPath = (parent == null) ? scene.Stage.GetDefaultPrim().GetPath() : parent.GetPath();
            var defaultPrim = scene.Stage.DefinePrim(primPath.AppendChild(new("collisionMaterials")));
            var enumName = Enum.GetName(material) ?? ("Matl" + material.ToString());
            SdfPath matlPath = defaultPrim.GetPath().AppendChild(new(enumName));
            if (scene.Stage.GetPrimAtPath(matlPath))
            {
                return matlPath;
            }

            var matlPrim = UsdShadeMaterial.Define(WeakPtr(), matlPath);
            var shaderPrim = UsdShadeShader.Define(WeakPtr(), matlPrim.GetPath().AppendChild(new("Shader")));

            shaderPrim.CreateIdAttr(new TfToken("UsdPreviewSurface"));
            shaderPrim.CreateInput(new("useSpecularWorkflow"), SdfValueTypeNames.Int).Set(1);
            shaderPrim.CreateInput(new TfToken("opacity"), SdfValueTypeNames.Float).Set(0.1f);
            var matlColor = NifUtils.GetMaterialColor(material);
            shaderPrim.CreateInput(new TfToken("diffuseColor"), SdfValueTypeNames.Color3f).Set(new GfVec3f(matlColor.R, matlColor.G, matlColor.B));
            matlPrim.CreateOutput(new("surface"), SdfValueTypeNames.Token).ConnectToSource(shaderPrim.ConnectableAPI(), new("surface"));
            matlPrim.CreateOutput(new("displacement"), SdfValueTypeNames.Token).ConnectToSource(shaderPrim.ConnectableAPI(), new("displacement"));

            UsdPhysicsMaterialAPI.Apply(shaderPrim.GetPrim());

            return matlPrim.GetPath();
        }

        static void CreateTransform(UsdGeomXformable prim, Vector3 translation = default, Matrix33 rotation = default, double scale = 1.0)
        {
            GfMatrix4d transform = new GfMatrix4d();
            transform.SetTranslateOnly(new(translation.X, translation.Y, translation.Z));
            transform.SetRotateOnly(NifToUsdMatrix3d(rotation));
            prim.AddTransformOp(UsdGeomXformOp.Precision.PrecisionDouble).Set(transform);

            prim.AddScaleOp(UsdGeomXformOp.Precision.PrecisionDouble).GetAttr().Set(new GfVec3d(scale));
        }

        static GfMatrix3d NifToUsdMatrix3d(Matrix33 mtx)
        {
            return new(mtx.M11, mtx.M12, mtx.M13,
                mtx.M21, mtx.M22, mtx.M23,
                mtx.M31, mtx.M32, mtx.M33);
        }

        static void CreateTransform(UsdGeomXformable prim, GfVec3d translation, GfMatrix3d rotation, GfVec3d scale)
        {
            GfMatrix4d transform = new GfMatrix4d();
            transform.SetTranslateOnly(translation);
            transform.SetRotateOnly(rotation);
            prim.AddTransformOp(UsdGeomXformOp.Precision.PrecisionDouble).Set(transform);

            prim.AddScaleOp(UsdGeomXformOp.Precision.PrecisionDouble).GetAttr().Set(scale);
        }

        static void CreateTransform(UsdGeomXformable prim, Vector4 translation, hkQuaternion rotation, double scale = 1.0)
        {
            GfMatrix4d transform = new();
            transform.SetTranslateOnly(new(translation.X, translation.Y, translation.Z));
            transform.SetRotateOnly(new GfQuatd(rotation.W, rotation.X, rotation.Y, rotation.Z));
            prim.AddTransformOp(UsdGeomXformOp.Precision.PrecisionDouble).Set(transform);

            prim.AddScaleOp(UsdGeomXformOp.Precision.PrecisionDouble).GetAttr().Set(new GfVec3d(scale));
        }

        static GfMatrix4d CreateTransformMtx(Vector3 translation, Matrix33 rotation, double scale = 1.0)
        {
            GfMatrix4d transform = new();
            var scaledRot = rotation;
            scaledRot.AddScale((float)scale);
            var rotMtx = NifToUsdMatrix3d(scaledRot);

            transform.SetTransform(rotMtx, new(translation.X, translation.Y, translation.Z));

            return transform;
        }

        static (GfQuatd, GfVec3d, GfVec3d) ToComponents(Matrix44 mtx)
        {
            Matrix4x4 matrix = new(mtx.M11, mtx.M12, mtx.M13, mtx.M41,
                                   mtx.M21, mtx.M22, mtx.M23, mtx.M42,
                                   mtx.M31, mtx.M32, mtx.M33, mtx.M43,
                                   mtx.M14, mtx.M24, mtx.M34, mtx.M44);

            if (Matrix4x4.Decompose(matrix, out Vector3 scale, out Quaternion rot, out Vector3 tr))
            {
                return (new(rot.W, rot.X, rot.Y, rot.Z), new(tr.X, tr.Y, tr.Z), new(scale.X, scale.Y, scale.Z));
            }

            throw new Exception();
        }

        public void Dispose()
        {
            scene.Close();
        }
    }
}
