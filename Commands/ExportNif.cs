using FluentResults;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Skyrim;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using NiflySharp.Structs;
using Noggog;
using pxr;
using SECmd.Utils;
using System.CommandLine;
using System.Numerics;
using USD.NET;
using static SECmd.AnimData.HandVariableData;
using Scene = USD.NET.Scene;

namespace SECmd.Commands
{
    internal class ExportNif
    {
        public static void Register(RootCommand root)
        {
            Option<FileInfo> fileOption = new("--input", "-i") { Description = "Source project file to be retargeted", Required = true };
            Option<FileInfo> outputOption = new("--output", "-o") { Description = "Output directory", DefaultValueFactory = parseResult => new(Environment.CurrentDirectory) };

            Command exportCommand = new("export", "Export nif file to usd")
            {
                fileOption,
                outputOption,
            };

            exportCommand.SetAction(parseResults =>
                Execute(parseResults.GetValue(fileOption)!,
                parseResults.GetValue(outputOption)!
                ));

            root.Subcommands.Add(exportCommand);
        }

        public static void Execute(FileInfo inputFile, FileInfo outputFile)
        {
            Scene scene = Scene.Create();// CreateInMemory();
            UsdStageWeakPtr stage = new(scene.Stage);
            scene.UpAxis = Scene.UpAxes.Z;
            //scene.MetersPerUnit = 10;

            NifFile nifFile = new();
            nifFile.Load(inputFile.FullName);
            var root = nifFile.GetRootNode();

            if (root is BSFadeNode fadeNode)
            {
                var primPath = new SdfPath($"/{nameof(BSFadeNode)}");
                var prim = scene.Stage.DefinePrim(primPath, new TfToken("Xform"));
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
                        CreatePolyMesh(scene.Stage, primPath, nifFile, shape);
                    }
                    else if(child is NiNode node)
                    {
                        RecurseNode(scene.Stage, primPath, nifFile, node);
                    }
                }

                var collisionRef = root.CollisionObject;
                if (collisionRef != null && nifFile.GetBlock(collisionRef) is bhkCollisionObject colObj)
                {
                    CreateCollision(scene.Stage, primPath, nifFile, colObj);
                }

