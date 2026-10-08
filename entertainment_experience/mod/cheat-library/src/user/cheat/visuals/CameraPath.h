#pragma once
#include <il2cpp-appdata.h>

#include <cheat-base/cheat/Feature.h>
#include <cheat-base/config/config.h>

#include <atomic>
#include <mutex>
#include <vector>

#include "FreeCamera.h"

namespace cheat::feature
{
	// Relic: "Free Camera Path" - keyframed camera moves for recording, on top of the free camera.
	//
	// Fly the free camera to a spot, press the Add key: that pose is a keyframe. Fly on, add the next. Press Play
	// and the camera is carried through the keyframes - straight or on a smooth curve, eased in and out of every
	// stop, pausing at a keyframe for as long as it is told to, at the field of view each keyframe recorded. Two
	// keyframes at the same spot with different angles are a pan; a keyframe with a hold is a pause; a stretch
	// without holds and with "stop at every keyframe" off is one continuous fly-through.
	//
	// The feature never writes a camera transform itself: it computes a pose per frame and hands it to the free
	// camera (FreeCamera::PushPose), which stays the only writer on both of its camera paths. See the .cpp.
	class CameraPath : public Feature
	{
	public:
		// One recorded camera pose plus how the path leaves it.
		struct Keyframe
		{
			FreeCameraPose pose;      // absolute position, the free camera's Euler degrees (canonical, see the .cpp), vertical FOV
			float hold = 0.f;         // seconds the camera waits AT this keyframe before moving on
			float travel = 4.f;       // seconds the move to the NEXT keyframe takes (in Loop: the last one's is the return)
			int   ease = 3;           // the easing of that move: 0 linear, 1 ease in, 2 ease out, 3 ease in & out
		};

		config::Field<Hotkey> f_AddKey;
		config::Field<Hotkey> f_PlayKey;
		config::Field<Hotkey> f_GoStartKey;
		config::Field<Hotkey> f_RemoveLastKey;

		// How the shot plays. Saved inside a named path file as well, so a loaded shot replays as it was made.
		config::Field<bool>  f_StopAtKeyframes;   // on: every move eases into a stop at its keyframe; off: continuous between holds
		config::Field<bool>  f_Curved;            // on: a smooth curve through the keyframes; off: straight lines
		config::Field<int>   f_Repeat;            // 0 once, 1 loop, 2 back and forth
		config::Field<float> f_Speed;             // playback speed multiplier
		config::Field<float> f_LeadIn;            // seconds to fly from wherever the camera is to the first keyframe (0 = start there)
		config::Field<bool>  f_AimAlongPath;      // look where the camera is going instead of at the recorded angles

		// Preferences.
		config::Field<float> f_Countdown;         // real seconds before the move starts (time to hit record)
		config::Field<bool>  f_GameTime;          // the clock follows the game's time scale (slow motion slows the camera too)
		config::Field<bool>  f_HideUI;            // switch the game UI off while the path plays, back on after
		config::Field<bool>  f_QuietOverlay;      // hide the mod's own overlays (status, info, FPS, notifications) while the path plays
		config::Field<bool>  f_ShowPath;          // draw the keyframes and the path on screen while not playing
		config::Field<float> f_DefaultTravel;     // for new keyframes
		config::Field<float> f_DefaultHold;
		config::Field<int>   f_DefaultEase;
		config::Field<int>   f_OrbitKeys;         // the orbit generator: keyframes on the circle
		config::Field<float> f_OrbitDistance;     // ...around the point this far ahead of the camera

		static CameraPath& GetInstance();

		const FeatureGUIInfo& GetGUIInfo() const override;
		void DrawMain() override;
		void DrawExternal() override;

		bool NeedStatusDraw() const override;
		void DrawStatus() override;

		void OnGameUpdate();
		void OnPathWatchdog();

	private:
		CameraPath();

		// ---- requests: made on the render thread (DrawMain) or the window thread (hotkeys), served on the game thread
		enum class Request { None, Capture, Update, Goto, InsertAfter, Play, Stop, RemoveLast, GoStart, ClearAll, Load, Save, Delete, DistributeTime, Orbit };
		struct Pending
		{
			Request kind = Request::None;
			int index = -1;
			std::string name;
		};
		void Post(Request kind, int index = -1, std::string name = {});
		void OnAddKey();
		void OnPlayKey();
		void OnGoStartKey();
		void OnRemoveLastKey();
		static bool HotkeysArmed();

