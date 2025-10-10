using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Voxels.Generation;

namespace Voxels
{
    public sealed class World : MonoBehaviour
    {
        [SerializeField] private int seed = 1337;
        [SerializeField] private Material voxelMaterial;
        [SerializeField] private Transform player;
        [SerializeField] private int horizontalViewDistance = 6;
        [SerializeField] private int verticalChunkCount = 1;

        private const int SeaLevel = 42;
        private const int SnowLine = 82;
        private const int TreePadding = 3;
        private const int BedrockThickness = 2;
        private const float CaveThreshold = 0.62f;
        private const float OreThreshold = 0.78f;

        private readonly Dictionary<Vector3Int, Chunk> _activeChunks = new Dictionary<Vector3Int, Chunk>();
        private readonly Queue<Chunk> _chunkPool = new Queue<Chunk>();
        private readonly HashSet<Vector3Int> _chunksToRemesh = new HashSet<Vector3Int>();
        private readonly HashSet<Vector3Int> _dirtyForSave = new HashSet<Vector3Int>();
        private readonly Dictionary<Vector3Int, byte[]> _pendingDiffs = new Dictionary<Vector3Int, byte[]>();
        private readonly List<Vector3Int> _releaseBuffer = new List<Vector3Int>();
        private readonly List<TreePlan> _treeScratch = new List<TreePlan>(64);

        private ChunkMesher _mesher;
        private Vector3Int _lastPlayerChunk;

        public Material VoxelMaterial => voxelMaterial;
        public int Seed => seed;

        private void Awake()
        {
            _mesher = new ChunkMesher();
            if (player == null && Camera.main != null)
            {
                player = Camera.main.transform;
            }
        }

        private void OnDestroy()
        {
            _mesher?.Dispose();
        }

        private void Update()
        {
            if (voxelMaterial == null || player == null)
            {
                return;
            }

            var playerChunk = WorldToChunk(player.position);
            if (playerChunk != _lastPlayerChunk)
            {
                _lastPlayerChunk = playerChunk;
            }

            EnsureChunkRing(playerChunk);
            ReleaseFarChunks(playerChunk);
            ProcessRemeshQueue();
        }

        public void SetSeed(int newSeed)
        {
            seed = newSeed;
            _dirtyForSave.Clear();
            _pendingDiffs.Clear();
            RegenerateVisible();
        }
        private void RegenerateVisible()
        {
            foreach (var pair in _activeChunks)
            {
                PopulateChunk(pair.Value);
                _chunksToRemesh.Add(pair.Key);
            }
        }

        private void EnsureChunkRing(Vector3Int center)
        {
            int radiusSq = horizontalViewDistance * horizontalViewDistance;
            int verticalCount = Mathf.Max(1, verticalChunkCount);
            for (int dx = -horizontalViewDistance; dx <= horizontalViewDistance; dx++)
            {
                for (int dz = -horizontalViewDistance; dz <= horizontalViewDistance; dz++)
                {
                    if (dx * dx + dz * dz > radiusSq)
                    {
                        continue;
                    }

                    for (int dy = 0; dy < verticalCount; dy++)
                    {
                        var coord = new Vector3Int(center.x + dx, dy, center.z + dz);
                        EnsureChunk(coord);
                    }
                }
            }
        }

        private void EnsureChunk(Vector3Int coord)
        {
            if (_activeChunks.ContainsKey(coord))
            {
                return;
            }

            Chunk chunk;
            if (_chunkPool.Count > 0)
            {
                chunk = _chunkPool.Dequeue();
                chunk.gameObject.name = $"Chunk {coord.x},{coord.y},{coord.z}";
            }
            else
            {
                chunk = CreateChunkObject();
            }

            chunk.gameObject.SetActive(true);
            chunk.transform.SetParent(transform, false);
            chunk.Initialize(coord, this);
            PopulateChunk(chunk);

            _activeChunks[coord] = chunk;
            ApplyPendingDiff(coord, chunk);
            _chunksToRemesh.Add(coord);
        }