                scene.SaveAs(Path.ChangeExtension(inputFile.Name, "usda"));
                scene.Close();
            }
        }

        static void RecurseNode(UsdStage stage, SdfPath path, NifFile nif, NiNode node)
        {
            string prefix = node.Name.String.IsNullOrEmpty() ? node.Name.String : nameof(NiNode);
            var primPath = path.AppendChild(USDUtils.GetPathName(prefix));
            var prim = UsdGeomXform.Define(new(stage), primPath);
            CreateTransform(prim, node.Translation, node.Rotation, node.Scale);

            var collisionRef = node.CollisionObject;
            if (collisionRef != null && nif.GetBlock(collisionRef) is bhkCollisionObject colObj)
            {
                CreateCollision(stage, path, nif, colObj);
            }

            foreach (var childRef in node.Children.References)
            {
                var child = nif.GetBlock(childRef);
                if (child == null) continue;

                if (child is INiShape shape)
                {
                    if (child is BSTriShape triShape)
                    {
                        CreatePolyMesh(stage, primPath, nif, triShape);
                    }
                    else if (child is NiTriShape niTriShape)
                    {
                        CreatePolyMesh(stage, primPath, nif, niTriShape);
                    }
                }
                else if (child is NiNode niNode)
                {
                    RecurseNode(stage, primPath, nif, niNode);
                }
            }
        }

        static void CreatePolyMesh(UsdStage stage, SdfPath path, NifFile nif, NiTriShape shape)
        {
            var extraPrim = path.AppendChild(new(shape.Name.String + "TriShape"));
            UsdGeomXform.Define(new(stage), extraPrim);

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
            UsdGeomMesh mesh = UsdGeomMesh.Define(new(stage), primPath);

            var prim = mesh.GetPrim();
            prim.SetSpecifier(SdfSpecifier.SdfSpecifierDef);
            //prim.SetTypeName(prim.GetTypeName());
            // Create vertex, triangle and face count attributes
            mesh.CreateFaceVertexCountsAttr().Set(faceVertexCounts);
            mesh.CreateFaceVertexIndicesAttr().Set(faceVertexIndices);
            mesh.CreatePointsAttr().Set(points);

            GfMatrix4d transform = new();
            transform.SetTranslateOnly(new(shape.Translation.X, shape.Translation.Y, shape.Translation.Z));
            transform.SetRotateOnly(NifToUsdMatrix3d(shape.Rotation));
            mesh.AddTransformOp(UsdGeomXformOp.Precision.PrecisionDouble).Set(transform);

            GfVec3d scaling = new(shape.Scale);
            mesh.AddScaleOp(UsdGeomXformOp.Precision.PrecisionDouble).GetAttr().Set(scaling);

            if (shape.HasNormals)
            {
                //TfTokenVector validInterpolations = [UsdGeomTokens.uniform, UsdGeomTokens.vertex, UsdGeomTokens.faceVarying];
                pxr.VtVec3fArray normals = new();
                pxr.VtIntArray normalsIndices = new();
                for (int i = 0; i < shape.GeometryData.Normals.Count; i++)
                {
                    normals.push_back(new(shape.GeometryData.Normals[i].X, shape.GeometryData.Normals[i].Y, shape.GeometryData.Normals[i].Z));
                    normalsIndices.push_back(i);
                }
                
                UsdGeomPrimvar primVar = (new UsdGeomPrimvarsAPI(mesh.GetPrim())).CreatePrimvar(UsdGeomTokens.normals, SdfValueTypeNames.Normal3fArray);
                primVar.SetIndices(normalsIndices);
                primVar.GetAttr().Set(normals);

                primVar.SetInterpolation(UsdGeomTokens.vertex);
                primVar.SetInterpolation(UsdGeomTokens.faceVarying);
                primVar.SetInterpolation(UsdGeomTokens.uniform);
            }

            if (shape.HasUVs)
            {
                pxr.VtVec2fArray uvs = new();
                pxr.VtIntArray uvIndices = new();
                for (int i = 0; i < shape.GeometryData.UVSets.Count; i++)
                {
                    uvs.push_back(new(shape.GeometryData.UVSets[i].U, 1.0f - shape.GeometryData.UVSets[i].V));
                    uvIndices.push_back(i);
                }
                UsdGeomPrimvar primVar = (new UsdGeomPrimvarsAPI(mesh.GetPrim())).
                    CreatePrimvar(UsdCs.UsdUtilsGetPrimaryUVSetName(), SdfValueTypeNames.TexCoord2fArray, UsdGeomTokens.vertex);
                primVar.SetIndices(uvIndices);
                primVar.GetAttr().Set(uvs);
            }
            if (shape.HasTangents)
            {
                pxr.VtVec3fArray tangents = new();
                pxr.VtIntArray tangentIndices = new();
                for (int i = 0; i < shape.GeometryData.Tangents.Count; i++)
                {
                    tangents.push_back(new(shape.GeometryData.Tangents[i].X, shape.GeometryData.Tangents[i].Y, shape.GeometryData.Tangents[i].Z));
                    tangentIndices.push_back(i);
                }
                UsdGeomPrimvar primVar = (new UsdGeomPrimvarsAPI(mesh.GetPrim())).CreatePrimvar(UsdGeomTokens.tangents, SdfValueTypeNames.Normal3fArray);
                primVar.SetIndices(tangentIndices);
                primVar.GetAttr().Set(tangents);
            }
            if (shape.HasVertexColors)
            {
                var primVar = mesh.CreateDisplayColorPrimvar(UsdGeomTokens.vertex);
                VtVec3fArray colors = new();
                foreach (var vColor in shape.GeometryData.VertexColors)
                {
                    colors.push_back(new GfVec3f(vColor.R, vColor.G, vColor.B));
                }
                primVar.GetAttr().Set(colors);
            }

            if (shape.HasShaderProperty)
            {
                var shaderProp = nif.GetBlock<INiShader>(shape.ShaderPropertyRef);
                if (shaderProp is BSLightingShaderProperty light)
                {
                    NiAlphaProperty? niAlphaProperty = null;
                    if(shape.HasAlphaProperty)
                    {
                        niAlphaProperty = nif.GetBlock(shape.AlphaPropertyRef);
                    }
                    var matlPath = CreateMaterial(stage, extraPrim, nif, light, niAlphaProperty);

                    UsdShadeMaterial matl = UsdShadeMaterial.Get(new(stage), matlPath);
                    UsdShadeMaterialBindingAPI.Apply(mesh.GetPrim()).Bind(matl);
                }
            }
        }

        static void CreatePolyMesh(UsdStage stage, SdfPath path, NifFile nif, BSTriShape shape)
        {
            var extraPrim = path.AppendChild(new(shape.Name.String.Replace(':','_') + "TriShape"));
            UsdGeomXform.Define(new(stage), extraPrim);

            VtVec3fArray points = new();
            VtIntArray faceVertexIndices = new();
            VtVec2fArray uvs = new();
            VtIntArray indices = new();
            VtVec3fArray normals = new();
            VtVec3fArray tangents = new();
            VtVec3fArray colors = new();

            for (int i = 0; i < shape.VertexDataSSE.Count; i++)
            {
                var vData = shape.VertexDataSSE[i];
                points.push_back(new(vData.Vertex.X, vData.Vertex.Y, vData.Vertex.Z));
                uvs.push_back(new((float)vData.UV.U, 1.0f - (float)vData.UV.V));
                
                float nX = (vData.Normal.X * 2) / 255.0f - 1.0f;
                float nY = (vData.Normal.Y * 2) / 255.0f - 1.0f;
                float nZ = (vData.Normal.Z * 2) / 255.0f - 1.0f;
                normals.push_back(new(nX,nY,nZ));

                float tX = (vData.Tangent.X * 2) / 255.0f - 1.0f;
                float tY = (vData.Tangent.Y * 2) / 255.0f - 1.0f;
                float tZ = (vData.Tangent.Z * 2) / 255.0f - 1.0f;
                tangents.push_back(new(tX, tY, tZ));

                colors.push_back(new(vData.VertexColors.R / 255.0f, vData.VertexColors.G / 255.0f, vData.VertexColors.B / 255.0f));

                indices.push_back(i);
            }
            for (int i = 0; i < shape.Triangles.Count; i++)
            {
                faceVertexIndices.push_back(shape.Triangles[i].V1);
                faceVertexIndices.push_back(shape.Triangles[i].V2);
                faceVertexIndices.push_back(shape.Triangles[i].V3);
            }

            pxr.VtIntArray faceVertexCounts = new(faceVertexIndices.size() / 3, 3);

            if (!UsdGeomMesh.ValidateTopology(faceVertexIndices, faceVertexCounts, points.size(), out string reason))
            {
                throw new Exception(reason);
            }


            var primPath = extraPrim.AppendChild(new(nameof(BSTriShape)));
            UsdGeomMesh mesh = UsdGeomMesh.Define(new(stage), primPath);

            var prim = mesh.GetPrim();
            prim.SetSpecifier(SdfSpecifier.SdfSpecifierDef);
            //prim.SetTypeName(prim.GetTypeName());
            // Create vertex, triangle and face count attributes
            mesh.CreateFaceVertexCountsAttr().Set(faceVertexCounts);
            mesh.CreateFaceVertexIndicesAttr().Set(faceVertexIndices);
            mesh.CreatePointsAttr().Set(points);

            GfMatrix4d transform = new();
            transform.SetTranslateOnly(new(shape.Translation.X, shape.Translation.Y, shape.Translation.Z));
            transform.SetRotateOnly(NifToUsdMatrix3d(shape.Rotation));
            mesh.AddTransformOp(UsdGeomXformOp.Precision.PrecisionDouble).Set(transform);

            GfVec3d scaling = new(shape.Scale);
            mesh.AddScaleOp(UsdGeomXformOp.Precision.PrecisionDouble).GetAttr().Set(scaling);

            if (shape.HasNormals)
            {
                UsdGeomPrimvar primVar = (new UsdGeomPrimvarsAPI(mesh.GetPrim())).CreatePrimvar(UsdGeomTokens.normals, SdfValueTypeNames.Normal3fArray);
                primVar.SetIndices(indices);
                primVar.GetAttr().Set(normals);

                primVar.SetInterpolation(UsdGeomTokens.vertex);
                primVar.SetInterpolation(UsdGeomTokens.faceVarying);
                primVar.SetInterpolation(UsdGeomTokens.uniform);
            }

            if (shape.HasUVs)
            {
                UsdGeomPrimvar primVar = (new UsdGeomPrimvarsAPI(mesh.GetPrim())).
                    CreatePrimvar(UsdCs.UsdUtilsGetPrimaryUVSetName(), SdfValueTypeNames.TexCoord2fArray, UsdGeomTokens.vertex);
                primVar.SetIndices(indices);
                primVar.GetAttr().Set(uvs);
            }
            if (shape.HasTangents)
            {
                UsdGeomPrimvar primVar = (new UsdGeomPrimvarsAPI(mesh.GetPrim())).CreatePrimvar(UsdGeomTokens.tangents, SdfValueTypeNames.Normal3fArray);
                primVar.SetIndices(indices);
                primVar.GetAttr().Set(tangents);
            }
            if (shape.HasVertexColors)
            {
                var primVar = mesh.CreateDisplayColorPrimvar(UsdGeomTokens.vertex);
                
                foreach (var vColor in shape.GeometryData.VertexColors)
                {
                    colors.push_back(new GfVec3f(vColor.R, vColor.G, vColor.B));
                }
                primVar.GetAttr().Set(colors);
            }

            if (shape.HasShaderProperty)
            {
                var shaderProp = nif.GetBlock<INiShader>(shape.ShaderPropertyRef);
                if (shaderProp is BSLightingShaderProperty light)
                {
                    NiAlphaProperty? niAlphaProperty = null;
                    if (shape.HasAlphaProperty)
                    {
                        niAlphaProperty = nif.GetBlock(shape.AlphaPropertyRef);
                    }
                    var matlPath = CreateMaterial(stage, extraPrim, nif, light, niAlphaProperty);

                    UsdShadeMaterial matl = UsdShadeMaterial.Get(new(stage), matlPath);
                    UsdShadeMaterialBindingAPI.Apply(mesh.GetPrim()).Bind(matl);
                }
            }
        }

        static SdfPath CreateMaterial(UsdStage stage, SdfPath path, NifFile nif, BSLightingShaderProperty shaderProp, NiAlphaProperty? alphaProp = null)
        {
            var material = UsdShadeMaterial.Define(new(stage), path.AppendChild(new("Matl")));
            var shaderPrim = UsdShadeShader.Define(new(stage), material.GetPath().AppendChild(new("Shader")));

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
            var alpha  = Math.Clamp(shaderProp.EmissiveMultiple / 255, 0, 1.0);
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
            var stReader = UsdShadeShader.Define(new(stage), material.GetPath().AppendChild(new("stReader")));
            stReader.CreateIdAttr(new TfToken("UsdPrimvarReader_float2"));

            var textureSet = nif.GetBlock(shaderProp.TextureSetRef);
            var env = GameEnvironment.Typical.Skyrim(SkyrimRelease.SkyrimSE).DataFolderPath;
            
            if (!string.IsNullOrEmpty(textureSet?.Textures[0].Content))
            {
                string filePath = Path.Combine(env.Path, textureSet.Textures[0].Content);
                if(!File.Exists(filePath))
                {
                    Console.WriteLine("Diffuse texture does not exist!");
                }
                var diffuseTex = UsdShadeShader.Define(new(stage), material.GetPath().AppendChild(new("diffuse")));
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
                    diffuseTex.CreateOutput(new("a"), SdfValueTypeNames.Float);
                    shaderPrim.CreateInput(new("opacity"), SdfValueTypeNames.Float).ConnectToSource(diffuseTex.ConnectableAPI(), new("a"));
                }
            }

            if (!string.IsNullOrEmpty(textureSet?.Textures[1].Content))
            {
                string filePath = Path.Combine(env.Path, textureSet.Textures[1].Content);
                if (!File.Exists(filePath))
                {
                    Console.WriteLine("Diffuse texture does not exist!");
                }
                var normalTex = UsdShadeShader.Define(new(stage), material.GetPath().AppendChild(new("normal")));
                normalTex.CreateIdAttr(new TfToken("UsdUVTexture"));
                normalTex.CreateInput(new TfToken("file"), UsdCs.SdfGetValueTypeAsset()).Set(
                    new SdfAssetPath(filePath));
                normalTex.CreateInput(new("st"), SdfValueTypeNames.Float2).ConnectToSource(stReader.ConnectableAPI(), new("result"));
                normalTex.CreateInput(new("wrapS"), SdfValueTypeNames.Token).Set(new TfToken("repeat"));
                normalTex.CreateInput(new("wrapT"), SdfValueTypeNames.Token).Set(new TfToken("repeat"));
                normalTex.CreateOutput(new("rgb"), SdfValueTypeNames.Float3);

                shaderPrim.CreateInput(new("normal"), SdfValueTypeNames.Color3f).ConnectToSource(normalTex.ConnectableAPI(), new("rgb"));
                
                //shaderPrim.CreateInput(new("clearcoat"), SdfValueTypeNames.Float).Set(0.5f);
                //normalTex.CreateOutput(new("a"), SdfValueTypeNames.Float);
                //shaderPrim.CreateInput(new("clearcoatRoughness"), SdfValueTypeNames.Float).ConnectToSource(normalTex.ConnectableAPI(), new("a"));
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

        static void CreateCollision(UsdStage stage, SdfPath path, NifFile nif, bhkCollisionObject collisionProp)
        {
            var extraPrim = path.AppendChild(USDUtils.GetPathName(nameof(bhkCollisionObject)));
            var bhkCollPrim = UsdGeomXform.Define(new(stage), extraPrim);

            bhkCollPrim.GetPrim().CreateAttribute(new TfToken("Flags"), SdfValueTypeNames.String)
                .Set(collisionProp.Flags.ToString());

            _ = new UsdModelAPI(bhkCollPrim).SetKind(KindTokens.group);

            var collBody = collisionProp.Body;
            if (collBody != null)
            {
                if (nif.GetBlock(collBody) is bhkRigidBody body)
                {
                    GetCollisionShape(stage, bhkCollPrim.GetPath(), nif, body);
                }
            }
        }

        static void GetCollisionShape(UsdStage stage, SdfPath path, NifFile nif, bhkRigidBody collisionProp)
        {
            // Export rotation and translation info even if a rigid body isn't a "T" variant
            UsdGeomXform rigidBodyPrim = UsdGeomXform.Define(new(stage), path.AppendChild(new("rb")));
            var rbInfo = collisionProp.RigidBodyInfo_bRBCI2010;
            if (collisionProp is bhkRigidBodyT)
            {
                CreateTransform(rigidBodyPrim, rbInfo.Translation * HAVOK_MULT, rbInfo.Rotation);
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

            var shape = nif.GetBlock(collisionProp.Shape);
            if (shape != null)
            {
                GetShape(stage, rigidBodyPrim.GetPath(), nif, shape);
            }
        }

        static readonly float HAVOK_MULT = 69.9904f;
        static void GetShape(UsdStage stage, SdfPath path, NifFile nif, bhkShape shape)
        {
            if (shape is bhkListShape listShape)
            {
                UsdGeomXform rigidBodyPrim = UsdGeomXform.Define(new(stage),
                    path.AppendChild(USDUtils.GetPathName(nameof(bhkListShape))));
                for (int i = 0; i < listShape.NumSubShapes; i++)
                {
                    var childShape = nif.GetBlock<bhkShape>(listShape.SubShapes.GetBlockRef(i));
                    GetShape(stage, rigidBodyPrim.GetPath(), nif, childShape);
                }
            }
            else if (shape is bhkConvexTransformShape transform)
            {
                UsdGeomXform collPrim = UsdGeomXform.Define(new(stage),
                    path.AppendChild(USDUtils.GetPathName(nameof(bhkConvexTransformShape))));
                var trTuple = ToComponents(transform.Transform);

                GfMatrix3d rotMtx = new(trTuple.Item1);
                trTuple.Item2[0] *= HAVOK_MULT;
                trTuple.Item2[1] *= HAVOK_MULT;
                trTuple.Item2[2] *= HAVOK_MULT;
                CreateTransform(collPrim, trTuple.Item2, rotMtx, trTuple.Item3);
                var childShape = nif.GetBlock(transform.Shape);
                GetShape(stage, collPrim.GetPath(), nif, childShape);
            }
            else if (shape is bhkListShape list)
            {

            }
            else if (shape is bhkBoxShape box)
            {
                var mesh = UsdGeomMesh.Define(new(stage),
                    path.AppendChild(USDUtils.GetPathName(nameof(bhkBoxShape))));
                var extents = (box.Dimensions * HAVOK_MULT);

                USDUtils.CreateBoxMesh(mesh, extents);

                var matlPath = GetCollisionMaterial(stage, nif, box.Material.Material_SHM);
                UsdShadeMaterial matl = UsdShadeMaterial.Get(new(stage), matlPath);
                UsdShadeMaterialBindingAPI.Apply(mesh.GetPrim()).Bind(matl);
            }
            else if (shape is bhkConvexVerticesShape convex)
            {
                UsdGeomMesh mesh = UsdGeomMesh.Define(new(stage),
                    path.AppendChild(USDUtils.GetPathName(nameof(bhkConvexVerticesShape))));
                var mcApi = UsdPhysicsMeshCollisionAPI.Apply(mesh.GetPrim());
                mcApi.CreateApproximationAttr(UsdPhysicsTokens.convexDecomposition); // indicator for convex vertices shape

                GfVec3f scaling = new(69.9904f);
                mesh.AddScaleOp(UsdGeomXformOp.Precision.PrecisionFloat).GetAttr().Set(scaling);
                var matApi = UsdPhysicsMaterialAPI.Apply(mesh.GetPrim());


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

                pxr.VtIntArray faceVertexCounts = new(faceVertexIndices.size() / 3, 3);

                if (!UsdGeomMesh.ValidateTopology(faceVertexIndices, faceVertexCounts, vPoints.size(), out string reason))
                {
                    throw new Exception(reason);
                }

                pxr.VtVec3fArray normals = new();
                pxr.VtIntArray normalsIndices = new();
                for (int i = 0; i < convex.Normals.Count; i++)
                {
                    normals.push_back(new(convex.Normals[i].X, convex.Normals[i].Y, convex.Normals[i].Z));
                    normalsIndices.push_back(i);
                }

                UsdGeomPrimvar primVar = (new UsdGeomPrimvarsAPI(mesh.GetPrim())).CreatePrimvar(UsdGeomTokens.normals, SdfValueTypeNames.Normal3fArray);
                primVar.SetIndices(normalsIndices);
                primVar.GetAttr().Set(normals);

                mesh.CreateFaceVertexCountsAttr().Set(faceVertexCounts);
                mesh.CreateFaceVertexIndicesAttr().Set(faceVertexIndices);
                mesh.CreatePointsAttr().Set(vPoints);

                var matlPath = GetCollisionMaterial(stage, nif, convex.Material.Material_SHM);
                UsdShadeMaterial matl = UsdShadeMaterial.Get(new(stage), matlPath);
                UsdShadeMaterialBindingAPI.Apply(mesh.GetPrim()).Bind(matl);
            }
            else if (shape is bhkMoppBvTreeShape mopp)
            {
                UsdGeomXform moppPrim = UsdGeomXform.Define(new(stage), path.AppendChild(USDUtils.GetPathName("mopp")));
                var cms = nif.GetBlock(mopp.Shape);

                if (cms != null)
                {
                    GetShape(stage, moppPrim.GetPath(), nif, cms);
                }
            }
            else if (shape is bhkCompressedMeshShape cms)
            {
                var cmsScope = UsdGeomXform.Define(new(stage), path.AppendChild(USDUtils.GetPathName(nameof(bhkCompressedMeshShape))));
                var cmsMesh = UsdGeomMesh.Define(new(stage), cmsScope.GetPath().AppendChild(USDUtils.GetPathName(nameof(bhkCompressedMeshShapeData))));
                cmsMesh.AddScaleOp(UsdGeomXformOp.Precision.PrecisionFloat)
                    .GetAttr().Set(new GfVec3f(cms.Scale.X, cms.Scale.Y, cms.Scale.Z));

                var cmsData = nif.GetBlock(cms.Data);
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
                        var vec = bigVert * HAVOK_MULT;
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

                    foreach(var chunkVert in chunk.Vertices)
                    {
                        Vector3 vec = chunkOrigin + (new Vector3(chunkVert.X, chunkVert.Y, chunkVert.Z) / 1000.0f);
                        vec *= HAVOK_MULT;
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
                
                pxr.VtIntArray faceVertexCounts = new(faceVertexIndices.size() / 3, 3);

                if (!UsdGeomMesh.ValidateTopology(faceVertexIndices, faceVertexCounts, points.size(), out string reason))
                {
                    throw new Exception(reason);
                }

                cmsMesh.CreateFaceVertexCountsAttr().Set(faceVertexCounts);
                cmsMesh.CreateFaceVertexIndicesAttr().Set(faceVertexIndices);
                cmsMesh.CreatePointsAttr().Set(points);

                List<SdfPath> materialPaths = new();
                foreach (var matlEntry in cmsData.ChunkMaterials)
                {
                    var matlPath = GetCollisionMaterial(stage, nif, matlEntry.Material, stage.GetPrimAtPath(cmsScope.GetPath()));
                    materialPaths.Add(matlPath);
                }

                for (int i = 0; i < matlIndices.Count; i++)
                {
                    VtIntArray subsetFaces = new((uint)matlIndices[i].Count);
                    subsetFaces.CopyFromArray([.. matlIndices[i]]);
                    var subset = UsdGeomSubset.CreateUniqueGeomSubset(cmsMesh, new("Subset" + i), UsdGeomTokens.face, subsetFaces);
                    UsdShadeMaterial matl = UsdShadeMaterial.Get(new(stage), materialPaths[i]);
                    UsdShadeMaterialBindingAPI.Apply(subset.GetPrim()).Bind(matl);
                }
            }
        }

        static SdfPath GetCollisionMaterial(UsdStage stage, NifFile nif, SkyrimHavokMaterial material, UsdPrim? parent = null)
        {
            SdfPath primPath = (parent == null) ? stage.GetDefaultPrim().GetPath() : parent.GetPath();
            var defaultPrim = stage.DefinePrim(primPath.AppendChild(new("collisionMaterials")));
            var enumName = Enum.GetName(material) ?? ("Matl"+material.ToString());
            SdfPath matlPath = defaultPrim.GetPath().AppendChild(new(enumName));
            if(stage.GetPrimAtPath(matlPath))
            {
                return matlPath;
            }

            var matlPrim = UsdShadeMaterial.Define(new(stage), matlPath);
            var shaderPrim = UsdShadeShader.Define(new(stage), matlPrim.GetPath().AppendChild(new("Shader")));

            shaderPrim.CreateIdAttr(new TfToken("UsdPreviewSurface"));
            shaderPrim.CreateInput(new("useSpecularWorkflow"), SdfValueTypeNames.Int).Set(1);
            shaderPrim.CreateInput(new TfToken("opacity"), SdfValueTypeNames.Float).Set(0.1f);
            var matlColor = NifUtils.GetMaterialColor(material);
            shaderPrim.CreateInput(new TfToken("diffuseColor"), SdfValueTypeNames.Color3f).Set(new GfVec3f(matlColor.R, matlColor.G,matlColor.B));
            matlPrim.CreateOutput(new("surface"), SdfValueTypeNames.Token).ConnectToSource(shaderPrim.ConnectableAPI(), new("surface"));
            matlPrim.CreateOutput(new("displacement"), SdfValueTypeNames.Token).ConnectToSource(shaderPrim.ConnectableAPI(), new("displacement"));
            
            var physicsMatl = UsdPhysicsMaterialAPI.Apply(shaderPrim.GetPrim());

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

        static (GfQuatd,GfVec3d,GfVec3d) ToComponents(Matrix44 mtx)
        {
            Matrix4x4 matrix = new(mtx.M11, mtx.M12, mtx.M13, mtx.M41,
                                   mtx.M21, mtx.M22, mtx.M23, mtx.M42,
                                   mtx.M31, mtx.M32, mtx.M33, mtx.M43,
                                   mtx.M14, mtx.M24, mtx.M34, mtx.M44);

            if(Matrix4x4.Decompose(matrix, out Vector3 scale, out Quaternion rot, out Vector3 tr))
            {
                return (new(rot.W, rot.X, rot.Y, rot.Z), new(tr.X, tr.Y, tr.Z), new(scale.X, scale.Y, scale.Z));
            }

            throw new Exception();
        }

    }
}
