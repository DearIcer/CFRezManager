using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CFRezManager;

// Writes the custom binary animation package (.cfan) consumed by the Unity preview viewer.
// The package carries skeleton bind poses, skinned meshes, and animation tracks for models
// the structural LTB parser fully decoded; the viewer rebuilds a SkinnedMeshRenderer and
// runtime AnimationClips from it. All data stays in the source coordinate space — the
// viewer applies the same Z flip as the OBJ loader.
internal static class LithTechAnimPackageExporter
{
    private const uint FormatVersion = 1;

    public static bool Export(string packagePath, LithTechObjExportSource source)
    {
        LithTechModelDocument document = source.Document;
        if (document.Skeleton is null || document.Animations.Count == 0)
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(packagePath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new BinaryWriter(stream, Encoding.UTF8);

            writer.Write((byte)'C');
            writer.Write((byte)'F');
            writer.Write((byte)'R');
            writer.Write((byte)'A');
            writer.Write(FormatVersion);

            WriteSkeleton(writer, document.Skeleton);
            WriteMeshes(writer, document, source, Path.GetDirectoryName(packagePath) ?? string.Empty);
            WriteAnimations(writer, document);
            return true;
        }
        catch
        {
            try
            {
                if (File.Exists(packagePath))
                {
                    File.Delete(packagePath);
                }
            }
            catch
            {
                // Best-effort cleanup of a partial package.
            }

            return false;
        }
    }

    private static void WriteSkeleton(BinaryWriter writer, LithTechModelSkeleton skeleton)
    {
        IReadOnlyList<LithTechModelNode> nodes = skeleton.Nodes;
        writer.Write(nodes.Count);

        var globals = new double[nodes.Count][];
        var namesPerParent = new Dictionary<int, HashSet<string>>();
        for (int i = 0; i < nodes.Count; i++)
        {
            LithTechModelNode node = nodes[i];
            globals[i] = node.GlobalTransform;

            string name = string.IsNullOrWhiteSpace(node.Name) ? $"node_{i}" : node.Name;
            if (!namesPerParent.TryGetValue(node.ParentIndex, out HashSet<string>? usedNames))
            {
                usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                namesPerParent[node.ParentIndex] = usedNames;
            }

            name = MakeUniqueName(name, usedNames);

            double[] local = node.ParentIndex >= 0 && node.ParentIndex < i
                ? Multiply(InvertAffine(globals[node.ParentIndex]), globals[i])
                : globals[i];
            DecomposeTrs(local, out double[] translation, out double[] rotation, out double[] scale);
            double[] bindPose = InvertAffine(globals[i]);

            WriteString(writer, name);
            writer.Write(node.ParentIndex);
            WriteVector3(writer, translation);
            WriteQuaternion(writer, rotation);
            WriteVector3(writer, scale);
            for (int element = 0; element < 16; element++)
            {
                writer.Write((float)bindPose[element]);
            }
        }
    }

    private static void WriteMeshes(BinaryWriter writer, LithTechModelDocument document, LithTechObjExportSource source, string directoryPath)
    {
        int nodeCount = document.Skeleton!.Nodes.Count;
        writer.Write(document.Meshes.Count);

        for (int meshIndex = 0; meshIndex < document.Meshes.Count; meshIndex++)
        {
            LithTechMesh mesh = document.Meshes[meshIndex];
            WriteString(writer, mesh.Name ?? $"mesh_{meshIndex}");
            WriteString(writer, ExportMeshTexture(mesh, source, directoryPath, meshIndex));

            writer.Write(mesh.Vertices.Count);
            writer.Write(mesh.TriangleIndices.Count);

            foreach (LithTechVector3 vertex in mesh.Vertices)
            {
                writer.Write((float)vertex.X);
                writer.Write((float)vertex.Y);
                writer.Write((float)vertex.Z);
            }

            bool hasNormals = mesh.HasNormals;
            writer.Write(hasNormals);
            if (hasNormals)
            {
                foreach (LithTechVector3 normal in mesh.Normals!)
                {
                    writer.Write((float)normal.X);
                    writer.Write((float)normal.Y);
                    writer.Write((float)normal.Z);
                }
            }

            bool hasUVs = mesh.HasTextureCoordinates;
            writer.Write(hasUVs);
            if (hasUVs)
            {
                foreach (LithTechVector2 uv in mesh.TextureCoordinates!)
                {
                    writer.Write((float)uv.X);
                    // Match the OBJ exporter's V flip so textures land the same way.
                    writer.Write((float)(1.0 - uv.Y));
                }
            }

            foreach (int index in mesh.TriangleIndices)
            {
                writer.Write(index);
            }

            for (int vertexIndex = 0; vertexIndex < mesh.Vertices.Count; vertexIndex++)
            {
                LithTechVertexSkin skin = ResolveVertexSkin(mesh, vertexIndex, nodeCount);
                WriteVertexSkin(writer, skin);
            }
        }
    }

