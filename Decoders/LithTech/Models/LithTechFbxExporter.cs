using System.IO;
using System.Windows.Media.Imaging;

namespace CFRezManager;

/// <summary>
/// Exports a decoded <see cref="LithTechModelDocument"/> (geometry, UVs, skeleton, skinning,
/// and every embedded animation as a separate FBX stack) to an FBX 7.4 binary file.
/// No centering or scaling is applied, but X is mirrored (with winding reversed) because
/// LithTech model data is left-handed while FBX importers assume right-handed coordinates.
/// When a <see cref="LithTechObjExportSource"/> with resolvers is supplied, resolved mesh
/// textures are embedded as PNG Video/Texture objects so the FBX is self-contained.
/// Returns the number of distinct textures embedded in the file.
/// </summary>
internal static class LithTechFbxExporter
{
    // FBX time unit: 1 second = 46186158000 KTime units.
    private const long KTimePerSecond = 46186158000L;

    // KeyAttrFlags value for linear interpolation (matches Blender's FBX exporter).
    private const int LinearKeyAttrFlags = 24840;

    public static int Export(string fbxPath, string modelName, LithTechModelDocument document, LithTechObjExportSource? textureSource = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrWhiteSpace(fbxPath))
        {
            throw new ArgumentException("An FBX output path is required.", nameof(fbxPath));
        }

        if (document.Meshes.Count == 0)
        {
            throw new ArgumentException("The model document contains no meshes.", nameof(document));
        }

        string fullPath = Path.GetFullPath(fbxPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new SceneBuilder(document, textureSource);
        builder.Build(fullPath);
        return builder.EmbeddedTextureCount;
    }

    private sealed class SceneBuilder(LithTechModelDocument document, LithTechObjExportSource? textureSource)
    {
        private long _nextId = 1;
        private readonly List<FbxNode> _objects = [];
        private readonly List<FbxNode> _connections = [];
        private readonly Dictionary<string, int> _definitionCounts = new(StringComparer.Ordinal);

        // Embedded texture dedupe: one Texture/Video pair per distinct bitmap or resolved path.
        private readonly Dictionary<BitmapSource, long> _textureIdByBitmap = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, long> _textureIdByReference = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _usedTextureNames = new(StringComparer.OrdinalIgnoreCase);

        // Bone Model ids captured during skeleton construction; indexed by skeleton node.
        private long[]? _boneModelIds;

        private LithTechModelSkeleton? Skeleton => document.Skeleton;

        public int EmbeddedTextureCount => _textureIdByBitmap.Count;

        private long NextId()
        {
            return _nextId++;
        }

        public void Build(string fbxPath)
        {
            var topLevel = new List<FbxNode>
            {
                BuildHeaderExtension(),
                BuildGlobalSettings(),
                BuildDocuments(),
                new FbxNode("References")
            };

            BuildSceneObjects();

            if (Skeleton is not null)
            {
                BuildAnimations();
            }

            topLevel.Add(BuildDefinitions());
            var objectsNode = new FbxNode("Objects");
            foreach (FbxNode node in _objects)
            {
                objectsNode.AddChild(node);
            }

            topLevel.Add(objectsNode);
            var connectionsNode = new FbxNode("Connections");
            foreach (FbxNode connection in _connections)
            {
                connectionsNode.AddChild(connection);
            }

            topLevel.Add(connectionsNode);
            if (Skeleton is not null && document.Animations.Count > 0)
            {
                topLevel.Add(BuildTakes());
            }

            FbxBinaryWriter.Write(fbxPath, topLevel);
        }

        private FbxNode BuildHeaderExtension()
        {
            var header = new FbxNode("FBXHeaderExtension");
            header.AddChild(new FbxNode("FBXHeaderVersion", 1003));
            header.AddChild(new FbxNode("FBXVersion", FbxBinaryWriter.Version7400));
            var timeStamp = new FbxNode("CreationTimeStamp");
            timeStamp.AddChild(new FbxNode("Version", 111));
            timeStamp.AddChild(new FbxNode("Year", 1970));
            timeStamp.AddChild(new FbxNode("Month", 1));
            timeStamp.AddChild(new FbxNode("Day", 1));
            timeStamp.AddChild(new FbxNode("Hour", 10));
            timeStamp.AddChild(new FbxNode("Minute", 0));
            timeStamp.AddChild(new FbxNode("Second", 0));
            timeStamp.AddChild(new FbxNode("Millisecond", 0));
            header.AddChild(timeStamp);
            header.AddChild(new FbxNode("Creator", "CFRezManager"));
            header.AddChild(new FbxNode("CreationTime", FbxBinaryWriter.FixedCreationTime));
            header.AddChild(new FbxNode("FileId", FbxBinaryWriter.FixedFileId));
            return header;
        }

        private static FbxNode BuildGlobalSettings()
        {
            var settings = new FbxNode("GlobalSettings");
            settings.AddChild(new FbxNode("Version", 1000));
            var properties = settings.AddChild(new FbxNode("Properties70"));
            properties.AddChild(P("UpAxis", "int", "Integer", "", 1));
            properties.AddChild(P("UpAxisSign", "int", "Integer", "", 1));
            properties.AddChild(P("FrontAxis", "int", "Integer", "", 2));
            properties.AddChild(P("FrontAxisSign", "int", "Integer", "", 1));
            properties.AddChild(P("CoordAxis", "int", "Integer", "", 0));
            properties.AddChild(P("CoordAxisSign", "int", "Integer", "", 1));
            properties.AddChild(P("OriginalUpAxis", "int", "Integer", "", 1));
            properties.AddChild(P("OriginalUpAxisSign", "int", "Integer", "", 1));
            properties.AddChild(P("UnitScaleFactor", "double", "Number", "", 1.0));
            properties.AddChild(P("OriginalUnitScaleFactor", "double", "Number", "", 1.0));
            properties.AddChild(P("TimeMode", "enum", "", "", 6));
            properties.AddChild(P("CustomFrameRate", "double", "Number", "", -1.0));
            return settings;
        }