        private Chunk CreateChunkObject()
        {
            var go = new GameObject("Chunk");
            go.layer = gameObject.layer;
            return go.AddComponent<Chunk>();
        }

        private void ReleaseFarChunks(Vector3Int center)
        {
            _releaseBuffer.Clear();
            int maxDistanceSq = (horizontalViewDistance + 1) * (horizontalViewDistance + 1);
            foreach (var pair in _activeChunks)
            {
                int dx = pair.Key.x - center.x;
                int dz = pair.Key.z - center.z;
                if (dx * dx + dz * dz > maxDistanceSq)
                {
                    _releaseBuffer.Add(pair.Key);
                }
            }

            for (int i = 0; i < _releaseBuffer.Count; i++)
            {
                var coord = _releaseBuffer[i];
                if (_activeChunks.TryGetValue(coord, out var chunk))
                {
                    chunk.gameObject.SetActive(false);
                    _activeChunks.Remove(coord);
                    _chunkPool.Enqueue(chunk);
                }
            }
        }

        private void ProcessRemeshQueue()
        {
            if (_chunksToRemesh.Count == 0)
            {
                return;
            }

            foreach (var coord in _chunksToRemesh)
            {
                if (_activeChunks.TryGetValue(coord, out var chunk))
                {
                    _mesher.RebuildChunk(chunk);
                }
            }

            _chunksToRemesh.Clear();
        }

        private void PopulateChunk(Chunk chunk)
        {
            GenerateChunkData(chunk.Coordinate, chunk.Blocks);
        }
        private void GenerateChunkData(Vector3Int chunkCoord, NativeArray<byte> blocks)
        {
            var size = VoxelMetrics.ChunkSize;
            var origin = Vector3Int.Scale(chunkCoord, size);
            _treeScratch.Clear();

            int sizeX = size.x;
            int sizeY = size.y;
            int sizeZ = size.z;

            for (int z = 0; z < sizeZ; z++)
            {
                int worldZ = origin.z + z;
                for (int x = 0; x < sizeX; x++)
                {
                    int worldX = origin.x + x;
                    var column = EvaluateColumn(worldX, worldZ);

                    for (int y = 0; y < sizeY; y++)
                    {
                        int worldY = origin.y + y;
                        int index = x + sizeX * (z + sizeZ * y);
                        blocks[index] = SampleBlockFromColumn(column, worldX, worldY, worldZ);
                    }

                    if (TryCreateTreePlan(column, chunkCoord, x, z, out var plan))
                    {
                        _treeScratch.Add(plan);
                    }
                }
            }

            if (_treeScratch.Count > 0)
            {
                ApplyTreePlans(blocks, _treeScratch);
                _treeScratch.Clear();
            }
        }

