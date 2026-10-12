using AssetsTools.NET;
using AssetsTools.NET.Extra;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UABEANext4.Logic.Mesh;

// based on https://github.com/Perfare/AssetStudio/blob/master/AssetStudio/Classes/Mesh.cs
// this is not in a plugin due to being needed by the previewer, plus it's shared by multiple plugins
public class MeshObj
{
    public ushort[] Indices;
    public List<Channel> Channels;
    public float[] Vertices;
    public float[] Normals;
    public float[] Tangents;
    public float[] Colors;
    public float[][] UVs;

    /// <summary>
    /// Number of vertices in the vertex buffer (m_VertexData.m_VertexCount).
    /// </summary>
    public int VertexCount;
    /// <summary>
    /// The whole index buffer widened to 32 bits, regardless of whether the mesh stores 16 or 32 bit indices.
    /// Indices of all submeshes are in here, back to back. Use <see cref="GetTriangleIndices"/> for drawable triangles.
    /// </summary>
    public uint[] IndexBuffer;
    public List<SubMeshInfo> SubMeshes;

    // size of one index in the original data (2 or 4), used to turn a submesh's firstByte into an index
    private int _bytesPerIndex = 2;

    public class SubMeshInfo
    {
        public uint FirstByte;
        public uint IndexCount;
        public int Topology;
        public uint BaseVertex;
    }

    public MeshObj()
    {
        Indices = [];
        Channels = [];
        Vertices = [];
        Normals = [];
        Tangents = [];
        Colors = [];
        UVs = [];
        IndexBuffer = [];
        SubMeshes = [];
    }

    public MeshObj(AssetsFileInstance fileInst, AssetTypeValueField baseField, UnityVersion version)
    {
        Indices = [];
        Channels = [];
        Vertices = [];
        Normals = [];
        Tangents = [];
        Colors = [];
        UVs = [];
        IndexBuffer = [];
        SubMeshes = [];

        Read(fileInst, baseField, version);
    }

    private void Read(AssetsFileInstance fileInst, AssetTypeValueField baseField, UnityVersion version)
    {
        ReadIndicesData(baseField);
        ReadSubMeshes(baseField);
        ReadChannels(baseField);
        ReadVertexData(fileInst, baseField, version);
    }

    private void ReadIndicesData(AssetTypeValueField baseField)
    {
        var indicesField = baseField["m_IndexBuffer.Array"].AsByteArray;
        var ushortArray = new ushort[indicesField.Length / 2];
        for (var i = 0; i + 1 < indicesField.Length; i += 2)
        {
            ushortArray[i / 2] = (ushort)(indicesField[i + 1] << 8 | indicesField[i]);
        }
        Indices = ushortArray;

        // meshes with more than 65535 vertices store 32 bit indices. Unity 2017.3+ has m_IndexFormat
        // (0 = 16 bit, 1 = 32 bit). older versions have m_Use16BitIndices (nonzero = 16 bit).
        var is32Bit = false;
        var indexFormatField = baseField["m_IndexFormat"];
        if (!indexFormatField.IsDummy)
        {
            is32Bit = indexFormatField.AsInt == 1;
        }
        else
        {
            var use16BitField = baseField["m_Use16BitIndices"];
            if (!use16BitField.IsDummy)
                is32Bit = use16BitField.AsInt == 0;
        }

        _bytesPerIndex = is32Bit ? 4 : 2;
        if (is32Bit)
        {
            var uintArray = new uint[indicesField.Length / 4];
            for (var i = 0; i + 3 < indicesField.Length; i += 4)
            {
                uintArray[i / 4] = (uint)(indicesField[i] | indicesField[i + 1] << 8 | indicesField[i + 2] << 16 | indicesField[i + 3] << 24);
            }
            IndexBuffer = uintArray;
        }
        else
        {
            var uintArray = new uint[ushortArray.Length];
            for (var i = 0; i < ushortArray.Length; i++)
            {
                uintArray[i] = ushortArray[i];
            }
            IndexBuffer = uintArray;
        }
    }