        private FbxNode BuildDocuments()
        {
            var documents = new FbxNode("Documents");
            documents.AddChild(new FbxNode("Count", 1));
            var doc = documents.AddChild(new FbxNode("Document", NextId(), "Scene\u0000\u0001Document", "Scene"));
            var properties = doc.AddChild(new FbxNode("Properties70"));
            properties.AddChild(P("SourceObject", "object", "", ""));
            doc.AddChild(new FbxNode("RootNode", 0L));
            return documents;
        }

        private FbxNode BuildDefinitions()
        {
            var definitions = new FbxNode("Definitions");
            definitions.AddChild(new FbxNode("Version", 100));
            definitions.AddChild(new FbxNode("Count", _definitionCounts.Values.Sum()));
            foreach ((string objectType, int count) in _definitionCounts)
            {
                var entry = definitions.AddChild(new FbxNode("ObjectType", objectType));
                entry.AddChild(new FbxNode("Count", count));
            }

            return definitions;
        }

        private void BuildSceneObjects()
        {
            // Skeleton first so bone Model ids exist when clusters and animations reference them.
            long[]? boneModelIds = null;
            double[][]? boneGlobals = null;
            if (Skeleton is { } skeleton)
            {
                boneModelIds = BuildSkeleton(skeleton, out boneGlobals);
                _boneModelIds = boneModelIds;
            }

            foreach (LithTechMesh mesh in document.Meshes)
            {
                BuildMesh(mesh, boneModelIds, boneGlobals);
            }
        }

        private long[] BuildSkeleton(LithTechModelSkeleton skeleton, out double[][] globals)
        {
            var nodes = skeleton.Nodes;
            globals = new double[nodes.Count][];
            var locals = new double[nodes.Count][];
            for (int i = 0; i < nodes.Count; i++)
            {
                // Conjugate by the X mirror (M·G·M) so bind poses match the mirrored geometry.
                globals[i] = MirrorTransformX(nodes[i].GlobalTransform);
                int parent = nodes[i].ParentIndex;
                locals[i] = parent >= 0 && parent < i
                    ? Multiply(Invert(globals[parent]) ?? CreateIdentity(), globals[i])
                    : globals[i];
            }

            var modelIds = new long[nodes.Count];
            for (int i = 0; i < nodes.Count; i++)
            {
                string nodeName = string.IsNullOrWhiteSpace(nodes[i].Name) ? $"Node{i}" : nodes[i].Name;
                long attributeId = NextId();
                _objects.Add(new FbxNode("NodeAttribute", attributeId, $"{nodeName}\u0000\u0001NodeAttribute", "LimbNode"));
                CountDefinition("NodeAttribute");

                long modelId = NextId();
                modelIds[i] = modelId;
                var model = new FbxNode("Model", modelId, $"{nodeName}\u0000\u0001Model", "LimbNode");
                model.AddChild(new FbxNode("Version", 232));
                DecomposeTrs(locals[i], out double[] translation, out double[] rotation, out double[] scale);
                var properties = model.AddChild(new FbxNode("Properties70"));
                properties.AddChild(P("Lcl Translation", "Lcl Translation", "", "A", translation[0], translation[1], translation[2]));
                properties.AddChild(P("Lcl Rotation", "Lcl Rotation", "", "A", rotation[0], rotation[1], rotation[2]));
                properties.AddChild(P("Lcl Scaling", "Lcl Scaling", "", "A", scale[0], scale[1], scale[2]));
                _objects.Add(model);
                CountDefinition("Model");

                _connections.Add(C("OO", attributeId, modelId));
                int parent = nodes[i].ParentIndex;
                _connections.Add(C("OO", modelId, parent >= 0 && parent < i ? modelIds[parent] : 0L));
            }

            return modelIds;
        }

        private void BuildMesh(LithTechMesh mesh, long[]? boneModelIds, double[][]? boneGlobals)
        {
            string meshName = string.IsNullOrWhiteSpace(mesh.Name) ? "Mesh" : mesh.Name;

            long geometryId = NextId();
            var geometry = new FbxNode("Geometry", geometryId, $"{meshName}\u0000\u0001Geometry", "Mesh");
            geometry.AddChild(new FbxNode("GeometryVersion", 224));

            var vertices = new double[mesh.Vertices.Count * 3];
            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                LithTechVector3 vertex = mesh.Vertices[i];
                // LithTech models are authored left-handed (they render correctly in Unity);
                // FBX importers are right-handed, so X is mirrored and the winding reversed.
                vertices[i * 3] = -vertex.X;
                vertices[i * 3 + 1] = vertex.Y;
                vertices[i * 3 + 2] = vertex.Z;
            }

            geometry.AddChild(new FbxNode("Vertices", vertices));

            // Reversed winding keeps faces front-facing after the X mirror.
            var windingIndices = new int[mesh.TriangleIndices.Count];
            int winding = 0;
            for (; winding + 2 < mesh.TriangleIndices.Count; winding += 3)
            {
                windingIndices[winding] = mesh.TriangleIndices[winding];
                windingIndices[winding + 1] = mesh.TriangleIndices[winding + 2];
                windingIndices[winding + 2] = mesh.TriangleIndices[winding + 1];
            }

