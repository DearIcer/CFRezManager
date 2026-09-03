using System.Buffers.Binary;
using System.Text;

namespace CFRezManager;

/// <summary>
/// Structural parser for LithTech Jupiter LTB models (PC D3D, file version 9, model versions 23-25).
/// Parses the full file layout: header, pieces/LODs with skin weights, the node (skeleton) tree,
/// and embedded animations. Layout follows the Monolith LTSDK loader, haekb/io_scene_lithtech
/// (reader_ltb_pc.py), and giaynhap/LTB2SMD. All reads are bounds-checked; any structural
/// inconsistency fails the parse so the caller can fall back to the heuristic decoder.
/// </summary>
internal static class LtbModelParser
{
    // Render object types stored per LOD.
    private const uint RenderObjectRigid = 4;
    private const uint RenderObjectSkeletal = 5;
    private const uint RenderObjectVertexAnimated = 6;
    private const uint RenderObjectNull = 7;

    // Vertex stream attribute masks.
    private const uint StreamPosition = 0x0001;
    private const uint StreamNormal = 0x0002;
    private const uint StreamColor = 0x0004;
    private const uint StreamUvSet1 = 0x0010;
    private const uint StreamUvSet2 = 0x0020;
    private const uint StreamUvSet3 = 0x0040;
    private const uint StreamUvSet4 = 0x0080;
    private const uint StreamBasisVectors = 0x0100;

    // Animation compression types.
    private const uint CompressionNone = 0;
    private const uint CompressionRelevant16 = 2;
    private const uint CompressionRelevant16RotOnly = 3;

    // Defensive caps for untrusted data.
    private const int MaxPieces = 4096;
    private const int MaxNodes = 8192;
    private const int MaxAnimations = 4096;
    private const int MaxKeyFrames = 1 << 18;
    private const int MaxVertices = 1 << 22;
    private const int MaxTriangles = 1 << 22;
    private const int MaxBonesPerVertex = 16;
    private const int MaxLodsPerPiece = 64;
    private const int MaxWeightSets = 4096;
    private const int MaxNodeTreeDepth = 512;
    private const int MaxStringLength = 4096;

    // The command string normally starts at offset 84, right after the 15 header counts.
    // Some CrossFire files carry 8 extra bytes before it (LTB2SMD skips u16+u16+u32 first),
    // so both candidates are tried; the piece-count check disambiguates.
    private static readonly int[] CommandStringOffsetCandidates = [84, 92];

    public static bool TryParse(
        byte[] data,
        string fallbackName,
        string storageDescription,
        int sourceByteCount,
        int decodedByteCount,
        out LithTechModelDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        if (data.Length < 86)
        {
            errorMessage = "LTB data is too short for a structural header.";
            return false;
        }

        try
        {
            return TryParseCore(data, fallbackName, storageDescription, sourceByteCount, decodedByteCount, out document, out errorMessage);
        }
        catch (Exception ex)
        {
            // Defensive: a structural parser must never take the app down on malformed data.
            document = null;
            errorMessage = $"LTB structural parse failed: {ex.Message}";
            return false;
        }
    }

    private static bool TryParseCore(
        byte[] data,
        string fallbackName,
        string storageDescription,
        int sourceByteCount,
        int decodedByteCount,
        out LithTechModelDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        var reader = new LtbReader(data);
        if (!reader.TryReadUInt16(out ushort fileType) || fileType != 1)
        {
            errorMessage = "LTB file type is not a PC D3D model.";
            return false;
        }

        if (!reader.TryReadUInt16(out ushort fileVersion) || fileVersion != 9)
        {
            errorMessage = $"Unsupported LTB file version {fileVersion}.";
            return false;
        }

        if (!reader.TrySkip(16) || !reader.TryReadUInt32(out uint modelVersion) || modelVersion < 23 || modelVersion > 25)
        {
            errorMessage = "Unsupported LTB model version.";
            return false;
        }

        if (!TryReadHeaderCounts(reader, out LtbHeaderCounts counts))
        {
            errorMessage = "LTB header counts are truncated or implausible.";
            return false;
        }

        foreach (int commandStringOffset in CommandStringOffsetCandidates)
        {
            var bodyReader = new LtbReader(data) { Position = commandStringOffset };
            if (TryParseBody(
                    bodyReader,
                    data,
                    counts,
                    modelVersion,
                    fallbackName,
                    storageDescription,
                    sourceByteCount,
                    decodedByteCount,
                    out document,
                    out string? bodyError))
            {
                return true;
            }

            // Keep the first (spec-layout) candidate's error; it is the primary interpretation.
            errorMessage ??= bodyError;
        }

        return false;
    }

    private static bool TryReadHeaderCounts(LtbReader reader, out LtbHeaderCounts counts)
    {
        counts = default;
        if (!reader.TryReadUInt32(out uint keyframeCount) ||
            !reader.TryReadUInt32(out uint animationCount) ||
            !reader.TryReadUInt32(out uint nodeCount) ||
            !reader.TryReadUInt32(out uint pieceCount) ||
            !reader.TryReadUInt32(out uint childModelCount) ||
            !reader.TryReadUInt32(out uint triCount) ||
            !reader.TryReadUInt32(out uint vertCount) ||
            !reader.TryReadUInt32(out uint vertexWeightCount) ||
            !reader.TryReadUInt32(out uint lodCount) ||
            !reader.TryReadUInt32(out uint socketCount) ||
            !reader.TryReadUInt32(out uint weightSetCount) ||
            !reader.TryReadUInt32(out uint stringCount) ||
            !reader.TryReadUInt32(out uint stringLengths) ||
            !reader.TryReadUInt32(out uint vertAnimDataSize) ||
            !reader.TryReadUInt32(out uint animDataSize))
        {
            return false;
        }

        if (nodeCount > MaxNodes ||
            pieceCount > MaxPieces ||
            animationCount > MaxAnimations ||
            triCount > MaxTriangles * 4 ||
            vertCount > MaxVertices * 4 ||
            lodCount > MaxPieces * MaxLodsPerPiece ||
            weightSetCount > MaxWeightSets)
        {
            return false;
        }

        counts = new LtbHeaderCounts(
            keyframeCount,
            animationCount,
            nodeCount,
            pieceCount,
            childModelCount,
            weightSetCount);
        return true;
    }

