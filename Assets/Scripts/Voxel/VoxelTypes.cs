using UnityEngine;

namespace Voxels
{
    /// <summary>
    /// Central place for common voxel metrics so that changing VOXEL_SIZE propagates everywhere.
    /// </summary>
    public static class VoxelMetrics
    {
        public const float VOXEL_SIZE = 0.10f;
        public const int CHUNK_SIZE_X = 32;
        public const int CHUNK_SIZE_Y = 128;
        public const int CHUNK_SIZE_Z = 32;
        public static readonly Vector3Int ChunkSize = new Vector3Int(CHUNK_SIZE_X, CHUNK_SIZE_Y, CHUNK_SIZE_Z);
        public static readonly Vector3 ChunkWorldSize = new Vector3(CHUNK_SIZE_X, CHUNK_SIZE_Y, CHUNK_SIZE_Z) * VOXEL_SIZE;

        public static Vector3 ToWorldPosition(Vector3Int blockPosition)
        {
            return (Vector3)blockPosition * VOXEL_SIZE;
        }

        public static Vector3Int ToBlockPosition(Vector3 worldPosition)
        {
            return new Vector3Int(
                Mathf.FloorToInt(worldPosition.x / VOXEL_SIZE),
                Mathf.FloorToInt(worldPosition.y / VOXEL_SIZE),
                Mathf.FloorToInt(worldPosition.z / VOXEL_SIZE));
        }

        public static Vector3Int ToChunkCoordinate(Vector3 worldPosition)
        {
            var block = ToBlockPosition(worldPosition);
            return new Vector3Int(
                Mathf.FloorToInt((float)block.x / CHUNK_SIZE_X),
                Mathf.FloorToInt((float)block.y / CHUNK_SIZE_Y),
                Mathf.FloorToInt((float)block.z / CHUNK_SIZE_Z));
        }
    }

    public enum VoxelType : byte
    {
        Air = 0,
        Grass = 1,
        Dirt = 2,
        Stone = 3,
        Wood = 4,
        Leaves = 5,
        Sand = 6,
        Snow = 7,
        Water = 8,
        Brick = 9,
        Metal = 10,
        Glass = 11
    }

    public static class VoxelTypes
    {
        public static bool IsSolid(byte id)
        {
            return id != (byte)VoxelType.Air;
        }

        public static bool IsOpaque(byte id)
        {
            return id != (byte)VoxelType.Air &&
                   id != (byte)VoxelType.Glass &&
                   id != (byte)VoxelType.Leaves &&
                   id != (byte)VoxelType.Water;
        }

        public static byte ClampToValid(byte id)
        {
            return (byte)Mathf.Clamp((int)id, 0, (int)VoxelType.Glass);
        }
    }
}