    private static string ExportMeshTexture(LithTechMesh mesh, LithTechObjExportSource source, string directoryPath, int meshIndex)
    {
        try
        {
            BitmapSource? bitmap = LithTechObjExporter.ResolveMeshTexture(mesh, source, out _);
            if (bitmap is null)
            {
                return string.Empty;
            }

            string fileName = $"cfan_tex_{meshIndex}.png";
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(directoryPath, fileName));
            encoder.Save(stream);
            return fileName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static LithTechVertexSkin ResolveVertexSkin(LithTechMesh mesh, int vertexIndex, int nodeCount)
    {
        if (mesh.Skin is not null && vertexIndex < mesh.Skin.Count)
        {
            return ClampSkin(mesh.Skin[vertexIndex], nodeCount);
        }

        int bone = mesh.RigidBoneIndex >= 0 && mesh.RigidBoneIndex < nodeCount ? mesh.RigidBoneIndex : 0;
        return LithTechVertexSkin.Single((byte)bone);
    }

    private static LithTechVertexSkin ClampSkin(LithTechVertexSkin skin, int nodeCount)
    {
        static byte Clamp(byte bone, int count) => bone < count ? bone : LithTechVertexSkin.UnusedBone;
        var clamped = new LithTechVertexSkin(
            Clamp(skin.Bone0, nodeCount), skin.Weight0,
            Clamp(skin.Bone1, nodeCount), skin.Weight1,
            Clamp(skin.Bone2, nodeCount), skin.Weight2,
            Clamp(skin.Bone3, nodeCount), skin.Weight3);
        return clamped.Bone0 == LithTechVertexSkin.UnusedBone ? LithTechVertexSkin.Single(0) : clamped;
    }

    private static void WriteVertexSkin(BinaryWriter writer, LithTechVertexSkin skin)
    {
        static byte SafeBone(byte bone) => bone == LithTechVertexSkin.UnusedBone ? (byte)0 : bone;
        writer.Write(SafeBone(skin.Bone0));
        writer.Write(SafeBone(skin.Bone1));
        writer.Write(SafeBone(skin.Bone2));
        writer.Write(SafeBone(skin.Bone3));
        writer.Write(skin.Bone0 == LithTechVertexSkin.UnusedBone ? 0f : skin.Weight0);
        writer.Write(skin.Bone1 == LithTechVertexSkin.UnusedBone ? 0f : skin.Weight1);
        writer.Write(skin.Bone2 == LithTechVertexSkin.UnusedBone ? 0f : skin.Weight2);
        writer.Write(skin.Bone3 == LithTechVertexSkin.UnusedBone ? 0f : skin.Weight3);
    }

    private static void WriteAnimations(BinaryWriter writer, LithTechModelDocument document)
    {
        int nodeCount = document.Skeleton!.Nodes.Count;
        writer.Write(document.Animations.Count);

        foreach (LithTechModelAnimation animation in document.Animations)
        {
            WriteString(writer, animation.Name ?? "animation");
            int keyCount = animation.KeyTimesSeconds.Count;
            writer.Write(keyCount);
            foreach (double time in animation.KeyTimesSeconds)
            {
                writer.Write((float)time);
            }

            for (int nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++)
            {
                LithTechNodeChannel? channel = nodeIndex < animation.Channels.Count ? animation.Channels[nodeIndex] : null;
                bool hasPositions = channel?.Positions is not null && channel.Positions.Count == keyCount;
                bool hasRotations = channel?.Rotations is not null && channel.Rotations.Count == keyCount;
                writer.Write(hasPositions);
                writer.Write(hasRotations);

                if (hasPositions)
                {
                    foreach (LithTechVector3 position in channel!.Positions!)
                    {
                        writer.Write((float)position.X);
                        writer.Write((float)position.Y);
                        writer.Write((float)position.Z);
                    }
                }

                if (hasRotations)
                {
                    foreach (LithTechQuaternion rotation in channel!.Rotations!)
                    {
                        writer.Write((float)rotation.X);
                        writer.Write((float)rotation.Y);
                        writer.Write((float)rotation.Z);
                        writer.Write((float)rotation.W);
                    }
                }
            }
        }
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteVector3(BinaryWriter writer, double[] value)
    {
        writer.Write((float)value[0]);
        writer.Write((float)value[1]);
        writer.Write((float)value[2]);
    }

    private static void WriteQuaternion(BinaryWriter writer, double[] value)
    {
        writer.Write((float)value[0]);
        writer.Write((float)value[1]);
        writer.Write((float)value[2]);
        writer.Write((float)value[3]);
    }

    private static string MakeUniqueName(string baseName, HashSet<string> usedNames)
    {
        string candidate = baseName;
        int suffix = 2;
        while (!usedNames.Add(candidate))
        {
            candidate = string.Format(CultureInfo.InvariantCulture, "{0}_{1}", baseName, suffix);
            suffix++;
        }

        return candidate;
    }

    // Row-major 4x4 helpers, translation column at [3], [7], [11] — same layout as LithTechModelNode.GlobalTransform.

    private static double[] Multiply(double[] a, double[] b)
    {
        var result = new double[16];
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                double sum = 0;
                for (int k = 0; k < 4; k++)
                {
                    sum += a[row * 4 + k] * b[k * 4 + column];
                }

                result[row * 4 + column] = sum;
            }
        }

        return result;
    }