        private ColumnData EvaluateColumn(int worldX, int worldZ)
        {
            float baseNoise = Noise.Fractal2D(worldX, worldZ, 160f, 5, 0.52f, 2.05f, seed);
            float detailNoise = Noise.Fractal2D(worldX, worldZ, 42f, 3, 0.55f, 2.4f, seed + 71);
            float ridgeNoise = Noise.Ridged2D(worldX, worldZ, 260f, 3, 0.48f, 2.15f, seed + 113);
            float continental = Mathf.PerlinNoise((worldX + seed * 0.27f) * 0.00045f, (worldZ - seed * 0.31f) * 0.00045f);

            float height = SeaLevel - 6f + baseNoise * 18f + detailNoise * 6f + ridgeNoise * 24f + (continental - 0.5f) * 22f;

            float temperature = Mathf.PerlinNoise((worldX + seed * 1.1f) * 0.00065f, (worldZ + seed * 0.9f) * 0.00065f);
            float moisture = Mathf.PerlinNoise((worldX - seed * 1.7f) * 0.00065f, (worldZ - seed * 1.3f) * 0.00065f);

            var biome = SelectBiome(temperature, moisture, continental);
            height += biome.HeightOffset;
            int surfaceHeight = Mathf.Clamp(Mathf.RoundToInt(height), BedrockThickness + 2, VoxelMetrics.CHUNK_SIZE_Y - 3);

            var column = new ColumnData
            {
                SurfaceHeight = surfaceHeight,
                WaterLevel = SeaLevel,
                SurfaceBlock = (byte)biome.Surface,
                SubSurfaceBlock = (byte)biome.Subsurface,
                FillerBlock = (byte)biome.Filler,
                HasWater = surfaceHeight < SeaLevel - 1,
                AllowTree = biome.TreeChance > 0,
                TreeChance = (byte)Mathf.Clamp(biome.TreeChance, 0, 255),
                TreeMinHeight = biome.TreeMinHeight,
                TreeMaxHeight = biome.TreeMaxHeight,
                TreeRadius = biome.TreeRadius,
                SnowOnLeaves = biome.SnowOnLeaves,
                TreeHash = Hash(worldX, worldZ) ^ (uint)seed
            };

            if (surfaceHeight >= SnowLine)
            {
                column.SurfaceBlock = (byte)VoxelType.Snow;
                column.SubSurfaceBlock = (byte)VoxelType.Stone;
                column.FillerBlock = (byte)VoxelType.Stone;
                column.SnowOnLeaves = true;
                column.AllowTree &= surfaceHeight < SnowLine + 6;
            }

            if (column.HasWater)
            {
                column.SurfaceBlock = (byte)VoxelType.Sand;
                column.SubSurfaceBlock = (byte)VoxelType.Sand;
                column.FillerBlock = (byte)VoxelType.Sand;
                column.AllowTree = false;
            }

            if (biome.IsDesert)
            {
                column.SurfaceBlock = (byte)VoxelType.Sand;
                column.SubSurfaceBlock = (byte)VoxelType.Sand;
                column.FillerBlock = (byte)VoxelType.Sand;
                column.HasWater = false;
                column.AllowTree = false;
            }

            return column;
        }