            for (; winding < mesh.TriangleIndices.Count; winding++)
            {
                windingIndices[winding] = mesh.TriangleIndices[winding];
            }

            var polygonIndices = new int[windingIndices.Length];
            for (int i = 0; i < polygonIndices.Length; i++)
            {
                int index = windingIndices[i];
                // The last index of each triangle is XOR-negated as the polygon terminator.
                polygonIndices[i] = i % 3 == 2 ? -index - 1 : index;
            }

            geometry.AddChild(new FbxNode("PolygonVertexIndex", polygonIndices));

            // Normals: prefer the stream decoded from the source model; fall back to angle-weighted smooth vertex normals so
            // importers (which otherwise default to flat per-face shading) do not render faceted geometry.
            IReadOnlyList<LithTechVector3> meshNormals = mesh.HasNormals
                ? mesh.Normals!
                : ComputeSmoothNormals(mesh);
            var normals = new double[meshNormals.Count * 3];
            for (int i = 0; i < meshNormals.Count; i++)
            {
                LithTechVector3 normal = NormalizeOrDefault(meshNormals[i]);
                normals[i * 3] = -normal.X; // mirrored with the geometry
                normals[i * 3 + 1] = normal.Y;
                normals[i * 3 + 2] = normal.Z;
            }

            var normalLayer = new FbxNode("LayerElementNormal", 0);
            normalLayer.AddChild(new FbxNode("Version", 101));
            normalLayer.AddChild(new FbxNode("Name", ""));
            normalLayer.AddChild(new FbxNode("MappingInformationType", "ByPolygonVertex"));
            normalLayer.AddChild(new FbxNode("ReferenceInformationType", "IndexToDirect"));
            normalLayer.AddChild(new FbxNode("Normals", normals));
            normalLayer.AddChild(new FbxNode("NormalsIndex", windingIndices));
            geometry.AddChild(normalLayer);

            if (mesh.HasTextureCoordinates && mesh.TextureCoordinates is not null)
            {
                var uvs = new double[mesh.TextureCoordinates.Count * 2];
                for (int i = 0; i < mesh.TextureCoordinates.Count; i++)
                {
                    LithTechVector2 uv = mesh.TextureCoordinates[i];
                    uvs[i * 2] = uv.X;
                    uvs[i * 2 + 1] = 1.0 - uv.Y; // V flip, matching the OBJ exporter.
                }

                var uvLayer = new FbxNode("LayerElementUV", 0);
                uvLayer.AddChild(new FbxNode("Version", 101));
                uvLayer.AddChild(new FbxNode("Name", "UVMap"));
                // ByPolygonVertex + IndexToDirect is the layout FBX importers actually accept
                // (Blender drops ByVertex/Direct layers with a warning). UVs stay per-vertex
                // values, indexed per polygon corner through the triangle index list.
                uvLayer.AddChild(new FbxNode("MappingInformationType", "ByPolygonVertex"));
                uvLayer.AddChild(new FbxNode("ReferenceInformationType", "IndexToDirect"));
                uvLayer.AddChild(new FbxNode("UV", uvs));
                uvLayer.AddChild(new FbxNode("UVIndex", windingIndices));
                geometry.AddChild(uvLayer);
            }

            var materialLayer = new FbxNode("LayerElementMaterial", 0);
            materialLayer.AddChild(new FbxNode("Version", 101));
            materialLayer.AddChild(new FbxNode("Name", ""));
            materialLayer.AddChild(new FbxNode("MappingInformationType", "AllSame"));
            materialLayer.AddChild(new FbxNode("ReferenceInformationType", "IndexToDirect"));
            materialLayer.AddChild(new FbxNode("Materials", new[] { 0 }));
            geometry.AddChild(materialLayer);
            _objects.Add(geometry);
            CountDefinition("Geometry");

            long modelId = NextId();
            var model = new FbxNode("Model", modelId, $"{meshName}\u0000\u0001Model", "Mesh");
            model.AddChild(new FbxNode("Version", 232));
            var properties = model.AddChild(new FbxNode("Properties70"));
            properties.AddChild(P("Lcl Translation", "Lcl Translation", "", "A", 0.0, 0.0, 0.0));
            properties.AddChild(P("Lcl Rotation", "Lcl Rotation", "", "A", 0.0, 0.0, 0.0));
            properties.AddChild(P("Lcl Scaling", "Lcl Scaling", "", "A", 1.0, 1.0, 1.0));
            _objects.Add(model);
            CountDefinition("Model");
            _connections.Add(C("OO", geometryId, modelId));
            _connections.Add(C("OO", modelId, 0L));