    private void ReadSubMeshes(AssetTypeValueField baseField)
    {
        var subMeshes = new List<SubMeshInfo>();
        var subMeshArray = baseField["m_SubMeshes.Array"];
        if (!subMeshArray.IsDummy)
        {
            foreach (var subMeshField in subMeshArray)
            {
                var firstByteField = subMeshField["firstByte"];
                var indexCountField = subMeshField["indexCount"];
                var topologyField = subMeshField["topology"];
                var baseVertexField = subMeshField["baseVertex"];

                subMeshes.Add(new SubMeshInfo
                {
                    FirstByte = firstByteField.IsDummy ? 0 : firstByteField.AsUInt,
                    IndexCount = indexCountField.IsDummy ? 0 : indexCountField.AsUInt,
                    Topology = topologyField.IsDummy ? 0 : topologyField.AsInt,
                    BaseVertex = baseVertexField.IsDummy ? 0 : baseVertexField.AsUInt
                });
            }
        }
        SubMeshes = subMeshes;
    }

    /// <summary>
    /// Builds the list of triangles to draw as a flat list of 32 bit indices into the vertex arrays.
    /// This walks each submesh, adds its base vertex, expands quads and triangle strips, skips lines and
    /// points, and drops any triangle that points outside the vertex buffer.
    /// If the mesh has no submesh info, the raw index buffer is used as a plain triangle list.
    /// </summary>
    public uint[] GetTriangleIndices()
    {
        var result = new List<uint>(IndexBuffer.Length);
        var vertexCount = (uint)Math.Max(VertexCount, 0);

        void AddTriangle(uint a, uint b, uint c)
        {
            if (a < vertexCount && b < vertexCount && c < vertexCount)
            {
                result.Add(a);
                result.Add(b);
                result.Add(c);
            }
        }

        if (SubMeshes.Count == 0)
        {
            for (var i = 0; i + 2 < IndexBuffer.Length; i += 3)
            {
                AddTriangle(IndexBuffer[i], IndexBuffer[i + 1], IndexBuffer[i + 2]);
            }
            return result.ToArray();
        }

        // submeshes record firstByte, but IndexBuffer is widened to 32 bit, so convert to an element index
        foreach (var subMesh in SubMeshes)
        {
            var start = (long)(subMesh.FirstByte / _bytesPerIndex);
            var count = (long)subMesh.IndexCount;
            if (start < 0 || start >= IndexBuffer.Length)
                continue;
            if (start + count > IndexBuffer.Length)
                count = IndexBuffer.Length - start;

            var baseVertex = subMesh.BaseVertex;
            uint Idx(long i) => IndexBuffer[start + i] + baseVertex;

            switch (subMesh.Topology)
            {
                case 0: // triangles
                {
                    for (long i = 0; i + 2 < count; i += 3)
                    {
                        AddTriangle(Idx(i), Idx(i + 1), Idx(i + 2));
                    }
                    break;
                }
                case 1: // triangle strip (deprecated)
                {
                    for (long i = 0; i + 2 < count; i++)
                    {
                        if ((i & 1) == 0)
                            AddTriangle(Idx(i), Idx(i + 1), Idx(i + 2));
                        else
                            AddTriangle(Idx(i + 1), Idx(i), Idx(i + 2));
                    }
                    break;
                }
                case 2: // quads
                {
                    for (long i = 0; i + 3 < count; i += 4)
                    {
                        AddTriangle(Idx(i), Idx(i + 1), Idx(i + 2));
                        AddTriangle(Idx(i), Idx(i + 2), Idx(i + 3));
                    }
                    break;
                }
                // lines, line strips and points can't be shown as solid surfaces
            }
        }

        return result.ToArray();
    }


    private void ReadChannels(AssetTypeValueField baseField)
    {
        var channelFields = baseField["m_VertexData"]["m_Channels.Array"];
        var channels = new List<Channel>();
        foreach (var channelField in channelFields)
        {
            channels.Add(new Channel(channelField));
        }
        Channels = channels;
    }

    private List<int> GetStreamLengths(UnityVersion version)
    {
        var streamLengths = new List<int>();
        var streamCount = Channels.Max(c => c.stream) + 1;
        for (var i = 0; i < streamCount; i++)
        {
            var maxEndOffset = 0;
            for (var j = 0; j < Channels.Count; j++)
            {
                if (Channels[j].stream == i)
                {
                    var channel = Channels[j];
                    var size = GetFormatSize(ToVertexFormatV2(channel.format, version));
                    var endOffset = channel.offset + (channel.dimension & 0xf) * size;
                    maxEndOffset = endOffset > maxEndOffset ? endOffset : maxEndOffset;
                }
            }
            streamLengths.Add(maxEndOffset);
        }

        return streamLengths;
    }