        private BiomeSettings SelectBiome(float temperature, float moisture, float continental)
        {
            if (temperature < 0.28f)
            {
                if (moisture > 0.55f)
                {
                    return new BiomeSettings
                    {
                        Surface = VoxelType.Grass,
                        Subsurface = VoxelType.Dirt,
                        Filler = VoxelType.Stone,
                        TreeChance = 90,
                        TreeMinHeight = 5,
                        TreeMaxHeight = 8,
                        TreeRadius = 2,
                        SnowOnLeaves = true,
                        HeightOffset = 4f
                    };
                }

                return new BiomeSettings
                {
                    Surface = VoxelType.Snow,
                    Subsurface = VoxelType.Stone,
                    Filler = VoxelType.Stone,
                    TreeChance = 0,
                    SnowOnLeaves = true,
                    HeightOffset = 8f
                };
            }

            if (temperature > 0.75f && moisture < 0.35f)
            {
                return new BiomeSettings
                {
                    Surface = VoxelType.Sand,
                    Subsurface = VoxelType.Sand,
                    Filler = VoxelType.Sand,
                    TreeChance = 0,
                    IsDesert = true,
                    HeightOffset = -2f
                };
            }

            if (moisture > 0.7f)
            {
                return new BiomeSettings
                {
                    Surface = VoxelType.Grass,
                    Subsurface = VoxelType.Dirt,
                    Filler = VoxelType.Stone,
                    TreeChance = 140,
                    TreeMinHeight = 5,
                    TreeMaxHeight = 9,
                    TreeRadius = 3,
                    HeightOffset = 2f
                };
            }

            if (moisture < 0.35f)
            {
                return new BiomeSettings
                {
                    Surface = VoxelType.Grass,
                    Subsurface = VoxelType.Dirt,
                    Filler = VoxelType.Stone,
                    TreeChance = 50,
                    TreeMinHeight = 4,
                    TreeMaxHeight = 6,
                    TreeRadius = 2,
                    HeightOffset = 0f
                };
            }

            return new BiomeSettings
            {
                Surface = VoxelType.Grass,
                Subsurface = VoxelType.Dirt,
                Filler = VoxelType.Stone,
                TreeChance = 110,
                TreeMinHeight = 5,
                TreeMaxHeight = 8,
                TreeRadius = 2,
                HeightOffset = 1.5f
            };
        }
        private bool TryCreateTreePlan(ColumnData column, Vector3Int chunkCoord, int localX, int localZ, out TreePlan plan)
        {
            plan = default;
            if (!column.AllowTree || column.TreeChance == 0)
            {
                return false;
            }

            var size = VoxelMetrics.ChunkSize;
            if (localX < TreePadding || localX >= size.x - TreePadding ||
                localZ < TreePadding || localZ >= size.z - TreePadding)
            {
                return false;
            }

            var origin = Vector3Int.Scale(chunkCoord, size);
            int localSurfaceY = column.SurfaceHeight - origin.y;
            if (localSurfaceY < 0 || localSurfaceY >= size.y - 8)
            {
                return false;
            }

            if ((column.TreeHash & 0xFFu) >= column.TreeChance)
            {
                return false;
            }

            int heightRange = Mathf.Max(1, column.TreeMaxHeight - column.TreeMinHeight + 1);
            int height = column.TreeMinHeight + (int)((column.TreeHash >> 8) % (uint)heightRange);
            height = Mathf.Clamp(height, 4, size.y - localSurfaceY - 2);

            int radius = Mathf.Clamp(column.TreeRadius, 2, 4);
            if (localSurfaceY + height + radius >= size.y)
            {
                height = Mathf.Clamp(height, 4, size.y - localSurfaceY - radius - 1);
                if (height < 4)
                {
                    return false;
                }
            }

            plan = new TreePlan
            {
                LocalBase = new Vector3Int(localX, localSurfaceY + 1, localZ),
                Height = height,
                Radius = radius,
                SnowCap = column.SnowOnLeaves
            };
            return true;
        }