            // LayerElementMaterial only stores polygon-to-slot indices.  FBX importers
            // also require a Material object connected to the mesh Model for the slot
            // to appear in the imported scene.  Keep one deterministic slot per mesh;
            // LithTechMesh currently exposes a single texture/material identity.
            long materialId = NextId();
            string materialLabel = mesh.TexturePath
                ?? mesh.MaterialHints?.FirstOrDefault(hint => !string.IsNullOrWhiteSpace(hint))
                ?? meshName;
            string materialName = string.IsNullOrWhiteSpace(materialLabel) ? $"{meshName}_Material" : materialLabel;
            BitmapSource? textureBitmap = ResolveMeshTexture(mesh, out string? textureReference);
            var material = new FbxNode("Material", materialId, $"{materialName}\u0000\u0001Material", "Phong");
            material.AddChild(new FbxNode("Version", 102));
            material.AddChild(new FbxNode("ShadingModel", "Phong"));
            material.AddChild(new FbxNode("MultiLayer", 0));
            var materialProperties = material.AddChild(new FbxNode("Properties70"));
            // Textured materials use a white base so importers do not darken the texture.
            double diffuse = textureBitmap is null ? 0.8 : 1.0;
            materialProperties.AddChild(P("DiffuseColor", "Color", "", "A", diffuse, diffuse, diffuse));
            materialProperties.AddChild(P("SpecularColor", "Color", "", "A", 0.2, 0.2, 0.2));
            materialProperties.AddChild(P("Shininess", "double", "Number", "A", 20.0));
            materialProperties.AddChild(P("TransparencyFactor", "double", "Number", "A", 0.0));
            _objects.Add(material);
            CountDefinition("Material");
            _connections.Add(C("OO", materialId, modelId));
            if (textureBitmap is not null)
            {
                BuildTexture(textureBitmap, textureReference, materialName, materialId);
            }

            BuildSkinning(mesh, meshName, geometryId, boneModelIds, boneGlobals);
        }

        private BitmapSource? ResolveMeshTexture(LithTechMesh mesh, out string? resolvedReference)
        {
            resolvedReference = null;
            if (textureSource is null)
            {
                return null;
            }

            try
            {
                return LithTechObjExporter.ResolveMeshTexture(mesh, textureSource, out resolvedReference);
            }
            catch
            {
                resolvedReference = null;
                return null;
            }
        }

        // FBX 7.4 texture wiring: a Video object carries the image (PNG bytes embedded in its
        // Content node, so the FBX stays self-contained) and a Texture object references it
        // and binds to the material's DiffuseColor property and the mesh UV set.
        private void BuildTexture(BitmapSource bitmap, string? reference, string materialName, long materialId)
        {
            string normalizedReference = NormalizeTextureReference(reference);
            if (_textureIdByBitmap.TryGetValue(bitmap, out long textureId) ||
                (normalizedReference.Length > 0 && _textureIdByReference.TryGetValue(normalizedReference, out textureId)))
            {
                _connections.Add(C("OP", textureId, materialId, "DiffuseColor"));
                return;
            }

            byte[]? pngData = EncodePng(bitmap);
            if (pngData is null)
            {
                return;
            }

            string baseName = normalizedReference.Length > 0
                ? Path.GetFileNameWithoutExtension(normalizedReference)
                : materialName;
            string textureName = MakeUniqueTextureName(SanitizeTextureName(baseName));
            string fileName = textureName + ".png";

            long videoId = NextId();
            var video = new FbxNode("Video", videoId, $"{textureName}\u0000\u0001Video", "Clip");
            video.AddChild(new FbxNode("Type", "Clip"));
            var videoProperties = video.AddChild(new FbxNode("Properties70"));
            videoProperties.AddChild(P("Path", "KString", "XRefUrl", "", fileName));
            video.AddChild(new FbxNode("UseMipMap", 0));
            video.AddChild(new FbxNode("Filename", fileName));
            video.AddChild(new FbxNode("RelativeFilename", fileName));
            video.AddChild(new FbxNode("Content", pngData));
            _objects.Add(video);
            CountDefinition("Video");

            textureId = NextId();
            var texture = new FbxNode("Texture", textureId, $"{textureName}\u0000\u0001Texture", "");
            texture.AddChild(new FbxNode("Type", "TextureVideoClip"));
            texture.AddChild(new FbxNode("Version", 202));
            texture.AddChild(new FbxNode("TextureName", $"{textureName}\u0000\u0001Texture"));
            var textureProperties = texture.AddChild(new FbxNode("Properties70"));
            textureProperties.AddChild(P("CurrentTextureBlendMode", "enum", "", "", 1));
            textureProperties.AddChild(P("UVSet", "KString", "TextureUVSet", "", "UVMap"));
            textureProperties.AddChild(P("UseMaterial", "bool", "", "", true));
            texture.AddChild(new FbxNode("Media", $"{textureName}\u0000\u0001Video"));
            texture.AddChild(new FbxNode("FileName", fileName));
            texture.AddChild(new FbxNode("RelativeFilename", fileName));
            _objects.Add(texture);
            CountDefinition("Texture");

            _connections.Add(C("OO", videoId, textureId));
            _connections.Add(C("OP", textureId, materialId, "DiffuseColor"));

            _textureIdByBitmap[bitmap] = textureId;
            if (normalizedReference.Length > 0)
            {
                _textureIdByReference[normalizedReference] = textureId;
            }
        }