    private static bool TryParseBody(
        LtbReader reader,
        byte[] data,
        LtbHeaderCounts counts,
        uint modelVersion,
        string fallbackName,
        string storageDescription,
        int sourceByteCount,
        int decodedByteCount,
        out LithTechModelDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        // Command string, visibility radius and optional OBB list finish the header.
        if (!reader.TryReadString(out _) ||
            !reader.TryReadSingle(out _) ||
            !reader.TryReadUInt32(out uint obbCount))
        {
            errorMessage = "LTB command string / visibility header is truncated.";
            return false;
        }

        if (obbCount > 4096)
        {
            errorMessage = "LTB OBB count is implausible.";
            return false;
        }

        int obbSize = modelVersion <= 23 ? 64 : 68;
        if (!reader.TrySkip(checked((int)obbCount * obbSize)))
        {
            errorMessage = "LTB OBB block is truncated.";
            return false;
        }

        // Pieces. The repeated piece count must match the header; this also
        // disambiguates the command string offset candidates.
        if (!reader.TryReadUInt32(out uint pieceCount) || pieceCount != counts.PieceCount || pieceCount > MaxPieces)
        {
            errorMessage = "LTB piece count does not match the header.";
            return false;
        }

        List<string> texturePaths = LithTechModelDecoder.ExtractEmbeddedTexturePaths(data);
        var meshes = new List<LithTechMesh>((int)pieceCount);
        for (int pieceIndex = 0; pieceIndex < pieceCount; pieceIndex++)
        {
            if (!TryReadPiece(reader, counts, meshes.Count, texturePaths, out LithTechMesh? mesh, out string? pieceError))
            {
                errorMessage = pieceError;
                return false;
            }

            if (mesh is not null)
            {
                meshes.Add(mesh);
            }
        }

        if (meshes.Count == 0)
        {
            errorMessage = "LTB contains no previewable mesh geometry.";
            return false;
        }

        // Node tree (skeleton).
        if (!TryReadSkeleton(reader, counts.NodeCount, out LithTechModelSkeleton? skeleton, out errorMessage))
        {
            return false;
        }

        // Weight sets (parsed only for layout; contents are not used yet).
        if (!TrySkipWeightSets(reader, counts, out errorMessage))
        {
            return false;
        }

        // Child models (entry 0 is SELF and is not stored).
        if (!reader.TryReadUInt32(out uint childModelCount) || childModelCount > MaxPieces)
        {
            errorMessage = "LTB child model count is truncated or implausible.";
            return false;
        }

        for (uint i = 1; i < childModelCount; i++)
        {
            if (!reader.TryReadString(out _))
            {
                errorMessage = "LTB child model name is truncated.";
                return false;
            }
        }

        // Animations.
        if (!TryReadAnimations(reader, counts, out List<LithTechModelAnimation>? animations, out errorMessage))
        {
            return false;
        }

        // Cross-check bone references now that the skeleton is known.
        if (skeleton is not null && !ValidateBoneReferences(meshes, skeleton.Nodes.Count, out errorMessage))
        {
            return false;
        }

        document = new LithTechModelDocument(fallbackName, meshes, storageDescription, sourceByteCount, decodedByteCount)
        {
            Skeleton = skeleton is { Nodes.Count: > 0 } ? skeleton : null,
            Animations = animations!
        };
        return true;
    }

    private static bool TryReadPiece(
        LtbReader reader,
        LtbHeaderCounts counts,
        int meshIndex,
        IReadOnlyList<string> texturePaths,
        out LithTechMesh? mesh,
        out string? errorMessage)
    {
        mesh = null;
        errorMessage = null;

        if (!reader.TryReadString(out string pieceName))
        {
            errorMessage = $"LTB piece {meshIndex + 1} name is truncated.";
            return false;
        }

        if (!reader.TryReadUInt32(out uint lodCount) || lodCount == 0 || lodCount > MaxLodsPerPiece)
        {
            errorMessage = $"LTB piece '{pieceName}' has an implausible LOD count.";
            return false;
        }

        if (!reader.TrySkip(checked((int)lodCount * sizeof(float))) || // LOD distances
            !reader.TrySkip(sizeof(uint) * 2)) // lodMin / lodMax
        {
            errorMessage = $"LTB piece '{pieceName}' LOD header is truncated.";
            return false;
        }

        for (uint lodIndex = 0; lodIndex < lodCount; lodIndex++)
        {
            bool capture = lodIndex == 0;
            var captureData = capture ? new LodCapture() : null;
            if (!TryReadLod(reader, pieceName, lodIndex, captureData, out errorMessage))
            {
                return false;
            }

            if (captureData is { IsNull: false } && captureData.Vertices.Count > 0 && captureData.TriangleIndices.Count >= 3)
            {
                string displayName = string.IsNullOrWhiteSpace(pieceName) ? $"Mesh {meshIndex + 1}" : pieceName;
                mesh = new LithTechMesh(
                    displayName,
                    captureData.Vertices,
                    captureData.TriangleIndices,
                    captureData.TextureCoordinates is { Count: > 0 } coords && coords.Count == captureData.Vertices.Count ? coords : null,
                    LithTechModelDecoder.ResolveTexturePath(texturePaths, displayName, meshIndex))
                {
                    Skin = captureData.Skin,
                    RigidBoneIndex = captureData.RigidBoneIndex,
                    Normals = captureData.Normals is { Count: > 0 } normals && normals.Count == captureData.Vertices.Count ? normals : null
                };
            }
        }

        return true;
    }