    private static int GetFormatSize(VertexFormatV2 format)
    {
        return format switch
        {
            VertexFormatV2.Float => 4,
            VertexFormatV2.Float16 => 2,
            VertexFormatV2.UNorm8 => 1,
            VertexFormatV2.SNorm8 => 1,
            VertexFormatV2.UNorm16 => 2,
            VertexFormatV2.SNorm16 => 2,
            VertexFormatV2.UInt8 => 1,
            VertexFormatV2.SInt8 => 1,
            VertexFormatV2.UInt16 => 2,
            VertexFormatV2.SInt16 => 2,
            VertexFormatV2.UInt32 => 4,
            VertexFormatV2.SInt32 => 4,
            _ => throw new Exception($"Unknown format {format}")
        };
    }

    private static VertexFormatV2 ToVertexFormatV2(int format, UnityVersion version)
    {
        if (version.major >= 2019)
        {
            return (VertexFormatV2)format;
        }
        else if (version.major >= 2017)
        {
            return (VertexFormatV1)format switch
            {
                VertexFormatV1.Float => VertexFormatV2.Float,
                VertexFormatV1.Float16 => VertexFormatV2.Float16,
                VertexFormatV1.Color or
                VertexFormatV1.UNorm8 => VertexFormatV2.UNorm8,
                VertexFormatV1.SNorm8 => VertexFormatV2.SNorm8,
                VertexFormatV1.UNorm16 => VertexFormatV2.UNorm16,
                VertexFormatV1.SNorm16 => VertexFormatV2.SNorm16,
                VertexFormatV1.UInt8 => VertexFormatV2.UInt8,
                VertexFormatV1.SInt8 => VertexFormatV2.SInt8,
                VertexFormatV1.UInt16 => VertexFormatV2.UInt16,
                VertexFormatV1.SInt16 => VertexFormatV2.SInt16,
                VertexFormatV1.UInt32 => VertexFormatV2.UInt32,
                VertexFormatV1.SInt32 => VertexFormatV2.SInt32,
                _ => throw new Exception($"Unknown format {format}")
            };
        }
        else
        {
            return (VertexChannelFormat)format switch
            {
                VertexChannelFormat.Float => VertexFormatV2.Float,
                VertexChannelFormat.Float16 => VertexFormatV2.Float16,
                VertexChannelFormat.Color => VertexFormatV2.UNorm8,
                VertexChannelFormat.Byte => VertexFormatV2.UInt8,
                VertexChannelFormat.UInt32 => VertexFormatV2.UInt32,
                _ => throw new Exception($"Unknown format {format}")
            };
        }
    }

    private static bool IsFormatInt(VertexFormatV2 format)
    {
        return format switch
        {
            VertexFormatV2.UInt8 => true,
            VertexFormatV2.SInt8 => true,
            VertexFormatV2.UInt16 => true,
            VertexFormatV2.SInt16 => true,
            VertexFormatV2.UInt32 => true,
            VertexFormatV2.SInt32 => true,
            _ => false
        };
    }

