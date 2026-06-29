#nullable enable

using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace ElevenLabs.Agents.Samples.GettingStarted
{
    /// <summary>
    /// Tiny WASD + mouse-look controller used by the GettingStarted scene so
    /// the sample can walk into a <see cref="TalkingBox"/>'s trigger without
    /// depending on Unity's Starter Assets or the Getting-Started template
    /// PlayerRobot prefab. Drop on a capsule with a non-trigger
    /// <see cref="CharacterController"/> and a child camera at head height.
    /// </summary>
    /// <remarks>
    /// Works under either input backend — the new <c>Input System Package</c>
    /// (active by default in Unity 6 templates) or the legacy <c>Input Manager</c>.
    /// Selection is done at compile time via Unity's <c>ENABLE_INPUT_SYSTEM</c>
    /// and <c>ENABLE_LEGACY_INPUT_MANAGER</c> defines so the sample compiles
    /// on a fresh project regardless of which mode the consumer picks. The
    /// asmdef references <c>Unity.InputSystem</c>, which is shipped by the
    /// <c>com.unity.inputsystem</c> package — declared as a package
    /// dependency so the import is automatic.
    /// </remarks>
    [RequireComponent(typeof(CharacterController))]
    public sealed class SimplePlayerController : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Walk speed in metres/second.")]
        private float moveSpeed = 4f;

        [SerializeField]
        [Tooltip(
            "Mouse-look sensitivity. The new Input System reports raw pixel deltas, "
                + "which are roughly 10× larger than the legacy 'Mouse X/Y' axis values, "
                + "so this is scaled internally to keep both backends feeling similar."
        )]
        private float mouseSensitivity = 0.15f;

        [SerializeField]
        [Tooltip(
            "Vertical look clamp (degrees). Prevents the camera from flipping past straight up/down."
        )]
        private float pitchClamp = 80f;

        [SerializeField]
        [Tooltip("Gravity applied while ungrounded, in metres/second^2 (positive value).")]
        private float gravity = 9.81f;

        [SerializeField]
        [Tooltip(
            "Child camera transform to rotate for vertical look. Auto-detected from children if unset."
        )]
        private Transform? cameraPivot;

        private CharacterController _controller = null!;
        private float _verticalVelocity;
        private float _pitch;

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            if (cameraPivot == null)
            {
                Camera child = GetComponentInChildren<Camera>();
                if (child != null)
                    cameraPivot = child.transform;
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
            (Vector2 move, Vector2 look) = ReadInput();

            // Mouse look — yaw rotates the body, pitch rotates the camera.
            float yaw = look.x * mouseSensitivity;
            float pitchDelta = look.y * mouseSensitivity;
            transform.Rotate(0f, yaw, 0f, Space.Self);
            if (cameraPivot != null)
            {
                _pitch = Mathf.Clamp(_pitch - pitchDelta, -pitchClamp, pitchClamp);
                cameraPivot.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
            }

            Vector3 step =
                (transform.right * move.x + transform.forward * move.y).normalized * moveSpeed;

            // Gravity. CharacterController.isGrounded latches false for one
            // frame between steps, so a tiny resting velocity keeps the
            // controller pinned to the ground for reliable trigger overlap.
            if (_controller.isGrounded && _verticalVelocity < 0f)
                _verticalVelocity = -2f;
            else
                _verticalVelocity -= gravity * Time.deltaTime;

            step.y = _verticalVelocity;
            _controller.Move(step * Time.deltaTime);
        }

        // Returns (move, look) for the current frame, picking the input
        // backend at compile time. `move` is WASD as a [-1, 1] x/y vector;
        // `look` is the mouse delta this frame.
        private static (Vector2 move, Vector2 look) ReadInput()
        {
            Vector2 move = Vector2.zero;
            Vector2 look = Vector2.zero;

#if ENABLE_INPUT_SYSTEM
            Keyboard? kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.wKey.isPressed)
                    move.y += 1f;
                if (kb.sKey.isPressed)
                    move.y -= 1f;
                if (kb.dKey.isPressed)
                    move.x += 1f;
                if (kb.aKey.isPressed)
                    move.x -= 1f;
            }
            Mouse? mouse = Mouse.current;
            if (mouse != null)
                look = mouse.delta.ReadValue();
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
            if (move == Vector2.zero)
                move = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
            if (look == Vector2.zero)
            {
                // Legacy mouse axes are scaled deltas, ~10× smaller than raw
                // pixels — multiply so the same mouseSensitivity feels similar.
                look = new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")) * 10f;
            }
#endif

            return (move, look);
        }
    }
}
