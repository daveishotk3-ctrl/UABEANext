using System.Collections.Generic;
using System.Numerics;

namespace UABEANext4.Logic.Project;

/// <summary>
/// The mesh data of one unique Mesh asset, ready to be uploaded to the GPU.
/// </summary>
public sealed class SceneMeshData
{
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Interleaved vertex data, 6 floats per vertex: position (x, y, z) then normal (x, y, z).
    /// Everything is in Unity's own coordinate space.
    /// </summary>
    public float[] Vertices { get; init; } = [];

    /// <summary>
    /// Triangle list indices into <see cref="Vertices"/> (counted in vertices, not floats).
    /// </summary>
    public uint[] Indices { get; init; } = [];

    public Vector3 BoundsMin { get; init; }
    public Vector3 BoundsMax { get; init; }
}

/// <summary>
/// One placed mesh: which mesh, and where it is in the world.
/// </summary>
public sealed class SceneInstance
{
    public string Name { get; init; } = string.Empty;
    public int MeshIndex { get; init; }

    /// <summary>
    /// Local-to-world matrix in Unity space, using System.Numerics' row vector convention
    /// (a point is transformed as <c>Vector3.Transform(point, World)</c>).
    /// It is built from every parent's position, rotation and scale, like Unity does.
    /// </summary>
    public Matrix4x4 World { get; init; } = Matrix4x4.Identity;

    /// <summary>
    /// The GameObject and all of its parents are active.
    /// </summary>
    public bool ActiveInHierarchy { get; init; } = true;

    /// <summary>
    /// The renderer component is enabled.
    /// </summary>
    public bool RendererEnabled { get; init; } = true;
}

public sealed class ProjectScene
{
    public List<SceneMeshData> Meshes { get; } = [];
    public List<SceneInstance> Instances { get; } = [];

    /// <summary>
    /// World space bounds of every instance, in Unity space.
    /// </summary>
    public Vector3 BoundsMin { get; set; }
    public Vector3 BoundsMax { get; set; }
    public bool HasBounds { get; set; }

    public int FileCount { get; set; }
    public int GameObjectsVisited { get; set; }
    public int MeshesMissing { get; set; }
    public int MeshesFailed { get; set; }
    public int InstancesWithoutTransform { get; set; }
    public List<string> Warnings { get; } = [];
}