		// ---- playback (game thread)
		enum class Phase { Idle = 0, Countdown, LeadIn, Hold, Travel };
		struct Playback
		{
			Phase phase = Phase::Idle;
			int   at = 0;               // the keyframe the camera is at / leaving
			int   dir = 1;              // +1 forward, -1 back (Back and forth)
			float elapsed = 0.f;        // seconds into the current phase
			bool  waiting = false;      // the clock is paused (camera not seated yet: a loading screen, a fresh enable)
			int64_t lastTickMs = 0;     // steady clock
			int   runFrom = 0;          // the run being travelled: first keyframe, segments, seconds, last keyframe
			int   runSegs = 0;
			float runTotal = 0.f;
			int   runEnd = 0;
			FreeCameraPose leadFrom{};  // the lead-in: from this pose to leadTo (keyframe leadTarget) in leadLen seconds
			FreeCameraPose leadTo{};
			int   leadTarget = 0;
			float leadLen = 0.f;        // 0 = the start pose has not been taken yet (the camera was off)
			bool  gotoOnly = false;     // the lead-in IS the whole playback (a flying "Go to")
			float holdPitch = 0.f;      // "Aim along the path": the aim the camera arrived with, blended during the hold
			float holdYaw = 0.f;
			bool  holdAim = false;
			FreeCameraPose last{};      // the pose pushed last frame (what Stop leaves the camera at)
			bool  hasLast = false;
			bool  hideUiWasOn = false;  // the Hide UI state to restore
			bool  hideUiForced = false;
			bool  quiet = false;        // the mod's overlays are hidden for this take
			bool  started = false;      // motion has begun (the menu was closed, the overlays quietened)
			uint32_t scene = 0;         // the scene the take started in (0 = unknown yet)
			float countdownLeft = 0.f;  // for the on-screen digits
		};
		void Serve(const Pending& req);
		void StartPlayback();
		void StartGoto(int index);
		void StopPlayback(const char* why, bool dropPush = true);
		void Tick(float dt, float rawDt);                                    // dt = path seconds (speed, time scale), rawDt = real seconds
		bool Evaluate(int from, int to, float w, FreeCameraPose& out) const;   // pose on the move from -> to at eased w in [0,1]
		app::Vector3 CurvePosition(int lo, int hi, float s) const;             // the raw curve between lo -> hi (forward order)
		bool  DepartAim(int from, int dir, float& pitch, float& yaw) const;   // "Aim along the path": where the move leaving `from` looks at its start
		float SegmentTravel(int from, int to) const;
		int   SegmentEase(int from, int to) const;
		bool  NextKeyframe(int from, int dir, int& next) const;              // false at the end of an open path
		void  BuildRun(int from, int dir, int& runSegs, float& runTotal, int& runEnd) const;
		void  RebuildArcTable();                                             // even speed along a curved segment
		void  Touched();                                                     // the keyframes changed (under m_Lock)

		// ---- keyframes: shared between the threads, guarded by m_Lock; the game thread touches them only inside
		//      short locked scopes that call nothing of il2cpp, the render thread draws from a copy (see the .cpp)
		std::mutex m_Lock;
		std::vector<Keyframe> m_Keys;
		uint32_t m_KeysVersion = 0;           // bumped on every change; the panel's write-back checks it
		uint32_t m_SceneId = 0;               // the scene the keyframes were recorded in (0 = unknown)
		uint32_t m_CurrentScene = 0;          // sampled on the game thread (once a second; every tick during a take)
		float m_Rate = 1.f;                   // path seconds per real second (Speed x the game's time scale when followed); game thread
		std::string m_LoadedName;             // the path file the keyframes came from / were saved to
		std::vector<Pending> m_Requests;
		Playback m_Play;
		std::atomic<int> m_PhaseView{ 0 };    // m_Play.phase, readable without the lock
		bool m_Dirty = false;                 // the autosave is behind the keyframes
		int64_t m_DirtyMs = 0;                // when they last changed (the autosave waits a second of quiet)
		bool m_AutosaveLoaded = false;
		int  m_Silence = 0;                   // ticks since OnGameUpdate last ran - OnPathWatchdog's heartbeat
		int  m_Selected = -1;                 // the keyframe opened in the panel's editor
		std::string m_Status;                 // one short line for the panel and the status pane

		// the arc-length table of every forward segment (game thread; rebuilt when the keyframes or the shape change)
		static const int kArcSamples = 32;
		struct ArcTable { float cum[kArcSamples + 1]; };
		std::vector<ArcTable> m_Arc;
		uint32_t m_ArcVersion = ~0u;
		int  m_ArcRepeat = -1;
		bool m_ArcCurved = false;

		// ---- on-screen preview: projected on the game thread, drawn on the render thread
		struct OverlayWorld { app::Vector3 position; int key; };       // key = keyframe index, or -1 - segment for a curve sample
		struct OverlayPoint { float x, y; bool visible; int key; };
		std::vector<OverlayWorld> m_OverlayWorld;   // game thread only
		std::vector<OverlayPoint> m_OverlayScratch; // game thread only, pre-sized before the guarded projection
		std::vector<OverlayPoint> m_Overlay;        // published under m_Lock
		bool m_OverlayValid = false;
		int m_OverlayFaults = 0;

		// ---- files
		std::filesystem::path m_Dir;
		std::vector<std::string> m_Files;     // the .json names in m_Dir, UTF-8 (under m_Lock)
		bool m_FilesStale = true;             // render thread only
		void RefreshFiles();
		bool SaveTo(const std::string& name, bool autosave);
		bool LoadFrom(const std::string& name, bool autosave);
		void Autosave();
	};
}
