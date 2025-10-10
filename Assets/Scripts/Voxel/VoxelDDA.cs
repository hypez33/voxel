using UnityEngine;

namespace Voxels
{
    public static class VoxelDDA
    {
        public static bool Cast(Ray ray, float maxDistance, World world, out VoxelHit hit)
        {
            hit = default;

            if (world == null || maxDistance <= 0f)
            {
                return false;
            }

            var direction = ray.direction;
            if (direction.sqrMagnitude < 1e-6f)
            {
                return false;
            }

            direction.Normalize();

            Vector3 start = ray.origin;
            Vector3Int block = VoxelMetrics.ToBlockPosition(start);

            Vector3 voxel = VoxelMetrics.ToWorldPosition(block);
            Vector3 delta = new Vector3(
                direction.x != 0f ? VoxelMetrics.VOXEL_SIZE / Mathf.Abs(direction.x) : float.PositiveInfinity,
                direction.y != 0f ? VoxelMetrics.VOXEL_SIZE / Mathf.Abs(direction.y) : float.PositiveInfinity,
                direction.z != 0f ? VoxelMetrics.VOXEL_SIZE / Mathf.Abs(direction.z) : float.PositiveInfinity);

            Vector3Int step = new Vector3Int(
                direction.x > 0f ? 1 : (direction.x < 0f ? -1 : 0),
                direction.y > 0f ? 1 : (direction.y < 0f ? -1 : 0),
                direction.z > 0f ? 1 : (direction.z < 0f ? -1 : 0));

            Vector3 next = new Vector3(
                direction.x > 0f ? (voxel.x + VoxelMetrics.VOXEL_SIZE - start.x) / direction.x :
                (direction.x < 0f ? (voxel.x - start.x) / direction.x : float.PositiveInfinity),
                direction.y > 0f ? (voxel.y + VoxelMetrics.VOXEL_SIZE - start.y) / direction.y :
                (direction.y < 0f ? (voxel.y - start.y) / direction.y : float.PositiveInfinity),
                direction.z > 0f ? (voxel.z + VoxelMetrics.VOXEL_SIZE - start.z) / direction.z :
                (direction.z < 0f ? (voxel.z - start.z) / direction.z : float.PositiveInfinity));

            float distance = 0f;
            Vector3Int lastNormal = Vector3Int.zero;

            while (distance <= maxDistance)
            {
                if (world.TryGetBlock(block, out var blockId) && VoxelTypes.IsSolid(blockId))
                {
                    hit = new VoxelHit(block, lastNormal, distance, blockId);
                    return true;
                }

                if (next.x < next.y && next.x < next.z)
                {
                    if (float.IsPositiveInfinity(next.x))
                    {
                        break;
                    }

                    block.x += step.x;
                    distance = next.x;
                    next.x += delta.x;
                    lastNormal = new Vector3Int(-step.x, 0, 0);
                }
                else if (next.y < next.z)
                {
                    if (float.IsPositiveInfinity(next.y))
                    {
                        break;
                    }

                    block.y += step.y;
                    distance = next.y;
                    next.y += delta.y;
                    lastNormal = new Vector3Int(0, -step.y, 0);
                }
                else
                {
                    if (float.IsPositiveInfinity(next.z))
                    {
                        break;
                    }

                    block.z += step.z;
                    distance = next.z;
                    next.z += delta.z;
                    lastNormal = new Vector3Int(0, 0, -step.z);
                }
            }

            return false;
        }
    }

    public readonly struct VoxelHit
    {
        public readonly Vector3Int Block;
        public readonly Vector3Int Normal;
        public readonly float Distance;
        public readonly byte BlockId;

        public VoxelHit(Vector3Int block, Vector3Int normal, float distance, byte blockId)
        {
            Block = block;
            Normal = normal;
            Distance = distance;
            BlockId = blockId;
        }

        public Vector3Int AdjacentBlock => Block + Normal;
    }
}
