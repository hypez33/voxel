using UnityEngine;
using UnityEngine.InputSystem;

namespace Voxels
{
    [RequireComponent(typeof(CharacterController))]
    public sealed class PlayerController : MonoBehaviour
    {
        [SerializeField] private Camera playerCamera;
        [SerializeField] private float moveSpeed = 6f;
        [SerializeField] private float sprintMultiplier = 1.7f;
        [SerializeField] private float jumpForce = 5f;
        [SerializeField] private float flySpeed = 12f;
        [SerializeField] private float lookSensitivity = 2.3f;
        [SerializeField] private float gravity = -18f;

        private CharacterController _controller;
        private float _verticalVelocity;
        private Vector2 _look;
        private bool _noClip;

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            if (playerCamera == null)
            {
                playerCamera = GetComponentInChildren<Camera>();
            }

            if (playerCamera != null)
            {
                playerCamera.nearClipPlane = Mathf.Max(0.05f, VoxelMetrics.VOXEL_SIZE * 0.2f);
            }
        }

        private void OnEnable()
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void OnDisable()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void Update()
        {
            var mouse = Mouse.current;
            var keyboard = Keyboard.current;
            if (mouse == null || keyboard == null)
            {
                return;
            }

            HandleMouseLook(mouse);

            if (keyboard.nKey.wasPressedThisFrame)
            {
                ToggleNoClip();
            }

            if (_noClip)
            {
                HandleNoClipMovement(keyboard);
            }
            else
            {
                HandleGroundMovement(keyboard);
            }
        }

        private void HandleMouseLook(Mouse mouse)
        {
            if (playerCamera == null)
            {
                return;
            }

            const float mouseScale = 0.1f;
            Vector2 delta = mouse.delta.ReadValue() * lookSensitivity * mouseScale;
            _look.x += delta.x;
            _look.y += delta.y;
            _look.y = Mathf.Clamp(_look.y, -89f, 89f);

            transform.localRotation = Quaternion.Euler(0f, _look.x, 0f);
            playerCamera.transform.localRotation = Quaternion.Euler(-_look.y, 0f, 0f);
        }

        private void HandleGroundMovement(Keyboard keyboard)
        {
            if (!_controller.enabled)
            {
                _controller.enabled = true;
            }

            Vector3 input = Vector3.zero;
            if (keyboard.wKey.isPressed) input.z += 1f;
            if (keyboard.sKey.isPressed) input.z -= 1f;
            if (keyboard.dKey.isPressed) input.x += 1f;
            if (keyboard.aKey.isPressed) input.x -= 1f;
            input = Vector3.ClampMagnitude(input, 1f);

            float speed = moveSpeed;
            if (keyboard.leftShiftKey.isPressed)
            {
                speed *= sprintMultiplier;
            }

            Vector3 move = transform.TransformDirection(input) * speed;

            if (_controller.isGrounded)
            {
                _verticalVelocity = -1f;
                if (keyboard.spaceKey.wasPressedThisFrame)
                {
                    _verticalVelocity = jumpForce;
                }
            }
            else
            {
                _verticalVelocity += gravity * Time.deltaTime;
            }

            move.y = _verticalVelocity;
            _controller.Move(move * Time.deltaTime);
        }

        private void HandleNoClipMovement(Keyboard keyboard)
        {
            if (_controller.enabled)
            {
                _controller.enabled = false;
            }

            Vector3 move = Vector3.zero;
            if (keyboard.wKey.isPressed) move += transform.forward;
            if (keyboard.sKey.isPressed) move -= transform.forward;
            if (keyboard.dKey.isPressed) move += transform.right;
            if (keyboard.aKey.isPressed) move -= transform.right;
            if (keyboard.spaceKey.isPressed) move += Vector3.up;
            if (keyboard.leftCtrlKey.isPressed || keyboard.cKey.isPressed) move += Vector3.down;

            if (move.sqrMagnitude > 1f)
            {
                move.Normalize();
            }

            float speed = flySpeed;
            if (keyboard.leftShiftKey.isPressed)
            {
                speed *= sprintMultiplier;
            }

            transform.position += move * speed * Time.deltaTime;
        }

        private void ToggleNoClip()
        {
            _noClip = !_noClip;
            if (_noClip)
            {
                _verticalVelocity = 0f;
                if (_controller.enabled)
                {
                    _controller.enabled = false;
                }
            }
            else
            {
                if (!_controller.enabled)
                {
                    _controller.enabled = true;
                }
            }
        }
    }
}