    private static bool TryReadLod(
        LtbReader reader,
        string pieceName,
        uint lodIndex,
        LodCapture? capture,
        out string? errorMessage)
    {
        errorMessage = null;
        string lodDescription = $"piece '{pieceName}' LOD {lodIndex}";

        if (!reader.TryReadUInt32(out uint textureCount) || textureCount > 4 ||
            !reader.TrySkip(sizeof(uint) * 4) || // texture indices
            !reader.TrySkip(sizeof(uint)) || // render style
            !reader.TryReadByte(out _) || // render priority
            !reader.TryReadUInt32(out uint renderObjectType))
        {
            errorMessage = $"LTB {lodDescription} header is truncated or implausible.";
            return false;
        }

        if (renderObjectType == RenderObjectNull)
        {
            // Null LODs carry a single filler u32 plus the trailing used-node list.
            return reader.TrySkip(sizeof(uint)) && TryReadUsedNodes(reader, out errorMessage)
                ? true
                : Fail(out errorMessage, $"LTB {lodDescription} null object is truncated.");
        }

        if (renderObjectType is not (RenderObjectRigid or RenderObjectSkeletal or RenderObjectVertexAnimated))
        {
            errorMessage = $"LTB {lodDescription} has an unsupported render object type {renderObjectType}.";
            return false;
        }

        if (!reader.TryReadUInt32(out uint objSize))
        {
            errorMessage = $"LTB {lodDescription} object size is truncated.";
            return false;
        }

        int objectDataStart = reader.Position;
        if (!reader.TryReadUInt32(out uint vertCount) ||
            !reader.TryReadUInt32(out uint polyCount) ||
            !reader.TryReadUInt32(out uint maxBonesPerTri) ||
            !reader.TryReadUInt32(out uint maxBonesPerVert))
        {
            errorMessage = $"LTB {lodDescription} geometry header is truncated.";
            return false;
        }

        if (vertCount > MaxVertices || polyCount > MaxTriangles ||
            maxBonesPerTri > MaxBonesPerVertex || maxBonesPerVert > MaxBonesPerVertex)
        {
            errorMessage = $"LTB {lodDescription} geometry counts are implausible.";
            return false;
        }

        bool isSkeletal = renderObjectType == RenderObjectSkeletal;
        bool useMatrixPalettes = false;
        uint paletteMinBone = 0;
        uint[]? boneRemap = null;
        var streamFlags = new uint[4];
        int rigidBoneIndex = -1;

        switch (renderObjectType)
        {
            case RenderObjectRigid:
                if (!TryReadStreamFlags(reader, streamFlags) || !reader.TryReadUInt32(out uint boneIndex))
                {
                    errorMessage = $"LTB {lodDescription} rigid header is truncated.";
                    return false;
                }

                rigidBoneIndex = boneIndex > int.MaxValue ? -1 : (int)boneIndex;
                break;
            case RenderObjectSkeletal:
                if (!reader.TryReadByte(out byte reIndexedBones) ||
                    !TryReadStreamFlags(reader, streamFlags) ||
                    !reader.TryReadByte(out byte matrixPalettes))
                {
                    errorMessage = $"LTB {lodDescription} skeletal header is truncated.";
                    return false;
                }

                useMatrixPalettes = matrixPalettes != 0;
                if (useMatrixPalettes)
                {
                    if (!reader.TryReadUInt32(out paletteMinBone) || !reader.TryReadUInt32(out uint paletteMaxBone))
                    {
                        errorMessage = $"LTB {lodDescription} matrix palette range is truncated.";
                        return false;
                    }

                    if (paletteMinBone > paletteMaxBone || paletteMaxBone >= MaxNodes)
                    {
                        errorMessage = $"LTB {lodDescription} matrix palette range is implausible.";
                        return false;
                    }

                    if (reIndexedBones != 0)
                    {
                        if (!reader.TryReadUInt32(out uint remapCount) || remapCount > MaxNodes)
                        {
                            errorMessage = $"LTB {lodDescription} bone remap table is truncated or implausible.";
                            return false;
                        }

                        boneRemap = new uint[remapCount];
                        for (uint i = 0; i < remapCount; i++)
                        {
                            if (!reader.TryReadUInt32(out boneRemap[i]))
                            {
                                errorMessage = $"LTB {lodDescription} bone remap table is truncated.";
                                return false;
                            }
                        }
                    }
                }

                break;
            case RenderObjectVertexAnimated:
                // Unduplicated vertex count plus two dummy u32s; vertex layout matches rigid meshes.
                if (!reader.TrySkip(sizeof(uint) * 3) || !TryReadStreamFlags(reader, streamFlags))
                {
                    errorMessage = $"LTB {lodDescription} vertex-animated header is truncated.";
                    return false;
                }

                break;
        }

        if (!HasPositionStream(streamFlags))
        {
            errorMessage = $"LTB {lodDescription} has no position stream.";
            return false;
        }

        // Vertex data: four streams, each holding vertCount consecutive elements.
        int blendFloatCount = isSkeletal
            ? (int)(useMatrixPalettes ? maxBonesPerVert : maxBonesPerTri) - 1
            : 0;
        float[][]? blendWeights = capture is not null && isSkeletal ? new float[(int)vertCount][] : null;
        byte[][]? paletteBoneIndices = capture is not null && isSkeletal && useMatrixPalettes ? new byte[(int)vertCount][] : null;

        for (int stream = 0; stream < streamFlags.Length; stream++)
        {
            uint mask = streamFlags[stream];
            if (mask == 0)
            {
                continue;
            }

            if (!TryReadVertexStream(
                    reader,
                    mask,
                    (int)vertCount,
                    blendFloatCount,
                    useMatrixPalettes,
                    capture,
                    blendWeights,
                    paletteBoneIndices,
                    out errorMessage))
            {
                errorMessage = $"LTB {lodDescription} vertex stream {stream} is malformed ({errorMessage}).";
                return false;
            }
        }

        // Index buffer.
        long indexCount = (long)polyCount * 3;
        if (capture is not null)
        {
            var indices = new List<int>((int)Math.Min(indexCount, 1 << 20));
            for (long i = 0; i < indexCount; i++)
            {
                if (!reader.TryReadUInt16(out ushort index) || index >= vertCount)
                {
                    errorMessage = $"LTB {lodDescription} index buffer is truncated or out of range.";
                    return false;
                }

                indices.Add(index);
            }

            capture.TriangleIndices = indices;
        }
        else if (!reader.TrySkip(checked((int)indexCount * sizeof(ushort))))
        {
            errorMessage = $"LTB {lodDescription} index buffer is truncated.";
            return false;
        }

        // Skeletal render-direct meshes store per-range bone sets after the index buffer.
        List<LtbBoneSet>? boneSets = null;
        if (isSkeletal && !useMatrixPalettes)
        {
            if (!reader.TryReadUInt32(out uint boneSetCount) || boneSetCount > vertCount + 1)
            {
                errorMessage = $"LTB {lodDescription} bone set table is truncated or implausible.";
                return false;
            }

            boneSets = new List<LtbBoneSet>((int)boneSetCount);
            for (uint i = 0; i < boneSetCount; i++)
            {
                if (!reader.TryReadUInt16(out ushort vertStart) ||
                    !reader.TryReadUInt16(out ushort setVertCount) ||
                    !reader.TryReadByte(out byte bone0) ||
                    !reader.TryReadByte(out byte bone1) ||
                    !reader.TryReadByte(out byte bone2) ||
                    !reader.TryReadByte(out byte bone3) ||
                    !reader.TryReadUInt32(out _))
                {
                    errorMessage = $"LTB {lodDescription} bone set {i} is truncated.";
                    return false;
                }

                boneSets.Add(new LtbBoneSet(vertStart, setVertCount, bone0, bone1, bone2, bone3));
            }
        }

        // Cross-check the consumed object bytes against objSize when it looks plausible.
        if (objSize > 0)
        {
            long expectedEnd = (long)objectDataStart + objSize;
            if (expectedEnd <= reader.Length && expectedEnd != reader.Position)
            {
                errorMessage = $"LTB {lodDescription} object size mismatch (objSize {objSize}, consumed {reader.Position - objectDataStart}).";
                return false;
            }
        }

        if (!TryReadUsedNodes(reader, out errorMessage))
        {
            errorMessage = $"LTB {lodDescription} used-node list is truncated.";
            return false;
        }

        if (capture is not null)
        {
            capture.RigidBoneIndex = rigidBoneIndex;
            if (isSkeletal && blendWeights is not null &&
                !TryBuildSkin(capture, blendWeights, paletteBoneIndices, boneSets, boneRemap, paletteMinBone, useMatrixPalettes, out errorMessage))
            {
                errorMessage = $"LTB {lodDescription} skin weights are inconsistent ({errorMessage}).";
                return false;
            }
        }

        return true;
    }

