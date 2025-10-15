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
        [SerializeField] private int verticalChunkCount = 1;

        [Header("Streaming / View Distance")]
        [SerializeField, Range(1, 32)] private int viewRadius = 14;
        [SerializeField, Range(1, 1024)] private int hardCapLoadedChunks = 700;
        [SerializeField, Range(1, 16)] private int genPerFrameBudget = 2;
        [SerializeField, Range(1, 16)] private int meshPerFrameBudget = 1;
        [SerializeField] private bool spiralOrdering = true;
        [SerializeField] private bool prewarmOnEnterNewChunk = true;

        private const int SeaLevel = 42;
        private const int SnowLine = 82;
        private const int TreePadding = 3;
        private const int BedrockThickness = 2;
        private const float CaveThreshold = 0.62f;
        private const float OreThreshold = 0.78f;

        private readonly Dictionary<Vector3Int, Chunk> _activeChunks = new Dictionary<Vector3Int, Chunk>();
        private readonly Queue<Chunk> _chunkPool = new Queue<Chunk>();
        private readonly HashSet<Vector3Int> _dirtyForSave = new HashSet<Vector3Int>();
        private readonly Dictionary<Vector3Int, byte[]> _pendingDiffs = new Dictionary<Vector3Int, byte[]>();
        private readonly List<Vector3Int> _releaseBuffer = new List<Vector3Int>();
        private readonly List<TreePlan> _treeScratch = new List<TreePlan>(64);
        private readonly HashSet<Vector3Int> _loadedChunks = new HashSet<Vector3Int>();
        private readonly HashSet<Vector3Int> _scheduledChunks = new HashSet<Vector3Int>();
        private readonly HashSet<Vector3Int> _meshScheduled = new HashSet<Vector3Int>();
        private readonly HashSet<Vector3Int> _targetChunks = new HashSet<Vector3Int>();
        private Queue<Vector3Int> _generationQueue = new Queue<Vector3Int>();
        private Queue<Vector3Int> _meshQueue = new Queue<Vector3Int>();

        private ChunkMesher _mesher;
        private Vector3Int _lastPlayerChunk;

        public Material VoxelMaterial => voxelMaterial;
        public int Seed => seed;
        public int ViewRadius => viewRadius;

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
                if (prewarmOnEnterNewChunk)
                {
                    PrewarmOuterRings(playerChunk);
                }
            }

            UpdateStreamingTargets(playerChunk);
            ProcessGenerationQueue();
            ProcessMeshQueue();
        }

        public void SetSeed(int newSeed)
        {
            seed = newSeed;
            _dirtyForSave.Clear();
            _pendingDiffs.Clear();
            _targetChunks.Clear();
            _generationQueue.Clear();
            _scheduledChunks.Clear();
            _meshQueue.Clear();
            _meshScheduled.Clear();
            RegenerateVisible();
        }
        private void RegenerateVisible()
        {
            foreach (var pair in _activeChunks)
            {
                GenerateChunkData(pair.Key, pair.Value.Blocks);
                ApplyPendingDiff(pair.Key, pair.Value);
                _loadedChunks.Add(pair.Key);
                EnqueueMesh(pair.Key);
            }
        }

        private void UpdateStreamingTargets(Vector3Int center)
        {
            int radius = Mathf.Max(1, viewRadius);
            int radiusSq = radius * radius;
            int verticalCount = Mathf.Max(1, verticalChunkCount);

            _targetChunks.Clear();

            for (int dy = 0; dy < verticalCount; dy++)
            {
                for (int dz = -radius; dz <= radius; dz++)
                {
                    int dzSq = dz * dz;
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        if (dx * dx + dzSq > radiusSq)
                        {
                            continue;
                        }

                        var coord = new Vector3Int(center.x + dx, dy, center.z + dz);
                        _targetChunks.Add(coord);
                    }
                }
            }

            ReleaseChunksOutsideTarget();
            PruneGenerationQueue();
            EnqueueMissingChunks(center);
        }

        private void PrewarmOuterRings(Vector3Int center)
        {
            int radius = Mathf.Max(1, viewRadius);
            int verticalCount = Mathf.Max(1, verticalChunkCount);
            int[] rings = { Mathf.Max(0, radius - 1), radius };

            foreach (int ring in rings)
            {
                int ringSq = ring * ring;
                int innerSq = Mathf.Max(0, (ring - 1) * (ring - 1));

                foreach (var offset in EnumerateOffsetsSpiral(radius))
                {
                    int distSq = offset.x * offset.x + offset.y * offset.y;
                    if (distSq > ringSq || distSq <= innerSq)
                    {
                        continue;
                    }

                    for (int dy = 0; dy < verticalCount; dy++)
                    {
                        var coord = new Vector3Int(center.x + offset.x, dy, center.z + offset.y);
                        TryScheduleChunk(coord, ensureTarget: false);
                    }
                }
            }
        }

        private void EnqueueMissingChunks(Vector3Int center)
        {
            int radius = Mathf.Max(1, viewRadius);
            int radiusSq = radius * radius;
            int verticalCount = Mathf.Max(1, verticalChunkCount);

            if (spiralOrdering)
            {
                foreach (var offset in EnumerateOffsetsSpiral(radius))
                {
                    int distSq = offset.x * offset.x + offset.y * offset.y;
                    if (distSq > radiusSq)
                    {
                        continue;
                    }

                    for (int dy = 0; dy < verticalCount; dy++)
                    {
                        var coord = new Vector3Int(center.x + offset.x, dy, center.z + offset.y);
                        TryScheduleChunk(coord);
                    }
                }
            }
            else
            {
                for (int dz = -radius; dz <= radius; dz++)
                {
                    int dzSq = dz * dz;
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        if (dx * dx + dzSq > radiusSq)
                        {
                            continue;
                        }

                        for (int dy = 0; dy < verticalCount; dy++)
                        {
                            var coord = new Vector3Int(center.x + dx, dy, center.z + dz);
                            TryScheduleChunk(coord);
                        }
                    }
                }
            }
        }

        private void ReleaseChunksOutsideTarget()
        {
            _releaseBuffer.Clear();
            foreach (var pair in _activeChunks)
            {
                if (!_targetChunks.Contains(pair.Key))
                {
                    _releaseBuffer.Add(pair.Key);
                }
            }

            for (int i = 0; i < _releaseBuffer.Count; i++)
            {
                ReleaseChunk(_releaseBuffer[i]);
            }
        }

        private void ReleaseChunk(Vector3Int coord)
        {
            if (_activeChunks.TryGetValue(coord, out var chunk))
            {
                chunk.gameObject.SetActive(false);
                _activeChunks.Remove(coord);
                _loadedChunks.Remove(coord);
                _meshScheduled.Remove(coord);
                _dirtyForSave.Remove(coord);
                _chunkPool.Enqueue(chunk);
            }
        }

        private void PruneGenerationQueue()
        {
            if (_generationQueue.Count == 0)
            {
                return;
            }

            var newQueue = new Queue<Vector3Int>(_generationQueue.Count);
            while (_generationQueue.Count > 0)
            {
                var coord = _generationQueue.Dequeue();
                if (_targetChunks.Contains(coord))
                {
                    newQueue.Enqueue(coord);
                }
                else
                {
                    _scheduledChunks.Remove(coord);
                }
            }

            _generationQueue = newQueue;
        }

        private void RemoveFromGenerationQueue(Vector3Int coord)
        {
            if (!_scheduledChunks.Contains(coord) || _generationQueue.Count == 0)
            {
                _scheduledChunks.Remove(coord);
                return;
            }

            var newQueue = new Queue<Vector3Int>(_generationQueue.Count);
            while (_generationQueue.Count > 0)
            {
                var queued = _generationQueue.Dequeue();
                if (queued != coord)
                {
                    newQueue.Enqueue(queued);
                }
            }

            _generationQueue = newQueue;
            _scheduledChunks.Remove(coord);
        }

        private bool TryScheduleChunk(Vector3Int coord, bool ensureTarget = true)
        {
            if (ensureTarget && !_targetChunks.Contains(coord))
            {
                return false;
            }

            if (_loadedChunks.Contains(coord) || _scheduledChunks.Contains(coord))
            {
                return false;
            }

            if (_activeChunks.Count + _scheduledChunks.Count >= hardCapLoadedChunks)
            {
                return false;
            }

            _scheduledChunks.Add(coord);
            _generationQueue.Enqueue(coord);
            return true;
        }

        private void ProcessGenerationQueue()
        {
            int budget = Mathf.Max(0, genPerFrameBudget);
            if (budget == 0)
            {
                return;
            }

            int processed = 0;
            while (_generationQueue.Count > 0 && processed < budget)
            {
                var coord = _generationQueue.Dequeue();
                _scheduledChunks.Remove(coord);

                if (!_targetChunks.Contains(coord) || !IsWithinViewDistance(coord))
                {
                    continue;
                }

                if (_loadedChunks.Contains(coord))
                {
                    continue;
                }

                var chunk = AcquireChunk(coord);
                GenerateChunkData(coord, chunk.Blocks);
                ApplyPendingDiff(coord, chunk);
                _loadedChunks.Add(coord);
                EnqueueMesh(coord);
                processed++;
            }
        }

        private void ProcessMeshQueue()
        {
            if (_mesher == null)
            {
                return;
            }

            int budget = Mathf.Max(0, meshPerFrameBudget);
            if (budget == 0)
            {
                return;
            }

            int processed = 0;
            while (_meshQueue.Count > 0 && processed < budget)
            {
                var coord = _meshQueue.Dequeue();
                _meshScheduled.Remove(coord);

                if (!IsWithinMeshDistance(coord))
                {
                    continue;
                }

                if (_activeChunks.TryGetValue(coord, out var chunk) && chunk != null)
                {
                    _mesher.RebuildChunk(chunk);
                    processed++;
                }
            }
        }

        private Chunk AcquireChunk(Vector3Int coord)
        {
            if (_activeChunks.TryGetValue(coord, out var existing))
            {
                return existing;
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

            _activeChunks[coord] = chunk;
            return chunk;
        }

        private void EnqueueMesh(Vector3Int coord)
        {
            if (!IsWithinMeshDistance(coord))
            {
                return;
            }

            if (_meshScheduled.Add(coord))
            {
                _meshQueue.Enqueue(coord);
            }
        }

        private bool IsWithinViewDistance(Vector3Int coord)
        {
            int radius = Mathf.Max(1, viewRadius);
            return ChunkDistanceSq(coord, _lastPlayerChunk) <= radius * radius;
        }

        private bool IsWithinMeshDistance(Vector3Int coord)
        {
            int radius = Mathf.Max(1, viewRadius + 2);
            return ChunkDistanceSq(coord, _lastPlayerChunk) <= radius * radius;
        }

        private static int ChunkDistanceSq(Vector3Int a, Vector3Int b)
        {
            int dx = a.x - b.x;
            int dz = a.z - b.z;
            return dx * dx + dz * dz;
        }

        private IEnumerable<Vector2Int> EnumerateOffsetsSpiral(int maxRadius)
        {
            yield return Vector2Int.zero;

            if (maxRadius <= 0)
            {
                yield break;
            }

            int x = 0;
            int z = 0;
            int dx = 0;
            int dz = -1;
            int steps = (maxRadius * 2 + 1) * (maxRadius * 2 + 1);

            for (int i = 0; i < steps; i++)
            {
                if (Mathf.Abs(x) <= maxRadius && Mathf.Abs(z) <= maxRadius)
                {
                    if (x != 0 || z != 0)
                    {
                        yield return new Vector2Int(x, z);
                    }
                }

                if (x == z || (x < 0 && x == -z) || (x > 0 && x == 1 - z))
                {
                    int temp = dx;
                    dx = -dz;
                    dz = temp;
                }

                x += dx;
                z += dz;
            }
        }

        private Chunk CreateChunkObject()
        {
            var go = new GameObject("Chunk");
            go.layer = gameObject.layer;
            return go.AddComponent<Chunk>();
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

        public float GetSurfaceHeightWorld(Vector3 worldPosition)
        {
            int blockX = Mathf.RoundToInt(worldPosition.x / VoxelMetrics.VOXEL_SIZE);
            int blockZ = Mathf.RoundToInt(worldPosition.z / VoxelMetrics.VOXEL_SIZE);
            var column = EvaluateColumn(blockX, blockZ);
            return (column.SurfaceHeight + 1f) * VoxelMetrics.VOXEL_SIZE;
        }

        private ColumnData EvaluateColumn(int worldX, int worldZ)
        {
            float baseNoise = Noise.Fractal2D(worldX, worldZ, VoxelMetrics.ScaleFreq(160f), 5, 0.52f, 2.05f, seed);
            float detailNoise = Noise.Fractal2D(worldX, worldZ, VoxelMetrics.ScaleFreq(42f), 3, 0.55f, 2.4f, seed + 71);
            float ridgeNoise = Noise.Ridged2D(worldX, worldZ, VoxelMetrics.ScaleFreq(260f), 3, 0.48f, 2.15f, seed + 113);
            float continental = Mathf.PerlinNoise((worldX + seed * 0.27f) * VoxelMetrics.ScaleFreq(0.00045f), (worldZ - seed * 0.31f) * VoxelMetrics.ScaleFreq(0.00045f));

            float height = SeaLevel - 6f + baseNoise * 18f + detailNoise * 6f + ridgeNoise * 24f + (continental - 0.5f) * 22f;

            float temperature = Mathf.PerlinNoise((worldX + seed * 1.1f) * VoxelMetrics.ScaleFreq(0.00065f), (worldZ + seed * 0.9f) * VoxelMetrics.ScaleFreq(0.00065f));
            float moisture = Mathf.PerlinNoise((worldX - seed * 1.7f) * VoxelMetrics.ScaleFreq(0.00065f), (worldZ - seed * 1.3f) * VoxelMetrics.ScaleFreq(0.00065f));

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
            float scale = VoxelMetrics.ScaleFreq(0.045f);
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

            float noise = Noise.Perlin3D(x + seed * 2, y - seed, z + seed * 3, VoxelMetrics.ScaleFreq(0.08f), seed + 312);
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
            EnqueueMesh(coord);
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
                EnqueueMesh(coord);
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

            RemoveFromGenerationQueue(chunkCoord);
            chunk = AcquireChunk(chunkCoord);
            GenerateChunkData(chunkCoord, chunk.Blocks);
            ApplyPendingDiff(chunkCoord, chunk);
            _loadedChunks.Add(chunkCoord);
            EnqueueMesh(chunkCoord);
            return chunk;
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
                chunk = GetOrCreateChunk(coord);
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
                EnqueueMesh(coord);
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
            public float CaveDensity;
            public bool ForceSnow;
            public bool IsMountain;
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
            public float HeightMultiplier;
            public float TerrainVariance;
            public float DetailVariance;
            public float CaveDensity;
            public bool ForceSnow;
            public bool IsMountain;
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




