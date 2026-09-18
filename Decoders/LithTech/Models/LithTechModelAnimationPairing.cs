using System.IO;

namespace CFRezManager;

/// <summary>
/// Pairs animation-only LTB documents (e.g. PV-Kukri_Beast.ltb, which carries the animation
/// set) with skeleton-only variant documents (PV-Kukri_Beast_WOMAN_BL.ltb, the rig actually
/// used per character variant). A variant file usually has zero animations of its own; when
/// a sibling file with the same family base name (variant suffixes stripped) contains
/// animations, those animations are attached to the variant document so preview/FBX export
/// of the variant is self-contained.
/// Channels are copied verbatim (matched by node name), NOT retargeted: the game applies the
/// same absolute animation to every variant, and each variant file's mesh + inverse bind
/// matrices are authored self-consistently for exactly that data — e.g. the WOMAN kukri
/// rebinds Box01 ~140° away from the male bind, but its knife mesh compensates, so absolute
/// playback lands the blade in the hand identically for both variants.
/// </summary>
internal static class LithTechModelAnimationPairing
{
    /// <summary>
    /// Returns <paramref name="document"/> unchanged when it has animations already or has no
    /// skeleton; otherwise looks for a sibling animation file and returns a copy of the
    /// document with the sibling's animations attached. <paramref name="siblingFileBytes"/>
    /// resolves a file name in the same folder/archive directory to its bytes (null when
    /// absent); it must never throw.
    /// </summary>
    public static LithTechModelDocument WithSiblingAnimations(
        LithTechModelDocument document,
        string selfFileName,
        Func<string, byte[]?> siblingFileBytes)
    {
        if (document.Skeleton is null || document.Animations.Count > 0)
        {
            return document;
        }

        string extension = Path.GetExtension(selfFileName);
        string selfStem = Path.GetFileNameWithoutExtension(selfFileName);
        if (string.IsNullOrWhiteSpace(selfStem) || string.IsNullOrWhiteSpace(extension))
        {
            return document;
        }

        foreach (string stem in LithTechObjExporter.StripModelVariantSuffixes(selfStem))
        {
            if (string.Equals(stem, selfStem, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            byte[]? data;
            try
            {
                data = siblingFileBytes(stem + extension);
            }
            catch
            {
                continue;
            }

            if (data is null)
            {
                continue;
            }

            if (!LithTechModelDecoder.TryDecode(data, stem, extension.TrimStart('.'), out LithTechModelDocument? source, out _) ||
                source?.Skeleton is null ||
                source.Animations.Count == 0)
            {
                continue;
            }

            IReadOnlyList<LithTechModelAnimation>? merged = MergeAnimations(document.Skeleton, source);
            if (merged is not null)
            {
                return document with { Animations = merged };
            }
        }

        return document;
    }

    /// <summary>
    /// Copies the animations of <paramref name="animationSource"/> onto the target skeleton,
    /// matching channels by node name (channel order may differ between the two files).
    /// Nodes without a source counterpart keep no channel (they stay at the bind pose).
    /// Returns null when no node names match.
    /// </summary>
    public static IReadOnlyList<LithTechModelAnimation>? MergeAnimations(
        LithTechModelSkeleton targetSkeleton,
        LithTechModelDocument animationSource)
    {
        if (animationSource.Skeleton is not { } sourceSkeleton || animationSource.Animations.Count == 0)
        {
            return null;
        }

        var sourceIndexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < sourceSkeleton.Nodes.Count; i++)
        {
            sourceIndexByName.TryAdd(sourceSkeleton.Nodes[i].Name, i);
        }

        bool anyMatch = targetSkeleton.Nodes.Any(node => sourceIndexByName.ContainsKey(node.Name));
        if (!anyMatch)
        {
            return null;
        }

        var result = new List<LithTechModelAnimation>(animationSource.Animations.Count);
        foreach (LithTechModelAnimation animation in animationSource.Animations)
        {
            var channels = new LithTechNodeChannel[targetSkeleton.Nodes.Count];
            for (int targetIndex = 0; targetIndex < targetSkeleton.Nodes.Count; targetIndex++)
            {
                if (sourceIndexByName.TryGetValue(targetSkeleton.Nodes[targetIndex].Name, out int sourceIndex) &&
                    sourceIndex < animation.Channels.Count &&
                    animation.Channels[sourceIndex] is { } channel)
                {
                    channels[targetIndex] = channel;
                }
            }

            result.Add(new LithTechModelAnimation(animation.Name, animation.KeyTimesSeconds, channels));
        }

        return result;
    }
}
