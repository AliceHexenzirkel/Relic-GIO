#include "pch-il2cpp.h"
#include "FreeCamera.h"

#include <math.h>
#include <helpers.h>
#include <cheat-base/render/renderer.h>
#include <cheat/events.h>
#include <cheat/game/EntityManager.h>
#include <cheat/game/util.h>
#include <cheat/teleport/MapTeleport.h>

namespace cheat::feature
{
	app::GameObject* freeCam = nullptr;
	app::GameObject* mainCam = nullptr;
	app::Object_1* freeCamObj = nullptr;
	app::Object_1* mainCamObj = nullptr;
	app::GameObject* damageOverlay = nullptr;
	app::GameObject* hpOverlay = nullptr;
	app::Transform* freeCam_Transform;
	app::Component_1* freeCam_Camera;
	app::Component_1* mainCam_Camera;
	app::Vector3 targetPosition;
	app::Vector3 smoothPosition;
	float smoothFOV = 0;
	double focalLength = 0;
	bool isEnabled = false;

	// Relic: "Move Character with Camera" - shared between the two camera paths and the carry handler.
	//
	// The client streams the world around the LOCAL AVATAR, never around the camera: the SECTR streamer's
	// per-frame flush anchors every sector layer - terrain, props and the TerrainGrass layer - on
	// EntityManager.GetLocalAvatarEntity().GetAbsolutePosition(); the main camera is read in the same call
	// but only for its forward vector, as a priority hint. So the only way to make the world appear around
	// a flown-out free camera is to take the character with it.
	//
	// The camera pose is carried in ABSOLUTE (world-shift corrected) space on both paths and converted back
	// to the transform's space at the moment it is written. MoleMole.WorldShiftManager re-bases the world
	// origin whenever the center agent - the local avatar, i.e. the character WE are flying - passes
	// DEFAULT_LIMIT_LENGTH (1024 units), which the Mondstadt -> Dragonspine flight this feature exists for
	// does within seconds. Everything a Transform holds is relative to that moving origin, so a pose kept
	// across frames in a plain C++ static has to be absolute or it silently ends up in a dead basis.
	app::Vector3 relicCamPoseRel{};               // THIS frame's camera pose, in the transform's own space
	bool         relicCamPoseValid = false;
	app::Vector3 relicLastCamWritten{};           // the ABSOLUTE pose we last wrote; catches a respawned camera
	bool         relicLastCamValid = false;
	bool         relicTeleportToCameraRequest = false;   // set by DrawMain, consumed on the game thread
	int          relicReleaseRetries = 0;
	int          relicCarryHoldFrames = 0;        // the carry stands down while a scene transmit runs
	bool         relicCarryApplied = false;       // we turned this character's collisions off
	uint32_t     relicCarryRuntimeID = 0;
	int          relicCarryHeartbeat = 0;         // frames since OnAvatarCarryUpdate last ran
	uint32_t     relicStrandedRuntimeID = 0;      // a body whose collisions we could not restore yet
	int          relicStrandedRetries = 0;
	bool         relicSyncReseed = false;         // the character moved by something other than the carry

	// Relic: the free camera can drive the GAME's own camera instead of a clone of it - the only path on 1.6,
	// the "Drive the Game Camera" option on 2.8. See RelicDriveRealCam below.
	static void CameraStateMgr_FlushStateData_Hook(void* __this, void* data, MethodInfo* method);

	app::Transform* relicCamTransform = nullptr;   // the game camera, re-resolved every frame
	app::Camera*    relicCamCamera = nullptr;
	app::Vector3    relicCamPosition{};            // the ABSOLUTE pose we want; written by OnGameUpdate, applied by the hook
	app::Vector3    relicCamEuler{};
	float           relicCamFov = 45.0f;
	bool            relicCamDrive = false;         // read by the hook, written by OnGameUpdate only
	bool            relicCamFlushSeen = false;     // did FlushStateData actually run while we were driving
	int             relicCamFramesWaiting = 0;
	int             relicUpdateSilence = 0;        // GameUpdate ticks since OnGameUpdate last ran - OnCameraWatchdog's heartbeat
	bool            relicInputBlocked = false;     // we called UIManager.EnableInput(false) (upstream's static local in OnGameUpdate)
	bool            relicRealCamActive = false;    // which of the two paths owns the current isEnabled session
	// Flipping the option while the free camera is on hands the pose from one path to the other, so the view
	// stays where it is instead of snapping back to the character.
	bool            relicHandoffValid = false;
	app::Vector3    relicHandoffPos{};             // absolute
	float           relicHandoffFov = 45.0f;
	// Relic: the pose the camera path handed over for the next update (FreeCamera::PushPose), consumed once by
	// whichever camera path runs that frame. Absolute position, like everything else kept across frames here.
	FreeCameraPose  relicPathPose{};
	bool            relicPathPoseValid = false;
	// miHoYoCamera.CameraStateMgr._camera - the Camera the manager flushes into. 0x50 in both dumps (the class
	// layout is identical on 1.6 and 2.8). Read once per enable, for a diagnostic only.
	constexpr size_t RELIC_CAMSTATEMGR_CAMERA_OFFSET = 0x50;

	FreeCamera::FreeCamera() : Feature(),
		NFP(f_Enabled, "Visuals::FreeCamera", "Free Camera", false),
		NFP(f_FreezeAnimation, "Visuals::FreeCamera", "Freeze animation", false),
		NFP(f_SetAvatarInvisible, "Visuals::FreeCamera", "Set avatar invisible", false),
		NF(f_BlockInput, "Visuals::FreeCamera", false),
		NF(f_DamageOverlay, "Visuals::FreeCamera", false),
		NF(f_HpOverlay, "Visuals::FreeCamera", false),
		NF(f_Speed, "Visuals::FreeCamera", 1.0f),
		NF(f_LookSens, "Visuals::FreeCamera", 1.0f),
		NF(f_RollSpeed, "Visuals::FreeCamera", 1.0f),
		NF(f_FOVSpeed, "Visuals::FreeCamera", 0.1f),
		NF(f_FOV, "Visuals::FreeCamera", 45.0f),
		NF(f_MovSmoothing, "Visuals::FreeCamera", 1.0f),
		NF(f_LookSmoothing, "Visuals::FreeCamera", 1.0f),
		NF(f_RollSmoothing, "Visuals::FreeCamera", 1.0f),
		NF(f_FovSmoothing, "Visuals::FreeCamera", 1.0f),
		NF(f_MoveAvatar, "Visuals::FreeCamera", false),
		NF(f_DriveGameCamera, "Visuals::FreeCamera", false),
		NF(f_Forward, "Visuals::FreeCamera", Hotkey('W')),
		NF(f_Backward, "Visuals::FreeCamera", Hotkey('S')),
		NF(f_Left, "Visuals::FreeCamera", Hotkey('A')),
		NF(f_Right, "Visuals::FreeCamera", Hotkey('D')),
		NF(f_Up, "Visuals::FreeCamera", Hotkey(VK_SPACE)),
		NF(f_Down, "Visuals::FreeCamera", Hotkey(VK_LCONTROL)),
		NF(f_LeftRoll, "Visuals::FreeCamera", Hotkey('Z')),
		NF(f_RightRoll, "Visuals::FreeCamera", Hotkey('X')),
		NF(f_ResetRoll, "Visuals::FreeCamera", Hotkey('C')),
		NF(f_IncFOV, "Visuals::FreeCamera", Hotkey('3')),
		NF(f_DecFOV, "Visuals::FreeCamera", Hotkey('1'))
	{
		events::GameUpdateEvent += MY_METHOD_HANDLER(FreeCamera::OnGameUpdate);
		// Relic: the character carry gets its OWN subscription on purpose. TEvent counts faults per handler
		// and stops calling one for good after 20 in a row, so if the carry lived inside OnGameUpdate a bad
		// streak could strand the character with collisions off for the rest of the session.
		events::GameUpdateEvent += MY_METHOD_HANDLER(FreeCamera::OnAvatarCarryUpdate);
		// Relic: and the camera's own recovery, for the same reason - everything that puts the camera back lives
		// in OnGameUpdate, so it cannot be OnGameUpdate that notices OnGameUpdate has been silenced.
		events::GameUpdateEvent += MY_METHOD_HANDLER(FreeCamera::OnCameraWatchdog);
		events::MoveSyncEvent += MY_METHOD_HANDLER(FreeCamera::OnMoveSync);
		// Relic: the postfix that drives the game's own camera (1.6 always, 2.8 behind f_DriveGameCamera). It
		// is inert until relicCamDrive is set, so installing it on both versions costs nothing.
		INSTALL_HOOK(app::miHoYoCamera_CameraStateMgr_FlushStateData, CameraStateMgr_FlushStateData_Hook);
	}

	const FeatureGUIInfo& FreeCamera::GetGUIInfo() const
	{
		TRANSLATED_GROUP_INFO("Free Camera", "Visuals");
		return info;
	}

