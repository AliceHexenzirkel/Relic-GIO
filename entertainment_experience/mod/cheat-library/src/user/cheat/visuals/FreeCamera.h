#pragma once
#include <il2cpp-appdata.h>

#include <cheat-base/cheat/Feature.h>
#include <cheat-base/config/config.h>

namespace cheat::feature
{
	// Relic: one camera pose as the free camera keeps it - the position ABSOLUTE (world-shift corrected), the
	// rotation the free camera's own Euler degrees (pitch about X, yaw about Y, roll about Z, any range - mouse
	// input never wraps them), the vertical field of view. The camera path (CameraPath.cpp) reads and writes these.
	struct FreeCameraPose
	{
		app::Vector3 position{};
		float pitch = 0.f;
		float yaw = 0.f;
		float roll = 0.f;
		float fov = 45.f;
	};

	class FreeCamera : public Feature
	{
	public:
		config::Field<TranslatedHotkey> f_Enabled;
		config::Field<TranslatedHotkey> f_FreezeAnimation;
		config::Field<TranslatedHotkey> f_SetAvatarInvisible;
		config::Field<bool> f_BlockInput;
		config::Field<bool> f_DamageOverlay;
		config::Field<bool> f_HpOverlay;
		config::Field<float> f_Speed;
		config::Field<float> f_LookSens;
		config::Field<float> f_RollSpeed;
		config::Field<float> f_FOVSpeed;
		config::Field<float> f_FOV;
		config::Field<float> f_MovSmoothing;
		config::Field<float> f_LookSmoothing;
		config::Field<float> f_RollSmoothing;
		config::Field<float> f_FovSmoothing;
		config::Field<bool> f_MoveAvatar;
		config::Field<bool> f_DriveGameCamera;   // 2.8 only in the UI; 1.6 always drives the game camera
		config::Field<Hotkey> f_Forward;
		config::Field<Hotkey> f_Backward;
		config::Field<Hotkey> f_Left;
		config::Field<Hotkey> f_Right;
		config::Field<Hotkey> f_Up;
		config::Field<Hotkey> f_Down;
		config::Field<Hotkey> f_IncFOV;
		config::Field<Hotkey> f_DecFOV;
		config::Field<Hotkey> f_LeftRoll;
		config::Field<Hotkey> f_RightRoll;
		config::Field<Hotkey> f_ResetRoll;

		static FreeCamera& GetInstance();

		const FeatureGUIInfo& GetGUIInfo() const override;
		void DrawMain() override;

		bool NeedStatusDraw() const override;
		void DrawStatus() override;

		void OnGameUpdate();
		void OnAvatarCarryUpdate();
		void OnCameraWatchdog();
		void OnMoveSync(uint32_t entityId, app::MotionInfo* syncInfo);

		// Relic: the camera-path interface. GAME THREAD ONLY - every one of these reads or writes state that the
		// camera update owns, and DrawMain runs on the render thread.
		//
		// The free camera stays the only writer of the camera transform: the path never touches a transform, it
		// hands the free camera a pose and the free camera's next update writes exactly that (no input, no
		// smoothing) and continues from it - so when a path ends, the targets equal the pose and the player's own
		// input carries on from there without a snap. Both camera paths (the clone and the driven game camera)
		// honour it, on 1.6 and 2.8 alike.
		bool IsRunning() const;                       // a camera is owned and a pose has been written this session
		bool GetPose(FreeCameraPose& out) const;      // the pose the last camera update wrote; false unless running
		void PushPose(const FreeCameraPose& pose);    // the NEXT camera update writes this pose; consumed once
		void DropPose();                              // revoke a pushed pose nobody consumed yet (a take that ends before its camera is back)
		app::Camera* CurrentCamera() const;           // the camera being looked through (for projections); null when none
		bool IsCarryHeld() const;                     // "Move Character with Camera" is standing down for a scene transmit

	private:
		FreeCamera();
	};
}