    private static byte[] GetVertexData(AssetsFileInstance fileInst, AssetTypeValueField baseField)
    {
        var usesStreamData = false;
        var offset = 0U;
        var size = 0U;
        var path = string.Empty;

        var streamData = baseField["m_StreamData"];
        if (!streamData.IsDummy)
        {
            offset = streamData["offset"].AsUInt;
            size = streamData["size"].AsUInt;
            path = streamData["path"].AsString;
            usesStreamData = size > 0 && path != string.Empty;
        }

        if (usesStreamData)
        {
            if (fileInst.parentBundle != null && path.StartsWith("archive:/"))
            {
                var archiveTrimmedPath = path;
                if (archiveTrimmedPath.StartsWith("archive:/"))
                    archiveTrimmedPath = archiveTrimmedPath.Substring(9);

                archiveTrimmedPath = Path.GetFileName(archiveTrimmedPath);

                AssetBundleFile bundle = fileInst.parentBundle.file;

                AssetsFileReader reader = bundle.DataReader;
                List<AssetBundleDirectoryInfo> dirInf = bundle.BlockAndDirInfo.DirectoryInfos;
                for (int i = 0; i < dirInf.Count; i++)
                {
                    AssetBundleDirectoryInfo info = dirInf[i];
                    if (info.Name == archiveTrimmedPath)
                    {
                        byte[] meshData;
                        lock (bundle.DataReader)
                        {
                            reader.Position = info.Offset + offset;
                            meshData = reader.ReadBytes((int)size);
                        }
                        return meshData;
                    }
                }
            }

            var rootPath = Path.GetDirectoryName(fileInst.path)
                ?? throw new FileNotFoundException("Can't find resS for mesh");

            var fixedStreamPath = path;

            // user may have extracted serialized file and resS from bundle to disk
            var bundleInst = fileInst.parentBundle;
            if (bundleInst == null && path.StartsWith("archive:/"))
            {
                fixedStreamPath = Path.GetFileName(fixedStreamPath);
            }
            if (!Path.IsPathRooted(fixedStreamPath) && rootPath != null)
            {
                fixedStreamPath = Path.Combine(rootPath, fixedStreamPath);
            }

            if (File.Exists(fixedStreamPath))
            {
                var stream = File.OpenRead(fixedStreamPath);
                stream.Position = offset;
                var data = new byte[size];
                stream.Read(data, 0, (int)size);
                return data;
            }
            // we still haven't found it yet. maybe a data.unity3d bundle?
            // in this case, we won't have the archive:/ prefix, so use the original path
            else if (bundleInst != null && TryGetBundleFileIndex(bundleInst.file, path, out var fileIdx))
            {
                var bundle = bundleInst.file;
                bundle.GetFileRange(fileIdx, out var bunOffset, out var _);
                var reader = bundle.DataReader;
                reader.Position = bunOffset + offset;
                return reader.ReadBytes((int)size);
            }
            else
            {
                throw new FileNotFoundException("Can't find resS for mesh");
            }
        }
        else
        {
            return baseField["m_VertexData"]["m_DataSize"].AsByteArray;
        }
    }

    private static bool TryGetBundleFileIndex(AssetBundleFile bunFile, string name, out int dirInf)
    {
        dirInf = bunFile.BlockAndDirInfo.DirectoryInfos.FindIndex(i => i.Name == name);
        return dirInf != -1;
    }

    private void ReadVertexData(AssetsFileInstance fileInst, AssetTypeValueField baseField, UnityVersion version)
    {
        var vertexCount = baseField["m_VertexData"]["m_VertexCount"].AsInt;
        VertexCount = vertexCount;
        var vertexData = GetVertexData(fileInst, baseField);
        var streamLengths = GetStreamLengths(version);
        var startPos = 0;
        for (var strIdx = 0; strIdx < streamLengths.Count; strIdx++)
        {
            var streamLength = streamLengths[strIdx];
            for (var chnIdx = 0; chnIdx < Channels.Count; chnIdx++)
            {
                var channel = Channels[chnIdx];
                if (channel.stream != strIdx)
                    continue;

                var dimension = channel.dimension & 0xf;
                var vertexFormat = ToVertexFormatV2(channel.format, version);
                var offset = channel.offset + startPos;
                var size = GetFormatSize(vertexFormat) * dimension;
                var data = new byte[size * vertexCount];
                for (var i = 0; i < vertexCount; i++)
                {
                    Buffer.BlockCopy(vertexData, offset + i * streamLength, data, i * size, size);
                }

                int[]? intItems = null;
                float[]? floatItems = null;
                if (IsFormatInt(vertexFormat))
                    intItems = ConvertIntArray(data, dimension, vertexFormat);
                else
                    floatItems = ConvertFloatArray(data, dimension, vertexFormat);

                SetCorrectArray(intItems!, floatItems!, chnIdx, version);
            }
            startPos += streamLengths[strIdx] * vertexCount;
        }
    }