	void FreeCamera::DrawMain()
	{
		ConfigWidget(_TR("Enable"), f_Enabled);
		ConfigWidget(_TR("Freeze Character Animation"), f_FreezeAnimation, _TR("Freezes the active character's animation."));
		ConfigWidget(_TR("Make Character invisible"), f_SetAvatarInvisible, _TR("Hide Character machine effects."));
		ConfigWidget(_TR("Block User Input"), f_BlockInput, _TR("If enabled, any input will be blocked."));
		if (f_Enabled->enabled())
		{
			ConfigWidget(_TR("Toggle Damage Overlay"), f_DamageOverlay, _TR("Remove damage output overlay"));
			if (ImGui::Button(_TR("Remove HP")))
			{
				f_HpOverlay = true;
			}
		}

		if (ImGui::BeginTable("FreeCameraDrawTable", 1, ImGuiTableFlags_NoBordersInBody))
		{
			ImGui::TableNextRow();
			ImGui::TableSetColumnIndex(0);

			ImGui::BeginGroupPanel(_TR("Settings"));
			{
				ConfigWidget(_TR("Movement Speed"), f_Speed, 0.01f, 0.01f, 1000.0f);
				ConfigWidget(_TR("Look Sensitivity"), f_LookSens, 0.01f, 0.01f, 100.0f);
				ConfigWidget(_TR("Roll Speed"), f_RollSpeed, 0.01f, 0.01f, 100.0f);
				ConfigWidget(_TR("FOV Speed"), f_FOVSpeed, 0.01f, 0.01f, 100.0f);
				ConfigWidget(_TR("Field of View"), f_FOV, 0.1f, 0.01f, 200.0f, _TR("Changes Vertical FoV. Horizontal FoV depends on the viewport's aspect ratio"));
				if (ImGui::Button(_TR("Convert FoV to 35mm FF focal length")))
					focalLength = 24 / (2 * tan((f_FOV * 3.14159265) / (2 * 180))); // FocalLength = (vertical) sensor size / 2 * tan( 2*(vertical) FoV * Pi / 180)  Remember to convert degree to radian.  
				ImGui::Text("%s: %f", _TR("Focal length"), focalLength);
				ImGui::Spacing();
				ConfigWidget(_TR("Movement Smoothing"), f_MovSmoothing, 0.01f, 0.001f, 1.0f, _TR("Lower = Smoother"));
				ConfigWidget(_TR("Look Smoothing"), f_LookSmoothing, 0.01f, 0.001f, 1.0f, _TR("Lower = Smoother"));
				ConfigWidget(_TR("Roll Smoothing"), f_RollSmoothing, 0.01f, 0.001f, 1.0f, _TR("Lower = Smoother"));
				ConfigWidget(_TR("FOV Smoothing"), f_FovSmoothing, 0.01f, 0.001f, 1.0f, _TR("Lower = Smoother"));

				ImGui::Spacing();
				ConfigWidget(_TR("Move Character with Camera"), f_MoveAvatar,
					_TR("Carries the active character along with the free camera, at the same speed and with the\n"
						"same smoothing as the camera itself, keeping whatever distance it had when you\n"
						"switched the option on.\n"
						"\n"
						"This is what makes the world appear around the shot: the game streams terrain, props\n"
						"and grass around the CHARACTER, never around the camera - fly away from it and\n"
						"everything beyond the loaded area stays bare.\n"
						"\n"
						"Collision is turned off while this runs, and on release the character is set down on\n"
						"the ground below it.\n"
						"The weather and the climate region are decided by the SERVER, so they follow only if\n"
						"it accepts the move - use the button below if they do not.\n"
						"Best combined with Make Character invisible and Block User Input. Turn No-Clip OFF:\n"
						"it shares the same WASD keys and would drive the character out from under the camera."));

#if RELIC_GAME_VERSION > 16
				// Relic: 1.6 always drives the game camera (its clone path kills the grass there); 2.8 gets the
				// same method as an option. Default off = upstream's clone, the tested 2.8 behaviour.
				ConfigWidget(_TR("Drive the Game Camera"), f_DriveGameCamera,
					_TR("Moves the game's own camera instead of a copy of it - the way 1.6 does.\n"
						"Everything that reads or faces the main camera then follows the free camera:\n"
						"HP bars and damage numbers, billboards, distance-based effects.\n"
						"\n"
						"Off = a cloned camera with the real one switched off, as in upstream Akebi.\n"
						"Can be flipped while the free camera is on; the view stays where it is."));
#endif

				if (f_Enabled->enabled())
				{
					if (ImGui::Button(_TR("Teleport Character to Camera")))
						relicTeleportToCameraRequest = true;
					ImGui::SameLine();
					HelpMarker(_TR("Sends the character to where the camera is, through the game's own scene\n"
						"transmit - the way the map teleport does. That is the only path that makes the\n"
						"server re-evaluate the scene point, so it is the one that can switch the region\n"
						"and the weather.\n"
						"\n"
						"It needs an unlocked waypoint in this scene; if there is none the game refuses it\n"
						"and nothing moves."));
				}
			}
			ImGui::EndGroupPanel();

			ImGui::BeginGroupPanel(_TR("Hotkeys"));
			{
				ConfigWidget(_TR("Forward"), f_Forward, true);
				ConfigWidget(_TR("Backward"), f_Backward, true);
				ConfigWidget(_TR("Left"), f_Left, true);
				ConfigWidget(_TR("Right"), f_Right, true);
				ConfigWidget(_TR("Up"), f_Up, true);
				ConfigWidget(_TR("Down"), f_Down, true);
				ConfigWidget(_TR("Roll Left"), f_LeftRoll, true);
				ConfigWidget(_TR("Roll Right"), f_RightRoll, true);
				ConfigWidget(_TR("Reset Roll"), f_ResetRoll, true);
				ConfigWidget(_TR("Increase FOV"), f_IncFOV, true);
				ConfigWidget(_TR("Decrease FOV"), f_DecFOV, true);
			}
			ImGui::EndGroupPanel();
			ImGui::EndTable();
		}
	}

	bool FreeCamera::NeedStatusDraw() const
	{
		return f_Enabled->enabled();
	}

	void FreeCamera::DrawStatus()
	{
		ImGui::Text("%s%s", _TR("Free Camera"), f_MoveAvatar ? " [+Char]" : "");
	}

	FreeCamera& FreeCamera::GetInstance()
	{
		static FreeCamera instance;
		return instance;
	}

	class CameraRotation
	{
	public:
		float pitch, yaw, roll;

		void InitializeFromTransform(app::Transform* t)
		{
			auto t_eulerAngles = app::Transform_get_eulerAngles(t, nullptr);
			pitch = t_eulerAngles.x;
			yaw = t_eulerAngles.y;
			roll = t_eulerAngles.z;
		}

		void LerpTowards(CameraRotation target, float lookRotationLerpPct, float rollRotationLerpPct)
		{
			yaw = app::Mathf_Lerp(yaw, target.yaw, lookRotationLerpPct, nullptr);
			pitch = app::Mathf_Lerp(pitch, target.pitch, lookRotationLerpPct, nullptr);
			roll = app::Mathf_Lerp(roll, target.roll, rollRotationLerpPct, nullptr);
		}

		void UpdateTransform(app::Transform* t)
		{
			app::Transform_set_eulerAngles(t, app::Vector3{ pitch, yaw, roll }, nullptr);
		}
	};

	auto targetRotation = CameraRotation();
	auto currentRotation = CameraRotation();

	// Relic: consume the pose the camera path pushed for this update, if any, and seat the targets AND the
	// current rotation on it - both camera paths call this in place of their input block. The pose is applied
	// without smoothing (the path has already shaped the motion; the free camera's lerps would only add a lag
	// that ends every shot late) and the targets are left equal to it, so the frame after a path ends the
	// player's own input continues from exactly where the path stopped, without a snap.
	static bool RelicTakePathPose(FreeCameraPose& out)
	{
		if (!relicPathPoseValid)
			return false;
		relicPathPoseValid = false;
		out = relicPathPose;

		targetPosition = out.position;
		targetRotation.pitch = out.pitch;
		targetRotation.yaw = out.yaw;
		targetRotation.roll = out.roll;
		currentRotation = targetRotation;
		// The FOV setting follows the path's value WITHOUT the field-changed event (that would keep the 2 s
		// config save armed for the whole playback); CameraPath persists it once when the path ends.
		FreeCamera::GetInstance().f_FOV.value() = out.fov;
		return true;
	}

