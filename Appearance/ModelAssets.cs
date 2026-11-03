using System.Security.Cryptography;
using System.Text.Json;
using UnityEngine;

namespace Modstoke.Virginia;

internal static class VirginiaModelAssets
{
    internal const string MotionHash = "762EBC059B1F152572AC2263CCD6EC672BDB1E6E6D2942187AD91691DACD321C";
    internal const string DressHash = "4F3EAC45C9AD631F23CF7BB95B52F134FA6B07E3170C07BA9737516DC3846306";

    internal static byte[] Read(string name, string hash)
    {
        var directory = Path.GetDirectoryName(typeof(VirginiaModelAssets).Assembly.Location)!;
        var path = Path.Combine(directory, "assets", name);
        using var stream = File.OpenRead(path);
        if (stream.Length is < 1 or > 33554432)
            throw new InvalidDataException("Virginia asset exceeds its supported size.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        // The authoring export is paired with this build, including bone order and facial frames.
        if (Convert.ToHexString(SHA256.HashData(bytes)) != hash)
            throw new InvalidDataException("Virginia asset does not match this build.");
        return bytes;
    }

    internal static MotionAsset Load()
    {
        var asset = JsonSerializer.Deserialize<MotionAsset>(Read("virginia-presentation.json", MotionHash),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, MaxDepth = 32 });
        if (asset is null || asset.Schema != "virginia-motion-1"
            || asset.Controls.Length != 4 || asset.Meshes.Length != 2)
            throw new InvalidDataException("Unsupported Virginia motion asset.");
        return asset;
    }

    internal static Vector3 V3(float[] v) => new(v[0], v[1], v[2]);
    private static Vector4 V4(float[] v) => new(v[0], v[1], v[2], v[3]);
    internal static Matrix4x4 Matrix(float[][] rows) => new(
        new(rows[0][0], rows[1][0], rows[2][0], rows[3][0]),
        new(rows[0][1], rows[1][1], rows[2][1], rows[3][1]),
        new(rows[0][2], rows[1][2], rows[2][2], rows[3][2]),
        new(rows[0][3], rows[1][3], rows[2][3], rows[3][3]));

    internal static Mesh Create(MeshSpec spec)
    {
        var mesh = new Mesh { name = spec.Name };
        try
        {
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.vertices = spec.Positions.Select(V3).ToArray();
            mesh.normals = spec.Normals.Select(V3).ToArray();
            mesh.tangents = spec.Tangents.Select(V4).ToArray();
            mesh.uv = spec.UvChannels[0].Select(v => new Vector2(v[0], v[1])).ToArray();
            if (spec.Colors is { Length: > 0 })
                mesh.colors = spec.Colors.Select(v => new Color(v[0], v[1], v[2], v[3])).ToArray();
            mesh.bindposes = spec.BindPoses.Select(Matrix).ToArray();
            var weights = new BoneWeight[spec.Positions.Length];
            for (var i = 0; i < weights.Length; i++)
            {
                var indices = spec.BoneIndices[i];
                var w = spec.BoneWeights[i];
                weights[i] = new BoneWeight
                {
                    boneIndex0 = indices[0], boneIndex1 = indices[1],
                    boneIndex2 = indices[2], boneIndex3 = indices[3],
                    weight0 = w[0], weight1 = w[1], weight2 = w[2], weight3 = w[3]
                };
            }
            mesh.boneWeights = weights;
            mesh.subMeshCount = spec.Submeshes.Length;
            for (var i = 0; i < spec.Submeshes.Length; i++)
                mesh.SetTriangles(spec.Submeshes[i].Triangles.SelectMany(t => t).ToArray(), i, false);
            foreach (var shape in spec.BlendShapes.Concat(spec.AuthoredShapes))
            {
                foreach (var frame in shape.Frames)
                {
                    var positions = new Vector3[weights.Length];
                    var normals = new Vector3[weights.Length];
                    var tangents = new Vector3[weights.Length];
                    for (var i = 0; i < frame.VertexIndices.Length; i++)
                    {
                        var vertex = frame.VertexIndices[i];
                        positions[vertex] = V3(frame.PositionDeltas[i]);
                        normals[vertex] = V3(frame.NormalDeltas[i]);
                        tangents[vertex] = V3(frame.TangentDeltas[i]);
                    }
                    mesh.AddBlendShapeFrame(shape.Name, frame.Weight, positions, normals, tangents);
                }
            }
            mesh.RecalculateBounds();
            return mesh;
        }
        catch
        {
            UnityEngine.Object.Destroy(mesh);
            throw;
        }
    }

    internal sealed class MotionAsset
    {
        public string Schema { get; set; } = "";
        public ControlSpec[] Controls { get; set; } = [];
        public MeshSpec[] Meshes { get; set; } = [];
    }

    internal sealed class ControlSpec
    {
        public string Name { get; set; } = "";
        public string ParentPath { get; set; } = "";
        public float[] AnchorLocal { get; set; } = [];
        public float[] BoneLocal { get; set; } = [];
        public float[] TipLocal { get; set; } = [];
    }

    internal sealed class MeshSpec
    {
        public string Name { get; set; } = "";
        public int OriginalVertexCount { get; set; }
        public int OriginalBoneCount { get; set; }
        public int OriginalBlendShapeCount { get; set; }
        public float[][] Positions { get; set; } = [];
        public float[][] Normals { get; set; } = [];
        public float[][] Tangents { get; set; } = [];
        public float[][]? Colors { get; set; }
        public float[][][] UvChannels { get; set; } = [];
        public float[][][] BindPoses { get; set; } = [];
        public int[][] BoneIndices { get; set; } = [];
        public float[][] BoneWeights { get; set; } = [];
        public BoneSpec[] Bones { get; set; } = [];
        public SubmeshSpec[] Submeshes { get; set; } = [];
        public ShapeSpec[] BlendShapes { get; set; } = [];
        public ShapeSpec[] AuthoredShapes { get; set; } = [];
    }

    internal sealed class BoneSpec
    {
        public string NormalizedSourcePath { get; set; } = "";
    }
    internal sealed class SubmeshSpec { public int[][] Triangles { get; set; } = []; }
    internal sealed class ShapeSpec
    {
        public string Name { get; set; } = "";
        public FrameSpec[] Frames { get; set; } = [];
    }
    internal sealed class FrameSpec
    {
        public float Weight { get; set; }
        public int[] VertexIndices { get; set; } = [];
        public float[][] PositionDeltas { get; set; } = [];
        public float[][] NormalDeltas { get; set; } = [];
        public float[][] TangentDeltas { get; set; } = [];
    }
}