    private static bool TryReadVertexStream(
        LtbReader reader,
        uint mask,
        int vertCount,
        int blendFloatCount,
        bool useMatrixPalettes,
        LodCapture? capture,
        float[][]? blendWeights,
        byte[][]? paletteBoneIndices,
        out string? errorMessage)
    {
        errorMessage = null;
        bool capturePositions = capture is not null && (mask & StreamPosition) != 0;
        bool captureNormals = capture is not null && (mask & StreamNormal) != 0;
        bool captureUv = capture is not null && (mask & StreamUvSet1) != 0;
        if (capturePositions)
        {
            capture!.Vertices = new List<LithTechVector3>(vertCount);
        }

        if (captureNormals)
        {
            capture!.Normals = new List<LithTechVector3>(vertCount);
        }

        if (captureUv)
        {
            capture!.TextureCoordinates = new List<LithTechVector2>(vertCount);
        }

        for (int vertex = 0; vertex < vertCount; vertex++)
        {
            if ((mask & StreamPosition) != 0)
            {
                if (!reader.TryReadSingle(out float x) ||
                    !reader.TryReadSingle(out float y) ||
                    !reader.TryReadSingle(out float z))
                {
                    errorMessage = $"position for vertex {vertex} is truncated";
                    return false;
                }

                if (capturePositions)
                {
                    capture!.Vertices.Add(new LithTechVector3(x, y, z));
                }

                if (blendFloatCount > 0)
                {
                    var blends = new float[blendFloatCount];
                    for (int blend = 0; blend < blendFloatCount; blend++)
                    {
                        if (!reader.TryReadSingle(out blends[blend]))
                        {
                            errorMessage = $"blend weights for vertex {vertex} are truncated";
                            return false;
                        }
                    }

                    if (blendWeights is not null)
                    {
                        blendWeights[vertex] = blends;
                    }
                }

                if (useMatrixPalettes)
                {
                    var bones = new byte[4];
                    for (int bone = 0; bone < bones.Length; bone++)
                    {
                        if (!reader.TryReadByte(out bones[bone]))
                        {
                            errorMessage = $"bone indices for vertex {vertex} are truncated";
                            return false;
                        }
                    }

                    if (paletteBoneIndices is not null)
                    {
                        paletteBoneIndices[vertex] = bones;
                    }
                }
            }

            if ((mask & StreamNormal) != 0)
            {
                if (!reader.TryReadSingle(out float nx) ||
                    !reader.TryReadSingle(out float ny) ||
                    !reader.TryReadSingle(out float nz))
                {
                    errorMessage = $"normal for vertex {vertex} is truncated";
                    return false;
                }

                if (captureNormals)
                {
                    capture!.Normals!.Add(new LithTechVector3(nx, ny, nz));
                }
            }

            if ((mask & StreamColor) != 0 && !reader.TrySkip(sizeof(uint)))
            {
                errorMessage = $"color for vertex {vertex} is truncated";
                return false;
            }

            for (uint uvSet = StreamUvSet1; uvSet <= StreamUvSet4; uvSet <<= 1)
            {
                if ((mask & uvSet) == 0)
                {
                    continue;
                }

                if (!reader.TryReadSingle(out float u) || !reader.TryReadSingle(out float v))
                {
                    errorMessage = $"texture coordinates for vertex {vertex} are truncated";
                    return false;
                }

                if (captureUv && uvSet == StreamUvSet1)
                {
                    capture!.TextureCoordinates!.Add(new LithTechVector2(u, v));
                }
            }

            if ((mask & StreamBasisVectors) != 0 && !reader.TrySkip(sizeof(float) * 6))
            {
                errorMessage = $"basis vectors for vertex {vertex} are truncated";
                return false;
            }
        }

        return true;
    }