	void EnableFreeCam()
	{
		auto& settings = FreeCamera::GetInstance();
		freeCam = reinterpret_cast<app::GameObject*>(freeCamObj);

		freeCam_Transform = app::GameObject_get_transform(freeCam, nullptr);
		// Relic: the whole free-camera pose is kept in ABSOLUTE space - see the note at the top of the file.
		auto freeCam_Transform_position = app::WorldShiftManager_GetAbsolutePosition(
			app::Transform_get_position(freeCam_Transform, nullptr), nullptr);

		freeCam_Camera = app::GameObject_GetComponentByName(freeCam, string_to_il2cppi("Camera"), nullptr);
		mainCam_Camera = app::GameObject_GetComponentByName(mainCam, string_to_il2cppi("Camera"), nullptr);

		if (isEnabled == false)
		{
			app::Camera_CopyFrom(reinterpret_cast<app::Camera*>(freeCam_Camera), reinterpret_cast<app::Camera*>(mainCam_Camera), nullptr);

			if (relicHandoffValid)
			{
				// Relic: switched over from driving the game camera mid-session - keep the view where it is.
				// The rotation globals and the absolute targetPosition carry over untouched (a move or a look
				// still in flight keeps easing); the clone was spawned wherever the real camera happened to
				// be, so put it at the handed-over pose before the lerp below reads it.
				relicHandoffValid = false;
				freeCam_Transform_position = relicHandoffPos;
				app::Transform_set_position(freeCam_Transform,
					app::WorldShiftManager_GetRelativePosition(relicHandoffPos, nullptr), nullptr);
				app::Camera_set_fieldOfView(reinterpret_cast<app::Camera*>(freeCam_Camera), relicHandoffFov, nullptr);
			}
			else
			{
				targetRotation.InitializeFromTransform(freeCam_Transform);
				currentRotation.InitializeFromTransform(freeCam_Transform);
				targetPosition = freeCam_Transform_position;
			}
			relicLastCamValid = false;
			relicCamPoseValid = false;
			isEnabled = true;
		}

		app::GameObject_set_active(mainCam, false, nullptr);
		app::GameObject_set_active(freeCam, true, nullptr);

		// MOVEMENT
		// Relic: a camera-path pose replaces the input for this update (see RelicTakePathPose), and the input is
		// not read while the F1 menu owns the keyboard. Upstream read the raw key state unconditionally, so
		// typing in the menu flew the camera (and '1'/'3' changed the FOV) - harmless enough on its own,
		// unbearable once the character is being carried along with it. No-Clip already guards the same way.
		FreeCameraPose relicPath{};
		const bool relicPathDriven = RelicTakePathPose(relicPath);
		const bool relicInput = !relicPathDriven && !renderer::IsInputLocked();
		if (relicInput)
		{
			if (settings.f_Forward.value().IsPressed())
				targetPosition = targetPosition + app::Transform_get_forward(freeCam_Transform, nullptr) * settings.f_Speed;
			if (settings.f_Backward.value().IsPressed())
				targetPosition = targetPosition - app::Transform_get_forward(freeCam_Transform, nullptr) * settings.f_Speed;
			if (settings.f_Right.value().IsPressed())
				targetPosition = targetPosition + app::Transform_get_right(freeCam_Transform, nullptr) * settings.f_Speed;
			if (settings.f_Left.value().IsPressed())
				targetPosition = targetPosition - app::Transform_get_right(freeCam_Transform, nullptr) * settings.f_Speed;

			if (settings.f_LeftRoll.value().IsPressed())
				targetRotation.roll += settings.f_RollSpeed;
			if (settings.f_RightRoll.value().IsPressed())
				targetRotation.roll -= settings.f_RollSpeed;
			if (settings.f_ResetRoll.value().IsPressed())
				targetRotation.roll = 0.0f;

			if (settings.f_Up.value().IsPressed())
				targetPosition = targetPosition + app::Transform_get_up(freeCam_Transform, nullptr) * settings.f_Speed;
			if (settings.f_Down.value().IsPressed())
				targetPosition = targetPosition - app::Transform_get_up(freeCam_Transform, nullptr) * settings.f_Speed;

			if (settings.f_DecFOV.value().IsPressed())
				settings.f_FOV -= settings.f_FOVSpeed;
			if (settings.f_IncFOV.value().IsPressed())
				settings.f_FOV += settings.f_FOVSpeed;

			// Update the target rotation based on mouse input
			auto mouseX = app::Input_GetAxis(string_to_il2cppi("Mouse X"), nullptr);
			auto mouseY = app::Input_GetAxis(string_to_il2cppi("Mouse Y"), nullptr);
			auto mouseInput = app::Vector2{ mouseX, mouseY * -1.0f };
			targetRotation.yaw += mouseInput.x * settings.f_LookSens;
			targetRotation.pitch += mouseInput.y * settings.f_LookSens;
		}

		// Commit the rotation changes to the transform
		currentRotation.UpdateTransform(freeCam_Transform);

		// Relic: lerp from the ABSOLUTE pose we wrote last frame, never from the transform read back. Nothing
		// legitimately moves a Cinemachine-stripped clone behind our back, and one thing moves it illegitimately:
		// MoleMole.WorldShiftManager re-bases only its registered agents, and an Object.Instantiate'd clone is
		// not one - after a re-base its transform keeps the OLD relative numbers, i.e. it has physically moved
		// by the shift. Reading that back and "absorbing" it as an external move would turn
		// every re-base into a 1024-unit jump of the view - reachable exactly when the character is carried
		// along. Writing rel(absolute) below puts the clone back in the new basis. A respawned clone re-seeds
		// through isEnabled = false instead.
		smoothPosition = relicPathDriven ? targetPosition
			: app::Vector3_Lerp(relicLastCamValid ? relicLastCamWritten : freeCam_Transform_position,
				targetPosition, settings.f_MovSmoothing, nullptr);
		relicLastCamWritten = smoothPosition;
		relicLastCamValid = true;

		// Back to the transform's own space to write it, and publish that same value for the character carry
		// so both are read in one basis within one frame.
		relicCamPoseRel = app::WorldShiftManager_GetRelativePosition(smoothPosition, nullptr);
		relicCamPoseValid = true;
		app::Transform_set_position(freeCam_Transform, relicCamPoseRel, nullptr);

		smoothFOV = relicPathDriven ? relicPath.fov
			: app::Mathf_Lerp(app::Camera_get_fieldOfView(reinterpret_cast<app::Camera*>(freeCam_Camera), nullptr), settings.f_FOV, settings.f_FovSmoothing, nullptr);
		app::Camera_set_fieldOfView(reinterpret_cast<app::Camera*>(freeCam_Camera), smoothFOV, nullptr);
		currentRotation.LerpTowards(targetRotation, settings.f_LookSmoothing, settings.f_RollSmoothing);
	}

	void DisableFreeCam()
	{
		// Relic: reset the state FIRST and touch the objects last, through locals. A destroyed Unity object
		// raises on any call; if that happens here the frame is lost to the fault guard, but nothing is left
		// pointing at the dead object, so the next frame cannot repeat it - twenty repeats would silence
		// OnGameUpdate for the session. A dead object needs neither call anyway: the real camera dies with its
		// level and the clone with it. Idempotent, so it is safe from the disabled branch every frame.
		auto* realCam = mainCam;
		auto* clone = freeCamObj;
		mainCam = nullptr;
		freeCamObj = nullptr;
		// The clone is gone, so freeCam / freeCam_Transform dangle from here on - the pose must never be read
		// outside the enabled branch.
		freeCam = nullptr;
		freeCam_Transform = nullptr;
		relicLastCamValid = false;
		relicCamPoseValid = false;
		isEnabled = false;

		if (realCam != nullptr && realCam->fields._.m_CachedPtr != nullptr)
			app::GameObject_set_active(realCam, true, nullptr);   // a no-op unless the clone path switched it off
		if (clone != nullptr && reinterpret_cast<app::GameObject*>(clone)->fields._.m_CachedPtr != nullptr)
			app::Object_1_Destroy_1(clone, nullptr);
	}

	// Relic: a destroyed Unity object keeps a live managed wrapper and RAISES on every call instead of
	// returning null (m_CachedPtr is the liveness idiom ESPRender and HideUI use). Drop every handle to a camera
	// the level swap took away BEFORE anything can touch it - the option flip, a release, a seed - and stand the
	// path that owned it down. Pure pointer reads apart from the clone teardown, so it runs first thing in
	// OnGameUpdate, ahead of its early return. Idempotent; both run paths call it again.
	static void RelicDropDeadCameras()
	{
		if (mainCam != nullptr && mainCam->fields._.m_CachedPtr == nullptr)
		{
			mainCam = nullptr;
			// A camera path pushes a pose before this sweep sees the death (its handler runs first); a pose meant
			// for a camera the level swap took away must not seat the next scene's camera at those coordinates.
			relicPathPoseValid = false;
			if (relicRealCamActive)
			{
				// The driven camera is gone: the hook must go quiet with it. Re-found by GameObject_Find on the
				// next run and re-seeded from it.
				relicCamDrive = false;
				relicCamTransform = nullptr;
				relicCamCamera = nullptr;
				relicCamPoseValid = false;
				relicLastCamValid = false;
				isEnabled = false;
			}
			else
			{
				// The clone path: a clone can outlive its original (Instantiate puts it in the ACTIVE scene, not
				// the original's), and EnableFreeCam reads the real camera every frame - run against a null
				// mainCam it would fault once per frame for the whole loading screen. End the clone session;
				// the next run re-clones from the new camera and re-seeds.
				DisableFreeCam();
			}
		}
		if (freeCamObj != nullptr
			&& reinterpret_cast<app::GameObject*>(freeCamObj)->fields._.m_CachedPtr == nullptr)
		{
			freeCamObj = nullptr;
			freeCam = nullptr;
			freeCam_Transform = nullptr;
			relicCamPoseValid = false;
			relicLastCamValid = false;
			relicPathPoseValid = false;   // same reason as above
			isEnabled = false;   // re-seed from the new camera next frame
		}
		// Upstream cached this once and called SetActive on it every frame; after a UI rebuild it is dead and
		// every frame faults - the same twenty-fault silence, and on the real path a silenced OnGameUpdate is
		// what would leave the hook driving the camera with nobody left to stop it.
		if (damageOverlay != nullptr && damageOverlay->fields._.m_CachedPtr == nullptr)
			damageOverlay = nullptr;
	}

