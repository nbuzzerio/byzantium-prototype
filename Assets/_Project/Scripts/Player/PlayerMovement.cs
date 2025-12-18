using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class PlayerMovement : MonoBehaviour
{
    [Header("Movement")]
    [SerializeField] private float moveSpeed = 5.5f;
    [SerializeField] private float acceleration = 14f;
    [SerializeField] private float deceleration = 18f;

    [Header("Facing")]
    [SerializeField] private Transform visualBody;   // drag Player/Body here
    [SerializeField] private float turnSpeed = 12f;  // higher = snappier


    [Header("Gravity")]
    [SerializeField] private float gravity = -20f;

    private CharacterController controller;

    private Vector3 horizontalVelocity; // x/z only (smoothed)
    private float verticalVelocity;     // y only

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
    }

    private void Update()
    {
        // 1) Input (WASD)
        float x = Input.GetAxisRaw("Horizontal");
        float z = Input.GetAxisRaw("Vertical");

        Vector3 input = new Vector3(x, 0f, z);
        input = Vector3.ClampMagnitude(input, 1f);

        // 2) Desired horizontal velocity in world space (relative to player forward/right)
        Vector3 desired = (transform.right * input.x + transform.forward * input.z) * moveSpeed;

        // 3) Smooth acceleration/deceleration
        float lerpRate = (input.sqrMagnitude > 0.001f) ? acceleration : deceleration;
        horizontalVelocity = Vector3.Lerp(horizontalVelocity, desired, lerpRate * Time.deltaTime);

        if (input.sqrMagnitude > 0.001f)
            RotateBodyTowards(desired);

        // 4) Gravity 
        if (controller.isGrounded && verticalVelocity < 0f)
            verticalVelocity = -2f; // keeps you "stuck" to ground slightly

        verticalVelocity += gravity * Time.deltaTime;

        // 5) Move
        Vector3 velocity = horizontalVelocity + Vector3.up * verticalVelocity;
        controller.Move(velocity * Time.deltaTime);
    }

    private void RotateBodyTowards(Vector3 worldMoveDir)
    {
        if (visualBody == null) return;

        // Only rotate on XZ plane
        worldMoveDir.y = 0f;
        if (worldMoveDir.sqrMagnitude < 0.0001f) return;

        Quaternion target = Quaternion.LookRotation(worldMoveDir, Vector3.up);
        visualBody.rotation = Quaternion.Slerp(visualBody.rotation, target, turnSpeed * Time.deltaTime);
    }

}
