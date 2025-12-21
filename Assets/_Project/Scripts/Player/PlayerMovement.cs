using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class PlayerMovement : MonoBehaviour
{
    // =========================================================
    // State
    // =========================================================
    public enum MovementState
    {
        Idle,
        Walking,
        Sprinting,
        Sliding,
        Airborne,
        Exhausted,
        Rolling
    }

    // Inspector-visible backing field (Unity doesn't show properties)
    [SerializeField] private MovementState currentState = MovementState.Idle;
    public MovementState CurrentState => currentState;

    // =========================================================
    // Debugging
    // =========================================================
    [Header("Debug HUD")]
    [SerializeField] private bool showDebugHUD = true;
    [SerializeField] private Vector2 debugHudOffset = new Vector2(12f, 12f);

    // =========================================================
    // Movement tuning
    // =========================================================
    [Header("Movement")]
    [SerializeField] private float moveSpeed = 5.5f;
    [SerializeField] private float acceleration = 14f;
    [SerializeField] private float deceleration = 18f;

    [Header("Sprint")]
    [SerializeField] private float sprintMultiplier = 2.5f;

    [Header("Facing")]
    [SerializeField] private Transform visualBody;
    [SerializeField] private float turnSpeed = 12f;

    [Header("Jump")]
    [SerializeField] private float jumpHeight = 1.5f;
    [SerializeField] private float jumpBufferTime = 0.10f;
    [SerializeField] private float coyoteTime = 0.10f;

    [Header("Gravity")]
    [SerializeField] private float gravity = -20f;

    // =========================================================
    // Ground / slope
    // =========================================================
    [Header("Ground Probe")]
    [SerializeField] private float groundRadius = 0.35f;
    [SerializeField] private float groundCheckDistance = 0.50f;
    [SerializeField] private LayerMask groundMask;
    [SerializeField] private float maxSlopeAngle = 45f;

    [Header("Steep Slope Slide")]
    [SerializeField] private bool enableSteepSlide = true;
    [SerializeField] private float steepSlideSpeed = 1.2f;
    [SerializeField] private float steepSlideAcceleration = 10f;
    [SerializeField] private float steepSlideKick = 1.0f;

    // =========================================================
    // Stamina
    // =========================================================
    [Header("Stamina")]
    [SerializeField] private float maxStamina = 100f;
    [SerializeField] private float staminaDrainPerSecond = 25f;
    [SerializeField] private float staminaRegenPerSecond = 18f;
    [SerializeField] private float exhaustedRegenDelay = 0.6f;

    // =========================================================
    // Dodge Roll (Prep)
    // =========================================================
    [Header("Dodge Roll")]
    [SerializeField] private KeyCode rollKey = KeyCode.LeftAlt;
    [SerializeField] private float rollDuration = 0.35f;
    [SerializeField] private float rollSpeed = 10f;
    [SerializeField] private float rollCost = 25f;

    // inspector-visible runtime for roll
    [SerializeField] private float rollTimer;
    [SerializeField] private bool isRolling;

    // =========================================================
    // Runtime info (Inspector-visible fields)
    // =========================================================
    [Header("Runtime (Read Only)")]
    [SerializeField] private float currentStamina; // backing field for inspector
    public float CurrentStamina => currentStamina;

    public bool IsGrounded { get; private set; }
    public bool OnWalkableGround { get; private set; }
    public Vector3 GroundNormal { get; private set; } = Vector3.up;
    public float GroundAngle { get; private set; }

    public bool CanRoll =>
        IsGrounded &&
        OnWalkableGround &&
        currentStamina >= rollCost &&
        !isRolling;

    // =========================================================
    // Internals
    // =========================================================
    private CharacterController controller;

    private Vector3 horizontalVelocity;
    private float verticalVelocity;

    private float jumpBufferCounter;
    private float coyoteCounter;

    private Vector3 steepSlideVelocity;
    private bool wasBracingLastFrame;

    private float exhaustedTimer;

    private Vector3 rollDirection;
    public bool IsRolling => isRolling;

    // =========================================================
    // Unity lifecycle
    // =========================================================
    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        currentStamina = maxStamina;
        currentState = MovementState.Idle;
        rollTimer = 0f;
        isRolling = false;
    }

    private void Update()
    {
        // 0) Ground probe
        UpdateGroundProbe();

        // 1) Input
        float x = Input.GetAxisRaw("Horizontal");
        float z = Input.GetAxisRaw("Vertical");

        Vector3 input = Vector3.ClampMagnitude(new Vector3(x, 0f, z), 1f);
        bool hasMoveInput = input.sqrMagnitude > 0.001f;

        // Intent signals
        bool forwardIntent = Input.GetKey(KeyCode.W);
        bool bracingNow = forwardIntent; // your design: only W prevents slipping
        bool sprintIntent = Input.GetKey(KeyCode.LeftShift);

        // Sprint gating (stamina + situation)
        bool sprintAllowed =
            IsGrounded &&
            OnWalkableGround &&
            forwardIntent &&
            currentStamina > 0.1f &&
            !isRolling;

        bool isSprinting = sprintIntent && sprintAllowed;

        // 2) Desired horizontal velocity
        float speed = isSprinting ? moveSpeed * sprintMultiplier : moveSpeed;
        Vector3 desired = (transform.right * input.x + transform.forward * input.z) * speed;

        // 3) Block uphill on too-steep surfaces
        if (IsGrounded && !OnWalkableGround && IsTryingToMoveUpSlope(desired))
            desired = Vector3.zero;

        // 4) Walkable slopes: hug ground plane
        if (IsGrounded && OnWalkableGround)
            desired = ProjectOnGround(desired);

        // 5) Smooth accel/decel
        float lerpRate = hasMoveInput ? acceleration : deceleration;
        horizontalVelocity = Vector3.Lerp(horizontalVelocity, desired, lerpRate * Time.deltaTime);

        // 6) Facing (rotate only with forward/back intent)
        if (hasMoveInput && Mathf.Abs(input.z) > 0.1f)
            RotateBodyTowards(desired);

        // 7) Steep slope rule (ACTIVE BRACING)
        ApplySteepSlopeSliding(bracingNow);

        // 8) Stamina update
        UpdateStamina(isSprinting);

        // 8.5) Roll input (prep)
        // NOTE: removed your duplicate LeftAlt test block; this is the single source of truth.
        if (!isRolling && Input.GetKeyDown(rollKey) && CanRoll)
        {
            if (TrySpendStamina(rollCost))
            {
                StartRoll(input);
            }
        }

        // 8.6) Roll update (timer only for now)
        if (isRolling)
        {
            UpdateRoll();
        }

        // 9) State update
        UpdateMovementState(hasMoveInput, bracingNow, isSprinting, sprintIntent, forwardIntent);

        // 10) Jumping (optional: we can later disable jumping during roll)
        HandleJump();

        // 11) Vertical velocity
        ApplyVerticalForces();

        // 12) Move
        controller.Move((horizontalVelocity + Vector3.up * verticalVelocity) * Time.deltaTime);
    }

    // =========================================================
    // Ground probe (controller-based)
    // =========================================================
    private void UpdateGroundProbe()
    {
        IsGrounded = false;
        OnWalkableGround = false;
        GroundNormal = Vector3.up;
        GroundAngle = 0f;

        Vector3 centerWorld = transform.TransformPoint(controller.center);
        float bottomY = centerWorld.y - (controller.height * 0.5f) + controller.radius;
        Vector3 origin = new Vector3(centerWorld.x, bottomY + 0.05f, centerWorld.z);

        if (Physics.SphereCast(
            origin,
            groundRadius,
            Vector3.down,
            out RaycastHit hit,
            groundCheckDistance,
            groundMask,
            QueryTriggerInteraction.Ignore))
        {
            IsGrounded = true;
            GroundNormal = hit.normal;
            GroundAngle = Vector3.Angle(GroundNormal, Vector3.up);
            OnWalkableGround = GroundAngle <= maxSlopeAngle;
        }
    }

    // =========================================================
    // Stamina
    // =========================================================
    private void UpdateStamina(bool isSprinting)
    {
        // start a short “breather” when we hit 0
        if (currentStamina <= 0f)
            exhaustedTimer = exhaustedRegenDelay;

        if (exhaustedTimer > 0f)
            exhaustedTimer -= Time.deltaTime;

        if (isSprinting)
        {
            currentStamina -= staminaDrainPerSecond * Time.deltaTime;
        }
        else
        {
            if (exhaustedTimer <= 0f)
                currentStamina += staminaRegenPerSecond * Time.deltaTime;
        }

        currentStamina = Mathf.Clamp(currentStamina, 0f, maxStamina);
    }

    private bool TrySpendStamina(float amount)
    {
        if (currentStamina < amount) return false;
        currentStamina -= amount;
        currentStamina = Mathf.Clamp(currentStamina, 0f, maxStamina);

        // apply the same regen delay when spending stamina
        exhaustedTimer = exhaustedRegenDelay;
        return true;
    }

    // =========================================================
    // Sliding
    // =========================================================
    private void ApplySteepSlopeSliding(bool bracingNow)
    {
        // Not on steep ground? decay slide velocity and exit.
        if (!enableSteepSlide || !IsGrounded || OnWalkableGround)
        {
            wasBracingLastFrame = false;
            steepSlideVelocity = Vector3.Lerp(steepSlideVelocity, Vector3.zero, steepSlideAcceleration * Time.deltaTime);
            return;
        }

        // On too-steep slope: holding W cancels sliding
        if (bracingNow)
        {
            steepSlideVelocity = Vector3.zero;
            wasBracingLastFrame = true;
            return;
        }

        Vector3 downSlope = GetDownSlopeDirection();
        float t = Mathf.InverseLerp(maxSlopeAngle, 90f, GroundAngle);
        Vector3 target = downSlope * (steepSlideSpeed * t);

        steepSlideVelocity = wasBracingLastFrame
            ? Vector3.Lerp(Vector3.zero, target, steepSlideKick)
            : Vector3.Lerp(steepSlideVelocity, target, steepSlideAcceleration * Time.deltaTime);

        horizontalVelocity += steepSlideVelocity;
        wasBracingLastFrame = false;
    }

    // =========================================================
    // State (for animations later)
    // =========================================================
    private void UpdateMovementState(bool hasMoveInput, bool bracingNow, bool isSprinting, bool sprintIntent, bool forwardIntent)
    {
        if (isRolling)
        {
            currentState = MovementState.Rolling;
            return;
        }

        if (!IsGrounded)
        {
            currentState = MovementState.Airborne;
            return;
        }

        // Too-steep ground: bracing vs sliding
        if (!OnWalkableGround)
        {
            currentState = bracingNow ? MovementState.Walking : MovementState.Sliding;
            return;
        }

        // Exhausted (trying to sprint but no stamina)
        bool tryingToSprint = sprintIntent && forwardIntent;
        if (tryingToSprint && currentStamina <= 0.1f)
        {
            currentState = MovementState.Exhausted;
            return;
        }

        if (!hasMoveInput)
        {
            currentState = MovementState.Idle;
            return;
        }

        currentState = isSprinting ? MovementState.Sprinting : MovementState.Walking;
    }

    // =========================================================
    // Jumping
    // =========================================================
    private void HandleJump()
    {
        if (Input.GetButtonDown("Jump"))
            jumpBufferCounter = jumpBufferTime;
        else
            jumpBufferCounter -= Time.deltaTime;

        if (IsGrounded)
            coyoteCounter = coyoteTime;
        else
            coyoteCounter -= Time.deltaTime;

        if (jumpBufferCounter > 0f && coyoteCounter > 0f)
        {
            jumpBufferCounter = 0f;
            coyoteCounter = 0f;
            verticalVelocity = Mathf.Sqrt(2f * jumpHeight * -gravity);
        }
    }

    private void ApplyVerticalForces()
    {
        if (IsGrounded && verticalVelocity < 0f)
            verticalVelocity = -2f;

        verticalVelocity += gravity * Time.deltaTime;
    }

    // =========================================================
    // Helpers
    // =========================================================
    private Vector3 ProjectOnGround(Vector3 v)
    {
        return Vector3.ProjectOnPlane(v, GroundNormal);
    }

    private Vector3 GetDownSlopeDirection()
    {
        Vector3 down = Vector3.ProjectOnPlane(Vector3.down, GroundNormal);
        return down.sqrMagnitude < 0.0001f ? Vector3.zero : down.normalized;
    }

    private bool IsTryingToMoveUpSlope(Vector3 desired)
    {
        if (GroundAngle < 0.5f) return false;

        Vector3 moveDir = new Vector3(desired.x, 0f, desired.z);
        if (moveDir.sqrMagnitude < 0.0001f) return false;
        moveDir.Normalize();

        Vector3 slopeRight = Vector3.Cross(GroundNormal, Vector3.up);
        if (slopeRight.sqrMagnitude < 0.0001f) return false;

        Vector3 slopeUp = Vector3.Cross(slopeRight, GroundNormal);
        slopeUp.y = 0f;
        if (slopeUp.sqrMagnitude < 0.0001f) return false;
        slopeUp.Normalize();

        return Vector3.Dot(moveDir, slopeUp) > 0.2f;
    }

    private void RotateBodyTowards(Vector3 dir)
    {
        if (visualBody == null) return;

        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f) return;

        Quaternion target = Quaternion.LookRotation(dir, Vector3.up);
        visualBody.rotation = Quaternion.Slerp(visualBody.rotation, target, turnSpeed * Time.deltaTime);
    }

    // =========================================================
    // Roll (Prep)
    // =========================================================
    private void StartRoll(Vector3 input)
    {
        isRolling = true;
        rollTimer = rollDuration;

        // Choose direction: input if present, otherwise forward
        Vector3 worldDir = (transform.right * input.x + transform.forward * input.z);
        worldDir.y = 0f;

        rollDirection = worldDir.sqrMagnitude > 0.001f ? worldDir.normalized : transform.forward;
    }

    private void UpdateRoll()
    {
        // Timer MUST tick down each frame while rolling
        rollTimer -= Time.deltaTime;

        if (rollTimer <= 0f)
        {
            rollTimer = 0f;
            isRolling = false;
            return;
        }

        // (No movement yet) — Day 05: apply roll velocity + input lock here
    }

    // =========================================================
    // Debug HUD
    // =========================================================
    private void OnGUI()
    {
        if (!showDebugHUD) return;

        GUI.Label(new Rect(debugHudOffset.x, debugHudOffset.y + 0f, 500f, 22f),
            $"State: {currentState}");

        GUI.Label(new Rect(debugHudOffset.x, debugHudOffset.y + 20f, 500f, 22f),
            $"Stamina: {currentStamina:0.0} / {maxStamina:0.0}");

        GUI.Label(new Rect(debugHudOffset.x, debugHudOffset.y + 40f, 500f, 22f),
            $"Grounded: {IsGrounded} | Walkable: {OnWalkableGround} | Angle: {GroundAngle:0.0}");

        GUI.Label(new Rect(debugHudOffset.x, debugHudOffset.y + 60f, 500f, 22f),
            $"CanRoll: {CanRoll} | Rolling: {isRolling} | RollTimer: {rollTimer:0.00}");
    }
}
