using UnityEngine;

public class CameraLook : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Transform cameraPivot;

    [Header("Sensitivity")]
    [SerializeField] private float mouseSensitivity = 2.0f;

    [Header("Pitch Clamp")]
    [SerializeField] private float minPitch = -60f;
    [SerializeField] private float maxPitch = 70f;

    private float pitch; // up/down rotation accumulator

    private void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        // Initialize pitch from current pivot rotation
        if (cameraPivot != null)
            pitch = cameraPivot.localEulerAngles.x;
    }

    private void Update()
    {
        if (cameraPivot == null)
            return;

        float mouseX = Input.GetAxisRaw("Mouse X") * mouseSensitivity;
        float mouseY = Input.GetAxisRaw("Mouse Y") * mouseSensitivity;

        // Yaw: rotate the whole player left/right
        transform.Rotate(0f, mouseX, 0f);

        // Pitch: rotate pivot up/down
        pitch -= mouseY;
        pitch = ClampPitch(pitch);

        cameraPivot.localRotation = Quaternion.Euler(pitch, 0f, 0f);

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        if (Input.GetMouseButtonDown(0))
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

    }

    private float ClampPitch(float value)
    {
        if (value > 180f) value -= 360f;
        return Mathf.Clamp(value, minPitch, maxPitch);
    }
}
