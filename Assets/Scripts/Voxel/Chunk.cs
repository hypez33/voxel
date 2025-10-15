using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Voxels
{
    [DisallowMultipleComponent]
    public sealed class Chunk : MonoBehaviour, IDisposable
    {
        public Vector3Int Coordinate { get; private set; }
        public Bounds Bounds => _bounds;
        public bool IsDirty { get; private set; }
        public NativeArray<byte> Blocks => _blocks;

        [NonSerialized] public Mesh Mesh;
        [NonSerialized] public MeshCollider MeshCollider;
        [NonSerialized] public MeshFilter MeshFilter;

        private NativeArray<byte> _blocks;
        private World _world;
        private Bounds _bounds;
        private bool _initialized;

        public void Initialize(Vector3Int coordinate, World world)
        {
            Coordinate = coordinate;
            _world = world;
            var chunkOriginBlocks = Vector3Int.Scale(coordinate, VoxelMetrics.ChunkSize);
            transform.position = VoxelMetrics.ToWorldPosition(chunkOriginBlocks);

            var sizeWorld = new Vector3(
                VoxelMetrics.CHUNK_SIZE_X,
                VoxelMetrics.CHUNK_SIZE_Y,
                VoxelMetrics.CHUNK_SIZE_Z) * VoxelMetrics.VOXEL_SIZE;
            var centerLocal = sizeWorld * 0.5f;
            _bounds = new Bounds(centerLocal, sizeWorld);

            var blockCount = VoxelMetrics.CHUNK_SIZE_X * VoxelMetrics.CHUNK_SIZE_Y * VoxelMetrics.CHUNK_SIZE_Z;
            if (!_blocks.IsCreated)
            {
                _blocks = new NativeArray<byte>(blockCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            }
            else
            {
                for (int i = 0; i < _blocks.Length; i++)
                {
                    _blocks[i] = 0;
                }
            }

            if (MeshFilter == null)
            {
                MeshFilter = GetComponent<MeshFilter>();
                if (MeshFilter == null)
                {
                    MeshFilter = gameObject.AddComponent<MeshFilter>();
                }
            }

            if (MeshCollider == null)
            {
                MeshCollider = GetComponent<MeshCollider>();
                if (MeshCollider == null)
                {
                    MeshCollider = gameObject.AddComponent<MeshCollider>();
                }
            }

            var renderer = GetComponent<MeshRenderer>();
            if (renderer == null)
            {
                renderer = gameObject.AddComponent<MeshRenderer>();
            }

            if (Mesh == null)
            {
                Mesh = new Mesh
                {
                    name = $"Chunk_{coordinate.x}_{coordinate.y}_{coordinate.z}"
                };
                Mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                Mesh.MarkDynamic();
            }

            MeshFilter.sharedMesh = Mesh;
            MeshCollider.sharedMesh = null;
            renderer.sharedMaterial = world.VoxelMaterial;
            IsDirty = true;
            _initialized = true;
        }

        public void Dispose()
        {
            if (_blocks.IsCreated)
            {
                _blocks.Dispose();
            }

            if (Mesh != null)
            {
                Destroy(Mesh);
                Mesh = null;
            }
        }

        private void OnDestroy()
        {
            Dispose();
        }

        public void Fill(Func<Vector3Int, byte> sampler)
        {
            if (!_initialized)
            {
                throw new InvalidOperationException("Chunk must be initialised before filling.");
            }

            var size = VoxelMetrics.ChunkSize;
            var baseBlock = Coordinate * size;
            int index = 0;
            for (int y = 0; y < size.y; y++)
            {
                for (int z = 0; z < size.z; z++)
                {
                    for (int x = 0; x < size.x; x++, index++)
                    {
                        var localBlock = new Vector3Int(x, y, z);
                        var samplePos = baseBlock + localBlock;
                        _blocks[index] = sampler(samplePos);
                    }
                }
            }

            IsDirty = true;
        }

        public bool InBounds(Vector3Int local)
        {
            return local.x >= 0 && local.x < VoxelMetrics.CHUNK_SIZE_X &&
                   local.y >= 0 && local.y < VoxelMetrics.CHUNK_SIZE_Y &&
                   local.z >= 0 && local.z < VoxelMetrics.CHUNK_SIZE_Z;
        }

        public byte GetBlock(Vector3Int local)
        {
            if (!InBounds(local))
            {
                return 0;
            }

            return _blocks[GetIndex(local)];
        }

        public void SetBlock(Vector3Int local, byte value)
        {
            if (!InBounds(local))
            {
                return;
            }

            var index = GetIndex(local);
            if (_blocks[index] == value)
            {
                return;
            }

            _blocks[index] = value;
            IsDirty = true;
            _world.NotifyChunkModified(this, index, value);
        }

        public void UploadMesh(Mesh.MeshDataArray meshDataArray, int vertexCount, int indexCount)
        {
            Mesh.Clear(false);
            Mesh.ApplyAndDisposeWritableMeshData(meshDataArray, Mesh);
            Mesh.bounds = _bounds;
            MeshCollider.sharedMesh = Mesh;
            IsDirty = false;
        }

        public int GetIndex(Vector3Int local)
        {
            return local.x +
                   VoxelMetrics.CHUNK_SIZE_X * (local.z +
                   VoxelMetrics.CHUNK_SIZE_Z * local.y);
        }
    }
}
