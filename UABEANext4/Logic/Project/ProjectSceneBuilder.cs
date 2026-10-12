using AssetsTools.NET;
using AssetsTools.NET.Extra;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using UABEANext4.AssetWorkspace;
using UABEANext4.Logic.Mesh;

namespace UABEANext4.Logic.Project;

/// <summary>
/// Rebuilds the world a set of assets files describes: finds every object that has a mesh,
/// works out where it sits (position, rotation and scale of the object and all of its parents),
/// and collects the decoded meshes.
/// </summary>
public static class ProjectSceneBuilder
{
    private const int MAX_WARNINGS = 25;

    public static ProjectScene Build(
        Workspace workspace,
        IReadOnlyList<AssetsFileInstance> files,
        Action<float, string>? progress,
        CancellationToken cancellationToken)
    {
        var scene = new ProjectScene { FileCount = files.Count };

        // shared between files so a mesh that many scenes use is only decoded once.
        // the value is the index into scene.Meshes, or -1 if the mesh can't be shown.
        var meshCache = new Dictionary<(AssetsFileInstance, long), int>();

        for (var fileIdx = 0; fileIdx < files.Count; fileIdx++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var index = fileIdx;
            var fileInst = files[index];
            progress?.Invoke((float)index / files.Count, $"Reading {fileInst.name}");

            var fileBuilder = new FileSceneBuilder(
                workspace, fileInst, scene, meshCache, cancellationToken,
                (fraction, text) => progress?.Invoke((index + fraction) / files.Count, text));
            fileBuilder.Build();
        }

        ComputeBounds(scene);
        progress?.Invoke(1f, "Done");
        return scene;
    }

    private static void ComputeBounds(ProjectScene scene)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var any = false;

        Span<Vector3> corners = stackalloc Vector3[8];
        foreach (var instance in scene.Instances)
        {
            var mesh = scene.Meshes[instance.MeshIndex];
            var bmin = mesh.BoundsMin;
            var bmax = mesh.BoundsMax;

            corners[0] = new Vector3(bmin.X, bmin.Y, bmin.Z);
            corners[1] = new Vector3(bmax.X, bmin.Y, bmin.Z);
            corners[2] = new Vector3(bmin.X, bmax.Y, bmin.Z);
            corners[3] = new Vector3(bmax.X, bmax.Y, bmin.Z);
            corners[4] = new Vector3(bmin.X, bmin.Y, bmax.Z);
            corners[5] = new Vector3(bmax.X, bmin.Y, bmax.Z);
            corners[6] = new Vector3(bmin.X, bmax.Y, bmax.Z);
            corners[7] = new Vector3(bmax.X, bmax.Y, bmax.Z);

            for (var i = 0; i < 8; i++)
            {
                var p = Vector3.Transform(corners[i], instance.World);
                if (!IsFinite(p))
                    continue;

                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
                any = true;
            }
        }

