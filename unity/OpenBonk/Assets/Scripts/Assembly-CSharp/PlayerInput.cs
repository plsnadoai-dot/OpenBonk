using Assets.Scripts.Inventory__Items__Pickups.Interactables;
using Assets.Scripts.Inventory__Items__Pickups;
using Assets.Scripts.Actors.Player;
using Assets.Scripts.Utility;
using Assets.Scripts.Saves___Serialization.Progression.Challenges;
using Assets.Scripts.Player.Movement;
using UnityEngine;

public class PlayerInput : MonoBehaviour
{
	public DetectInteractables detectInteractables;

	private float moveHorizontal;

	private float moveVertical;

	private bool jumping;

	private bool interacting;

	private bool sliding;

	private bool aiming;

	private bool holdingJump;

	private bool holdingWallrun;

	public Vector3 cameraRotation;

	private Vector3 desiredCameraRotation;

	private float cameraSmoothingMin = 0.25f;

	private float cameraSmoothingMax = 0.75f;

	// Experimental opt-in. OFF unless explicitly enabled by a future settings UI.
	private const string AutoCameraPrefKey = "openbonk_auto_camera_enabled";
	private const float AutoCameraManualDelay = 0.5f;
	private const float AutoCameraYawSpeed = 120f; // degrees/second, speed 3/5
	private float lastManualCameraTime = -100f;

	private bool hasWallrunActionBound;

	private string stringMouseX = "Mouse X";

	private string stringMouseY = "Mouse Y";

	private int jumpBufferTicks = 4;

	private int currentJumpBufferTick;

	private void Awake()
	{
		PlayerHealth.A_Died += OnPlayerDied;
		KeyListener.A_MapChanged += OnInputMappingChanged;
	}

	private void OnDestroy()
	{
		PlayerHealth.A_Died -= OnPlayerDied;
		KeyListener.A_MapChanged -= OnInputMappingChanged;
	}

	private void Start()
	{
		hasWallrunActionBound = MyInputManager.IsActionBound(MyInputManager.Wallrun);
	}

	private void Update()
	{
		if (!CanInput() || MyTime.paused || MyTime.unpauseTick >= MyTime.tick || IsConsoleOpen()) return;
		MovementInput();
		RotationInput();
	}

	public static bool IsConsoleOpen()
	{
		return Assets.Scripts.UI.Debug.DebugConsole.Instance != null && Assets.Scripts.UI.Debug.DebugConsole.Instance.IsActive();
	}

	public Vector3 GetWishDir()
	{
		Quaternion rotation = Quaternion.Euler(0f, cameraRotation.y, 0f);
		return (rotation * Vector3.forward * moveVertical + rotation * Vector3.right * moveHorizontal).normalized;
	}

	// Preserve analog magnitude for presentation; movement direction remains normalized.
	internal float MovementInputMagnitude => Mathf.Clamp01(new Vector2(moveHorizontal, moveVertical).magnitude);

	private void TestInput()
	{
	}

	private void MovementInput()
	{
		moveHorizontal = MyInputManager.GetAxis(MyInputManager.MoveHorizontal);
		moveVertical = MyInputManager.GetAxis(MyInputManager.MoveVertical);
		if (ChallengesTracker.HasChallengeModifier("inverted_controls"))
		{
			moveHorizontal = -moveHorizontal;
			moveVertical = -moveVertical;
		}
#if UNITY_ANDROID && !UNITY_EDITOR
		if (AndroidNativeInput.PhysicalJumpDown)
#else
		if (MyInputManager.GetButtonDown(MyInputManager.Jump) ||
			(MyInputManager.GetButtonDown(MyInputManager.JumpBhop) && MyPlayer.Instance.playerMovement.CanBhopJump()))
#endif
		{
			QueueJump();
		}
		holdingJump = MyInputManager.GetButton(MyInputManager.Jump);
		var controls = SaveManager.Instance.config.cfControlSettings;
		if (controls.hold_crouch == 1) sliding = MyInputManager.GetButton(MyInputManager.Slide);
		else if (MyInputManager.GetButtonDown(MyInputManager.Slide)) sliding = !sliding;
		if (MyInputManager.GetButtonDown(MyInputManager.Interact)) interacting = true;
		if (!MyPlayer.Instance.inventory.weaponInventory.HasAimableWeapon()) aiming = false;
		else if (controls.hold_aim == 1) aiming = MyInputManager.GetButton(MyInputManager.Aim);
		else if (MyInputManager.GetButtonDown(MyInputManager.Aim)) aiming = !aiming;
		holdingWallrun = !hasWallrunActionBound || MyInputManager.GetButton(MyInputManager.Wallrun);
	}