	// Relic: drive the game's OWN camera instead of a clone of it. The only path on 1.6; on 2.8 the
	// "Drive the Game Camera" option (default off - upstream's clone stays the tested 2.8 default).
	//
	// Upstream instantiates /EntityRoot/MainCamera(Clone), strips the clone's Cinemachine components and then
	// keeps the real camera's GameObject deactivated for as long as the free camera runs. On 1.6 that is what
	// makes every blade of terrain grass disappear: MoleMole.MonoMiHoYoVegetationManager (1.6 dump.cs:1704006)
	// rides on that GameObject and drives the whole native grass system from its own enable/disable edges -
	// OnEnable @0x0271F350 calls UnityEngine.MiHoYoVegetationManager.Intial, OnDisable @0x0271F230 calls
	// Shutdown - and Shutdown is a PROCESS-WIDE switch, not a per-camera one (UnityPlayer's Internal_Intial
	// @0x00F97D10 only takes the camera instance id and the callee never reads it). Instantiate makes it
	// worse: the clone carries a copy of that singleton component, whose Awake @0x0271F100 destroys the
	// duplicate, and the dying copy's OnDisable calls Shutdown once more. Nothing puts the grass back until
	// the real camera is activated again - which is why it returns the moment the free camera is switched off.
	//
	// Nothing about a free camera needs a second camera. The pose of the game camera is applied by
	// miHoYoCamera.CameraStateMgr.FlushStateData (1.6 @0x067F2160, 2.8 @0x041A33F0), the last writer of the
	// frame: Flush runs the post processer before it and PostFlushTop after it writes no transform, and the
	// scheduler task that tail-calls Flush has the same shape on both versions (tools/callscan.py shows all
	// of that from the shipped binaries - FlushStateData is the only method in the chain that calls
	// Transform.set_position/set_rotation and Camera.set_fieldOfView). So we let the game compute and apply
	// its camera as usual and overwrite the result at the end of that same call. The game camera is never
	// cloned, never deactivated and never destroyed, so the vegetation manager - and everything else bound to
	// that GameObject - cannot notice the free camera at all.
	//
	// On 2.8 the grass does not need it (the clone loses no grass there), but everything that reads or faces
	// the MAIN camera - HP bars and damage numbers, billboards, distance-based effects - keeps looking at the
	// deactivated real camera under the clone design, and follows the free camera only when the real camera
	// is the one being driven. Hence the option.
	static void CameraStateMgr_FlushStateData_Hook(void* __this, void* data, MethodInfo* method)
	{
		// Postfix: let the game write its own pose first, then replace it. Calling the origin first also means
		// a fault in our code cannot cost the game its camera update - HookGuard never re-runs the origin.
		CALL_ORIGIN(CameraStateMgr_FlushStateData_Hook, __this, data, method);

		if (!relicCamDrive || relicCamTransform == nullptr)
			return;
		// Relic: the hook's own stand-downs, because relicCamDrive is otherwise cleared only by OnGameUpdate and
		// a handler the fault guard has silenced can never clear it (OnCameraWatchdog covers that within ~2 s;
		// these two act at once). A pure pointer read on the driven GameObject - a destroyed one keeps its
		// wrapper and raises on any call, so relicCamTransform != nullptr proves nothing - and the free-camera
		// toggle itself, whose hotkey is applied on the input path, independent of any handler. RelicDriveRealCam
		// re-arms all three every tick, so a live OnGameUpdate is unaffected.
		if (mainCam == nullptr || mainCam->fields._.m_CachedPtr == nullptr
			|| !FreeCamera::GetInstance().f_Enabled->enabled())
		{
			relicCamDrive = false;
			relicCamTransform = nullptr;
			relicCamCamera = nullptr;
			return;
		}

		if (!relicCamFlushSeen)
		{
			relicCamFlushSeen = true;
			LOG_DEBUG("[freecam] CameraStateMgr.FlushStateData reached - driving the game camera.");
			// The manager flushes into its own bound Camera (CameraStateMgr._camera). That must be the camera
			// we drive, or the pose we write lands on one the game is not rendering with - the one way this
			// path can look "dead" on a version it was not proven on. Diagnostic only, checked once per enable.
			auto bound = *reinterpret_cast<app::Camera**>(
				reinterpret_cast<uint8_t*>(__this) + RELIC_CAMSTATEMGR_CAMERA_OFFSET);
			if (bound != nullptr && relicCamCamera != nullptr && bound != relicCamCamera)
				LOG_WARNING("[freecam] CameraStateMgr is bound to camera %p but the free camera drives %p "
					"(/EntityRoot/MainCamera(Clone)) - the view will not follow.", bound, relicCamCamera);
		}

		// relicCamPosition is absolute; convert at apply time, never reuse a stale relative value - a
		// world-shift re-base can land between OnGameUpdate and this call.
		app::Transform_set_position(relicCamTransform,
			app::WorldShiftManager_GetRelativePosition(relicCamPosition, nullptr), nullptr);
		app::Transform_set_eulerAngles(relicCamTransform, relicCamEuler, nullptr);
		if (relicCamCamera != nullptr)
			app::Camera_set_fieldOfView(relicCamCamera, relicCamFov, nullptr);
	}

