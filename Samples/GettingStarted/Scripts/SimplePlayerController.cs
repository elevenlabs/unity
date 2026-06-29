#nullable enable

using UnityEngine;

namespace ElevenLabs.Agents.Samples.GettingStarted
{
    /// <summary>
    /// Tiny WASD + mouse-look controller used by the GettingStarted scene so
    /// the sample can walk into a <see cref="TalkingBox"/>'s trigger without
    /// depending on Unity's Starter Assets or the Getting-Started template
    /// PlayerRobot prefab. Drop on a capsule with a non-trigger
    /// <see cref="CharacterController"/> and a child camera at head height.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public sealed class SimplePlayerController : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Walk speed in metres/second.")]
        private float moveSpeed = 4f;

        [SerializeField]
        [Tooltip("Mouse-look sensitivity, degrees per pixel of mouse delta.")]
        private float mouseSensitivity = 2f;

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
            // Mouse look — yaw rotates the body, pitch rotates the camera.
            float yaw = Input.GetAxisRaw("Mouse X") * mouseSensitivity;
            float pitchDelta = Input.GetAxisRaw("Mouse Y") * mouseSensitivity;
            transform.Rotate(0f, yaw, 0f, Space.Self);
            if (cameraPivot != null)
            {
                _pitch = Mathf.Clamp(_pitch - pitchDelta, -pitchClamp, pitchClamp);
                cameraPivot.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
            }

            // Horizontal movement in body-local space.
            float h = Input.GetAxisRaw("Horizontal");
            float v = Input.GetAxisRaw("Vertical");
            Vector3 move = (transform.right * h + transform.forward * v).normalized * moveSpeed;

            // Gravity. CharacterController.isGrounded latches false for one
            // frame between steps, so a tiny resting velocity keeps the
            // controller pinned to the ground for reliable trigger overlap.
            if (_controller.isGrounded && _verticalVelocity < 0f)
                _verticalVelocity = -2f;
            else
                _verticalVelocity -= gravity * Time.deltaTime;

            move.y = _verticalVelocity;
            _controller.Move(move * Time.deltaTime);
        }
    }
}