	private void QueueJump()
	{
		jumping = true;
		currentJumpBufferTick = 0;
	}

	private void AbilityInput()
	{
	}

	public void SetSpawnDirection(Vector3 direction, float pitch = 0f)
	{
		float yaw = Quaternion.LookRotation(direction).eulerAngles.y;
		desiredCameraRotation = new Vector3(pitch, yaw, 0f);
		cameraRotation = desiredCameraRotation;
	}

	public bool IsHoldingJump()
	{
		return MyInputManager.GetButton(MyInputManager.Jump);
	}

	public void RotationInput()
	{
#if UNITY_ANDROID && !UNITY_EDITOR
		ManualRotation();
		AutoRotation();
		PlayerCamera.Instance.CameraInput(cameraRotation);
#else
		if (Cursor.lockState != CursorLockMode.None)
		{
			ManualRotation();
			AutoRotation();
			PlayerCamera.Instance.CameraInput(cameraRotation);
		}
		else PlayerCamera.Instance.MovePositionOnly();
#endif
	}

	private void AutoRotation()
	{
		if (PlayerPrefs.GetInt(AutoCameraPrefKey, 0) != 1) return;
		if (Time.unscaledTime - lastManualCameraTime < AutoCameraManualDelay) return;
		if (MovementInputMagnitude < 0.2f || MyPlayer.Instance == null ||
			MyPlayer.Instance.playerMovement == null || IsMovementDisabled()) return;

		// Use actual horizontal velocity, not the camera-relative input axes.
		// Ignore low speeds so collisions and small physics impulses do not jerk the view.
		Vector3 velocity = MyPlayer.Instance.playerMovement.GetVelocity();
		velocity.y = 0f;
		if (velocity.sqrMagnitude < 1f) return;
		float targetYaw = Mathf.Atan2(velocity.x, velocity.z) * Mathf.Rad2Deg;
		float yaw = Mathf.MoveTowardsAngle(desiredCameraRotation.y, targetYaw,
			AutoCameraYawSpeed * Time.deltaTime);
		desiredCameraRotation.y = yaw;
		// Preserve the existing camera smoothing, and never modify pitch.
		float smoothing = GetCameraSmoothing();
		if (smoothing > 0f)
		{
			float factor = cameraSmoothingMin + (cameraSmoothingMax - cameraSmoothingMin) * (1f - smoothing);
			cameraRotation.y = Mathf.LerpAngle(cameraRotation.y, yaw,
				Mathf.Clamp01(Time.deltaTime * factor * 20f));
		}
		else cameraRotation.y = yaw;
	}

	private void ManualRotation()
	{
		var controls = SaveManager.Instance.config.cfControlSettings;
		float mouseX = Input.GetAxis(stringMouseX);
		float mouseY = Input.GetAxis(stringMouseY);
#if UNITY_ANDROID && !UNITY_EDITOR
		// Unity can expose a touchscreen drag as mouse motion as well. The
		// touch sticks already provide look input, so do not count that drag a
		// second time while a finger is down. A mouse still works without touch.
		Vector2 mouse = FilterTouchMouse(new Vector2(mouseX, mouseY), Input.touchCount > 0);
		mouseX = mouse.x;
		mouseY = mouse.y;
		Vector2 touchLook = AndroidTouchFreeLook.Delta();
		mouseX += touchLook.x;
		mouseY += touchLook.y;
#endif
		float lookX = MyInputManager.GetAxis(MyInputManager.LookHorizontal);
		float lookY = MyInputManager.GetAxis(MyInputManager.LookVertical);
		float keyX = 0f, keyY = 0f;
		if (controls.rotate_camera_with_arrow_keys == 1)
		{
			keyX = Input.GetKey(KeyCode.LeftArrow) ? -3f : Input.GetKey(KeyCode.RightArrow) ? 3f : 0f;
			keyY = Input.GetKey(KeyCode.UpArrow) ? 3f : Input.GetKey(KeyCode.DownArrow) ? -3f : 0f;
		}
		if (Mathf.Abs(mouseX) + Mathf.Abs(mouseY) + Mathf.Abs(lookX) +
			Mathf.Abs(lookY) + Mathf.Abs(keyX) + Mathf.Abs(keyY) > 0.001f)
			lastManualCameraTime = Time.unscaledTime;
		ApplyRotationDelta(mouseX, mouseY, lookX, lookY, keyX, keyY, Time.deltaTime);
	}

