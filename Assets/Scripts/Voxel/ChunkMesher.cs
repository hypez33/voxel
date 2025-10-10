using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Voxels
{
    public sealed class ChunkMesher : IDisposable
    {
        private NativeArray<Color32> _palette;

        public ChunkMesher()
        {
            _palette = BlockPalette.GetNativePalette(Allocator.Persistent);
        }

        public void Dispose()
        {
            if (_palette.IsCreated)
            {
                _palette.Dispose();
            }
        }

        public void RebuildChunk(Chunk chunk)
        {
            var blocks = chunk.Blocks;
            if (!blocks.IsCreated)
            {
                return;
            }

            using var vertices = new NativeList<float3>(Allocator.TempJob);
            using var normals = new NativeList<float3>(Allocator.TempJob);
            using var colors = new NativeList<Color32>(Allocator.TempJob);
            using var indices = new NativeList<int>(Allocator.TempJob);
            using var mask = new NativeArray<MaskEntry>(VoxelMetrics.CHUNK_SIZE_X * VoxelMetrics.CHUNK_SIZE_Y, Allocator.TempJob);

            var job = new GreedyMesherJob
            {
                Blocks = blocks,
                Palette = _palette,
                Vertices = vertices,
                Normals = normals,
                Colors = colors,
                Indices = indices,
                Mask = mask,
                ChunkSize = new int3(VoxelMetrics.CHUNK_SIZE_X, VoxelMetrics.CHUNK_SIZE_Y, VoxelMetrics.CHUNK_SIZE_Z),
                VoxelSize = VoxelMetrics.VOXEL_SIZE
            };

            job.Run();

            var vertexCount = vertices.Length;
            var indexCount = indices.Length;
            var meshDataArray = Mesh.AllocateWritableMeshData(1);
            var meshData = meshDataArray[0];

            meshData.SetVertexBufferParams(vertexCount,
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
                new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3, 1),
                new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4, 2));

            var positionBuffer = meshData.GetVertexData<float3>(0);
            var normalBuffer = meshData.GetVertexData<float3>(1);
            var colorBuffer = meshData.GetVertexData<Color32>(2);

            for (int i = 0; i < vertexCount; i++)
            {
                positionBuffer[i] = vertices[i];
                normalBuffer[i] = normals[i];
                colorBuffer[i] = colors[i];
            }

            meshData.SetIndexBufferParams(indexCount, IndexFormat.UInt32);
            var indexBuffer = meshData.GetIndexData<int>();
            for (int i = 0; i < indexCount; i++)
            {
                indexBuffer[i] = indices[i];
            }

            meshData.subMeshCount = 1;
            meshData.SetSubMesh(0, new SubMeshDescriptor(0, indexCount)
            {
                topology = MeshTopology.Triangles,
                bounds = chunk.Bounds
            }, MeshUpdateFlags.DontRecalculateBounds);

            chunk.UploadMesh(meshDataArray, vertexCount, indexCount);
        }

        [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
        private struct GreedyMesherJob : IJob
        {
            [ReadOnly] public NativeArray<byte> Blocks;
            [ReadOnly] public NativeArray<Color32> Palette;
            public NativeList<float3> Vertices;
            public NativeList<float3> Normals;
            public NativeList<Color32> Colors;
            public NativeList<int> Indices;
            public NativeArray<MaskEntry> Mask;
            public int3 ChunkSize;
            public float VoxelSize;

            private static readonly int3[] AxisVectors =
            {
                new int3(1, 0, 0),
                new int3(0, 1, 0),
                new int3(0, 0, 1)
            };

            public void Execute()
            {
                for (int axis = 0; axis < 3; axis++)
                {
                    int u = (axis + 1) % 3;
                    int v = (axis + 2) % 3;
                    int sizeAxis = GetDimension(axis);
                    int sizeU = GetDimension(u);
                    int sizeV = GetDimension(v);

                    for (int slice = -1; slice < sizeAxis; slice++)
                    {
                        BuildMask(axis, u, v, slice, sizeU, sizeV);
                        EmitQuads(axis, u, v, slice, sizeU, sizeV);
                    }
                }
            }

            private int GetDimension(int axis)
            {
                return axis switch
                {
                    0 => ChunkSize.x,
                    1 => ChunkSize.y,
                    _ => ChunkSize.z
                };
            }

            private void BuildMask(int axis, int u, int v, int slice, int sizeU, int sizeV)
            {
                int maskLength = sizeU * sizeV;
                for (int i = 0; i < maskLength; i++)
                {
                    Mask[i] = default;
                }

                var coord = new int3(0, 0, 0);
                var neighbour = new int3(0, 0, 0);

                for (int j = 0; j < sizeV; j++)
                {
                    coord[v] = j;
                    neighbour[v] = j;

                    for (int i = 0; i < sizeU; i++)
                    {
                        coord[u] = i;
                        neighbour[u] = i;
                        coord[axis] = slice;
                        neighbour[axis] = slice + 1;

                        byte blockA = 0;
                        byte blockB = 0;
                        bool hasA = slice >= 0;
                        bool hasB = slice < GetDimension(axis) - 1;

                        if (hasA)
                        {
                            blockA = SampleBlock(coord);
                        }

                        if (hasB)
                        {
                            blockB = SampleBlock(neighbour);
                        }

                        bool solidA = hasA && VoxelTypes.IsSolid(blockA);
                        bool solidB = hasB && VoxelTypes.IsSolid(blockB);
                        bool opaqueA = solidA && VoxelTypes.IsOpaque(blockA);
                        bool opaqueB = solidB && VoxelTypes.IsOpaque(blockB);

                        var entry = default(MaskEntry);
                        bool visible = false;
                        if (solidA && !opaqueB)
                        {
                            visible = true;
                            entry.Block = blockA;
                            entry.Normal = 1;
                        }
                        else if (solidB && !opaqueA)
                        {
                            visible = true;
                            entry.Block = blockB;
                            entry.Normal = -1;
                        }
                        else if (opaqueA && !opaqueB)
                        {
                            visible = true;
                            entry.Block = blockA;
                            entry.Normal = 1;
                        }
                        else if (opaqueB && !opaqueA)
                        {
                            visible = true;
                            entry.Block = blockB;
                            entry.Normal = -1;
                        }

                        if (visible)
                        {
                            entry.Visible = 1;
                            Mask[i + j * sizeU] = entry;
                        }
                    }
                }
            }

            private void EmitQuads(int axis, int u, int v, int slice, int sizeU, int sizeV)
            {
                for (int j = 0; j < sizeV; j++)
                {
                    int i = 0;
                    while (i < sizeU)
                    {
                        int maskIndex = i + j * sizeU;
                        var entry = Mask[maskIndex];
                        if (entry.Visible == 0)
                        {
                            i++;
                            continue;
                        }

                        int width = 1;
                        while (i + width < sizeU)
                        {
                            var next = Mask[maskIndex + width];
                            if (next.Visible == 0 || next.Block != entry.Block || next.Normal != entry.Normal)
                            {
                                break;
                            }

                            width++;
                        }

                        int height = 1;
                        bool done = false;
                        while (!done && j + height < sizeV)
                        {
                            for (int k = 0; k < width; k++)
                            {
                                var test = Mask[maskIndex + k + height * sizeU];
                                if (test.Visible == 0 || test.Block != entry.Block || test.Normal != entry.Normal)
                                {
                                    done = true;
                                    break;
                                }
                            }

                            if (!done)
                            {
                                height++;
                            }
                        }

                        EmitQuad(entry, axis, u, v, slice, i, j, width, height);

                        for (int y = 0; y < height; y++)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                Mask[maskIndex + x + y * sizeU] = default;
                            }
                        }

                        i += width;
                    }
                }
            }

            private void EmitQuad(in MaskEntry entry, int axis, int u, int v, int slice, int startU, int startV, int width, int height)
            {
                bool positive = entry.Normal > 0;
                float3 normal = float3.zero;
                normal[axis] = positive ? 1f : -1f;

                float3 du = float3.zero; du[u] = width;
                float3 dv = float3.zero; dv[v] = height;
                float3 baseCorner = float3.zero; baseCorner[u] = startU; baseCorner[v] = startV; baseCorner[axis] = slice + 1;
                var vertexStart = Vertices.Length;
                float3 offset0 = float3.zero;
                float3 offset1 = du;
                float3 offset2 = du + dv;
                float3 offset3 = dv;

                AddVertex(baseCorner, offset0, normal, axis, u, v, positive, entry.Block);
                AddVertex(baseCorner, offset1, normal, axis, u, v, positive, entry.Block);
                AddVertex(baseCorner, offset2, normal, axis, u, v, positive, entry.Block);
                AddVertex(baseCorner, offset3, normal, axis, u, v, positive, entry.Block);

                if (positive)
                {
                    Indices.Add(vertexStart + 0);
                    Indices.Add(vertexStart + 1);
                    Indices.Add(vertexStart + 2);
                    Indices.Add(vertexStart + 0);
                    Indices.Add(vertexStart + 2);
                    Indices.Add(vertexStart + 3);
                }
                else
                {
                    Indices.Add(vertexStart + 0);
                    Indices.Add(vertexStart + 2);
                    Indices.Add(vertexStart + 1);
                    Indices.Add(vertexStart + 0);
                    Indices.Add(vertexStart + 3);
                    Indices.Add(vertexStart + 2);
                }
            }

            private void AddVertex(float3 baseCorner, float3 offset, float3 normal, int axis, int u, int v, bool positive, byte blockId)
            {
                float3 position = (baseCorner + offset) * VoxelSize;
                Vertices.Add(position);
                Normals.Add(normal);
                byte ao = ComputeAO(baseCorner, offset, axis, u, v, positive);
                Colors.Add(ApplyAO(blockId, ao));
            }

            private byte ComputeAO(float3 baseCorner, float3 offset, int axis, int u, int v, bool positive)
            {
                int3 axisDir = AxisVectors[axis] * (positive ? 1 : -1);
                int3 uAxis = AxisVectors[u];
                int3 vAxis = AxisVectors[v];

                int3 vertex = (int3)math.round(baseCorner + offset);
                int3 uDir = (offset[u] > 0f) ? uAxis : -uAxis;
                int3 vDir = (offset[v] > 0f) ? vAxis : -vAxis;

                int3 sideA = vertex + axisDir + uDir;
                int3 sideB = vertex + axisDir + vDir;
                int3 corner = vertex + axisDir + uDir + vDir;

                bool s1 = IsOpaque(sideA);
                bool s2 = IsOpaque(sideB);
                bool c = IsOpaque(corner);

                if (s1 && s2)
                {
                    return 3;
                }

                byte occlusion = 0;
                if (s1)
                {
                    occlusion++;
                }

                if (s2)
                {
                    occlusion++;
                }

        if (c)
                {
                    occlusion++;
                }

                return occlusion;
            }

            private Color32 ApplyAO(byte blockId, byte ao)
            {
                int paletteIndex = math.clamp((int)blockId, 0, Palette.Length - 1);
                var baseColor = Palette[paletteIndex];
                if (ao == 0)
                {
                    return baseColor;
                }

                float attenuation = math.saturate(1f - 0.18f * ao);
                byte r = (byte)math.clamp((int)math.round(baseColor.r * attenuation), 0, 255);
                byte g = (byte)math.clamp((int)math.round(baseColor.g * attenuation), 0, 255);
                byte b = (byte)math.clamp((int)math.round(baseColor.b * attenuation), 0, 255);
                return new Color32(r, g, b, baseColor.a);
            }

            private bool IsOpaque(int3 pos)
            {
                var block = SampleBlock(pos);
                return VoxelTypes.IsOpaque(block);
            }

            private byte SampleBlock(int3 pos)
            {
                if (pos.x < 0 || pos.y < 0 || pos.z < 0 || pos.x >= ChunkSize.x || pos.y >= ChunkSize.y || pos.z >= ChunkSize.z)
                {
                    return 0;
                }

                int index = pos.x + ChunkSize.x * (pos.z + ChunkSize.z * pos.y);
                return Blocks[index];
            }
        }

        private struct MaskEntry
        {
            public byte Visible;
            public byte Block;
            public sbyte Normal;
        }
    }
}
