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