    private void SetCorrectArray(int[] intItems, float[] floatItems, int channelIndex, UnityVersion version)
    {
        if (version.major >= 2018)
        {
            var channelType = (ChannelTypeV3)channelIndex;
            switch (channelType)
            {
                case ChannelTypeV3.Vertex: Vertices = floatItems; break;
                case ChannelTypeV3.Normal: Normals = floatItems; break;
                case ChannelTypeV3.Tangent: Tangents = floatItems; break;
                case ChannelTypeV3.Color: Colors = floatItems; break;
                case ChannelTypeV3.TexCoord0:
                case ChannelTypeV3.TexCoord1:
                case ChannelTypeV3.TexCoord2:
                case ChannelTypeV3.TexCoord3:
                case ChannelTypeV3.TexCoord4:
                case ChannelTypeV3.TexCoord5:
                case ChannelTypeV3.TexCoord6:
                case ChannelTypeV3.TexCoord7:
                {
                    if (UVs.Length == 0)
                    {
                        UVs = new float[8][];
                    }
                    UVs[(int)channelType - (int)ChannelTypeV3.TexCoord0] = floatItems;
                    break;
                }
                case ChannelTypeV3.BlendWeight:
                case ChannelTypeV3.BlendIndices:
                {
                    // ignore for now
                    break;
                }
            }
        }
        else // if (version.major >= 5)
        {
            var channelType = (ChannelTypeV2)channelIndex;
            switch (channelType)
            {
                case ChannelTypeV2.Vertex: Vertices = floatItems; break;
                case ChannelTypeV2.Normal: Normals = floatItems; break;
                case ChannelTypeV2.Color: Colors = floatItems; break;
                case ChannelTypeV2.TexCoord0:
                case ChannelTypeV2.TexCoord1:
                case ChannelTypeV2.TexCoord2:
                case ChannelTypeV2.TexCoord3:
                {
                    if (UVs.Length == 0)
                    {
                        UVs = new float[4][];
                    }
                    UVs[(int)channelType - (int)ChannelTypeV2.TexCoord0] = floatItems;
                    break;
                }
                case ChannelTypeV2.Tangent: Tangents = floatItems; break;
            }
        }
    }

    private static int[] ConvertIntArray(byte[] data, int dims, VertexFormatV2 format)
    {
        var size = GetFormatSize(format);
        var count = data.Length / size;
        var items = new int[count];
        switch (format)
        {
            case VertexFormatV2.UInt8:
            case VertexFormatV2.SInt8:
            {
                for (var i = 0; i < count; i++)
                {
                    items[i] = data[i];
                }
                return items;
            }
            case VertexFormatV2.UInt16:
            case VertexFormatV2.SInt16:
            {
                var src = 0;
                for (var i = 0; i < count; i++, src += 2)
                {
                    items[i] = data[src] | data[src + 1] << 8;
                }
                return items;
            }
            case VertexFormatV2.UInt32:
            case VertexFormatV2.SInt32:
            {
                var src = 0;
                for (var i = 0; i < count; i++, src += 4)
                {
                    items[i] = data[src] | data[src + 1] << 8 | data[src + 2] << 16 | data[src + 3] << 24;
                }
                return items;
            }
            default:
                throw new Exception($"Unknown format {format}");
        }
    }

    private static float[] ConvertFloatArray(byte[] data, int dims, VertexFormatV2 format)
    {
        var size = GetFormatSize(format);
        var count = data.Length / size;
        var items = new float[count];
        switch (format)
        {
            case VertexFormatV2.Float:
            {
                var src = 0;
                for (var i = 0; i < count; i++, src += 4)
                {
                    items[i] = BitConverter.ToSingle(data, src);
                }
                return items;
            }
            case VertexFormatV2.Float16:
            {
                var src = 0;
                for (var i = 0; i < count; i++, src += 2)
                {
                    items[i] = (float)BitConverter.UInt16BitsToHalf((ushort)(data[src] | data[src + 1] << 8));
                }
                return items;
            }
            case VertexFormatV2.UNorm8:
            {
                for (var i = 0; i < count; i++)
                {
                    items[i] = data[i] / 255f;
                }
                return items;
            }
            case VertexFormatV2.SNorm8:
            {
                for (var i = 0; i < count; i++)
                {
                    items[i] = Math.Max((sbyte)data[i] / 127f, -1f);
                }
                return items;
            }
            case VertexFormatV2.UNorm16:
            {
                var src = 0;
                for (var i = 0; i < count; i++, src += 2)
                {
                    items[i] = (data[src] | data[src + 1] << 8) / 65535f;
                }
                return items;
            }
            case VertexFormatV2.SNorm16:
            {
                var src = 0;
                for (var i = 0; i < count; i++, src += 2)
                {
                    items[i] = Math.Max((short)(data[src] | data[src + 1] << 8) / 32767f, -1f);
                }
                return items;
            }
            default:
                throw new Exception($"Unknown format {format}");
        }
    }
}