    private static double[] InvertAffine(double[] m)
    {
        double a = m[0], b = m[1], c = m[2];
        double d = m[4], e = m[5], f = m[6];
        double g = m[8], h = m[9], i = m[10];
        double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        if (Math.Abs(det) < 1e-12)
        {
            det = 1e-12;
        }

        double invDet = 1.0 / det;
        var result = new double[16];
        result[0] = (e * i - f * h) * invDet;
        result[1] = (c * h - b * i) * invDet;
        result[2] = (b * f - c * e) * invDet;
        result[4] = (f * g - d * i) * invDet;
        result[5] = (a * i - c * g) * invDet;
        result[6] = (c * d - a * f) * invDet;
        result[8] = (d * h - e * g) * invDet;
        result[9] = (b * g - a * h) * invDet;
        result[10] = (a * e - b * d) * invDet;
        result[3] = -(result[0] * m[3] + result[1] * m[7] + result[2] * m[11]);
        result[7] = -(result[4] * m[3] + result[5] * m[7] + result[6] * m[11]);
        result[11] = -(result[8] * m[3] + result[9] * m[7] + result[10] * m[11]);
        result[15] = 1.0;
        return result;
    }

    private static void DecomposeTrs(double[] m, out double[] translation, out double[] rotation, out double[] scale)
    {
        translation = [m[3], m[7], m[11]];

        double sx = ColumnLength(m, 0);
        double sy = ColumnLength(m, 1);
        double sz = ColumnLength(m, 2);

        double det =
            m[0] * (m[5] * m[10] - m[6] * m[9]) -
            m[1] * (m[4] * m[10] - m[6] * m[8]) +
            m[2] * (m[4] * m[9] - m[5] * m[8]);
        if (det < 0)
        {
            sx = -sx;
        }

        scale = [sx, sy, sz];

        double r00 = m[0] / sx, r01 = m[1] / sy, r02 = m[2] / sz;
        double r10 = m[4] / sx, r11 = m[5] / sy, r12 = m[6] / sz;
        double r20 = m[8] / sx, r21 = m[9] / sy, r22 = m[10] / sz;
        rotation = MatrixToQuaternion(r00, r01, r02, r10, r11, r12, r20, r21, r22);
    }

    private static double ColumnLength(double[] m, int column)
    {
        double x = m[column];
        double y = m[4 + column];
        double z = m[8 + column];
        return Math.Sqrt(x * x + y * y + z * z);
    }

    private static double[] MatrixToQuaternion(
        double r00, double r01, double r02,
        double r10, double r11, double r12,
        double r20, double r21, double r22)
    {
        double trace = r00 + r11 + r22;
        double x, y, z, w;
        if (trace > 0)
        {
            double s = Math.Sqrt(trace + 1.0) * 2.0;
            w = 0.25 * s;
            x = (r21 - r12) / s;
            y = (r02 - r20) / s;
            z = (r10 - r01) / s;
        }
        else if (r00 > r11 && r00 > r22)
        {
            double s = Math.Sqrt(1.0 + r00 - r11 - r22) * 2.0;
            w = (r21 - r12) / s;
            x = 0.25 * s;
            y = (r01 + r10) / s;
            z = (r02 + r20) / s;
        }
        else if (r11 > r22)
        {
            double s = Math.Sqrt(1.0 + r11 - r00 - r22) * 2.0;
            w = (r02 - r20) / s;
            x = (r01 + r10) / s;
            y = 0.25 * s;
            z = (r12 + r21) / s;
        }
        else
        {
            double s = Math.Sqrt(1.0 + r22 - r00 - r11) * 2.0;
            w = (r10 - r01) / s;
            x = (r02 + r20) / s;
            y = (r12 + r21) / s;
            z = 0.25 * s;
        }

        double length = Math.Sqrt(x * x + y * y + z * z + w * w);
        if (length < 1e-12)
        {
            return [0, 0, 0, 1];
        }

        return [x / length, y / length, z / length, w / length];
    }
}
