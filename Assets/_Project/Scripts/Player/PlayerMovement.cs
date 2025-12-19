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

    [Header("Jump")]
    [SerializeField] private float jumpHeight = 1.5f;
    [SerializeField] private float jumpBufferTime = 0.10f;
    [SerializeField] private float coyoteTime = 0.10f;

    [Header("Gravity")]
    [SerializeField] private float gravity = -20f;

    [Header("Ground Check (Probe)")]
    [SerializeField] private Transform groundCheck;   // Empty child near feet
    [SerializeField] private float groundRadius = 0.25f;
    [SerializeField] private float groundCheckDistance = 0.25f;
    [SerializeField] private LayerMask groundMask;
    [SerializeField] private float maxSlopeAngle = 45f;

    [Header("Steep Slope Slide")]
    [SerializeField] private bool enableSteepSlide = true;
    [SerializeField] private float steepSlideSpeed = 1.2f;        // base slide speed
    [SerializeField] private float steepSlideAcceleration = 10f;  // how fast slide ramps once already sliding
    [SerializeField] private float steepSlideKick = 1.0f;         // immediate kick when bracing stops (0..1)

    public bool IsGrounded { get; private set; }
    public bool OnWalkableGround { get; private set; }
    public Vector3 GroundNormal { get; private set; } = Vector3.up;
    public float GroundAngle { get; private set; }

    private CharacterController controller;

    private Vector3 horizontalVelocity; // x/z only (smoothed)
    private float verticalVelocity;     // y only

    private float jumpBufferCounter;
    private float coyoteCounter;

    private Vector3 steepSlideVelocity;
    private bool wasBracingLastFrame;

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
    }

    private void Update()
    {
        // 0) Ground probe
        UpdateGroundProbe();

        // 1) Read input
        float x = Input.GetAxisRaw("Horizontal");
        float z = Input.GetAxisRaw("Vertical");

        Vector3 input = new Vector3(x, 0f, z);
        input = Vector3.ClampMagnitude(input, 1f);
        bool hasMoveInput = input.sqrMagnitude > 0.001f;

        // 2) Desired horizontal velocity
        Vector3 desired = (transform.right * input.x + transform.forward * input.z) * moveSpeed;

        // 3) Block uphill on too-steep surfaces
        if (IsGrounded && !OnWalkableGround && IsTryingToMoveUpSlope(desired))
        {
            desired = Vector3.zero;
        }

        // 4) Walkable slopes: hug ground plane
        if (IsGrounded && OnWalkableGround)
        {
            desired = ProjectOnGround(desired);
        }

        // 5) Smooth accel/decel
        float lerpRate = hasMoveInput ? acceleration : deceleration;
        horizontalVelocity = Vector3.Lerp(horizontalVelocity, desired, lerpRate * Time.deltaTime);

        // Strafe-friendly facing: rotate only with forward/back intent
        if (hasMoveInput && Mathf.Abs(input.z) > 0.1f)
            RotateBodyTowards(desired);

        // 6) Steep slope rule (ACTIVE BRACING):
        // On too-steep slopes, you slip unless you're actively holding W (forward).
        if (enableSteepSlide && IsGrounded && !OnWalkableGround)
        {
            bool bracingNow = input.z > 0.1f; // ONLY W/forward prevents slipping

            if (bracingNow)
            {
                // While bracing, cancel slide completely (no drift)
                steepSlideVelocity = Vector3.zero;
                wasBracingLastFrame = true;
            }
            else
            {
                Vector3 downSlope = GetDownSlopeDirection();

                // scale by steepness: 0 at maxSlopeAngle, 1 at 90
                float t = Mathf.InverseLerp(maxSlopeAngle, 90f, GroundAngle);

                Vector3 targetSlide = downSlope * (steepSlideSpeed * t);

                // If we JUST stopped bracing this frame, kick immediately into sliding
                if (wasBracingLastFrame)
                {
                    steepSlideVelocity = Vector3.Lerp(Vector3.zero, targetSlide, steepSlideKick);
                }
                else
                {
                    steepSlideVelocity = Vector3.Lerp(steepSlideVelocity, targetSlide, steepSlideAcceleration * Time.deltaTime);
                }

                horizontalVelocity += steepSlideVelocity;

                wasBracingLastFrame = false;
            }
        }
        else
        {
            // leaving steep ground -> clear bracing state and slide
            wasBracingLastFrame = false;
            steepSlideVelocity = Vector3.Lerp(steepSlideVelocity, Vector3.zero, steepSlideAcceleration * Time.deltaTime);
        }

        // 7) Jump buffer
        if (Input.GetButtonDown("Jump"))
            jumpBufferCounter = jumpBufferTime;
        else
            jumpBufferCounter -= Time.deltaTime;

        // 8) Coyote time (stable)
        if (controller.isGrounded)
            coyoteCounter = coyoteTime;
        else
            coyoteCounter -= Time.deltaTime;

        // 9) Jump
        if (jumpBufferCounter > 0f && coyoteCounter > 0f)
        {
            jumpBufferCounter = 0f;
            coyoteCounter = 0f;

            float jumpVelocity = Mathf.Sqrt(2f * jumpHeight * -gravity);
            verticalVelocity = jumpVelocity;
        }

        // 10) Ground stick
        if (IsGrounded && verticalVelocity < 0f)
            verticalVelocity = -2f;

        // 11) Gravity
        verticalVelocity += gravity * Time.deltaTime;

        // 12) Move
        Vector3 velocity = horizontalVelocity + Vector3.up * verticalVelocity;
        controller.Move(velocity * Time.deltaTime);
    }

    private void UpdateGroundProbe()
    {
        IsGrounded = false;
        OnWalkableGround = false;
        GroundNormal = Vector3.up;
        GroundAngle = 0f;

        if (groundCheck == null) return;

        Vector3 origin = groundCheck.position + Vector3.up * 0.05f;
        float castDistance = groundCheckDistance + 0.05f;

        if (Physics.SphereCast(origin, groundRadius, Vector3.down, out RaycastHit hit, castDistance, groundMask, QueryTriggerInteraction.Ignore))
        {
            IsGrounded = true;
            GroundNormal = hit.normal;
            GroundAngle = Vector3.Angle(GroundNormal, Vector3.up);
            OnWalkableGround = GroundAngle <= maxSlopeAngle;
        }
    }

    private Vector3 ProjectOnGround(Vector3 worldVelocity)
    {
        if (!IsGrounded) return worldVelocity;
        return Vector3.ProjectOnPlane(worldVelocity, GroundNormal);
    }

    private Vector3 GetDownSlopeDirection()
    {
        Vector3 down = Vector3.ProjectOnPlane(Vector3.down, GroundNormal);
        if (down.sqrMagnitude < 0.0001f) return Vector3.zero;
        return down.normalized;
    }

    private bool IsTryingToMoveUpSlope(Vector3 desiredWorldVelocity)
    {
        if (!IsGrounded) return false;
        if (GroundAngle < 0.5f) return false;

        Vector3 slopeRight = Vector3.Cross(GroundNormal, Vector3.up);
        if (slopeRight.sqrMagnitude < 0.0001f) return false;

        Vector3 slopeUp = Vector3.Cross(slopeRight, GroundNormal);
        slopeUp.y = 0f;
        if (slopeUp.sqrMagnitude < 0.0001f) return false;
        slopeUp.Normalize();

        Vector3 moveDir = desiredWorldVelocity;
        moveDir.y = 0f;
        if (moveDir.sqrMagnitude < 0.0001f) return false;
        moveDir.Normalize();

        return Vector3.Dot(moveDir, slopeUp) > 0.2f;
    }

    private void OnDrawGizmosSelected()
    {
        if (groundCheck == null) return;

        Vector3 origin = groundCheck.position + Vector3.up * 0.05f;
        float castDistance = groundCheckDistance + 0.05f;

        bool hit = Physics.SphereCast(origin, groundRadius, Vector3.down, out _, castDistance, groundMask, QueryTriggerInteraction.Ignore);

        Gizmos.color = hit ? Color.green : Color.red;
        Gizmos.DrawWireSphere(origin, groundRadius);
        Gizmos.DrawLine(origin, origin + Vector3.down * castDistance);
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