	// The counterpart of EnableFreeCam(): upstream's input handling, applied to the game's camera.
	static void RelicDriveRealCam()
	{
		auto& settings = FreeCamera::GetInstance();

		// Relic: a destroyed Unity object still has a managed wrapper and raises on every call, so the null
		// test below cannot see a camera the level swap took away - RelicDropDeadCameras (m_CachedPtr) can, and
		// it stands the hook down with it, or it would keep writing to a dead transform once per frame.
		RelicDropDeadCameras();
		if (mainCam == nullptr)
			mainCam = app::GameObject_Find(string_to_il2cppi("/EntityRoot/MainCamera(Clone)"), nullptr);
		if (mainCam == nullptr)
			return;

		auto camTransform = app::GameObject_get_transform(mainCam, nullptr);
		if (camTransform == nullptr)
		{
			mainCam = nullptr;   // the level was swapped under us - look the camera up again next frame
			return;
		}
		auto camComponent = app::GameObject_GetComponentByName(mainCam, string_to_il2cppi("Camera"), nullptr);
		// Relic: the pose the frame actually left behind, in ABSOLUTE space - see the note at the top.
		auto curCamPosition = app::WorldShiftManager_GetAbsolutePosition(
			app::Transform_get_position(camTransform, nullptr), nullptr);

		if (isEnabled == false)
		{
			// This path needs the real camera ON. Only the clone path ever switches it off and its release
			// switches it back, but a level swap and an option flip in the same frame can skip that release -
			// and SetActive(true) on an active object is a no-op, so this is free.
			app::GameObject_set_active(mainCam, true, nullptr);

			if (relicHandoffValid)
			{
				// Relic: switched over from the clone mid-session - keep the view where it is (the rotation
				// globals and the absolute targetPosition carry over untouched, so a move or a look still in
				// flight keeps easing).
				relicHandoffValid = false;
				relicCamPosition = relicHandoffPos;
				relicCamFov = relicHandoffFov;
			}
			else
			{
				// Seed from wherever the game's camera is right now, so switching on does not jump the view.
				targetRotation.InitializeFromTransform(camTransform);
				currentRotation.InitializeFromTransform(camTransform);
				targetPosition = curCamPosition;
				relicCamPosition = targetPosition;
				relicCamFov = settings.f_FOV;
				if (camComponent != nullptr)
					relicCamFov = app::Camera_get_fieldOfView(reinterpret_cast<app::Camera*>(camComponent), nullptr);
			}
			relicCamFlushSeen = false;
			relicCamFramesWaiting = 0;
			relicLastCamValid = false;
			relicCamPoseValid = false;
			isEnabled = true;
		}

		// MOVEMENT - upstream's block, with the basis vectors taken from the game camera.
		// Relic: a camera-path pose replaces the input for this update, and the input is gated on the menu not
		// owning the keyboard - see the same notes in EnableFreeCam.
		FreeCameraPose relicPath{};
		const bool relicPathDriven = RelicTakePathPose(relicPath);
		const bool relicInput = !relicPathDriven && !renderer::IsInputLocked();
		if (relicInput)
		{
			if (settings.f_Forward.value().IsPressed())
				targetPosition = targetPosition + app::Transform_get_forward(camTransform, nullptr) * settings.f_Speed;
			if (settings.f_Backward.value().IsPressed())
				targetPosition = targetPosition - app::Transform_get_forward(camTransform, nullptr) * settings.f_Speed;
			if (settings.f_Right.value().IsPressed())
				targetPosition = targetPosition + app::Transform_get_right(camTransform, nullptr) * settings.f_Speed;
			if (settings.f_Left.value().IsPressed())
				targetPosition = targetPosition - app::Transform_get_right(camTransform, nullptr) * settings.f_Speed;

			if (settings.f_LeftRoll.value().IsPressed())
				targetRotation.roll += settings.f_RollSpeed;
			if (settings.f_RightRoll.value().IsPressed())
				targetRotation.roll -= settings.f_RollSpeed;
			if (settings.f_ResetRoll.value().IsPressed())
				targetRotation.roll = 0.0f;

			if (settings.f_Up.value().IsPressed())
				targetPosition = targetPosition + app::Transform_get_up(camTransform, nullptr) * settings.f_Speed;
			if (settings.f_Down.value().IsPressed())
				targetPosition = targetPosition - app::Transform_get_up(camTransform, nullptr) * settings.f_Speed;

			if (settings.f_DecFOV.value().IsPressed())
				settings.f_FOV -= settings.f_FOVSpeed;
			if (settings.f_IncFOV.value().IsPressed())
				settings.f_FOV += settings.f_FOVSpeed;

			auto mouseX = app::Input_GetAxis(string_to_il2cppi("Mouse X"), nullptr);
			auto mouseY = app::Input_GetAxis(string_to_il2cppi("Mouse Y"), nullptr);
			auto mouseInput = app::Vector2{ mouseX, mouseY * -1.0f };
			targetRotation.yaw += mouseInput.x * settings.f_LookSens;
			targetRotation.pitch += mouseInput.y * settings.f_LookSens;
		}

		// Commit this frame's pose, then advance the smoothed rotation - upstream applies it one frame late.
		relicCamEuler = app::Vector3{ currentRotation.pitch, currentRotation.yaw, currentRotation.roll };
		// Relic: no "absorb whatever moved the camera behind our back" here either (see EnableFreeCam): the
		// only writer of this transform after our postfix is our postfix, a respawned camera re-seeds through
		// isEnabled = false, and the read-back differs from relicLastCamWritten by exactly the world-shift
		// delta whenever a re-base lands between the postfix write and this read.
		relicCamPosition = relicPathDriven ? targetPosition
			: app::Vector3_Lerp(relicCamPosition, targetPosition, settings.f_MovSmoothing, nullptr);
		relicLastCamWritten = relicCamPosition;
		relicLastCamValid = true;
		// The transform-space pose for this frame, for the character carry (see EnableFreeCam).
		relicCamPoseRel = app::WorldShiftManager_GetRelativePosition(relicCamPosition, nullptr);
		relicCamPoseValid = true;
		relicCamFov = relicPathDriven ? relicPath.fov
			: app::Mathf_Lerp(relicCamFov, settings.f_FOV, settings.f_FovSmoothing, nullptr);
		currentRotation.LerpTowards(targetRotation, settings.f_LookSmoothing, settings.f_RollSmoothing);

		relicCamTransform = camTransform;
		relicCamCamera = reinterpret_cast<app::Camera*>(camComponent);
		relicCamDrive = true;

		// Apply it here as well: where FlushStateData is not the live camera path this is the only write there
		// is, and where it is, the hook overwrites the same values a moment later.
		app::Transform_set_position(camTransform, relicCamPoseRel, nullptr);
		app::Transform_set_eulerAngles(camTransform, relicCamEuler, nullptr);
		if (relicCamCamera != nullptr)
			app::Camera_set_fieldOfView(relicCamCamera, relicCamFov, nullptr);

		if (!relicCamFlushSeen && ++relicCamFramesWaiting == 300)
			LOG_WARNING("[freecam] CameraStateMgr.FlushStateData has not run in 300 frames - the free camera is "
				"only writing the camera transform from Update.");
	}

	// The counterpart of DisableFreeCam(). Nothing to unwind: the game never stopped computing its own camera,
	// and FlushStateData applies it again on the very next call. Unconditional on purpose - the hook must go
	// quiet whatever state the rest is in.
	static void RelicReleaseRealCam()
	{
		relicCamDrive = false;
		relicCamTransform = nullptr;
		relicCamCamera = nullptr;
		mainCam = nullptr;
		relicLastCamValid = false;
		relicCamPoseValid = false;
		isEnabled = false;
	}

	// Which path this frame wants. 1.6 has no choice (see above); 2.8 reads the option.
	static bool RelicUseRealCam()
	{
#if RELIC_GAME_VERSION <= 16
		return true;
#else
		return FreeCamera::GetInstance().f_DriveGameCamera;
#endif
	}

	// Stand down whichever path owns the camera. Exactly one of them does while isEnabled, and each release
	// only unwinds what its own path set up (only the clone path ever deactivated the real camera).
	// The pending camera-path pose is deliberately NOT dropped here: the "Drive the Game Camera" flip stands one
	// path down and seeds the other from the handoff in the same tick, and the pose pushed for that tick must
	// reach the new path (dropping it would repeat a frame and then skip one). The disabled branch of OnGameUpdate,
	// RelicDropDeadCameras and OnCameraWatchdog drop it - those are the cases where no camera should take it.
	static void RelicStopCamera()
	{
		if (relicRealCamActive)
			RelicReleaseRealCam();
		else
			DisableFreeCam();
	}

	// Upstream's free camera: a clone of the game camera, the real one deactivated. Kept as the 2.8 default
	// because it is the path that has been tested there.
	static void RelicRunCloneCam()
	{
		// Relic: a Unity object whose native side is gone keeps a live managed wrapper and RAISES on
		// every call instead of returning null, so a level swap would leave both of these dangling
		// and every frame after it would fault - twenty of them and the fault guard switches this handler
		// off for the session. RelicDropDeadCameras is that check (m_CachedPtr, the idiom ESPRender and
		// HideUI use); it already ran at the top of OnGameUpdate and is idempotent.
		RelicDropDeadCameras();
		if (mainCam == nullptr)
			mainCam = app::GameObject_Find(string_to_il2cppi("/EntityRoot/MainCamera(Clone)"), nullptr);
		if (mainCam == nullptr)
			return;   // nothing to clone from yet (a loading screen); EnableFreeCam reads the real camera every frame
		if (freeCamObj == nullptr)
		{
			freeCamObj = app::Object_1_Instantiate_2(reinterpret_cast<app::Object_1*>(mainCam), nullptr);

			auto mainCamTransform = app::GameObject_get_transform(mainCam, nullptr);
			auto mainCamPos = app::Transform_get_position(mainCamTransform, nullptr);
			auto freeCamObjTransform = app::GameObject_get_transform(reinterpret_cast<app::GameObject*>(freeCamObj), nullptr);
			app::Transform_set_position(freeCamObjTransform, mainCamPos, nullptr);

			auto CinemachineBrain = app::GameObject_GetComponentByName(reinterpret_cast<app::GameObject*>(freeCamObj), string_to_il2cppi("CinemachineBrain"), nullptr);
			auto CinemachineExternalCamera = app::GameObject_GetComponentByName(reinterpret_cast<app::GameObject*>(freeCamObj), string_to_il2cppi("CinemachineExternalCamera"), nullptr);
			app::Object_1_Destroy_1(reinterpret_cast<app::Object_1*>(CinemachineBrain), nullptr);
			app::Object_1_Destroy_1(reinterpret_cast<app::Object_1*>(CinemachineExternalCamera), nullptr);

			app::GameObject_set_active(mainCam, false, nullptr);
			app::GameObject_set_active(mainCam, true, nullptr);
			app::GameObject_set_active(reinterpret_cast<app::GameObject*>(freeCamObj), false, nullptr);
		}
		if (freeCamObj)
			EnableFreeCam();
	}

	// Relic: "Make Character invisible" calls MoleMole.Miscs.SetUILocalAvatarVisible, which does not exist in 1.6 -
	// it was added later (the whole 1.6 Miscs class is unobfuscated and has no such member), so on 1.6 the call would
	// simply be skipped and the option would do nothing. 2.8's method is only
	//     localAvatar.GetRendererComponent().SetRendererVisible(visible, 14, true)
	// and 1.6 ships both halves as ordinary instance methods, which the game itself calls back to back in
	// InteractionManager::ResumeAvatarVisibleSet. The "reason" is a bit index in a bit stack that starts all-ones
	// (visible = every bit set), so clearing one bit hides the avatar; 14 is the slot 2.8 reserves for exactly this
	// and no 1.6 game code ever passes it, so nothing else can clear or restore it behind our back.
#if RELIC_GAME_VERSION <= 16
	static const int32_t RELIC_VISIBLE_REASON_UI_LOCAL_AVATAR = 14;