        scene.HasBounds = any;
        scene.BoundsMin = any ? min : Vector3.Zero;
        scene.BoundsMax = any ? max : Vector3.Zero;
    }

    private static bool IsFinite(Vector3 v)
    {
        return float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    }

    private sealed class GameObjectInfo
    {
        public string Name = string.Empty;
        public bool Active = true;
        public long TransformPathId;
    }

    private sealed class TransformInfo
    {
        public long GameObjectPathId;
        public long FatherPathId;
        public Vector3 Position;
        public Quaternion Rotation = Quaternion.Identity;
        public Vector3 Scale = Vector3.One;

        public bool Resolved;
        public Matrix4x4 World = Matrix4x4.Identity;
        public bool ActiveInHierarchy = true;
    }

    private readonly struct MeshSource
    {
        public readonly long GameObjectPathId;
        public readonly int MeshFileId;
        public readonly long MeshPathId;
        public readonly bool Enabled;

        public MeshSource(long gameObjectPathId, int meshFileId, long meshPathId, bool enabled)
        {
            GameObjectPathId = gameObjectPathId;
            MeshFileId = meshFileId;
            MeshPathId = meshPathId;
            Enabled = enabled;
        }
    }

    private sealed class FileSceneBuilder
    {
        private readonly Workspace _workspace;
        private readonly AssetsFileInstance _fileInst;
        private readonly ProjectScene _scene;
        private readonly Dictionary<(AssetsFileInstance, long), int> _meshCache;
        private readonly CancellationToken _cancellationToken;
        private readonly Action<float, string> _progress;

        // GameObjects and Transforms are only read when something needs them, then kept.
        // null is kept too, so a broken object isn't read again for every child.
        private readonly Dictionary<long, GameObjectInfo?> _gameObjects = new();
        private readonly Dictionary<long, TransformInfo?> _transforms = new();

        public FileSceneBuilder(
            Workspace workspace,
            AssetsFileInstance fileInst,
            ProjectScene scene,
            Dictionary<(AssetsFileInstance, long), int> meshCache,
            CancellationToken cancellationToken,
            Action<float, string> progress)
        {
            _workspace = workspace;
            _fileInst = fileInst;
            _scene = scene;
            _meshCache = meshCache;
            _cancellationToken = cancellationToken;
            _progress = progress;
        }

        public void Build()
        {
            _progress(0f, $"Finding renderers in {_fileInst.name}");
            var sources = CollectMeshSources();

            for (var i = 0; i < sources.Count; i++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if ((i & 0x3f) == 0)
                {
                    _progress(0.1f + 0.9f * i / sources.Count, $"{_fileInst.name}: placing object {i + 1} of {sources.Count}");
                }

                AddInstance(sources[i]);
            }

            _scene.GameObjectsVisited += _gameObjects.Count;
        }

        private List<MeshSource> CollectMeshSources()
        {
            // which renderers are switched off, by the GameObject they belong to
            var disabledRenderers = new HashSet<long>();
            foreach (var info in _fileInst.file.GetAssetsOfType(AssetClassID.MeshRenderer))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var bf = _workspace.GetBaseField(_fileInst, info.PathId);
                    if (bf == null)
                        continue;

                    var enabledField = bf["m_Enabled"];
                    if (!enabledField.IsDummy && !enabledField.AsBool)
                    {
                        disabledRenderers.Add(bf["m_GameObject"]["m_PathID"].AsLong);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // a renderer we can't read just counts as enabled
                }
            }

            var sources = new List<MeshSource>();

            foreach (var info in _fileInst.file.GetAssetsOfType(AssetClassID.MeshFilter))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var bf = _workspace.GetBaseField(_fileInst, info.PathId);
                    if (bf == null)
                        continue;

                    var goPathId = bf["m_GameObject"]["m_PathID"].AsLong;
                    var meshPtr = bf["m_Mesh"];
                    sources.Add(new MeshSource(
                        goPathId,
                        meshPtr["m_FileID"].AsInt,
                        meshPtr["m_PathID"].AsLong,
                        !disabledRenderers.Contains(goPathId)));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AddWarning($"{_fileInst.name}: a MeshFilter (path id {info.PathId}) could not be read.");
                }
            }

            // skinned meshes are placed in their bind pose, at their GameObject's transform
            foreach (var info in _fileInst.file.GetAssetsOfType(AssetClassID.SkinnedMeshRenderer))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var bf = _workspace.GetBaseField(_fileInst, info.PathId);
                    if (bf == null)
                        continue;

                    var goPathId = bf["m_GameObject"]["m_PathID"].AsLong;
                    var meshPtr = bf["m_Mesh"];
                    var enabledField = bf["m_Enabled"];
                    sources.Add(new MeshSource(
                        goPathId,
                        meshPtr["m_FileID"].AsInt,
                        meshPtr["m_PathID"].AsLong,
                        enabledField.IsDummy || enabledField.AsBool));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AddWarning($"{_fileInst.name}: a SkinnedMeshRenderer (path id {info.PathId}) could not be read.");
                }
            }

            return sources;
        }

        private void AddInstance(MeshSource source)
        {
            var gameObject = GetGameObject(source.GameObjectPathId);
            if (gameObject == null || gameObject.TransformPathId == 0)
            {
                _scene.InstancesWithoutTransform++;
                return;
            }

            var transform = GetTransform(gameObject.TransformPathId);
            if (transform == null)
            {
                _scene.InstancesWithoutTransform++;
                return;
            }

            var meshIndex = ResolveMesh(source.MeshFileId, source.MeshPathId);
            if (meshIndex < 0)
                return;

            _scene.Instances.Add(new SceneInstance
            {
                Name = gameObject.Name,
                MeshIndex = meshIndex,
                World = transform.World,
                ActiveInHierarchy = transform.ActiveInHierarchy,
                RendererEnabled = source.Enabled
            });
        }

        private GameObjectInfo? GetGameObject(long pathId)
        {
            if (_gameObjects.TryGetValue(pathId, out var cached))
                return cached;

            GameObjectInfo? result = null;
            try
            {
                var bf = _workspace.GetBaseField(_fileInst, pathId);
                if (bf != null)
                {
                    result = new GameObjectInfo
                    {
                        Name = bf["m_Name"].AsString ?? string.Empty
                    };

                    var activeField = bf["m_IsActive"];
                    result.Active = activeField.IsDummy || activeField.AsBool;

                    // the first component of every GameObject is its Transform (or RectTransform)
                    var components = bf["m_Component.Array"];
                    if (components.Children.Count > 0)
                    {
                        var pair = components[0];
                        var componentPtr = pair[pair.Children.Count - 1];
                        result.TransformPathId = componentPtr["m_PathID"].AsLong;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = null;
            }

            _gameObjects[pathId] = result;
            return result;
        }

        private TransformInfo? ReadTransform(long pathId)
        {
            if (_transforms.TryGetValue(pathId, out var cached))
                return cached;

            TransformInfo? result = null;
            try
            {
                var bf = _workspace.GetBaseField(_fileInst, pathId);
                if (bf != null)
                {
                    result = new TransformInfo
                    {
                        GameObjectPathId = bf["m_GameObject"]["m_PathID"].AsLong
                    };

                    // a parent in another file isn't supported, so treat that transform as a root
                    var father = bf["m_Father"];
                    result.FatherPathId = father["m_FileID"].AsInt == 0 ? father["m_PathID"].AsLong : 0;

                    var pos = bf["m_LocalPosition"];
                    result.Position = new Vector3(pos["x"].AsFloat, pos["y"].AsFloat, pos["z"].AsFloat);

                    var rot = bf["m_LocalRotation"];
                    var quat = new Quaternion(rot["x"].AsFloat, rot["y"].AsFloat, rot["z"].AsFloat, rot["w"].AsFloat);
                    var lengthSquared = quat.LengthSquared();
                    result.Rotation = float.IsFinite(lengthSquared) && lengthSquared > 1e-12f
                        ? Quaternion.Normalize(quat)
                        : Quaternion.Identity;

                    var scale = bf["m_LocalScale"];
                    result.Scale = new Vector3(scale["x"].AsFloat, scale["y"].AsFloat, scale["z"].AsFloat);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = null;
            }

            _transforms[pathId] = result;
            return result;
        }

        /// <summary>
        /// Gets a transform with its world matrix worked out. A transform's world matrix is its own local
        /// matrix (scale, then rotation, then position) placed inside its parent's world matrix. Parents are
        /// worked out first, walking up the chain instead of recursing so a very deep hierarchy can't overflow.
        /// </summary>
        private TransformInfo? GetTransform(long pathId)
        {
            var chain = new List<TransformInfo>();
            var seen = new HashSet<long>();
            TransformInfo? resolvedAncestor = null;

            var current = pathId;
            while (current != 0)
            {
                // a transform that is its own ancestor would never end
                if (!seen.Add(current))
                    break;

                var tf = ReadTransform(current);
                if (tf == null)
                    break;

                if (tf.Resolved)
                {
                    resolvedAncestor = tf;
                    break;
                }

                chain.Add(tf);
                current = tf.FatherPathId;
            }

            if (chain.Count == 0)
                return resolvedAncestor;

            // chain[0] is the requested transform and chain[^1] is the top-most one still to do
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                var tf = chain[i];
                var parent = i == chain.Count - 1 ? resolvedAncestor : chain[i + 1];

                var local = Matrix4x4.CreateScale(tf.Scale)
                    * Matrix4x4.CreateFromQuaternion(tf.Rotation)
                    * Matrix4x4.CreateTranslation(tf.Position);

                tf.World = parent != null ? local * parent.World : local;

                var gameObject = GetGameObject(tf.GameObjectPathId);
                var selfActive = gameObject == null || gameObject.Active;
                tf.ActiveInHierarchy = selfActive && (parent == null || parent.ActiveInHierarchy);
                tf.Resolved = true;
            }

            return chain[0];
        }

        private int ResolveMesh(int fileId, long pathId)
        {
            if (pathId == 0)
            {
                _scene.MeshesMissing++;
                return -1;
            }

            AssetInst? meshAsset;
            try
            {
                meshAsset = _workspace.GetAssetInst(_fileInst, fileId, pathId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                meshAsset = null;
            }

            // built-in meshes (cube, sphere...) live in files that don't ship with the game's data,
            // and meshes in a file that isn't loaded can't be found either
            if (meshAsset == null || meshAsset.Type != AssetClassID.Mesh)
            {
                _scene.MeshesMissing++;
                return -1;
            }

            var key = (meshAsset.FileInstance, meshAsset.PathId);
            if (_meshCache.TryGetValue(key, out var cachedIndex))
                return cachedIndex;

            var index = -1;
            try
            {
                var meshBf = _workspace.GetBaseField(meshAsset);
                if (meshBf == null)
                    throw new InvalidOperationException("the mesh could not be read");

                var version = new UnityVersion(meshAsset.FileInstance.file.Metadata.UnityVersion);
                var meshObj = new MeshObj(meshAsset.FileInstance, meshBf, version);

                var nameField = meshBf["m_Name"];
                var name = nameField.IsDummy ? string.Empty : nameField.AsString ?? string.Empty;

                var data = BuildMeshData(name, meshObj);
                if (data != null)
                {
                    index = _scene.Meshes.Count;
                    _scene.Meshes.Add(data);
                }
                else
                {
                    _scene.MeshesFailed++;
                    AddWarning($"Mesh \"{name}\" in {meshAsset.FileInstance.name} has no triangles to show.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _scene.MeshesFailed++;
                AddWarning($"A mesh in {meshAsset.FileInstance.name} (path id {meshAsset.PathId}) failed to decode: {ex.Message}");
            }

            _meshCache[key] = index;
            return index;
        }

        private static SceneMeshData? BuildMeshData(string name, MeshObj mesh)
        {
            var vertexCount = mesh.VertexCount;
            if (vertexCount <= 0 || mesh.Vertices.Length < vertexCount * 3)
                return null;

            var triangles = mesh.GetTriangleIndices();
            if (triangles.Length == 0)
                return null;

            // positions are normally 3 floats, but some formats pad them to 4
            var posStride = mesh.Vertices.Length / vertexCount;
            var normalStride = mesh.Normals.Length / vertexCount;
            var hasNormals = normalStride >= 3 && mesh.Normals.Length >= vertexCount * normalStride;

            var vertices = new float[vertexCount * 6];
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);

            for (var i = 0; i < vertexCount; i++)
            {
                var x = mesh.Vertices[i * posStride];
                var y = mesh.Vertices[i * posStride + 1];
                var z = mesh.Vertices[i * posStride + 2];

                vertices[i * 6] = x;
                vertices[i * 6 + 1] = y;
                vertices[i * 6 + 2] = z;

                if (hasNormals)
                {
                    vertices[i * 6 + 3] = mesh.Normals[i * normalStride];
                    vertices[i * 6 + 4] = mesh.Normals[i * normalStride + 1];
                    vertices[i * 6 + 5] = mesh.Normals[i * normalStride + 2];
                }

                if (float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z))
                {
                    min = Vector3.Min(min, new Vector3(x, y, z));
                    max = Vector3.Max(max, new Vector3(x, y, z));
                }
            }

            if (min.X > max.X)
                return null; // no usable vertex at all

            if (!hasNormals)
            {
                ComputeNormals(vertices, triangles);
            }

            return new SceneMeshData
            {
                Name = name,
                Vertices = vertices,
                Indices = triangles,
                BoundsMin = min,
                BoundsMax = max
            };
        }

        /// <summary>
        /// For meshes with no normals: gives every vertex the average of the face normals around it.
        /// </summary>
        private static void ComputeNormals(float[] vertices, uint[] triangles)
        {
            for (var i = 0; i + 2 < triangles.Length; i += 3)
            {
                var ia = (int)triangles[i] * 6;
                var ib = (int)triangles[i + 1] * 6;
                var ic = (int)triangles[i + 2] * 6;

                var a = new Vector3(vertices[ia], vertices[ia + 1], vertices[ia + 2]);
                var b = new Vector3(vertices[ib], vertices[ib + 1], vertices[ib + 2]);
                var c = new Vector3(vertices[ic], vertices[ic + 1], vertices[ic + 2]);

                var faceNormal = Vector3.Cross(b - a, c - a);
                if (!float.IsFinite(faceNormal.X) || !float.IsFinite(faceNormal.Y) || !float.IsFinite(faceNormal.Z))
                    continue;

                AddNormal(vertices, ia, faceNormal);
                AddNormal(vertices, ib, faceNormal);
                AddNormal(vertices, ic, faceNormal);
            }

            for (var i = 0; i < vertices.Length; i += 6)
            {
                var n = new Vector3(vertices[i + 3], vertices[i + 4], vertices[i + 5]);
                n = n.LengthSquared() > 1e-20f ? Vector3.Normalize(n) : Vector3.UnitY;
                vertices[i + 3] = n.X;
                vertices[i + 4] = n.Y;
                vertices[i + 5] = n.Z;
            }
        }

        private static void AddNormal(float[] vertices, int vertexOffset, Vector3 normal)
        {
            vertices[vertexOffset + 3] += normal.X;
            vertices[vertexOffset + 4] += normal.Y;
            vertices[vertexOffset + 5] += normal.Z;
        }

        private void AddWarning(string message)
        {
            if (_scene.Warnings.Count < MAX_WARNINGS)
            {
                _scene.Warnings.Add(message);
            }
            else if (_scene.Warnings.Count == MAX_WARNINGS)
            {
                _scene.Warnings.Add("More warnings were left out.");
            }
        }
    }
}