        private void ApplyTreePlans(NativeArray<byte> blocks, List<TreePlan> treePlans)
        {
            var size = VoxelMetrics.ChunkSize;
            int sizeX = size.x;
            int sizeZ = size.z;

            for (int i = 0; i < treePlans.Count; i++)
            {
                var plan = treePlans[i];

                if (plan.LocalBase.y > 0 && plan.LocalBase.y - 1 < size.y)
                {
                    int belowIndex = plan.LocalBase.x + sizeX * (plan.LocalBase.z + sizeZ * (plan.LocalBase.y - 1));
                    if (belowIndex >= 0 && belowIndex < blocks.Length)
                    {
                        blocks[belowIndex] = (byte)VoxelType.Dirt;
                    }
                }

                for (int y = 0; y < plan.Height; y++)
                {
                    int ly = plan.LocalBase.y + y;
                    if (ly < 0 || ly >= size.y)
                    {
                        break;
                    }

                    int idx = plan.LocalBase.x + sizeX * (plan.LocalBase.z + sizeZ * ly);
                    blocks[idx] = (byte)VoxelType.Wood;
                }

                int canopyBase = plan.LocalBase.y + plan.Height - 1;
                for (int offsetY = -2; offsetY <= plan.Radius; offsetY++)
                {
                    int ly = canopyBase + offsetY;
                    if (ly < 0 || ly >= size.y)
                    {
                        continue;
                    }

                    int radius = plan.Radius - Mathf.Max(0, Mathf.Abs(offsetY) - 1);
                    radius = Mathf.Max(1, radius);

                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        int lz = plan.LocalBase.z + dz;
                        if (lz < 0 || lz >= sizeZ)
                        {
                            continue;
                        }

                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int lx = plan.LocalBase.x + dx;
                            if (lx < 0 || lx >= sizeX)
                            {
                                continue;
                            }

                            if (Mathf.Abs(dx) + Mathf.Abs(dz) > radius + 1)
                            {
                                continue;
                            }

                            int idx = lx + sizeX * (lz + sizeZ * ly);
                            if (blocks[idx] != (byte)VoxelType.Air)
                            {
                                continue;
                            }

                            if (offsetY == plan.Radius && plan.SnowCap)
                            {
                                blocks[idx] = (byte)VoxelType.Snow;
                            }
                            else
                            {
                                blocks[idx] = (byte)VoxelType.Leaves;
                            }
                        }
                    }
                }
            }
        }
        private byte SampleBlockFromColumn(ColumnData column, int worldX, int worldY, int worldZ)
        {
            if (worldY <= 0)
            {
                return (byte)VoxelType.Stone;
            }

            if (column.HasWater && worldY > column.SurfaceHeight && worldY <= column.WaterLevel)
            {
                return (byte)VoxelType.Water;
            }

            if (worldY > column.SurfaceHeight)
            {
                return (byte)VoxelType.Air;
            }

            if (worldY == column.SurfaceHeight)
            {
                return column.SurfaceBlock;
            }

            int depth = column.SurfaceHeight - worldY;
            if (depth <= 3)
            {
                return column.SubSurfaceBlock;
            }

            if (worldY <= BedrockThickness)
            {
                return (byte)VoxelType.Stone;
            }

            byte block = column.FillerBlock;
            if (block == (byte)VoxelType.Dirt && depth > 8)
            {
                block = (byte)VoxelType.Stone;
            }

            if (block == (byte)VoxelType.Stone && ShouldPlaceOre(worldX, worldY, worldZ))
            {
                block = (byte)VoxelType.Metal;
            }

            if (IsCave(worldX, worldY, worldZ) && worldY < column.SurfaceHeight - 3)
            {
                return (byte)VoxelType.Air;
            }

            return block;
        }

        private byte SampleTerrainBlock(Vector3Int blockCoords)
        {
            var chunkCoord = BlockToChunk(blockCoords, out var local);
            var column = EvaluateColumn(blockCoords.x, blockCoords.z);
            byte block = SampleBlockFromColumn(column, blockCoords.x, blockCoords.y, blockCoords.z);

            if (TryCreateTreePlan(column, chunkCoord, local.x, local.z, out var plan))
            {
                var treeBlock = SampleTreeBlock(plan, local.x, local.y, local.z);
                if (treeBlock.HasValue)
                {
                    block = treeBlock.Value;
                }
            }

            return block;
        }

        private byte? SampleTreeBlock(TreePlan plan, int localX, int localY, int localZ)
        {
            int baseY = plan.LocalBase.y;

            if (localX == plan.LocalBase.x && localZ == plan.LocalBase.z)
            {
                if (localY == baseY - 1 && baseY > 0)
                {
                    return (byte)VoxelType.Dirt;
                }

                if (localY >= baseY && localY < baseY + plan.Height)
                {
                    return (byte)VoxelType.Wood;
                }
            }

            int canopyBase = plan.LocalBase.y + plan.Height - 1;
            int offsetY = localY - canopyBase;
            if (offsetY >= -2 && offsetY <= plan.Radius)
            {
                int radius = plan.Radius - Mathf.Max(0, Mathf.Abs(offsetY) - 1);
                radius = Mathf.Max(1, radius);

                int dx = localX - plan.LocalBase.x;
                int dz = localZ - plan.LocalBase.z;
                if (Mathf.Abs(dx) <= radius && Mathf.Abs(dz) <= radius && Mathf.Abs(dx) + Mathf.Abs(dz) <= radius + 1)
                {
                    if (offsetY == plan.Radius && plan.SnowCap)
                    {
                        return (byte)VoxelType.Snow;
                    }

                    return (byte)VoxelType.Leaves;
                }
            }

            return null;
        }

        private bool IsCave(int x, int y, int z)
        {
            float scale = 0.045f;
            float n1 = Noise.Perlin3D(x, y, z, scale, seed);
            float n2 = Noise.Perlin3D(x + seed, y - seed, z + seed * 2, scale * 1.8f, seed + 271);
            return (n1 * 0.6f + n2 * 0.4f) > CaveThreshold;
        }

        private bool ShouldPlaceOre(int x, int y, int z)
        {
            if (y > SeaLevel - 6 || y < 4)
            {
                return false;
            }

            float noise = Noise.Perlin3D(x + seed * 2, y - seed, z + seed * 3, 0.08f, seed + 312);
            return noise > OreThreshold;
        }
        public bool TryGetBlock(Vector3Int worldBlock, out byte block)
        {
            var chunkCoord = BlockToChunk(worldBlock, out var local);
            if (_activeChunks.TryGetValue(chunkCoord, out var chunk))
            {
                block = chunk.GetBlock(local);
                return true;
            }

            block = SampleTerrainBlock(worldBlock);

            if (_pendingDiffs.TryGetValue(chunkCoord, out var diffData))
            {
                ApplyDiffToSample(diffData, local, ref block);
            }

            return true;
        }

        public void SetBlockGlobal(Vector3Int worldBlock, byte value)
        {
            var chunkCoord = BlockToChunk(worldBlock, out var local);
            var chunk = GetOrCreateChunk(chunkCoord);
            chunk.SetBlock(local, value);
        }

        public void RemoveSphere(Vector3 centerWorld, float radius)
        {
            float radiusSqr = radius * radius;
            float invVoxel = 1f / VoxelMetrics.VOXEL_SIZE;
            Vector3 centerBlock = centerWorld * invVoxel;

            int minX = Mathf.FloorToInt(centerBlock.x - radius * invVoxel) - 1;
            int maxX = Mathf.CeilToInt(centerBlock.x + radius * invVoxel) + 1;
            int minY = Mathf.FloorToInt(centerBlock.y - radius * invVoxel) - 1;
            int maxY = Mathf.CeilToInt(centerBlock.y + radius * invVoxel) + 1;
            int minZ = Mathf.FloorToInt(centerBlock.z - radius * invVoxel) - 1;
            int maxZ = Mathf.CeilToInt(centerBlock.z + radius * invVoxel) + 1;

            for (int y = minY; y <= maxY; y++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        var blockCoord = new Vector3Int(x, y, z);
                        Vector3 blockCenter = VoxelMetrics.ToWorldPosition(blockCoord) + Vector3.one * (VoxelMetrics.VOXEL_SIZE * 0.5f);
                        if ((blockCenter - centerWorld).sqrMagnitude <= radiusSqr)
                        {
                            SetBlockGlobal(blockCoord, (byte)VoxelType.Air);
                        }
                    }
                }
            }
        }

        public void Explode(Vector3 centerWorld, float radius)
        {
            RemoveSphere(centerWorld, radius);
        }

        public void NotifyChunkModified(Chunk chunk, int blockIndex, byte _)
        {
            var coord = chunk.Coordinate;
            _chunksToRemesh.Add(coord);
            _dirtyForSave.Add(coord);

            var local = IndexToLocal(blockIndex);
            if (local.x == 0) MarkNeighbour(coord + new Vector3Int(-1, 0, 0));
            if (local.x == VoxelMetrics.CHUNK_SIZE_X - 1) MarkNeighbour(coord + new Vector3Int(1, 0, 0));
            if (local.y == 0) MarkNeighbour(coord + new Vector3Int(0, -1, 0));
            if (local.y == VoxelMetrics.CHUNK_SIZE_Y - 1) MarkNeighbour(coord + new Vector3Int(0, 1, 0));
            if (local.z == 0) MarkNeighbour(coord + new Vector3Int(0, 0, -1));
            if (local.z == VoxelMetrics.CHUNK_SIZE_Z - 1) MarkNeighbour(coord + new Vector3Int(0, 0, 1));
        }

        private void MarkNeighbour(Vector3Int coord)
        {
            if (_activeChunks.ContainsKey(coord))
            {
                _chunksToRemesh.Add(coord);
            }
        }

        private static Vector3Int IndexToLocal(int index)
        {
            int sizeX = VoxelMetrics.CHUNK_SIZE_X;
            int sizeZ = VoxelMetrics.CHUNK_SIZE_Z;
            int y = index / (sizeX * sizeZ);
            int rem = index - y * sizeX * sizeZ;
            int z = rem / sizeX;
            int x = rem - z * sizeX;
            return new Vector3Int(x, y, z);
        }

        private Chunk GetOrCreateChunk(Vector3Int chunkCoord)
        {
            if (_activeChunks.TryGetValue(chunkCoord, out var chunk))
            {
                return chunk;
            }

            EnsureChunk(chunkCoord);
            return _activeChunks[chunkCoord];
        }

        private Vector3Int BlockToChunk(Vector3Int block, out Vector3Int local)
        {
            int cx = FloorDiv(block.x, VoxelMetrics.CHUNK_SIZE_X);
            int cy = FloorDiv(block.y, VoxelMetrics.CHUNK_SIZE_Y);
            int cz = FloorDiv(block.z, VoxelMetrics.CHUNK_SIZE_Z);
            local = new Vector3Int(
                Mod(block.x, VoxelMetrics.CHUNK_SIZE_X),
                Mod(block.y, VoxelMetrics.CHUNK_SIZE_Y),
                Mod(block.z, VoxelMetrics.CHUNK_SIZE_Z));
            return new Vector3Int(cx, cy, cz);
        }

        private static int FloorDiv(int value, int size)
        {
            if (value >= 0)
            {
                return value / size;
            }

            return ((value + 1) / size) - 1;
        }

        private static int Mod(int value, int size)
        {
            int result = value % size;
            if (result < 0)
            {
                result += size;
            }

            return result;
        }

        private static Vector3Int WorldToChunk(Vector3 worldPosition)
        {
            return new Vector3Int(
                Mathf.FloorToInt(worldPosition.x / (VoxelMetrics.CHUNK_SIZE_X * VoxelMetrics.VOXEL_SIZE)),
                0,
                Mathf.FloorToInt(worldPosition.z / (VoxelMetrics.CHUNK_SIZE_Z * VoxelMetrics.VOXEL_SIZE)));
        }

        public void CollectDirtyChunks(List<Vector3Int> buffer)
        {
            buffer.Clear();
            foreach (var coord in _dirtyForSave)
            {
                buffer.Add(coord);
            }
        }
        public bool WriteChunkDiff(Vector3Int coord, BinaryWriter writer)
        {
            if (!_activeChunks.TryGetValue(coord, out var chunk))
            {
                EnsureChunk(coord);
                chunk = _activeChunks[coord];
            }

            var blocks = chunk.Blocks;
            using var baseBlocks = new NativeArray<byte>(blocks.Length, Allocator.Temp);
            RegenerateBaseChunk(coord, baseBlocks);
            return WriteDiffBlocks(blocks, baseBlocks, writer);
        }

        private void RegenerateBaseChunk(Vector3Int coord, NativeArray<byte> baseBlocks)
        {
            GenerateChunkData(coord, baseBlocks);
        }

        private static bool WriteDiffBlocks(NativeArray<byte> current, NativeArray<byte> baseline, BinaryWriter writer)
        {
            int count = current.Length;
            int runCount = 0;
            bool hasChanges = false;

            long runPosition = writer.BaseStream.Position;
            writer.Write(runCount);

            int index = 0;
            while (index < count)
            {
                bool differs = current[index] != baseline[index];
                int start = index;
                index++;
                while (index < count && (current[index] != baseline[index]) == differs)
                {
                    index++;
                }

                int length = index - start;
                writer.Write((byte)(differs ? 1 : 0));
                writer.Write(length);
                if (differs)
                {
                    for (int i = start; i < start + length; i++)
                    {
                        writer.Write(current[i]);
                    }
                    hasChanges = true;
                }

                runCount++;
            }

            long endPos = writer.BaseStream.Position;
            writer.BaseStream.Position = runPosition;
            writer.Write(runCount);
            writer.BaseStream.Position = endPos;
            return hasChanges;
        }

        public void ApplyChunkDiff(Vector3Int coord, byte[] diffData)
        {
            if (_activeChunks.TryGetValue(coord, out var chunk))
            {
                ApplyDiffToChunk(diffData, chunk.Blocks);
                _chunksToRemesh.Add(coord);
            }
            else
            {
                _pendingDiffs[coord] = diffData;
            }
        }

        private void ApplyPendingDiff(Vector3Int coord, Chunk chunk)
        {
            if (_pendingDiffs.TryGetValue(coord, out var diff))
            {
                ApplyDiffToChunk(diff, chunk.Blocks);
                _pendingDiffs.Remove(coord);
            }
        }

        private static void ApplyDiffToChunk(byte[] diffData, NativeArray<byte> blocks)
        {
            using var reader = new BinaryReader(new MemoryStream(diffData, false));
            int runCount = reader.ReadInt32();
            int index = 0;
            for (int r = 0; r < runCount; r++)
            {
                byte flag = reader.ReadByte();
                int length = reader.ReadInt32();

                if (flag == 0)
                {
                    index += length;
                }
                else
                {
                    for (int i = 0; i < length; i++, index++)
                    {
                        blocks[index] = reader.ReadByte();
                    }
                }
            }
        }

        private void ApplyDiffToSample(byte[] diffData, Vector3Int local, ref byte sample)
        {
            using var reader = new BinaryReader(new MemoryStream(diffData, false));
            int runCount = reader.ReadInt32();
            int targetIndex = LocalToIndex(local);
            int cursor = 0;
            for (int r = 0; r < runCount; r++)
            {
                byte flag = reader.ReadByte();
                int length = reader.ReadInt32();
                if (targetIndex < cursor + length)
                {
                    if (flag == 1)
                    {
                        int skip = targetIndex - cursor;
                        reader.BaseStream.Position += skip;
                        sample = reader.ReadByte();
                    }
                    break;
                }

                if (flag == 1)
                {
                    reader.BaseStream.Position += length;
                }

                cursor += length;
            }
        }

        private static int LocalToIndex(Vector3Int local)
        {
            return local.x +
                   VoxelMetrics.CHUNK_SIZE_X * (local.z +
                   VoxelMetrics.CHUNK_SIZE_Z * local.y);
        }

        public void ClearSaveDirtyFlag(Vector3Int coord)
        {
            _dirtyForSave.Remove(coord);
        }

        private static uint Hash(int x, int z)
        {
            unchecked
            {
                uint h = (uint)(x * 73856093) ^ (uint)(z * 19349663);
                return (h << 13) ^ (h >> 17);
            }
        }
        private struct ColumnData
        {
            public int SurfaceHeight;
            public int WaterLevel;
            public byte SurfaceBlock;
            public byte SubSurfaceBlock;
            public byte FillerBlock;
            public bool HasWater;
            public bool AllowTree;
            public byte TreeChance;
            public int TreeMinHeight;
            public int TreeMaxHeight;
            public int TreeRadius;
            public bool SnowOnLeaves;
            public uint TreeHash;
        }

        private struct BiomeSettings
        {
            public VoxelType Surface;
            public VoxelType Subsurface;
            public VoxelType Filler;
            public int TreeChance;
            public int TreeMinHeight;
            public int TreeMaxHeight;
            public int TreeRadius;
            public bool SnowOnLeaves;
            public bool IsDesert;
            public float HeightOffset;
        }

        private struct TreePlan
        {
            public Vector3Int LocalBase;
            public int Height;
            public int Radius;
            public bool SnowCap;
        }
    }
}