	internal static Vector2 FilterTouchMouse(Vector2 mouse, bool hasTouch)
	{
		return hasTouch ? Vector2.zero : mouse;
	}

	private void ApplyRotationDelta(float mouseX, float mouseY, float lookX, float lookY, float keyX, float keyY, float deltaTime)
	{
		var controls = SaveManager.Instance.config.cfControlSettings;
		float horizontal = mouseX * controls.sensitivity + lookX * controls.controller_sensitivity * deltaTime * 50f + keyX;
		float vertical = lookY * controls.controller_sensitivity * deltaTime * 50f - mouseY * controls.sensitivity + keyY;
		if (controls.inverted_horizontal_axis == 1) horizontal = -horizontal;
		if (controls.inverted_vertical_axis == 1) vertical = -vertical;
		if (ChallengesTracker.HasChallengeModifier("inverted_controls")) { horizontal = -horizontal; vertical = -vertical; }
		desiredCameraRotation = new Vector3(Mathf.Clamp(desiredCameraRotation.x + vertical, -40f, 75f), desiredCameraRotation.y + horizontal, 0f);
		float smoothing = GetCameraSmoothing();
		if (smoothing > 0f)
		{
			float factor = cameraSmoothingMin + (cameraSmoothingMax - cameraSmoothingMin) * (1f - smoothing);
			cameraRotation = Vector3.Lerp(cameraRotation, desiredCameraRotation, deltaTime * factor * 20f);
		}
		else cameraRotation = desiredCameraRotation;
	}

	private float GetCameraSmoothing()
	{
		return SaveManager.Instance.config.cfControlSettings.look_smoothing;
	}

	private void FixedUpdate()
	{
		if (GameManager.Instance == null || !GameManager.Instance.IsPlaying() ||
			MyPlayer.Instance == null || MyPlayer.Instance.playerMovement == null || MyTime.paused) return;
		MyPlayer player = MyPlayer.Instance;
#if UNITY_ANDROID && !UNITY_EDITOR
		if (AndroidTouchInput.TakeJumpRequest() && CanInput() && MyTime.unpauseTick < MyTime.tick && !IsConsoleOpen())
			QueueJump();
		holdingJump = AndroidNativeInput.PhysicalJumpHeld || AndroidTouchInput.Held(MyInputManager.Jump);
#endif
		if (!player.IsDead() && !IsMovementDisabled())
			player.playerMovement.SetBetterInput(GetInputState());
		player.playerMovement.MovementTick();
		if (interacting) detectInteractables.TryInteract();
		if (currentJumpBufferTick < jumpBufferTicks && ++currentJumpBufferTick >= jumpBufferTicks) jumping = false;
		interacting = false;
	}

	private void OnPlayerDied()
	{
		MyPlayer.Instance.playerMovement.SetBetterInput(default(InputState));
	}

	private InputState GetInputState()
	{
		return new InputState(moveHorizontal, moveVertical, cameraRotation.y, cameraRotation.x,
			jumping, sliding, holdingJump, holdingWallrun);
	}

	private bool CanInput()
	{
		return GameManager.Instance != null && GameManager.Instance.GetPlayer() != null &&
			PlayerCamera.Instance != null && PlayerCamera.Instance.GetCameraState() == PlayerCamera.ECameraState.Player3rd;
	}

	private bool IsMovementDisabled()
	{
		return ChallengesTracker.HasChallengeModifier("no_movement");
	}

	public bool IsAiming()
	{
		return aiming;
	}

	private void OnInputMappingChanged()
	{
		hasWallrunActionBound = MyInputManager.IsActionBound(MyInputManager.Wallrun);
	}
}