	static void ApplyRendererVisible(app::BaseEntity* entity, bool visible)
	{
		if (entity == nullptr
			|| app::MoleMole_BaseEntity_GetRendererComponent == nullptr
			|| app::MoleMole_BaseEntityRendererComponent_SetRendererVisible == nullptr)
			return;

		auto renderer = app::MoleMole_BaseEntity_GetRendererComponent(entity, nullptr);
		if (renderer != nullptr)
			app::MoleMole_BaseEntityRendererComponent_SetRendererVisible(
				renderer, visible, RELIC_VISIBLE_REASON_UI_LOCAL_AVATAR, true, nullptr);
	}
#endif

	static void SetLocalAvatarVisible(bool visible)
	{
#if RELIC_GAME_VERSION <= 16
		// Never remember a raw entity pointer across frames - the game recycles the managed entity (which is why
		// EntityManager hooks entity destruction). Remember the runtime id and resolve it again each time; a gone
		// entity resolves to the empty one, whose raw() is null and which ApplyRendererVisible ignores.
		static uint32_t s_HiddenRuntimeID = 0;

		auto& manager = game::EntityManager::instance();
		auto* avatarEntity = manager.avatar();
		auto* avatarRaw = avatarEntity->raw();
		auto avatarID = avatarEntity->runtimeID();

		if (!visible)
		{
			if (s_HiddenRuntimeID == avatarID && avatarID != 0)
				return;   // already hidden: SetRendererVisible walks every renderer, do not repeat it per frame

			if (s_HiddenRuntimeID != 0)   // the player switched character while hidden - give the old one back
			{
				auto* previous = manager.entity(s_HiddenRuntimeID);
				if (previous != nullptr)
					ApplyRendererVisible(previous->raw(), true);
			}
			ApplyRendererVisible(avatarRaw, false);
			s_HiddenRuntimeID = avatarID;
		}
		else
		{
			if (s_HiddenRuntimeID == 0)
				return;

			auto* previous = manager.entity(s_HiddenRuntimeID);
			if (previous != nullptr)
				ApplyRendererVisible(previous->raw(), true);
			if (avatarRaw != nullptr && avatarID != s_HiddenRuntimeID)
				ApplyRendererVisible(avatarRaw, true);
			s_HiddenRuntimeID = 0;
		}
#else
		if (app::Miscs_SetUILocalAvatarVisible != nullptr)
			app::Miscs_SetUILocalAvatarVisible(visible, nullptr);
#endif
	}

	// Relic: give a carried character its collisions back. Returns false when the body cannot be reached
	// right now - a swapped-out or mid-transmit avatar is not IsActive, so EntityManager hands back the empty
	// entity and Entity::rigidbody() is null until it is loaded again. The caller must NOT forget about it:
	// leaving a character with collisions off is the one outcome that must never survive a session.
	static bool RelicRestoreCollisions(uint32_t runtimeID)
	{
		if (runtimeID == 0)
			return true;

		auto* entity = game::EntityManager::instance().entity(runtimeID);
		if (entity == nullptr || entity->raw() == nullptr)
			return false;

		auto* rb = entity->rigidbody();
		if (rb == nullptr)
			return false;

		app::Rigidbody_set_velocity(rb, app::Vector3{ 0.f, 0.f, 0.f }, nullptr);
		app::Rigidbody_set_detectCollisions(rb, true, nullptr);
		return true;
	}

	// Hand a body we could not restore to the retrier in OnGameUpdate - a different handler, so it survives
	// this one being switched off by the fault guard.
	static void RelicHandOffStranded(uint32_t runtimeID)
	{
		if (runtimeID == 0)
			return;
		relicStrandedRuntimeID = runtimeID;
		relicStrandedRetries = 0;
	}

	// Best-effort "put it down where it is" - the release path's ground probe, without the retry budget.
	static void RelicGroundSnap(uint32_t runtimeID)
	{
		auto* entity = game::EntityManager::instance().entity(runtimeID);
		if (entity == nullptr || entity->raw() == nullptr)
			return;

		auto pos = entity->relativePosition();
		if (IsVectorZero(pos))
			return;

		float ground = app::Miscs_CalcCurrentGroundWaterHeight(pos.x, pos.z, nullptr);
		if (ground > 0.f)
		{
			pos.y = ground + 5.f;
			entity->setRelativePosition(pos);
		}
	}

