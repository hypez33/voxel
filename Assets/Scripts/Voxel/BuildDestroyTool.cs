using UnityEngine;
using UnityEngine.InputSystem;
using VoxelGame;

namespace Voxels
{
    public sealed class BuildDestroyTool : MonoBehaviour
    {
        [SerializeField] private World world;
        [SerializeField] private Camera viewCamera;
        [SerializeField] private DebrisPool debrisPool;
        [SerializeField] private float interactRangeM = 4.0f; // 4 m reach (~40 voxels)
        [SerializeField] private float mineRadiusM = 0.25f;
        [SerializeField] private float explosionRadius = 2.4f;
        [SerializeField] private float toolCooldown = 0.08f;

        private float _nextActionTime;
        private byte _currentBlock = (byte)VoxelType.Stone;

        private void Awake()
        {
            if (viewCamera == null)
            {
                viewCamera = GetComponentInChildren<Camera>();
            }
        }

        private void OnValidate()
        {
            mineRadiusM = Mathf.Max(mineRadiusM, VoxelMetrics.VOXEL_SIZE * 0.5f);
            explosionRadius = Mathf.Max(explosionRadius, mineRadiusM);
        }

        private void Update()
        {
            if (world == null || viewCamera == null)
            {
                return;
            }

            var keyboard = Keyboard.current;
            var mouse = Mouse.current;
            if (keyboard == null || mouse == null)
            {
                return;
            }

            HandleBlockSelection(keyboard);
            HandlePrimarySecondary(mouse);
            HandleExplosion(keyboard);
        }

        private void HandleBlockSelection(Keyboard keyboard)
        {
            for (int i = 0; i < 9; i++)
            {
                var key = (Key)((int)Key.Digit1 + i);
                var control = keyboard[key];
                if (control != null && control.wasPressedThisFrame)
                {
                    _currentBlock = (byte)(i + 1);
                    break;
                }
            }
        }

        private void HandlePrimarySecondary(Mouse mouse)
        {
            if (Time.time < _nextActionTime)
            {
                return;
            }

            var ray = new Ray(viewCamera.transform.position, viewCamera.transform.forward);
            if (!VoxelDDA.Cast(ray, interactRangeM, world, out var hit))
            {
                return;
            }

            if (mouse.leftButton.isPressed)
            {
                _nextActionTime = Time.time + toolCooldown;
                Vector3 center = VoxelMetrics.ToWorldPosition(hit.Block) + Vector3.one * (VoxelMetrics.VOXEL_SIZE * 0.5f);
                world.RemoveSphere(center, mineRadiusM);
                debrisPool?.SpawnBurst(center, 18, mineRadiusM * 0.5f);
            }
            else if (mouse.rightButton.isPressed)
            {
                _nextActionTime = Time.time + toolCooldown;
                Vector3Int placeBlock = hit.Block + hit.Normal;
                if (world.TryGetBlock(placeBlock, out var existing) && !VoxelTypes.IsSolid(existing))
                {
                    world.SetBlockGlobal(placeBlock, _currentBlock);
                }
            }
        }

        private void HandleExplosion(Keyboard keyboard)
        {
            if (!keyboard.gKey.wasPressedThisFrame)
            {
                return;
            }

            var ray = new Ray(viewCamera.transform.position, viewCamera.transform.forward);
            if (!VoxelDDA.Cast(ray, interactRangeM * 1.5f, world, out var hit))
            {
                return;
            }

            Vector3 center = VoxelMetrics.ToWorldPosition(hit.Block) + Vector3.one * (VoxelMetrics.VOXEL_SIZE * 0.5f);
            world.Explode(center, explosionRadius);
            debrisPool?.SpawnBurst(center, 36, explosionRadius);
        }
    }
}


