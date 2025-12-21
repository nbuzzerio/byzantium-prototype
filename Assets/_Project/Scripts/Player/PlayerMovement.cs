using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class PlayerMovement : MonoBehaviour
{
    // =========================================================
    // State (future animation driver)
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
    [SerializeField] private float lockedJumpBufferTime = 0.60f;
    [Tooltip("If you press Jump during roll/recovery, keep it buffered this long so it can fire on unlock.")]


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
    // Dodge Roll
    // =========================================================
    [Header("Dodge Roll")]
    [SerializeField] private KeyCode rollKey = KeyCode.LeftControl;   // <-- CHANGED
    [SerializeField] private float rollDuration = 0.35f;
    [SerializeField] private float rollSpeed = 10f;
    [SerializeField] private float rollCost = 25f;

    [Tooltip("Prevents spamming rolls back-to-back.")]
    [SerializeField] private float rollCooldown = 0.45f;

    [Tooltip("Short post-roll lock so the roll feels weighty.")]
    [SerializeField] private float rollRecoveryTime = 0.12f;

    [Tooltip("Optional: camera-relative input/roll. If empty, uses player transform.")]
    [SerializeField] private Transform cameraTransform;

    // =========================================================
    // Runtime (Inspector visible)
    // =========================================================
    [Header("Runtime (Read Only)")]
    [SerializeField] private float currentStamina;
    public float CurrentStamina => currentStamina;

    public bool IsGrounded { get; private set; }
    public bool OnWalkableGround { get; private set; }
    public Vector3 GroundNormal { get; private set; } = Vector3.up;
    public float GroundAngle { get; private set; }

    [SerializeField] private bool isRolling;
    [SerializeField] private float rollTimer;
    [SerializeField] private float rollCooldownTimer;
    [SerializeField] private float rollRecoveryTimer;

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

    // =========================================================
    // Convenience / gating
    // =========================================================
    public bool IsRolling => isRolling;

    private bool IsInputLocked => isRolling || rollRecoveryTimer > 0f;

    public bool CanRoll =>
        !IsInputLocked &&
        rollCooldownTimer <= 0f &&
        IsGrounded &&
        OnWalkableGround &&
        currentStamina >= rollCost;

    // =========================================================
    // Unity lifecycle
    // =========================================================
    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        currentStamina = maxStamina;

        currentState = MovementState.Idle;

        isRolling = false;
        rollTimer = 0f;
        rollCooldownTimer = 0f;
        rollRecoveryTimer = 0f;
    }

    private void Update()
    {
        // 0) Timers (cooldown + recovery)
        TickTimers();

        // 1) Ground probe
        UpdateGroundProbe();

        // 2) Jump buffering should work EVEN during roll/recovery
        // (You can press Jump during the roll and it will fire on unlock if still valid.)
        BufferJumpInput();
        UpdateCoyoteTimer();

        // 3) Read movement input (always)
        Vector3 input = ReadMoveInput(out bool hasMoveInput);
        bool forwardIntent = Input.GetKey(KeyCode.W);
        bool sprintIntent = Input.GetKey(KeyCode.LeftShift);
        bool bracingNow = forwardIntent; // your design

        // 4) Roll trigger
        TryStartRoll(input);

        // 5) Locked phase: move via roll/recovery; DO NOT run normal movement
        if (IsInputLocked)
        {
            UpdateLockedMovement();
            UpdateLockedState();
            return;
        }

        // 6) Normal movement
        bool isSprinting = GetIsSprinting(sprintIntent, forwardIntent);
        UpdateNormalMovement(input, hasMoveInput, isSprinting, bracingNow);

        // 7) Stamina
        UpdateStamina(isSprinting);

        // 8) State
        UpdateMovementState(hasMoveInput, bracingNow, isSprinting, sprintIntent, forwardIntent);

        // 9) Jump attempt (this now consumes buffered jump if available)
        TryConsumeBufferedJump();

        // 10) Gravity
        ApplyVerticalForces();

        // 11) Move
        controller.Move((horizontalVelocity + Vector3.up * verticalVelocity) * Time.deltaTime);
    }

    // =========================================================
    // Jump buffering (works through roll/recovery)
    // =========================================================
    private void BufferJumpInput()
    {
        // If Jump pressed, refresh buffer.
        // During roll/recovery, we use a longer buffer so it survives the lock.
        if (Input.GetButtonDown("Jump"))
        {
            jumpBufferCounter = IsInputLocked ? lockedJumpBufferTime : jumpBufferTime;
            return;
        }

        // IMPORTANT: Don’t burn the buffer while locked.
        // We want "press during roll -> jump on unlock".
        if (!IsInputLocked)
            jumpBufferCounter -= Time.deltaTime;
    }


    private void UpdateCoyoteTimer()
    {
        if (IsGrounded)
            coyoteCounter = coyoteTime;
        else
            coyoteCounter -= Time.deltaTime;
    }

    private void TryConsumeBufferedJump()
    {
        // Only attempt jump when NOT locked.
        // (We still buffered the input while locked.)
        if (jumpBufferCounter <= 0f) return;
        if (coyoteCounter <= 0f) return;

        jumpBufferCounter = 0f;
        coyoteCounter = 0f;

        verticalVelocity = Mathf.Sqrt(2f * jumpHeight * -gravity);
    }

    // =========================================================
    // Input
    // =========================================================
    private Vector3 ReadMoveInput(out bool hasMoveInput)
    {
        float x = Input.GetAxisRaw("Horizontal");
        float z = Input.GetAxisRaw("Vertical");

        Vector3 input = Vector3.ClampMagnitude(new Vector3(x, 0f, z), 1f);
        hasMoveInput = input.sqrMagnitude > 0.001f;
        return input;
    }

    // =========================================================
    // Timers
    // =========================================================
    private void TickTimers()
    {
        if (rollCooldownTimer > 0f)
            rollCooldownTimer -= Time.deltaTime;

        if (rollRecoveryTimer > 0f)
            rollRecoveryTimer -= Time.deltaTime;
    }

    // =========================================================
    // Rolling
    // =========================================================
    private void TryStartRoll(Vector3 input)
    {
        if (!Input.GetKeyDown(rollKey)) return;
        if (!CanRoll) return;
        if (!TrySpendStamina(rollCost)) return;

        StartRoll(input);
    }

    private void StartRoll(Vector3 input)
    {
        isRolling = true;
        rollTimer = rollDuration;
        rollCooldownTimer = rollCooldown;

        Vector3 worldDir = GetMoveWorldDirection(input);
        worldDir.y = 0f;

        if (worldDir.sqrMagnitude <= 0.001f)
        {
            Vector3 fwd = cameraTransform != null ? cameraTransform.forward : transform.forward;
            fwd.y = 0f;
            worldDir = fwd.sqrMagnitude > 0.001f ? fwd.normalized : transform.forward;
        }

        rollDirection = worldDir.normalized;

        if (IsGrounded && OnWalkableGround)
        {
            Vector3 groundedDir = ProjectOnGround(rollDirection);
            if (groundedDir.sqrMagnitude > 0.0001f)
                rollDirection = groundedDir.normalized;
        }

        RotateBodyTowards(rollDirection);
    }

    private void UpdateRoll()
    {
        rollTimer -= Time.deltaTime;

        if (rollTimer <= 0f)
        {
            rollTimer = 0f;
            isRolling = false;

            // start recovery lock
            rollRecoveryTimer = rollRecoveryTime;

            // small momentum carry
            horizontalVelocity = rollDirection * (rollSpeed * 0.25f);
            return;
        }

        horizontalVelocity = rollDirection * rollSpeed;
    }

    // =========================================================
    // Locked movement (rolling or recovery)
    // =========================================================
    private void UpdateLockedMovement()
    {
        if (isRolling)
        {
            UpdateRoll();
        }
        else
        {
            horizontalVelocity = Vector3.Lerp(horizontalVelocity, Vector3.zero, deceleration * Time.deltaTime);
        }

        // NOTE: we do NOT consume the buffered jump while locked.
        // We just keep gravity going.
        ApplyVerticalForces();

        controller.Move((horizontalVelocity + Vector3.up * verticalVelocity) * Time.deltaTime);
    }

    private void UpdateLockedState()
    {
        if (isRolling)
        {
            currentState = MovementState.Rolling;
            return;
        }

        currentState = IsGrounded ? MovementState.Idle : MovementState.Airborne;
    }

    // =========================================================
    // Normal movement flow
    // =========================================================
    private bool GetIsSprinting(bool sprintIntent, bool forwardIntent)
    {
        bool sprintAllowed =
            IsGrounded &&
            OnWalkableGround &&
            forwardIntent &&
            currentStamina > 0.1f;

        return sprintIntent && sprintAllowed;
    }

    private void UpdateNormalMovement(Vector3 input, bool hasMoveInput, bool isSprinting, bool bracingNow)
    {
        float speed = isSprinting ? moveSpeed * sprintMultiplier : moveSpeed;
        Vector3 desired = GetMoveWorldDirection(input) * speed;

        if (IsGrounded && !OnWalkableGround && IsTryingToMoveUpSlope(desired))
            desired = Vector3.zero;

        if (IsGrounded && OnWalkableGround)
            desired = ProjectOnGround(desired);

        float lerpRate = hasMoveInput ? acceleration : deceleration;
        horizontalVelocity = Vector3.Lerp(horizontalVelocity, desired, lerpRate * Time.deltaTime);

        if (hasMoveInput && Mathf.Abs(input.z) > 0.1f)
            RotateBodyTowards(desired);

        ApplySteepSlopeSliding(bracingNow);
    }

    // =========================================================
    // Ground probe
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

        exhaustedTimer = exhaustedRegenDelay;
        return true;
    }

    // =========================================================
    // Sliding
    // =========================================================
    private void ApplySteepSlopeSliding(bool bracingNow)
    {
        if (!enableSteepSlide || !IsGrounded || OnWalkableGround)
        {
            wasBracingLastFrame = false;
            steepSlideVelocity = Vector3.Lerp(steepSlideVelocity, Vector3.zero, steepSlideAcceleration * Time.deltaTime);
            return;
        }

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
    // State
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

        if (!OnWalkableGround)
        {
            currentState = bracingNow ? MovementState.Walking : MovementState.Sliding;
            return;
        }

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
    // Gravity
    // =========================================================
    private void ApplyVerticalForces()
    {
        if (IsGrounded && verticalVelocity < 0f)
            verticalVelocity = -2f;

        verticalVelocity += gravity * Time.deltaTime;
    }

    // =========================================================
    // Helpers
    // =========================================================
    private Vector3 GetMoveWorldDirection(Vector3 input)
    {
        if (cameraTransform != null)
        {
            Vector3 camForward = cameraTransform.forward;
            Vector3 camRight = cameraTransform.right;

            camForward.y = 0f;
            camRight.y = 0f;

            camForward.Normalize();
            camRight.Normalize();

            return camRight * input.x + camForward * input.z;
        }

        return transform.right * input.x + transform.forward * input.z;
    }

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
    // Debug HUD
    // =========================================================
    private void OnGUI()
    {
        if (!showDebugHUD) return;

        GUI.Label(new Rect(debugHudOffset.x, debugHudOffset.y + 0f, 720f, 22f),
            $"State: {currentState}");

        GUI.Label(new Rect(debugHudOffset.x, debugHudOffset.y + 20f, 720f, 22f),
            $"Stamina: {currentStamina:0.0} / {maxStamina:0.0}");

        GUI.Label(new Rect(debugHudOffset.x, debugHudOffset.y + 40f, 720f, 22f),
            $"Grounded: {IsGrounded} | Walkable: {OnWalkableGround} | Angle: {GroundAngle:0.0}");

        GUI.Label(new Rect(debugHudOffset.x, debugHudOffset.y + 60f, 720f, 22f),
            $"CanRoll: {CanRoll} | Rolling: {isRolling} | RollT: {rollTimer:0.00} | CD: {rollCooldownTimer:0.00} | Rec: {rollRecoveryTimer:0.00} | JumpBuf: {jumpBufferCounter:0.00}");
    }
}