    private static bool TryBuildSkin(
        LodCapture capture,
        float[][] blendWeights,
        byte[][]? paletteBoneIndices,
        List<LtbBoneSet>? boneSets,
        uint[]? boneRemap,
        uint paletteMinBone,
        bool useMatrixPalettes,
        out string? errorMessage)
    {
        errorMessage = null;
        int vertCount = blendWeights.Length;
        var skins = new LithTechVertexSkin[vertCount];

        if (useMatrixPalettes)
        {
            for (int vertex = 0; vertex < vertCount; vertex++)
            {
                float[] blends = blendWeights[vertex] ?? [];
                byte[] bones = paletteBoneIndices?[vertex] ?? [LithTechVertexSkin.UnusedBone, 0, 0, 0];
                if (!TryResolveSkinWeights(blends, 4, skins, vertex, slot => ResolvePaletteBone(bones[slot], boneRemap, paletteMinBone), out errorMessage))
                {
                    return false;
                }
            }
        }
        else
        {
            // Bone sets assign a 4-slot bone list to each vertex range.
            var vertexBones = new byte[vertCount][];
            foreach (LtbBoneSet set in boneSets!)
            {
                int end = Math.Min(vertCount, set.VertStart + set.VertCount);
                for (int vertex = set.VertStart; vertex < end; vertex++)
                {
                    vertexBones[vertex] = set.Bones;
                }
            }

            for (int vertex = 0; vertex < vertCount; vertex++)
            {
                byte[]? bones = vertexBones[vertex];
                if (bones is null)
                {
                    errorMessage = $"vertex {vertex} is not covered by any bone set";
                    return false;
                }

                float[] blends = blendWeights[vertex] ?? [];
                if (!TryResolveSkinWeights(blends, blends.Length + 1, skins, vertex, slot => slot < bones.Length ? bones[slot] : LithTechVertexSkin.UnusedBone, out errorMessage))
                {
                    return false;
                }
            }
        }

        capture.Skin = skins;
        return true;
    }

    private static int ResolvePaletteBone(byte rawIndex, uint[]? boneRemap, uint paletteMinBone)
    {
        if (rawIndex == LithTechVertexSkin.UnusedBone)
        {
            return -1;
        }

        if (boneRemap is not null)
        {
            return rawIndex < boneRemap.Length ? (int)boneRemap[rawIndex] : -1;
        }

        return (int)(paletteMinBone + rawIndex);
    }

    private static bool TryResolveSkinWeights(
        float[] blends,
        int weightCount,
        LithTechVertexSkin[] skins,
        int vertex,
        Func<int, int> boneForSlot,
        out string? errorMessage)
    {
        errorMessage = null;

        // The stored blend weights map to the FIRST bone slots in order;
        // the LAST slot's weight is implicit (1 minus the stored sum).
        // Verified against io_scene_lithtech and the RF016 sample set.
        var weights = new float[Math.Min(weightCount, 4)];
        if (weights.Length == 0)
        {
            skins[vertex] = LithTechVertexSkin.Single(0);
            return true;
        }

        float storedSum = 0;
        for (int i = 0; i < weights.Length - 1; i++)
        {
            float blend = i < blends.Length ? blends[i] : 0;
            weights[i] = blend;
            storedSum += blend;
        }

        weights[^1] = MathF.Max(0, 1.0f - storedSum);

        var bones = new byte[4];
        var finalWeights = new float[4];
        int influenceCount = 0;
        float total = 0;
        for (int slot = 0; slot < weights.Length; slot++)
        {
            int bone = boneForSlot(slot);
            if (bone < 0 || bone > 254)
            {
                continue;
            }

            bones[influenceCount] = (byte)bone;
            finalWeights[influenceCount] = weights[slot];
            total += weights[slot];
            influenceCount++;
        }

        if (influenceCount == 0 || total <= 0)
        {
            errorMessage = $"vertex {vertex} has no valid bone influences";
            return false;
        }

        for (int i = 0; i < influenceCount; i++)
        {
            finalWeights[i] /= total;
        }

        for (int i = influenceCount; i < 4; i++)
        {
            bones[i] = LithTechVertexSkin.UnusedBone;
            finalWeights[i] = 0;
        }

        skins[vertex] = new LithTechVertexSkin(
            bones[0], finalWeights[0],
            bones[1], finalWeights[1],
            bones[2], finalWeights[2],
            bones[3], finalWeights[3]);
        return true;
    }