	// Relic: "Move Character with Camera".
	//
	// Why this exists: the client streams the world around the local avatar, so a free camera flown away
	// from the character shows whatever was already loaded and nothing else - bare terrain, no grass. The
	// fix is to take the character with the camera. It is driven from the camera's POSE, not from an
	// integrated delta: the offset between the two is measured once when the option arms and the character
	// is placed at camera + offset every frame after that, so the speed and the smoothing are the free
	// camera's by construction and a frame the carry has to skip costs nothing.
	//
	// This is a separate GameUpdate handler on purpose - see the note in the constructor.
	void FreeCamera::OnAvatarCarryUpdate()
	{
		static app::Vector3 s_Offset{};        // character position MINUS camera pose, in transform space
		static bool         s_HasOffset = false;

		relicCarryHeartbeat = 0;               // proof of life for the recovery path in OnGameUpdate

		auto& manager = game::EntityManager::instance();
		auto* avatarEntity = manager.avatar();     // never null - an unresolved avatar is the empty entity
		auto  runtimeID = avatarEntity->runtimeID();

		// ---- the "Teleport Character to Camera" button, consumed on the game thread ----------------
		// Handled before everything else so a retrying ground probe cannot swallow the request. MapTeleport
		// drives its own multi-stage task (and a loading screen for anything past ~60 m), so the carry stops
		// WRITING for a while - but it does not release: collisions stay off and the velocity stays zeroed,
		// or a request the teleport quietly refuses (no unlocked waypoint in this scene, no LoadingManager)
		// would drop the character out of the sky. When the hold ends the offset is simply re-captured from
		// wherever the character actually is.
		if (relicTeleportToCameraRequest)
		{
			relicTeleportToCameraRequest = false;
			if (isEnabled && relicCamPoseValid && avatarEntity->raw() != nullptr)
			{
				// Both paths keep their pose absolute already.
				app::Vector3 camAbs = relicRealCamActive ? relicCamPosition : smoothPosition;
				if (!IsVectorZero(camAbs))
				{
					// The player's scene, not the map UI's - they differ whenever the map is open on
					// another region, and transmitting into the wrong scene is not recoverable in-place.
					// Only stand the carry down if the request was accepted: MapTeleport refuses when the
					// scene has no unlocked waypoint, and standing down for a teleport that never happens
					// would leave the character hanging with nothing driving it.
					if (MapTeleport::GetInstance().TeleportTo(camAbs, true, game::GetCurrentPlayerSceneID()))
					{
						relicCarryHoldFrames = 300;
						relicSyncReseed = true;
					}
				}
			}
		}

		const bool armed = f_Enabled->enabled() && f_MoveAvatar && isEnabled;
		const bool hold = relicCarryHoldFrames > 0;
		if (hold)
			--relicCarryHoldFrames;

		// ---- release -------------------------------------------------------------------------------
		// Driven by our own flag, never by isEnabled alone: the option can be switched off while the free
		// camera stays on, and a character switch has to release the OLD body or it stays ghosted. A hold is
		// NOT a release - it only stops the position write.
		if (relicCarryApplied && !hold && (!armed || runtimeID != relicCarryRuntimeID))
		{
			bool restored = false;
			auto* released = (runtimeID == relicCarryRuntimeID) ? avatarEntity : manager.entity(relicCarryRuntimeID);
			if (released != nullptr && released->raw() != nullptr)
			{
				auto* rb = released->rigidbody();
				if (rb != nullptr)
				{
					app::Rigidbody_set_velocity(rb, app::Vector3{ 0.f, 0.f, 0.f }, nullptr);

					// Put the character down before collisions come back, or it either falls through the
					// terrain or eats the whole drop as fall damage. Same call the map teleport uses - and
					// the same guard, because it raycasts scene ground colliders and returns <= 0 where
					// nothing has streamed in yet. Carrying the character is what streams that terrain, so
					// here the probe is expected to succeed; the retry budget is for the frames right after
					// a long flight where it has not caught up yet.
					auto pos = released->relativePosition();
					if (!IsVectorZero(pos))
					{
						float ground = app::Miscs_CalcCurrentGroundWaterHeight(pos.x, pos.z, nullptr);
						if (ground > 0.f)
						{
							pos.y = ground + 5.f;
							released->setRelativePosition(pos);
						}
						else if (++relicReleaseRetries < 300)
						{
							return;   // hold collisions OFF and retry - do not drop the player into nothing
						}
						else
						{
							LOG_WARNING("[freecam] no ground under the character after 300 frames - releasing "
								"anyway, it may fall.");
						}
					}
					app::Rigidbody_set_detectCollisions(rb, true, nullptr);
					restored = true;
				}
			}
			// Every one of the three checks above can fail on a body that is mid-rebuild, and the flags below
			// are what the recovery in OnGameUpdate keys off - so a failed restore has to be handed on, not
			// dropped, or that character keeps its collisions off for the rest of the session.
			if (!restored)
				RelicHandOffStranded(relicCarryRuntimeID);
			relicReleaseRetries = 0;
			relicCarryApplied = false;
			relicCarryRuntimeID = 0;
			s_HasOffset = false;
		}

		// A hold is not a release. If the option - or the free camera - is switched off while a scene
		// transmit is in flight, the release above is gated on !hold and does not run, and the per-frame
		// clamp further down sits behind this same early return: the body would keep our detectCollisions
		// = false and simply fall. Hold it still until the hold expires and the release can put it down.
		// The position is deliberately not written; that is exactly what the hold suppresses.
		if (!armed && hold && relicCarryApplied)
		{
			auto* held = (runtimeID == relicCarryRuntimeID) ? avatarEntity : manager.entity(relicCarryRuntimeID);
			if (held != nullptr && held->raw() != nullptr)
			{
				auto* rb = held->rigidbody();
				if (rb != nullptr)
				{
					app::Rigidbody_set_detectCollisions(rb, false, nullptr);
					app::Rigidbody_set_velocity(rb, app::Vector3{ 0.f, 0.f, 0.f }, nullptr);
				}
			}
			s_HasOffset = false;
			return;
		}

		if (!armed)
			return;

		// ---- guards --------------------------------------------------------------------------------
		if (avatarEntity->raw() == nullptr)
			return;
		auto* rigidBody = avatarEntity->rigidbody();   // null while loading / mid-rebuild
		if (rigidBody == nullptr)
			return;

		// ---- (re)seat on the body actually being controlled -----------------------------------------
		if (!relicCarryApplied || runtimeID != relicCarryRuntimeID)
		{
			// A character switch during a teleport hold never reaches the release above (gated on !hold),
			// so give the body we are leaving behind its collisions back here or it stays ghosted.
			if (relicCarryApplied && relicCarryRuntimeID != 0 && runtimeID != relicCarryRuntimeID
				&& !RelicRestoreCollisions(relicCarryRuntimeID))
				RelicHandOffStranded(relicCarryRuntimeID);

			relicCarryApplied = true;
			relicCarryRuntimeID = runtimeID;
			s_HasOffset = false;             // never reuse an offset captured for another body
			relicReleaseRetries = 0;
		}

		// Collisions have to be off: with them on the character is stopped by the first hillside while the
		// camera flies on, and the feature silently dies after a few metres. Zeroing the velocity every frame
		// is what keeps the body from accumulating a fall - it is also all that runs during a teleport hold.
		//
		// Rigidbody.collisionDetectionMode is deliberately NOT touched (No-Clip does): neither appdata table
		// has a getter for it, so that write could never be observed or restored.
		app::Rigidbody_set_detectCollisions(rigidBody, false, nullptr);
		app::Rigidbody_set_velocity(rigidBody, app::Vector3{ 0.f, 0.f, 0.f }, nullptr);

		if (hold)
		{
			s_HasOffset = false;   // the teleport moves the character; re-measure once it is done
			return;
		}

		// Consume it: the pose is published by the camera path once per frame, and a frame where that path
		// did not run (a null UIManager singleton early in a load, a camera the level swapped away) must not
		// be answered with last frame's value - in the transform's space that could be a re-based basis.
		// Skipping is free here precisely because the carry is pose-driven and not an integration.
		if (!relicCamPoseValid)
			return;
		relicCamPoseValid = false;
		const app::Vector3 camPose = relicCamPoseRel;

		auto prevPos = avatarEntity->relativePosition();
		if (IsVectorZero(prevPos))          // the entity is not placed in the scene yet
			return;

		// Pose-drive, not delta-integration: the character is held at a fixed offset from the camera and
		// re-derived from the camera's pose every frame. A frame the carry has to skip (loading, an entity
		// rebuild, a teleport hold) therefore costs nothing - with an integrated delta it would have been a
		// permanent desync. The offset is measured once per arming, so the character keeps whatever distance
		// it had from the camera when the option was switched on.
		//
		// Both terms are read in the same frame and the same basis, so a world-shift re-base moves them
		// together and the offset survives it untouched.
		if (!s_HasOffset)
		{
			s_Offset = prevPos - camPose;
			s_HasOffset = true;
			return;                          // nothing to move on the frame we measured
		}

		app::Vector3 target = camPose + s_Offset;
		app::Vector3 step = target - prevPos;
		if (IsVectorZero(step))
			return;

		// Backstop: one frame must never move the character further than the camera plausibly could. With an
		// absolute-space pose and a re-measured offset nothing legitimate reaches this, so it is a pure
		// sanity guard - and it re-measures rather than dropping the frame, so it cannot desync the carry.
		float dist = GetVectorMagnitude(step);
		float limit = f_Speed * 10.f;
		if (limit < 50.f)
			limit = 50.f;
		if (dist > limit)
		{
			LOG_WARNING("[freecam] %.0fm camera step - re-measuring the character offset instead of jumping.", dist);
			s_HasOffset = false;
			relicSyncReseed = true;   // whatever moved the character, it was not us - do not report it as speed
			return;
		}

		// setRelativePosition, NEVER setAbsolutePosition: the absolute setter is hooked by MapTeleport and a
		// per-frame write would re-enter its teleport state machine.
		avatarEntity->setRelativePosition(target);
	}

	// Relic: while we are carrying the character, describe the move to the server as a plausible walk/run
	// instead of a per-frame teleport. No-Clip has the same fix but its copy early-returns unless No-Clip
	// itself is on, so this feature needs its own.
	void FreeCamera::OnMoveSync(uint32_t entityId, app::MotionInfo* syncInfo)
	{
		static app::Vector3 prevPosition = {};
		static int64_t prevSyncTime = 0;

		if (!(f_Enabled->enabled() && f_MoveAvatar && isEnabled))
		{
			prevSyncTime = 0;
			return;
		}
		if (syncInfo == nullptr || syncInfo->fields.pos_ == nullptr || syncInfo->fields.speed_ == nullptr)
			return;

		auto& manager = game::EntityManager::instance();
		auto* avatarEntity = manager.avatar();
		if (avatarEntity->raw() == nullptr || avatarEntity->runtimeID() != entityId)
			return;

		auto avatarPosition = avatarEntity->absolutePosition();   // MotionInfo.pos_ is ABSOLUTE

		// A teleport hold means the scene transmit is moving the character, not us, and the backstop raises
		// the same flag whenever something else did. Re-seed instead of dividing that jump by the frame time
		// and telling the server the player is running at a few thousand units a second.
		if (relicCarryHoldFrames > 0 || relicSyncReseed)
		{
			relicSyncReseed = false;
			prevPosition = avatarPosition;
			prevSyncTime = 0;
			return;
		}

		auto currentTime = util::GetCurrentTimeMillisec();
		if (prevSyncTime > 0)
		{
			auto timeDiff = ((float)(currentTime - prevSyncTime)) / 1000.f;
			if (timeDiff > 0.f)
			{
				auto velocity = (avatarPosition - prevPosition) / timeDiff;
				float speed = GetVectorMagnitude(velocity);
				if (speed > 0.1f)
				{
					syncInfo->fields.motionState = (speed < 2.f) ? app::MotionState__Enum::MotionWalk
																 : app::MotionState__Enum::MotionRun;
					syncInfo->fields.speed_->fields.x = velocity.x;
					syncInfo->fields.speed_->fields.y = velocity.y;
					syncInfo->fields.speed_->fields.z = velocity.z;
				}
			}
			syncInfo->fields.pos_->fields.x = avatarPosition.x;
			syncInfo->fields.pos_->fields.y = avatarPosition.y;
			syncInfo->fields.pos_->fields.z = avatarPosition.z;
		}
		prevPosition = avatarPosition;
		prevSyncTime = currentTime;
	}

