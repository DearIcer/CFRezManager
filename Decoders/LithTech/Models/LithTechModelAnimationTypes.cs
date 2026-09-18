namespace CFRezManager;

// Skeleton node parsed from an LTB node tree. ParentIndex is -1 for the root.
// GlobalTransform is a row-major 4x4 matrix (16 elements, translation column at [3], [7], [11]).
public sealed record LithTechModelNode(string Name, int ParentIndex, double[] GlobalTransform);

public sealed record LithTechModelSkeleton(IReadOnlyList<LithTechModelNode> Nodes);

// One animation: dense per-node channels, expanded to one entry per keyframe.
public sealed record LithTechModelAnimation(
    string Name,
    IReadOnlyList<double> KeyTimesSeconds,
    IReadOnlyList<LithTechNodeChannel> Channels);

// Dense per-keyframe channel data for a single node. Both lists are null when the
// channel is entirely absent (vertex animation or no data), otherwise each has
// exactly KeyTimesSeconds.Count entries.
public sealed record LithTechNodeChannel(
    IReadOnlyList<LithTechVector3>? Positions,
    IReadOnlyList<LithTechQuaternion>? Rotations);

public readonly record struct LithTechQuaternion(double X, double Y, double Z, double W);

// Per-vertex skin weights: up to 4 bone influences, 255 marks an unused slot.
public readonly record struct LithTechVertexSkin(
    byte Bone0,
    float Weight0,
    byte Bone1,
    float Weight1,
    byte Bone2,
    float Weight2,
    byte Bone3,
    float Weight3)
{
    public const byte UnusedBone = 255;

    public static LithTechVertexSkin Single(byte bone)
    {
        return new LithTechVertexSkin(bone, 1.0f, UnusedBone, 0.0f, UnusedBone, 0.0f, UnusedBone, 0.0f);
    }

    public int InfluenceCount
    {
        get
        {
            if (Bone0 == UnusedBone)
            {
                return 0;
            }

            if (Bone1 == UnusedBone)
            {
                return 1;
            }

            if (Bone2 == UnusedBone)
            {
                return 2;
            }

            return Bone3 == UnusedBone ? 3 : 4;
        }
    }
}

internal static class LithTechModelSkeletonExtensions
{
    /// <summary>
    /// True for root nodes whose bind transform is a coordinate conversion rather than plain
    /// identity (the LithTech "Scene Root" is typically a 180° Y flip). Animation channels of
    /// such a root's children are stored in model space (they equal the stored node globals),
    /// so exporters must pin the root to identity during playback instead of composing its
    /// channel — otherwise the root transform is applied twice and the model swings around.
    /// Identity roots keep their channel untouched so genuine root motion is preserved.
    /// </summary>
    public static bool IsCoordinateConversionRoot(this LithTechModelSkeleton skeleton, int nodeIndex)
    {
        LithTechModelNode node = skeleton.Nodes[nodeIndex];
        if (node.ParentIndex >= 0)
        {
            return false;
        }

        double[] g = node.GlobalTransform;
        return Math.Abs(g[0] - 1.0) > 1e-3 ||
               Math.Abs(g[5] - 1.0) > 1e-3 ||
               Math.Abs(g[10] - 1.0) > 1e-3 ||
               Math.Abs(g[1]) > 1e-3 || Math.Abs(g[2]) > 1e-3 ||
               Math.Abs(g[4]) > 1e-3 || Math.Abs(g[6]) > 1e-3 ||
               Math.Abs(g[8]) > 1e-3 || Math.Abs(g[9]) > 1e-3 ||
               Math.Abs(g[3]) > 1e-3 || Math.Abs(g[7]) > 1e-3 || Math.Abs(g[11]) > 1e-3;
    }
}
