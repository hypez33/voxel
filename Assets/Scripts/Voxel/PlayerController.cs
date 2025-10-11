using UnityEngine;
using UnityEngine.InputSystem;

namespace Voxels
{
    [RequireComponent(typeof(CharacterController))]
    public sealed class PlayerController : MonoBehaviour
    {
        [SerializeField] private Camera playerCamera;
        [SerializeField] private float moveSpeed = 4.5f;
        [SerializeField] private float sprintMultiplier = 1.6667f;
        [SerializeField] private float jumpForce = 5f;
        [SerializeField] private float flySpeed = 12f;
        [SerializeField] private float gravity = -18f;

        private CharacterController _controller;
        private float _verticalVelocity;
        private Vector2 _look;
        private bool _noClip;

        private const float MouseSensitivity = 0.10f;

        private void Awake()
        {
            transform.localScale = Vector3.one;

            _controller = GetComponent<CharacterController>();
            if (playerCamera == null)
            {
                playerCamera = GetComponentInChildren<Camera>();
            }

            if (_controller != null)
            {
                _controller.height = 0.15f;
                _controller.radius = 0.075f;
                _controller.center = new Vector3(0f, _controller.height * 0.5f, 0f);
                // Fix für Fehler 1: stepOffset muss kleiner sein als height + radius * 2
                _controller.stepOffset = Mathf.Min(_controller.stepOffset, _controller.height + _controller.radius * 2f - 0.01f);
            }

            if (playerCamera != null)
            {
                playerCamera.fieldOfView = 60f;
                playerCamera.nearClipPlane = Mathf.Max(0.05f, VoxelMetrics.VOXEL_SIZE * 0.2f);
                var localPos = playerCamera.transform.localPosition;
                localPos.y = 0.14f;
                playerCamera.transform.localPosition = localPos;
            }
        }

        private void Start()
        {
            SnapToSurface();
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
                return;
            }

            if (_noClip)
            {
                HandleNoClipMovement(keyboard);
            }
            else
            {
                // Stelle sicher dass der Controller aktiviert ist bevor HandleGroundMovement aufgerufen wird
                if (_controller != null && !_controller.enabled)
                {
                    _controller.enabled = true;
                    _verticalVelocity = 0f;
                }
                HandleGroundMovement(keyboard);
            }
        }

        private void SnapToSurface()
        {
            var world = FindFirstObjectByType<World>();
            if (world == null || _controller == null)
            {
                return;
            }

            float surfaceY = world.GetSurfaceHeightWorld(transform.position) + 0.02f;
            
            // Controller kurz deaktivieren um Position direkt zu setzen
            bool wasEnabled = _controller.enabled;
            _controller.enabled = false;

            // Position direkt setzen
            Vector3 newPos = new Vector3(transform.position.x, surfaceY, transform.position.z);
            transform.position = newPos;
            _verticalVelocity = 0f;

            // Controller wieder aktivieren wenn er vorher an war
            if (wasEnabled)
            {
                _controller.enabled = true;
            }
        }

        private void HandleMouseLook(Mouse mouse)
        {
            if (playerCamera == null)
            {
                return;
            }

            Vector2 delta = mouse.delta.ReadValue() * MouseSensitivity;
            _look.x += delta.x;
            _look.y += delta.y;
            _look.y = Mathf.Clamp(_look.y, -89f, 89f);

            transform.localRotation = Quaternion.Euler(0f, _look.x, 0f);
            playerCamera.transform.localRotation = Quaternion.Euler(-_look.y, 0f, 0f);
        }

        private void HandleGroundMovement(Keyboard keyboard)
        {
            if (_controller == null || !_controller.enabled || !gameObject.activeInHierarchy)
            {
                return;
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

            // Move wird nur aufgerufen wenn alle Bedingungen erfüllt sind
            _controller.Move(move * Time.deltaTime);
        }

        private void HandleNoClipMovement(Keyboard keyboard)
        {
            if (_controller != null && _controller.enabled)
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
            _verticalVelocity = 0f;

            if (_controller == null)
            {
                return;
            }

            if (_noClip)
            {
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
                SnapToSurface();
            }
        }
    }
}