        private static byte[]? EncodePng(BitmapSource bitmap)
        {
            try
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = new MemoryStream();
                encoder.Save(stream);
                return stream.ToArray();
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeTextureReference(string? reference)
        {
            return string.IsNullOrWhiteSpace(reference)
                ? string.Empty
                : reference.Replace('\\', '/').Trim().Trim('"');
        }

        private static string SanitizeTextureName(string name)
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            string sanitized = new string(name.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
            return string.IsNullOrWhiteSpace(sanitized) ? "texture" : sanitized;
        }

        private string MakeUniqueTextureName(string baseName)
        {
            if (_usedTextureNames.Add(baseName))
            {
                return baseName;
            }

            for (int index = 2; ; index++)
            {
                string candidate = $"{baseName}_{index}";
                if (_usedTextureNames.Add(candidate))
                {
                    return candidate;
                }
            }
        }

        private void BuildSkinning(LithTechMesh mesh, string meshName, long geometryId, long[]? boneModelIds, double[][]? boneGlobals)
        {
            if (boneModelIds is null || boneGlobals is null)
            {
                return;
            }

            // bone index -> vertex indices / weights for that bone's cluster.
            Dictionary<int, (List<int> Indexes, List<double> Weights)>? clusters = null;
            if (mesh.Skin is { } skin)
            {
                clusters = new Dictionary<int, (List<int>, List<double>)>();
                int vertexCount = Math.Min(skin.Count, mesh.Vertices.Count);
                for (int vertex = 0; vertex < vertexCount; vertex++)
                {
                    LithTechVertexSkin weights = skin[vertex];
                    byte[] bones = [weights.Bone0, weights.Bone1, weights.Bone2, weights.Bone3];
                    float[] values = [weights.Weight0, weights.Weight1, weights.Weight2, weights.Weight3];
                    double sum = 0;
                    for (int slot = 0; slot < 4; slot++)
                    {
                        if (bones[slot] != LithTechVertexSkin.UnusedBone && values[slot] > 0 && bones[slot] < boneModelIds.Length)
                        {
                            sum += values[slot];
                        }
                    }

                    if (sum <= 0)
                    {
                        continue;
                    }

                    double scale = Math.Abs(sum - 1.0) > 1e-6 ? 1.0 / sum : 1.0;
                    for (int slot = 0; slot < 4; slot++)
                    {
                        int bone = bones[slot];
                        if (bone == LithTechVertexSkin.UnusedBone || values[slot] <= 0 || bone >= boneModelIds.Length)
                        {
                            continue;
                        }

                        if (!clusters.TryGetValue(bone, out (List<int> Indexes, List<double> Weights) cluster))
                        {
                            cluster = ([], []);
                            clusters[bone] = cluster;
                        }

                        cluster.Indexes.Add(vertex);
                        cluster.Weights.Add(values[slot] * scale);
                    }
                }
            }
            else if (mesh.RigidBoneIndex >= 0 && mesh.RigidBoneIndex < boneModelIds.Length)
            {
                var indexes = new List<int>(mesh.Vertices.Count);
                var weights = new List<double>(mesh.Vertices.Count);
                for (int vertex = 0; vertex < mesh.Vertices.Count; vertex++)
                {
                    indexes.Add(vertex);
                    weights.Add(1.0);
                }

                clusters = new Dictionary<int, (List<int>, List<double>)> { [mesh.RigidBoneIndex] = (indexes, weights) };
            }

            if (clusters is null || clusters.Count == 0)
            {
                return;
            }

            var skeleton = Skeleton!;
            long skinId = NextId();
            var skinDeformer = new FbxNode("Deformer", skinId, $"Skin_{meshName}\u0000\u0001Deformer", "Skin");
            skinDeformer.AddChild(new FbxNode("Version", 101));
            _objects.Add(skinDeformer);
            CountDefinition("Deformer");
            _connections.Add(C("OO", skinId, geometryId));

            foreach ((int bone, (List<int> indexes, List<double> weights)) in clusters.OrderBy(pair => pair.Key))
            {
                string boneName = bone < skeleton.Nodes.Count ? skeleton.Nodes[bone].Name : $"Node{bone}";
                long clusterId = NextId();
                var clusterNode = new FbxNode("Deformer", clusterId, $"Cluster_{boneName}\u0000\u0001Deformer", "Cluster");
                clusterNode.AddChild(new FbxNode("Version", 100));
                clusterNode.AddChild(new FbxNode("Indexes", indexes.ToArray()));
                clusterNode.AddChild(new FbxNode("Weights", weights.ToArray()));
                double[] global = boneGlobals[bone];
                // FBX matrix arrays are column-major (translation at 12/13/14); the document
                // stores row-major (translation at 3/7/11), so transpose on write.
                clusterNode.AddChild(new FbxNode("Transform", ToFbxMatrix(Invert(global) ?? CreateIdentity())));
                clusterNode.AddChild(new FbxNode("TransformLink", ToFbxMatrix(global)));
                _objects.Add(clusterNode);
                CountDefinition("Deformer");
                _connections.Add(C("OO", clusterId, skinId));
                _connections.Add(C("OO", boneModelIds[bone], clusterId));
            }
        }

        private void BuildAnimations()
        {
            var skeleton = Skeleton!;
            foreach (LithTechModelAnimation animation in document.Animations)
            {
                BuildAnimation(skeleton, animation);
            }
        }

        private void BuildAnimation(LithTechModelSkeleton skeleton, LithTechModelAnimation animation)
        {
            if (animation.KeyTimesSeconds.Count == 0)
            {
                return;
            }

            var keyTimes = new long[animation.KeyTimesSeconds.Count];
            for (int i = 0; i < keyTimes.Length; i++)
            {
                keyTimes[i] = (long)Math.Round(animation.KeyTimesSeconds[i] * KTimePerSecond);
            }

            string animationName = string.IsNullOrWhiteSpace(animation.Name) ? "Take" : animation.Name;
            long stackId = NextId();
            var stack = new FbxNode("AnimationStack", stackId, $"{animationName}\u0000\u0001AnimStack", "");
            var stackProperties = stack.AddChild(new FbxNode("Properties70"));
            stackProperties.AddChild(P("LocalStart", "KTime", "Time", "", keyTimes[0]));
            stackProperties.AddChild(P("LocalStop", "KTime", "Time", "", keyTimes[^1]));
            stackProperties.AddChild(P("ReferenceStart", "KTime", "Time", "", keyTimes[0]));
            stackProperties.AddChild(P("ReferenceStop", "KTime", "Time", "", keyTimes[^1]));
            _objects.Add(stack);
            CountDefinition("AnimationStack");
            _connections.Add(C("OO", stackId, 0L));

            long layerId = NextId();
            var layer = new FbxNode("AnimationLayer", layerId, $"{animationName}\u0000\u0001AnimLayer", "");
            _objects.Add(layer);
            CountDefinition("AnimationLayer");
            _connections.Add(C("OO", layerId, stackId));

            int channelCount = Math.Min(animation.Channels.Count, skeleton.Nodes.Count);
            for (int nodeIndex = 0; nodeIndex < channelCount; nodeIndex++)
            {
                LithTechNodeChannel? channel = animation.Channels[nodeIndex];
                if (channel is null || _boneModelIds is null || nodeIndex >= _boneModelIds.Length)
                {
                    continue;
                }

                long modelId = _boneModelIds[nodeIndex];
                if (channel.Positions is { Count: > 0 } positions)
                {
                    int count = Math.Min(positions.Count, keyTimes.Length);
                    var x = new double[count];
                    var y = new double[count];
                    var z = new double[count];
                    for (int key = 0; key < count; key++)
                    {
                        x[key] = -positions[key].X; // mirrored with the geometry
                        y[key] = positions[key].Y;
                        z[key] = positions[key].Z;
                    }

                    BuildCurveNode(animationName, layerId, modelId, "Lcl Translation", keyTimes, x, y, z);
                }

                if (channel.Rotations is { Count: > 0 } rotations)
                {
                    (double X, double Y, double Z)[] eulers = ConvertRotationsToEulerDegrees(rotations);
                    int count = Math.Min(eulers.Length, keyTimes.Length);
                    var x = new double[count];
                    var y = new double[count];
                    var z = new double[count];
                    for (int key = 0; key < count; key++)
                    {
                        x[key] = eulers[key].X;
                        y[key] = eulers[key].Y;
                        z[key] = eulers[key].Z;
                    }

                    BuildCurveNode(animationName, layerId, modelId, "Lcl Rotation", keyTimes, x, y, z);
                }
            }
        }

        private void BuildCurveNode(
            string animationName,
            long layerId,
            long modelId,
            string fbxProperty,
            long[] keyTimes,
            double[] x,
            double[] y,
            double[] z)
        {
            long curveNodeId = NextId();
            var curveNode = new FbxNode("AnimationCurveNode", curveNodeId, $"{animationName}\u0000\u0001AnimCurveNode", "");
            var properties = curveNode.AddChild(new FbxNode("Properties70"));
            properties.AddChild(P("d|X", "Number", "", "A", x[0]));
            properties.AddChild(P("d|Y", "Number", "", "A", y[0]));
            properties.AddChild(P("d|Z", "Number", "", "A", z[0]));
            _objects.Add(curveNode);
            CountDefinition("AnimationCurveNode");
            _connections.Add(C("OO", curveNodeId, layerId));
            _connections.Add(C("OP", curveNodeId, modelId, fbxProperty));

            BuildCurve(curveNodeId, keyTimes, x, "d|X");
            BuildCurve(curveNodeId, keyTimes, y, "d|Y");
            BuildCurve(curveNodeId, keyTimes, z, "d|Z");
        }

        private void BuildCurve(long curveNodeId, long[] keyTimes, double[] values, string channel)
        {
            int count = values.Length;
            var curveKeyTimes = new long[count];
            Array.Copy(keyTimes, curveKeyTimes, count);
            var keyValues = new float[count];
            for (int i = 0; i < count; i++)
            {
                keyValues[i] = (float)values[i];
            }

            var flags = new int[count];
            Array.Fill(flags, LinearKeyAttrFlags);
            var attributeData = new float[count * 4]; // 4 zero values per key (slopes / velocity)
            var refCounts = new int[count];
            Array.Fill(refCounts, 1);

            long curveId = NextId();
            var curve = new FbxNode("AnimationCurve", curveId, "\u0000\u0001AnimCurve", "");
            curve.AddChild(new FbxNode("Default", (float)values[0]));
            curve.AddChild(new FbxNode("KeyVer", 4008));
            curve.AddChild(new FbxNode("KeyTime", curveKeyTimes));
            curve.AddChild(new FbxNode("KeyValueFloat", keyValues));
            curve.AddChild(new FbxNode("KeyAttrFlags", flags));
            curve.AddChild(new FbxNode("KeyAttrDataFloat", attributeData));
            curve.AddChild(new FbxNode("KeyAttrRefCount", refCounts));
            _objects.Add(curve);
            CountDefinition("AnimationCurve");
            _connections.Add(C("OP", curveId, curveNodeId, channel));
        }

        private FbxNode BuildTakes()
        {
            var takes = new FbxNode("Takes");
            foreach (LithTechModelAnimation animation in document.Animations)
            {
                if (animation.KeyTimesSeconds.Count == 0)
                {
                    continue;
                }

                long start = (long)Math.Round(animation.KeyTimesSeconds[0] * KTimePerSecond);
                long stop = (long)Math.Round(animation.KeyTimesSeconds[^1] * KTimePerSecond);
                string name = string.IsNullOrWhiteSpace(animation.Name) ? "Take" : animation.Name;
                var take = takes.AddChild(new FbxNode("Take", name));
                take.AddChild(new FbxNode("FileName", name));
                take.AddChild(new FbxNode("LocalTime", start, stop));
                take.AddChild(new FbxNode("ReferenceTime", start, stop));
            }

            return takes;
        }

        private void CountDefinition(string objectType)
        {
            _definitionCounts[objectType] = _definitionCounts.TryGetValue(objectType, out int count) ? count + 1 : 1;
        }

        private static FbxNode P(string name, string type, string label, string flags, params object[] values)
        {
            var node = new FbxNode("P", name, type, label, flags);
            foreach (object value in values)
            {
                node.AddProperty(value);
            }

            return node;
        }

        private static FbxNode C(string kind, long childId, long parentId, string? property = null)
        {
            return property is null
                ? new FbxNode("C", kind, childId, parentId)
                : new FbxNode("C", kind, childId, parentId, property);
        }

        private static (double X, double Y, double Z)[] ConvertRotationsToEulerDegrees(IReadOnlyList<LithTechQuaternion> rotations)
        {
            var result = new (double, double, double)[rotations.Count];
            double previousX = 0, previousY = 0, previousZ = 0, previousW = 1;
            for (int i = 0; i < rotations.Count; i++)
            {
                LithTechQuaternion quaternion = rotations[i];
                double qx = quaternion.X, qy = quaternion.Y, qz = quaternion.Z, qw = quaternion.W;
                double length = Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
                if (length <= 1e-12)
                {
                    qx = 0;
                    qy = 0;
                    qz = 0;
                    qw = 1;
                }
                else
                {
                    qx /= length;
                    qy /= length;
                    qz /= length;
                    qw /= length;
                }

                // Mirror the rotation across the YZ plane along with the geometry:
                // for M = diag(-1,1,1), q' = (qx, -qy, -qz, qw).
                qy = -qy;
                qz = -qz;

                // Keep sign continuity so the Euler sequence stays smooth.
                if (i > 0 && qx * previousX + qy * previousY + qz * previousZ + qw * previousW < 0)
                {
                    qx = -qx;
                    qy = -qy;
                    qz = -qz;
                    qw = -qw;
                }

                previousX = qx;
                previousY = qy;
                previousZ = qz;
                previousW = qw;
                result[i] = MatrixToEulerDegrees(QuaternionToMatrix(qx, qy, qz, qw));
            }

            return result;
        }

        private static double[] QuaternionToMatrix(double x, double y, double z, double w)
        {
            double xx = x * x, yy = y * y, zz = z * z;
            double xy = x * y, xz = x * z, yz = y * z;
            double wx = w * x, wy = w * y, wz = w * z;
            return
            [
                1 - 2 * (yy + zz), 2 * (xy - wz), 2 * (xz + wy), 0,
                2 * (xy + wz), 1 - 2 * (xx + zz), 2 * (yz - wx), 0,
                2 * (xz - wy), 2 * (yz + wx), 1 - 2 * (xx + yy), 0,
                0, 0, 0, 1
            ];
        }

        private static double[] CreateIdentity()
        {
            return [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
        }

        // Mirrors a row-major 4x4 across the YZ plane (M·G·M with M = diag(-1,1,1)):
        // negates the entries that mix the X axis, including translation X.
        private static double[] MirrorTransformX(double[] m)
        {
            var result = (double[])m.Clone();
            result[1] = -result[1]; // row 0, column 1
            result[2] = -result[2]; // row 0, column 2
            result[3] = -result[3]; // row 0, column 3 (translation X)
            result[4] = -result[4]; // row 1, column 0
            result[8] = -result[8]; // row 2, column 0
            return result;
        }

        // Angle-weighted per-vertex normals accumulated from the triangles that reference each
        // vertex. Hard edges stay hard because the source meshes duplicate vertices along them.
        private static IReadOnlyList<LithTechVector3> ComputeSmoothNormals(LithTechMesh mesh)
        {
            int vertexCount = mesh.Vertices.Count;
            var sums = new double[vertexCount * 3];
            int usableIndexCount = mesh.TriangleIndices.Count - mesh.TriangleIndices.Count % 3;
            for (int index = 0; index < usableIndexCount; index += 3)
            {
                int a = mesh.TriangleIndices[index];
                int b = mesh.TriangleIndices[index + 1];
                int c = mesh.TriangleIndices[index + 2];
                if (a < 0 || a >= vertexCount || b < 0 || b >= vertexCount || c < 0 || c >= vertexCount)
                {
                    continue;
                }

                LithTechVector3 pa = mesh.Vertices[a];
                LithTechVector3 pb = mesh.Vertices[b];
                LithTechVector3 pc = mesh.Vertices[c];
                // Unnormalized cross product; its length is 2x the triangle area.
                double ux = pb.X - pa.X, uy = pb.Y - pa.Y, uz = pb.Z - pa.Z;
                double vx = pc.X - pa.X, vy = pc.Y - pa.Y, vz = pc.Z - pa.Z;
                double nx = uy * vz - uz * vy;
                double ny = uz * vx - ux * vz;
                double nz = ux * vy - uy * vx;
                double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (length <= 1e-20)
                {
                    continue;
                }

                nx /= length;
                ny /= length;
                nz /= length;
                AccumulateCornerNormal(sums, a, pa, pb, pc, nx, ny, nz);
                AccumulateCornerNormal(sums, b, pb, pc, pa, nx, ny, nz);
                AccumulateCornerNormal(sums, c, pc, pa, pb, nx, ny, nz);
            }

            var result = new LithTechVector3[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                result[i] = NormalizeOrDefault(new LithTechVector3(sums[i * 3], sums[i * 3 + 1], sums[i * 3 + 2]));
            }

            return result;
        }

        // Weights the face normal by the corner angle at (current, next, previous).
        private static void AccumulateCornerNormal(double[] sums, int vertex, LithTechVector3 current, LithTechVector3 next, LithTechVector3 previous, double nx, double ny, double nz)
        {
            double ux = next.X - current.X, uy = next.Y - current.Y, uz = next.Z - current.Z;
            double vx = previous.X - current.X, vy = previous.Y - current.Y, vz = previous.Z - current.Z;
            double lengthU = Math.Sqrt(ux * ux + uy * uy + uz * uz);
            double lengthV = Math.Sqrt(vx * vx + vy * vy + vz * vz);
            double angle;
            if (lengthU <= 1e-20 || lengthV <= 1e-20)
            {
                angle = 1.0;
            }
            else
            {
                double cosine = Math.Clamp((ux * vx + uy * vy + uz * vz) / (lengthU * lengthV), -1.0, 1.0);
                angle = Math.Acos(cosine);
            }

            sums[vertex * 3] += nx * angle;
            sums[vertex * 3 + 1] += ny * angle;
            sums[vertex * 3 + 2] += nz * angle;
        }

        private static LithTechVector3 NormalizeOrDefault(LithTechVector3 vector)
        {
            double length = Math.Sqrt(vector.X * vector.X + vector.Y * vector.Y + vector.Z * vector.Z);
            return length <= 1e-12
                ? new LithTechVector3(0, 1, 0)
                : new LithTechVector3(vector.X / length, vector.Y / length, vector.Z / length);
        }

        // Row-major (document convention) to column-major (FBX array convention).
        private static double[] ToFbxMatrix(double[] rowMajor)
        {
            var result = new double[16];
            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    result[column * 4 + row] = rowMajor[row * 4 + column];
                }
            }

            return result;
        }

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