	// Relic: the recovery for the camera itself. TEvent switches a handler off for the session after 20
	// consecutive faults, and everything that puts the camera back - the real camera re-activated, the clone
	// destroyed, the hook stood down, the input unblocked - lives in OnGameUpdate. A silenced OnGameUpdate would
	// leave the player looking through a frozen clone with the real camera off (or, on the real path, carried by
	// a hook nobody stops) until a game restart. This handler has its own fault budget, does nothing but count
	// while OnGameUpdate is alive, and its teardown is the same liveness-gated, state-first release the normal
	// path uses. Same pattern as the carry heartbeat in OnGameUpdate.
	void FreeCamera::OnCameraWatchdog()
	{
		if (++relicUpdateSilence <= 120)
			return;
		relicUpdateSilence = 0;   // and again in ~2 s if the teardown could not reach everything

		const bool armed = isEnabled || mainCam != nullptr || freeCamObj != nullptr || relicCamDrive
			|| relicInputBlocked;
		if (!armed)
			return;

		LOG_WARNING("[freecam] OnGameUpdate has not run for 120 ticks - releasing the camera and the input.");
		DisableFreeCam();          // first: it is the one that re-activates a real camera the clone path switched off
		RelicReleaseRealCam();
		damageOverlay = nullptr;
		hpOverlay = nullptr;
		relicHandoffValid = false;
		relicPathPoseValid = false;
		if (relicInputBlocked)
		{
			auto uiManager = GET_SINGLETON(MoleMole_UIManager);
			if (uiManager != nullptr)
			{
				app::MoleMole_UIManager_EnableInput(uiManager, true, false, false, nullptr);
				relicInputBlocked = false;
			}
		}
	}

	// Relic: the camera-path interface (see the header). Game thread only.
	bool FreeCamera::IsRunning() const
	{
		return isEnabled && relicLastCamValid;
	}

	bool FreeCamera::GetPose(FreeCameraPose& out) const
	{
		if (!isEnabled || !relicLastCamValid)
			return false;
		// relicLastCamWritten is the absolute pose whichever path wrote last; currentRotation is what the
		// transform was given this update (the smoothed value, not the target still easing towards).
		out.position = relicLastCamWritten;
		out.pitch = currentRotation.pitch;
		out.yaw = currentRotation.yaw;
		out.roll = currentRotation.roll;
		out.fov = relicRealCamActive ? relicCamFov : smoothFOV;
		return true;
	}

	void FreeCamera::PushPose(const FreeCameraPose& pose)
	{
		relicPathPose = pose;
		relicPathPoseValid = true;
	}

	// A plain flag store: CameraPath also calls it from a Stop served on the render or window thread, where a
	// one-frame race only means the camera consumes the pose once more before the flag is seen - never a fault.
	void FreeCamera::DropPose()
	{
		relicPathPoseValid = false;
	}

	app::Camera* FreeCamera::CurrentCamera() const
	{
		if (!isEnabled)
			return nullptr;
		if (relicRealCamActive)
			return relicCamCamera;
		return reinterpret_cast<app::Camera*>(freeCam_Camera);
	}

	bool FreeCamera::IsCarryHeld() const
	{
		return relicCarryApplied && relicCarryHoldFrames > 0;
	}

	void FreeCamera::OnGameUpdate()
	{
		relicUpdateSilence = 0;                // proof of life for OnCameraWatchdog
		// Relic: pure pointer reads (plus a liveness-gated teardown), so it is safe - and necessary - ahead of
		// the early return below: the hook keys off what it leaves behind, and a loading screen is exactly when
		// the camera dies.
		RelicDropDeadCameras();

		auto uiManager = GET_SINGLETON(MoleMole_UIManager);
		if (uiManager == nullptr)
			return;

		// Relic: recovery for the one outcome that must never survive a session - a character left with
		// its collisions off. OnAvatarCarryUpdate is the only code that turns them back on, and TEvent
		// stops calling a handler for good after 20 consecutive faults, so if it ever goes quiet while
		// the carry is applied this puts the collisions back. It fires only on silence (two seconds
		// without a tick), never during normal operation, so it cannot pre-empt the proper release.
		if (relicCarryApplied && ++relicCarryHeartbeat > 120)
		{
			relicCarryHeartbeat = 0;
			RelicGroundSnap(relicCarryRuntimeID);   // do not hand the collisions back in mid-air
			if (RelicRestoreCollisions(relicCarryRuntimeID))
			{
				LOG_WARNING("[freecam] the character carry stopped running - collisions restored.");
				relicCarryApplied = false;
				relicCarryRuntimeID = 0;
			}
			// else: the body is not reachable right now - keep the flag and try again in another ~2 s.
		}

		// The same job for a body the release could not reach. It lives here, in the other handler, on
		// purpose: this is the code of last resort and it must not share a fault budget with the carry.
		if (relicStrandedRuntimeID != 0)
		{
			if (RelicRestoreCollisions(relicStrandedRuntimeID))
			{
				relicStrandedRuntimeID = 0;
				relicStrandedRetries = 0;
			}
			else if (++relicStrandedRetries > 1800)   // ~30 s: the entity is gone for good
			{
				LOG_WARNING("[freecam] could not restore collisions on entity %u - giving up.",
					relicStrandedRuntimeID);
				relicStrandedRuntimeID = 0;
				relicStrandedRetries = 0;
			}
		}

		if (f_Enabled->enabled())
		{
			// A handoff is only ever consumed later in this same call; one left over from a call that faulted
			// before its consumer ran is stale by construction (it may even be a pose from another scene).
			relicHandoffValid = false;
			const bool wantReal = RelicUseRealCam();
			if (isEnabled && relicRealCamActive != wantReal)
			{
				// Relic: the "Drive the Game Camera" option flipped while the free camera is on. Hand the pose
				// to the other path so the view stays put, then stand the current path down; the wanted path
				// seeds from the handoff. Both poses are absolute already.
				relicHandoffPos = relicRealCamActive ? relicCamPosition : smoothPosition;
				relicHandoffFov = relicRealCamActive ? relicCamFov : smoothFOV;
				relicHandoffValid = !IsVectorZero(relicHandoffPos);
				RelicStopCamera();
			}
			relicRealCamActive = wantReal;
			if (wantReal)
				RelicDriveRealCam();   // the game camera itself - no clone, nothing deactivated (see above)
			else
				RelicRunCloneCam();    // upstream's design: a clone of it, the real one switched off
			if (damageOverlay == nullptr)
				damageOverlay = app::GameObject_Find(string_to_il2cppi("/Canvas/Pages/InLevelMainPage/GrpMainPage/ParticleDamageTextContainer"), nullptr);
			else
				app::GameObject_SetActive(damageOverlay, !f_DamageOverlay, nullptr);

			if (f_HpOverlay)  //Fixed an issue where HpOverlay could not be removed properly.
			{
				Sleep(200);
				f_HpOverlay = false;
				hpOverlay = app::GameObject_Find(string_to_il2cppi("AvatarBoardCanvasV2(Clone)"), nullptr);
				while (hpOverlay != nullptr)
				{
					app::GameObject_SetActive(hpOverlay, false, nullptr);
					hpOverlay = app::GameObject_Find(string_to_il2cppi("AvatarBoardCanvasV2(Clone)"), nullptr);
				}
			}

			if (f_BlockInput) {
				if (!relicInputBlocked) {
					app::MoleMole_UIManager_EnableInput(uiManager, false, false, false, nullptr);
					relicInputBlocked = true;
				}
			} else {
				if (relicInputBlocked) {
					app::MoleMole_UIManager_EnableInput(uiManager, true, false, false, nullptr);
					relicInputBlocked = false;
				}
			}
		}
		else
		{
			RelicStopCamera();
			relicHandoffValid = false;
			relicPathPoseValid = false;   // the free camera is off: a pushed pose must not seat it when it comes back
			damageOverlay = nullptr;
			hpOverlay = nullptr;

			if (relicInputBlocked) {
				app::MoleMole_UIManager_EnableInput(uiManager, true, false, false, nullptr);
				relicInputBlocked = false;
			}
		}

		// Taiga#5555: There's probably be a better way of implementing this. But for now, this is just what I came up with.
		auto& manager = game::EntityManager::instance();
		auto animator = manager.avatar()->animator();
		auto rigidBody = manager.avatar()->rigidbody();
		// Relic: upstream wrote `&&`, but BOTH pointers are dereferenced below - one of them being null
		// (mid-load, entity rebuild) would fault every frame until the handler's fault budget silences it.
		if (animator == nullptr || rigidBody == nullptr)
			return;

		static bool changed = false;
		static bool isVisible = false;

		if (f_FreezeAnimation->enabled())
		{
			//auto constraints = app::Rigidbody_get_constraints(rigidBody, nullptr);
			//LOG_DEBUG("%s", magic_enum::enum_name(constraints).data());
			app::Rigidbody_set_constraints(rigidBody, app::RigidbodyConstraints__Enum::FreezePosition, nullptr);
			app::Animator_set_speed(animator, 0.f, nullptr);
			changed = false;
		}
		else
		{
			app::Rigidbody_set_constraints(rigidBody, app::RigidbodyConstraints__Enum::FreezeRotation, nullptr);
			if (!changed)
			{
				app::Animator_set_speed(animator, 1.f, nullptr);
				changed = true;
			}
		}
		
		if (f_SetAvatarInvisible->enabled())
		{
			SetLocalAvatarVisible(false);
			isVisible = false;
		}
		else
		{
			if (!isVisible)
			{
				SetLocalAvatarVisible(true);
				isVisible = true;
			}
		}		
	}
}