    private static bool TryReadUsedNodes(LtbReader reader, out string? errorMessage)
    {
        errorMessage = null;
        if (!reader.TryReadByte(out byte usedNodeCount) || !reader.TrySkip(usedNodeCount))
        {
            errorMessage = "used-node list is truncated";
            return false;
        }

        return true;
    }

    private static bool TryReadSkeleton(
        LtbReader reader,
        uint nodeCount,
        out LithTechModelSkeleton? skeleton,
        out string? errorMessage)
    {
        skeleton = null;
        errorMessage = null;

        if (nodeCount == 0)
        {
            skeleton = new LithTechModelSkeleton([]);
            return true;
        }

        var nodes = new List<LithTechModelNode>((int)nodeCount);
        if (!TryReadNode(reader, -1, nodes, 0, nodeCount, out errorMessage))
        {
            return false;
        }

        if (nodes.Count != (int)nodeCount)
        {
            errorMessage = $"LTB node tree ended after {nodes.Count} nodes, expected {nodeCount}.";
            return false;
        }

        skeleton = new LithTechModelSkeleton(nodes);
        return true;
    }

    private static bool TryReadNode(
        LtbReader reader,
        int parentIndex,
        List<LithTechModelNode> nodes,
        int depth,
        uint expectedNodeCount,
        out string? errorMessage)
    {
        errorMessage = null;
        if (depth > MaxNodeTreeDepth || nodes.Count >= expectedNodeCount)
        {
            errorMessage = "LTB node tree is deeper or larger than the header allows.";
            return false;
        }

        if (!reader.TryReadString(out string name) ||
            !reader.TryReadUInt16(out _) || // node index (redundant with preorder position)
            !reader.TryReadByte(out _)) // flags
        {
            errorMessage = "LTB node record is truncated.";
            return false;
        }

        var transform = new double[16];
        for (int i = 0; i < transform.Length; i++)
        {
            if (!reader.TryReadSingle(out float value))
            {
                errorMessage = $"LTB node '{name}' transform is truncated.";
                return false;
            }

            transform[i] = value;
        }

        if (!reader.TryReadUInt32(out uint childCount) || childCount > expectedNodeCount)
        {
            errorMessage = $"LTB node '{name}' has an implausible child count.";
            return false;
        }

        int ownIndex = nodes.Count;
        nodes.Add(new LithTechModelNode(name, parentIndex, transform));
        for (uint i = 0; i < childCount; i++)
        {
            if (!TryReadNode(reader, ownIndex, nodes, depth + 1, expectedNodeCount, out errorMessage))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TrySkipWeightSets(LtbReader reader, LtbHeaderCounts counts, out string? errorMessage)
    {
        errorMessage = null;
        if (!reader.TryReadUInt32(out uint weightSetCount) || weightSetCount > MaxWeightSets)
        {
            errorMessage = "LTB weight set count is truncated or implausible.";
            return false;
        }

        for (uint set = 0; set < weightSetCount; set++)
        {
            if (!reader.TryReadString(out _) || !reader.TryReadUInt32(out uint weightCount) || weightCount > MaxNodes)
            {
                errorMessage = $"LTB weight set {set} is truncated or implausible.";
                return false;
            }

            if (!reader.TrySkip(checked((int)weightCount * sizeof(float))))
            {
                errorMessage = $"LTB weight set {set} weights are truncated.";
                return false;
            }
        }

        return true;
    }

    private static bool TryReadAnimations(
        LtbReader reader,
        LtbHeaderCounts counts,
        out List<LithTechModelAnimation>? animations,
        out string? errorMessage)
    {
        animations = [];
        errorMessage = null;

        if (!reader.TryReadUInt32(out uint animationCount) || animationCount > MaxAnimations)
        {
            errorMessage = "LTB animation count is truncated or implausible.";
            return false;
        }

        var result = new List<LithTechModelAnimation>((int)animationCount);
        for (uint animIndex = 0; animIndex < animationCount; animIndex++)
        {
            if (!TryReadAnimation(reader, counts, animIndex, out LithTechModelAnimation? animation, out errorMessage))
            {
                return false;
            }

            result.Add(animation!);
        }

        animations = result;
        return true;
    }

    private static bool TryReadAnimation(
        LtbReader reader,
        LtbHeaderCounts counts,
        uint animIndex,
        out LithTechModelAnimation? animation,
        out string? errorMessage)
    {
        animation = null;
        errorMessage = null;

        if (!reader.TrySkip(sizeof(float) * 3) || // bounding dims
            !reader.TryReadString(out string name) ||
            !reader.TryReadUInt32(out uint compressionType) || compressionType > CompressionRelevant16RotOnly ||
            !reader.TryReadUInt32(out _) || // interpolation time (ms)
            !reader.TryReadUInt32(out uint keyFrameCount) || keyFrameCount > MaxKeyFrames)
        {
            errorMessage = $"LTB animation {animIndex} header is truncated or implausible.";
            return false;
        }

        var keyTimes = new double[keyFrameCount];
        for (uint key = 0; key < keyFrameCount; key++)
        {
            if (!reader.TryReadUInt32(out uint timeMs) || !reader.TryReadString(out _))
            {
                errorMessage = $"LTB animation '{name}' keyframe table is truncated.";
                return false;
            }

            keyTimes[key] = timeMs / 1000.0;
        }

        var channels = new LithTechNodeChannel[counts.NodeCount];
        for (uint node = 0; node < counts.NodeCount; node++)
        {
            if (!TryReadAnimationChannel(reader, compressionType, (int)keyFrameCount, name, node, out LithTechNodeChannel? channel, out errorMessage))
            {
                return false;
            }

            channels[node] = channel!;
        }

        animation = new LithTechModelAnimation(name, keyTimes, channels);
        return true;
    }

    private static bool TryReadAnimationChannel(
        LtbReader reader,
        uint compressionType,
        int keyFrameCount,
        string animationName,
        uint nodeIndex,
        out LithTechNodeChannel? channel,
        out string? errorMessage)
    {
        channel = null;
        errorMessage = null;

        if (compressionType == CompressionNone)
        {
            if (!reader.TryReadByte(out byte isVertexAnim))
            {
                errorMessage = $"LTB animation '{animationName}' channel {nodeIndex} is truncated.";
                return false;
            }

            if (isVertexAnim != 0)
            {
                // Vertex animation: per keyframe a vertex count plus vec3 deltas; contents are discarded.
                for (int key = 0; key < keyFrameCount; key++)
                {
                    if (!reader.TryReadUInt32(out uint vertCount) || vertCount > MaxVertices ||
                        !reader.TrySkip(checked((int)vertCount * sizeof(float) * 3)))
                    {
                        errorMessage = $"LTB animation '{animationName}' vertex channel {nodeIndex} is truncated.";
                        return false;
                    }
                }

                channel = new LithTechNodeChannel(null, null);
                return true;
            }

            if (!TryReadUncompressedChannel(reader, keyFrameCount, out channel))
            {
                errorMessage = $"LTB animation '{animationName}' channel {nodeIndex} is truncated.";
                return false;
            }

            return true;
        }

        // Relevant (RLE) compression: a subset of keys, expanded by repeating the last value.
        if (!reader.TryReadUInt32(out uint numPos) || numPos > keyFrameCount)
        {
            errorMessage = $"LTB animation '{animationName}' channel {nodeIndex} position count is implausible.";
            return false;
        }

        var positions = new LithTechVector3[numPos];
        for (uint i = 0; i < numPos; i++)
        {
            if (!TryReadChannelPosition(reader, compressionType, out positions[i]))
            {
                errorMessage = $"LTB animation '{animationName}' channel {nodeIndex} positions are truncated.";
                return false;
            }
        }

        if (!reader.TryReadUInt32(out uint numQuat) || numQuat > keyFrameCount)
        {
            errorMessage = $"LTB animation '{animationName}' channel {nodeIndex} rotation count is implausible.";
            return false;
        }

        var rotations = new LithTechQuaternion[numQuat];
        for (uint i = 0; i < numQuat; i++)
        {
            if (!TryReadChannelRotation(reader, compressionType, out rotations[i]))
            {
                errorMessage = $"LTB animation '{animationName}' channel {nodeIndex} rotations are truncated.";
                return false;
            }
        }

        channel = new LithTechNodeChannel(
            ExpandChannel(positions, keyFrameCount, new LithTechVector3(0, 0, 0)),
            ExpandChannel(rotations, keyFrameCount, new LithTechQuaternion(0, 0, 0, 1)));
        return true;
    }

    private static bool TryReadUncompressedChannel(LtbReader reader, int keyFrameCount, out LithTechNodeChannel? channel)
    {
        channel = null;
        var positions = new LithTechVector3[keyFrameCount];
        for (int key = 0; key < keyFrameCount; key++)
        {
            if (!reader.TryReadSingle(out float x) ||
                !reader.TryReadSingle(out float y) ||
                !reader.TryReadSingle(out float z))
            {
                return false;
            }

            positions[key] = new LithTechVector3(x, y, z);
        }

        var rotations = new LithTechQuaternion[keyFrameCount];
        for (int key = 0; key < keyFrameCount; key++)
        {
            if (!reader.TryReadSingle(out float x) ||
                !reader.TryReadSingle(out float y) ||
                !reader.TryReadSingle(out float z) ||
                !reader.TryReadSingle(out float w))
            {
                return false;
            }

            rotations[key] = new LithTechQuaternion(x, y, z, w);
        }

        channel = new LithTechNodeChannel(positions, rotations);
        return true;
    }

    private static bool TryReadChannelPosition(LtbReader reader, uint compressionType, out LithTechVector3 position)
    {
        position = default;
        if (compressionType == CompressionRelevant16)
        {
            if (!reader.TryReadInt16(out short x) ||
                !reader.TryReadInt16(out short y) ||
                !reader.TryReadInt16(out short z))
            {
                return false;
            }

            position = new LithTechVector3(x / 16.0, y / 16.0, z / 16.0);
            return true;
        }

        if (!reader.TryReadSingle(out float fx) ||
            !reader.TryReadSingle(out float fy) ||
            !reader.TryReadSingle(out float fz))
        {
            return false;
        }

        position = new LithTechVector3(fx, fy, fz);
        return true;
    }

    private static bool TryReadChannelRotation(LtbReader reader, uint compressionType, out LithTechQuaternion rotation)
    {
        rotation = default;
        if (compressionType is CompressionRelevant16 or CompressionRelevant16RotOnly)
        {
            if (!reader.TryReadInt16(out short x) ||
                !reader.TryReadInt16(out short y) ||
                !reader.TryReadInt16(out short z) ||
                !reader.TryReadInt16(out short w))
            {
                return false;
            }

            rotation = new LithTechQuaternion(x / 32767.0, y / 32767.0, z / 32767.0, w / 32767.0);
            return true;
        }

        if (!reader.TryReadSingle(out float fx) ||
            !reader.TryReadSingle(out float fy) ||
            !reader.TryReadSingle(out float fz) ||
            !reader.TryReadSingle(out float fw))
        {
            return false;
        }

        rotation = new LithTechQuaternion(fx, fy, fz, fw);
        return true;
    }

    private static T[] ExpandChannel<T>(T[] keys, int keyFrameCount, T emptyValue)
    {
        if (keys.Length == keyFrameCount)
        {
            return keys;
        }

        var expanded = new T[keyFrameCount];
        T last = keys.Length > 0 ? keys[0] : emptyValue;
        for (int i = 0; i < keyFrameCount; i++)
        {
            if (i < keys.Length)
            {
                last = keys[i];
            }

            expanded[i] = last;
        }

        return expanded;
    }

    private static bool ValidateBoneReferences(List<LithTechMesh> meshes, int nodeCount, out string? errorMessage)
    {
        errorMessage = null;
        foreach (LithTechMesh mesh in meshes)
        {
            if (mesh.RigidBoneIndex >= nodeCount)
            {
                errorMessage = $"Mesh '{mesh.Name}' references rigid bone {mesh.RigidBoneIndex} but the skeleton has {nodeCount} nodes.";
                return false;
            }

            if (mesh.Skin is null)
            {
                continue;
            }

            foreach (LithTechVertexSkin skin in mesh.Skin)
            {
                if ((skin.Bone0 != LithTechVertexSkin.UnusedBone && skin.Bone0 >= nodeCount) ||
                    (skin.Bone1 != LithTechVertexSkin.UnusedBone && skin.Bone1 >= nodeCount) ||
                    (skin.Bone2 != LithTechVertexSkin.UnusedBone && skin.Bone2 >= nodeCount) ||
                    (skin.Bone3 != LithTechVertexSkin.UnusedBone && skin.Bone3 >= nodeCount))
                {
                    errorMessage = $"Mesh '{mesh.Name}' references a bone outside the skeleton.";
                    return false;
                }
            }
        }

        return true;
    }

    private static bool HasPositionStream(uint[] streamFlags)
    {
        foreach (uint mask in streamFlags)
        {
            if ((mask & StreamPosition) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadStreamFlags(LtbReader reader, uint[] streamFlags)
    {
        for (int i = 0; i < streamFlags.Length; i++)
        {
            if (!reader.TryReadUInt32(out streamFlags[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Fail(out string? errorMessage, string message)
    {
        errorMessage = message;
        return false;
    }

    private readonly record struct LtbHeaderCounts(
        uint KeyframeCount,
        uint AnimationCount,
        uint NodeCount,
        uint PieceCount,
        uint ChildModelCount,
        uint WeightSetCount);

    private sealed class LodCapture
    {
        public bool IsNull { get; init; }
        public List<LithTechVector3> Vertices { get; set; } = [];
        public List<LithTechVector3>? Normals { get; set; }
        public List<LithTechVector2>? TextureCoordinates { get; set; }
        public List<int> TriangleIndices { get; set; } = [];
        public LithTechVertexSkin[]? Skin { get; set; }
        public int RigidBoneIndex { get; set; } = -1;
    }

    private sealed class LtbBoneSet(ushort vertStart, ushort vertCount, byte bone0, byte bone1, byte bone2, byte bone3)
    {
        public ushort VertStart { get; } = vertStart;
        public ushort VertCount { get; } = vertCount;
        public byte[] Bones { get; } = [bone0, bone1, bone2, bone3];
    }

    /// <summary>
    /// Bounds-checked little-endian cursor over the LTB byte buffer.
    /// Every read returns false instead of throwing when data runs out.
    /// </summary>
    private sealed class LtbReader(byte[] data)
    {
        private readonly byte[] _data = data;

        public int Position { get; set; }

        public int Length => _data.Length;

        private int Remaining => _data.Length - Position;

        public bool TrySkip(int count)
        {
            if (count < 0 || count > Remaining)
            {
                return false;
            }

            Position += count;
            return true;
        }

        public bool TryReadByte(out byte value)
        {
            value = 0;
            if (Remaining < sizeof(byte))
            {
                return false;
            }

            value = _data[Position];
            Position += sizeof(byte);
            return true;
        }

        public bool TryReadUInt16(out ushort value)
        {
            value = 0;
            if (Remaining < sizeof(ushort))
            {
                return false;
            }

            value = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Position, sizeof(ushort)));
            Position += sizeof(ushort);
            return true;
        }

        public bool TryReadInt16(out short value)
        {
            value = 0;
            if (Remaining < sizeof(short))
            {
                return false;
            }

            value = BinaryPrimitives.ReadInt16LittleEndian(_data.AsSpan(Position, sizeof(short)));
            Position += sizeof(short);
            return true;
        }

        public bool TryReadUInt32(out uint value)
        {
            value = 0;
            if (Remaining < sizeof(uint))
            {
                return false;
            }

            value = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(Position, sizeof(uint)));
            Position += sizeof(uint);
            return true;
        }

        public bool TryReadSingle(out float value)
        {
            value = 0;
            if (Remaining < sizeof(float))
            {
                return false;
            }

            value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(Position, sizeof(float))));
            Position += sizeof(float);
            return true;
        }

        // LTB strings: u16 byte length + ASCII bytes, no NUL terminator.
        // Some writers append trailing control bytes (e.g. 0x01) to names; strip them.
        private static readonly char[] TrailingControlChars =
            ['\x00', '\x01', '\x02', '\x03', '\x04', '\x05', '\x06', '\x07',
             '\x08', '\x09', '\x0B', '\x0C', '\x0E', '\x0F',
             '\x10', '\x11', '\x12', '\x13', '\x14', '\x15', '\x16', '\x17',
             '\x18', '\x19', '\x1A', '\x1B', '\x1C', '\x1D', '\x1E', '\x1F'];

        public bool TryReadString(out string value)
        {
            value = string.Empty;
            if (!TryReadUInt16(out ushort length) || length > MaxStringLength || length > Remaining)
            {
                return false;
            }

            value = Encoding.ASCII.GetString(_data, Position, length).TrimEnd(TrailingControlChars);
            Position += length;
            return true;
        }
    }
}
