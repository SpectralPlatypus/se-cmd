using pxr;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SECmd.Utils
{
    public static class USDUtils
    {
        static int count = 0;
        public static TfToken GetPathName(string prefix) => new($"{prefix}_{count++}");

        public static TfToken SanitizeName(string name)
        {
            return new(Regex.Replace(name, @"[^A-Za-z0-9_]+", "_"));
        }

        public static void CreateBoxMesh(UsdGeomMesh mesh, Vector3 extents)
        {
            Vector3[] vertices = {
                    new Vector3 (-1, -1, -1),
                    new Vector3 (1, -1, -1),
                    new Vector3 (1, 1, -1),
                    new Vector3 (-1, 1, -1),
                    new Vector3 (-1, 1, 1),
                    new Vector3 (1, 1, 1),
                    new Vector3 (1, -1, 1),
                    new Vector3 (-1, -1, 1),
                };

            int[] triangles = {
                   0, 2, 1, //face front
	                0, 3, 2,
                   2, 3, 4, //face top
	                2, 4, 5,
                   1, 2, 5, //face right
	                1, 5, 6,
                   0, 7, 4, //face left
	                0, 4, 3,
                   5, 4, 7, //face back
	                5, 7, 6,
                   0, 6, 7, //face bottom
	                0, 1, 6
                };

            pxr.VtVec3fArray points = new();
            pxr.VtIntArray faceVertexIndices = new();
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] *= extents;
                points.push_back(new(vertices[i].X, vertices[i].Y, vertices[i].Z));
            }

            foreach (var tri in triangles)
            {
                faceVertexIndices.push_back(tri);
            }

            pxr.VtIntArray faceVertexCounts = new(faceVertexIndices.size() / 3, 3);

            if (!UsdGeomMesh.ValidateTopology(faceVertexIndices, faceVertexCounts, points.size(), out string reason))
            {
                throw new Exception(reason);
            }

            mesh.CreateFaceVertexCountsAttr().Set(faceVertexCounts);
            mesh.CreateFaceVertexIndicesAttr().Set(faceVertexIndices);
            mesh.CreatePointsAttr().Set(points);
        }

        public static void CreateStringAttribute(UsdPrim prim, string key, string value)
        {
            prim.CreateAttribute(new TfToken(key), SdfValueTypeNames.String)
                .Set(value);
        }
        public static void CreateFloatAttribute(UsdPrim prim, string key, float value)
        {
            prim.CreateAttribute(new TfToken(key), SdfValueTypeNames.Float)
                .Set(value);
        }

        internal static SdfValueTypeName GetSdfType(object val) => val switch
        {
            int => SdfValueTypeNames.Int,
            float => SdfValueTypeNames.Float,
            string => SdfValueTypeNames.String,
            GfVec3f => SdfValueTypeNames.Vector3f,
            _ => throw new Exception("Unknown type")
        };

        internal static Matrix4x4 ToMatrix4x4(this GfMatrix4d mtx)
        {
            Matrix4x4 mat = new();
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    mat[i, j] = (float)mtx.GetRow(i)[j];

            return mat;
        }

        internal static GfMatrix4d ToGfMatrix4d(this Matrix4x4 mtx)
        {
            GfMatrix4d mat = new();
            for (int i = 0; i < 4; i++)
                mat.SetRow(i, new(mtx[i, 0], mtx[i, 1], mtx[i, 2], mtx[i, 3]));

            return mat;
        }
    }

}