        // Gauss-Jordan inverse with partial pivoting; null when the matrix is singular.
        private static double[]? Invert(double[] matrix)
        {
            var augmented = new double[4, 8];
            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    augmented[row, column] = matrix[row * 4 + column];
                }

                augmented[row, 4 + row] = 1;
            }

            for (int column = 0; column < 4; column++)
            {
                int pivot = column;
                for (int row = column + 1; row < 4; row++)
                {
                    if (Math.Abs(augmented[row, column]) > Math.Abs(augmented[pivot, column]))
                    {
                        pivot = row;
                    }
                }

                if (Math.Abs(augmented[pivot, column]) < 1e-15)
                {
                    return null;
                }

                if (pivot != column)
                {
                    for (int k = 0; k < 8; k++)
                    {
                        (augmented[column, k], augmented[pivot, k]) = (augmented[pivot, k], augmented[column, k]);
                    }
                }

                double divisor = augmented[column, column];
                for (int k = 0; k < 8; k++)
                {
                    augmented[column, k] /= divisor;
                }

                for (int row = 0; row < 4; row++)
                {
                    if (row == column)
                    {
                        continue;
                    }

                    double factor = augmented[row, column];
                    if (factor == 0)
                    {
                        continue;
                    }

                    for (int k = 0; k < 8; k++)
                    {
                        augmented[row, k] -= factor * augmented[column, k];
                    }
                }
            }

            var result = new double[16];
            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    result[row * 4 + column] = augmented[row, 4 + column];
                }
            }

            return result;
        }

        // Decomposes a local bind matrix into translation, Euler XYZ degrees, and scale.
        private static void DecomposeTrs(double[] m, out double[] translation, out double[] rotation, out double[] scale)
        {
            translation = [m[3], m[7], m[11]];
            double scaleX = ColumnLength(m, 0);
            double scaleY = ColumnLength(m, 1);
            double scaleZ = ColumnLength(m, 2);
            scale = [scaleX, scaleY, scaleZ];

            var ortho = new double[16];
            Array.Copy(m, ortho, 16);
            NormalizeColumn(ortho, 0, scaleX);
            NormalizeColumn(ortho, 1, scaleY);
            NormalizeColumn(ortho, 2, scaleZ);
            (double x, double y, double z) = MatrixToEulerDegrees(ortho);
            rotation = [x, y, z];
        }

        private static double ColumnLength(double[] m, int column)
        {
            double x = m[column];
            double y = m[4 + column];
            double z = m[8 + column];
            return Math.Sqrt(x * x + y * y + z * z);
        }

        private static void NormalizeColumn(double[] m, int column, double length)
        {
            if (length <= 1e-15)
            {
                return;
            }

            m[column] /= length;
            m[4 + column] /= length;
            m[8 + column] /= length;
        }

        // Euler XYZ degrees from a rotation matrix with M = Rz * Ry * Rx (column-vector convention).
        private static (double X, double Y, double Z) MatrixToEulerDegrees(double[] m)
        {
            double m20 = m[8];
            double x, y, z;
            if (Math.Abs(m20) < 1 - 1e-9)
            {
                y = Math.Asin(Math.Clamp(-m20, -1, 1));
                x = Math.Atan2(m[9], m[10]);
                z = Math.Atan2(m[4], m[0]);
            }
            else
            {
                y = Math.Asin(Math.Clamp(-m20, -1, 1));
                z = 0;
                x = Math.Atan2(-m[1], m[5]);
            }

            const double radToDeg = 180.0 / Math.PI;
            return (x * radToDeg, y * radToDeg, z * radToDeg);
        }
    }
}
