#include "pch-il2cpp.h"
#include "CameraPath.h"

#include <cmath>
#include <cfloat>
#include <cctype>
#include <chrono>
#include <fstream>
#include <algorithm>
#include <helpers.h>
#include <cheat-base/render/renderer.h>
#include <cheat-base/relic-guard.h>
#include <cheat/events.h>
#include <cheat/game/util.h>
#include <cheat/visuals/HideUI.h>
#include <misc/cpp/imgui_stdlib.h>

namespace cheat::feature
{
	// Relic: "Free Camera Path" - keyframed camera moves for people who record the game.
	//
	// HOW IT DRIVES THE CAMERA. The free camera stays the only writer of the camera transform, on both of its
	// paths (the clone of the game camera and the driven game camera). Every frame of a playback this feature
	// computes ONE pose - absolute position, the free camera's own Euler degrees, FOV - and hands it over with
	// FreeCamera::PushPose; the free camera's next update writes exactly that pose, skips its input block and its
	// smoothing for that frame, and leaves its targets equal to it. So a path never fights the player's input, a
	// path ending leaves the camera exactly where the last keyframe put it (the player's own flying continues from
	// there without a snap), the "Move Character with Camera" carry follows a path like any other camera move
	// (it is pose-driven), and a world-shift re-base cannot land between two half-written values because the pose
	// is absolute end to end. A push is consumed once: if this feature ever goes quiet (the fault guard silences a
	// handler after 20 consecutive faults) the camera is simply back under the player's hand next frame - nothing
	// stays frozen or driven. The feature is registered BEFORE FreeCamera in cheat.cpp so its GameUpdate handler
	// runs first and the push lands in the same tick; the design does not depend on that (only the latency does).
	// Nothing is pushed while the camera is not seated (a loading screen), a take ends when the scene changes,
	// and every stop revokes a push nobody consumed: a pose belongs to the scene it was recorded in and must never
	// seat the next scene's camera.
	//
	// THREADS. Three of them touch this object: the game thread (OnGameUpdate: playback, captures, the projection
	// of the on-screen preview - the only il2cpp caller), the render thread (DrawMain / DrawExternal / DrawStatus:
	// the panel and the drawing, never il2cpp) and the window thread (the hotkeys' PressedEvent). Everything
	// shared lives behind m_Lock, and NO locked scope calls il2cpp or ImGui: every handler runs under SEH (TEvent's
	// fault guard), a structured exception is not guaranteed to unwind the C++ frames in between, and a
	// std::lock_guard alive at the fault would never release - the render thread (which is the Present hook) would
	// then block forever on its next DrawMain and the game would hang. So the game thread samples il2cpp into
	// locals first and locks only around C++ work; DrawMain copies the state under a short lock, draws from the
	// copy and writes its edits back under another short lock (dropped if the keyframes changed meanwhile - a
	// hotkey capture during that one render frame); the projection writes into a pre-sized plain buffer under
	// relic::Try with no object constructed inside the guarded body. Toasts (the hotkey feedback with the menu
	// closed) are emitted outside the lock, as the manager's own toggle toasts are.
	//
	// TIMING. A path is a list of keyframes; each keyframe holds the camera for `hold` seconds and then travels
	// to the next in `travel` seconds with its own easing. Consecutive keyframes without a hold form a RUN when
	// "Stop at every keyframe" is off: the easing is then applied to the run as a whole and the camera flies
	// through the inner keyframes without slowing (one continuous fly-through); with it on, a run is one segment
	// and every keyframe is a stop. A pan in place (two keyframes at the same spot) is always a run of its own, so
	// the move before it eases to a stop and the move after it eases away. The clock is the steady clock (each
	// frame's delta capped at 100 ms so a hitch cannot skip a segment), times the playback speed, optionally times
	// the game's time scale so slow motion slows the camera with the world; the countdown before the shot runs on
	// real seconds whatever the speed - it is the operator's margin to hit record.
	//
	// SHAPE. Straight lines, or a centripetal Catmull-Rom curve through the keyframes (the parameterisation that
	// cannot loop or cusp, whatever the spacing), re-timed by an arc-length table so the speed along a curved
	// segment is even. Angles take the short way round per axis between the two spellings of a view that lie
	// nearest each other (a full spin needs a keyframe half way), and on the curve they follow a monotone cubic
	// through the neighbours in time so a turn does not overshoot its keyframe. "Aim along the path" derives
	// pitch and yaw from the direction of travel instead, turning towards the next move during a hold.

	static const int   kMaxKeyframes = 200;
	static const float kMinTravel = 0.05f;      // a travel below this is treated as instant
	static const float kMaxDt = 0.1f;           // one frame never advances the clock more than this...
	static const float kCarryMaxDt = 1.f / 30.f; // ...and with the character carried no more than a 30 fps frame: the carry's
	                                             // backstop is per FRAME (max(f_Speed*10, 50) units), and a 100 ms hitch at the
	                                             // peak of a 600 units/s flight would be a 90-unit step
	static const int   kCurveSamples = 12;      // preview samples per segment
	static const float kSameSpot = 0.05f;       // two keyframes closer than this are "the same spot" (a pan)
	static const float kCarryLeadIn = 1.f;      // with the character carried, a jump cut would leave it behind: fly at least this
	static const float kCarrySafeSpeed = 600.f; // ...and no faster than this on average (the carry's backstop trips at ~50 units a frame)
	static const float kCarryWait = 5.f;        // ...and wait this long for a scene transmit of the carry to end
	static const char* kAutosaveName = "_autosave";
	static const int   kOverlayFaultLimit = 5;
	static const int   kMaxPhaseStepsPerTick = 16;
	static const float kDegPerRad = 180.f / 3.14159265f;
	static const float kCountdownFontSize = 96.f;

	enum EaseKind { EaseLinear = 0, EaseIn = 1, EaseOut = 2, EaseInOut = 3, EaseCount = 4 };
	enum RepeatKind { RepeatOnce = 0, RepeatLoop = 1, RepeatPingPong = 2, RepeatCount = 3 };

	static const char* EaseName(int kind)
	{
		switch (kind)
		{
		case EaseIn:    return _TR("Ease in");
		case EaseOut:   return _TR("Ease out");
		case EaseInOut: return _TR("Ease in & out");
		default:        return _TR("Linear");
		}
	}

	static const char* RepeatName(int kind)
	{
		switch (kind)
		{
		case RepeatLoop:     return _TR("Loop");
		case RepeatPingPong: return _TR("Back and forth");
		default:             return _TR("Once");
		}
	}

	static int64_t NowMs()
	{
		using namespace std::chrono;
		return duration_cast<milliseconds>(steady_clock::now().time_since_epoch()).count();
	}

	static float Clamp01(float u)
	{
		return u < 0.f ? 0.f : (u > 1.f ? 1.f : u);
	}

	static float Ease(int kind, float u)
	{
		u = Clamp01(u);
		switch (kind)
		{
		case EaseIn:    return u * u;
		case EaseOut:   return 1.f - (1.f - u) * (1.f - u);
		case EaseInOut: return u * u * (3.f - 2.f * u);   // smoothstep
		default:        return u;
		}
	}

	// The short way round: the signed difference of two angles in [-180, 180).
	static float Wrap180(float d)
	{
		d = fmodf(d + 180.f, 360.f);
		if (d < 0.f)
			d += 360.f;
		return d - 180.f;
	}

	static float Lerp(float a, float b, float t)
	{
		return a + (b - a) * t;
	}

	static app::Vector3 Lerp(const app::Vector3& a, const app::Vector3& b, float t)
	{
		return a + (b - a) * t;
	}

	// The free camera's Euler angles are continuous (the mouse never wraps them) and its pitch can pass 90 - the
	// same view then has two spellings ((p, y, r) and (180 - p, y + 180, r + 180); Unity applies Z, then X, then
	// Y) and two straight-down poses could interpolate as a half spin. One spelling: |pitch| <= 90, every angle in
	// [-180, 180).
	static void Canonical(FreeCameraPose& p)
	{
		p.pitch = Wrap180(p.pitch);
		p.yaw = Wrap180(p.yaw);
		p.roll = Wrap180(p.roll);
		if (p.pitch > 90.f || p.pitch < -90.f)
		{
			p.pitch = Wrap180(180.f - p.pitch);
			p.yaw = Wrap180(p.yaw + 180.f);
			p.roll = Wrap180(p.roll + 180.f);
		}
	}

	// The other spelling of the same view.
	static FreeCameraPose AltSpelling(const FreeCameraPose& p)
	{
		FreeCameraPose q = p;
		q.pitch = Wrap180(180.f - p.pitch);
		q.yaw = Wrap180(p.yaw + 180.f);
		q.roll = Wrap180(p.roll + 180.f);
		return q;
	}

	static float AngularGap(const FreeCameraPose& a, const FreeCameraPose& b)
	{
		return fabsf(Wrap180(b.pitch - a.pitch)) + fabsf(Wrap180(b.yaw - a.yaw)) + fabsf(Wrap180(b.roll - a.roll));
	}

	// The spelling of `v` that lies nearest `ref`. Two keyframes captured on either side of straight down carry
	// different canonical spellings (yaw and roll both ~180 apart); interpolated per axis from the canonical ones
	// the signs could disagree and the image would spin a full turn for a nod. Between the nearest spellings the
	// nod is a nod.
	static FreeCameraPose Nearest(const FreeCameraPose& ref, const FreeCameraPose& v)
	{
		FreeCameraPose alt = AltSpelling(v);
		return AngularGap(ref, alt) < AngularGap(ref, v) ? alt : v;
	}

	static bool GetCanonicalPose(FreeCamera& fc, FreeCameraPose& out)
	{
		if (!fc.GetPose(out))
			return false;
		Canonical(out);
		return true;
	}

	// The direction the camera looks along for a pitch (down positive) and a yaw (right positive), Unity's frame.
	static app::Vector3 Forward(float pitch, float yaw)
	{
		const float p = pitch / kDegPerRad, y = yaw / kDegPerRad;
		return { sinf(y) * cosf(p), -sinf(p), cosf(y) * cosf(p) };
	}

	// ...and back: the pitch and yaw that look along a direction. False for a standstill.
	static bool AimFromDirection(app::Vector3 dir, float& pitch, float& yaw)
	{
		const float len = GetVectorMagnitude(dir);
		if (len < 1e-4f)
			return false;
		dir = dir / len;
		float y = dir.y;
		if (y > 1.f) y = 1.f;
		if (y < -1.f) y = -1.f;
		yaw = atan2f(dir.x, dir.z) * kDegPerRad;
		pitch = -asinf(y) * kDegPerRad;
		return true;
	}

	// With the character carried, how long a flight over `dist` must take so the carry can follow.
	static float CarryFlightLen(float dist)
	{
		float len = dist / kCarrySafeSpeed;
		return len < kCarryLeadIn ? kCarryLeadIn : len;
	}

	// Centripetal Catmull-Rom (alpha 0.5) between p1 and p2 at t in [0, 1], Barry-Goldman form. The knots are
	// the square roots of the chord lengths, which is the parameterisation that neither loops nor cusps however
	// unevenly the points are spaced; the chords are floored so no knot interval can be zero.
	static app::Vector3 CatmullRom(const app::Vector3& p0, const app::Vector3& p1, const app::Vector3& p2, const app::Vector3& p3, float t)
	{
		auto knot = [](float prev, const app::Vector3& a, const app::Vector3& b)
		{
			float d = GetVectorMagnitude(b - a);
			if (d < 1e-4f)
				d = 1e-4f;
			return prev + sqrtf(d);
		};
		const float t0 = 0.f;
		const float t1 = knot(t0, p0, p1);
		const float t2 = knot(t1, p1, p2);
		const float t3 = knot(t2, p2, p3);
		const float tt = t1 + (t2 - t1) * Clamp01(t);

		auto A1 = p0 * ((t1 - tt) / (t1 - t0)) + p1 * ((tt - t0) / (t1 - t0));
		auto A2 = p1 * ((t2 - tt) / (t2 - t1)) + p2 * ((tt - t1) / (t2 - t1));
		auto A3 = p2 * ((t3 - tt) / (t3 - t2)) + p3 * ((tt - t2) / (t3 - t2));
		auto B1 = A1 * ((t2 - tt) / (t2 - t0)) + A2 * ((tt - t0) / (t2 - t0));
		auto B2 = A2 * ((t3 - tt) / (t3 - t1)) + A3 * ((tt - t1) / (t3 - t1));
		return B1 * ((t2 - tt) / (t2 - t1)) + B2 * ((tt - t1) / (t2 - t1));
	}

	// Fritsch-Butland tangent for a monotone cubic: the secants before and after a knot (per second) and the
	// two interval lengths. Zero where the value turns around, so the curve never overshoots a keyframe.
	static float MonotoneTangent(float sPrev, float sNext, float hPrev, float hNext, bool hasPrev, bool hasNext)
	{
		if (!hasPrev && !hasNext)
			return 0.f;
		if (!hasPrev)
			return sNext;
		if (!hasNext)
			return sPrev;
		if (sPrev * sNext <= 0.f)
			return 0.f;
		const float w1 = 2.f * hNext + hPrev;
		const float w2 = hNext + 2.f * hPrev;
		return (w1 + w2) / (w1 / sPrev + w2 / sNext);
	}

	// Cubic Hermite on [v0, v1] over an interval of h seconds, tangents per second, at t in [0, 1].
	static float Hermite(float v0, float v1, float m0, float m1, float h, float t)
	{
		const float t2 = t * t;
		const float t3 = t2 * t;
		const float h00 = 2.f * t3 - 3.f * t2 + 1.f;
		const float h10 = t3 - 2.f * t2 + t;
		const float h01 = -2.f * t3 + 3.f * t2;
		const float h11 = t3 - t2;
		return h00 * v0 + h10 * h * m0 + h01 * v1 + h11 * h * m1;
	}

	// ImGui and the JSON are UTF-8, the file system is UTF-16, and MSVC's path(std::string) / path::string() go
	// through the ANSI code page: route every name through UTF-8 explicitly.
	static std::filesystem::path NamePath(const std::filesystem::path& dir, const std::string& utf8Name, const char* suffix)
	{
		std::u8string u8(reinterpret_cast<const char8_t*>(utf8Name.c_str()));
		u8 += reinterpret_cast<const char8_t*>(suffix);
		return dir / std::filesystem::path(u8);
	}

	static std::string StemUtf8(const std::filesystem::path& p)
	{
		auto u8 = p.stem().u8string();
		return std::string(u8.begin(), u8.end());
	}

	CameraPath::CameraPath() : Feature(),
		NF(f_AddKey, "Visuals::CameraPath", Hotkey('K')),
		NF(f_PlayKey, "Visuals::CameraPath", Hotkey('P')),
		NF(f_GoStartKey, "Visuals::CameraPath", Hotkey(VK_HOME)),
		NF(f_RemoveLastKey, "Visuals::CameraPath", Hotkey(VK_DELETE)),
		NF(f_StopAtKeyframes, "Visuals::CameraPath", true),
		NF(f_Curved, "Visuals::CameraPath", true),
		NF(f_Repeat, "Visuals::CameraPath", 0),
		NF(f_Speed, "Visuals::CameraPath", 1.0f),
		NF(f_LeadIn, "Visuals::CameraPath", 2.0f),
		NF(f_AimAlongPath, "Visuals::CameraPath", false),
		NF(f_Countdown, "Visuals::CameraPath", 3.0f),
		NF(f_GameTime, "Visuals::CameraPath", false),
		NF(f_HideUI, "Visuals::CameraPath", false),
		NF(f_QuietOverlay, "Visuals::CameraPath", true),
		NF(f_ShowPath, "Visuals::CameraPath", true),
		NF(f_DefaultTravel, "Visuals::CameraPath", 4.0f),
		NF(f_DefaultHold, "Visuals::CameraPath", 0.0f),
		NF(f_DefaultEase, "Visuals::CameraPath", (int)EaseInOut),
		NF(f_OrbitKeys, "Visuals::CameraPath", 8),
		NF(f_OrbitDistance, "Visuals::CameraPath", 15.0f),
		m_Dir(util::GetCurrentPath() / "campaths")
	{
		// Never FreeCamera::GetInstance() here: constructing it first would put its handlers ahead of ours.
		f_AddKey.value().PressedEvent += MY_METHOD_HANDLER(CameraPath::OnAddKey);
		f_PlayKey.value().PressedEvent += MY_METHOD_HANDLER(CameraPath::OnPlayKey);
		f_GoStartKey.value().PressedEvent += MY_METHOD_HANDLER(CameraPath::OnGoStartKey);
		f_RemoveLastKey.value().PressedEvent += MY_METHOD_HANDLER(CameraPath::OnRemoveLastKey);
		events::GameUpdateEvent += MY_METHOD_HANDLER(CameraPath::OnGameUpdate);
		// Its own subscription, its own fault budget: it undoes the take's side effects (hidden UI, quiet
		// overlays) if OnGameUpdate is ever silenced mid-take - the same shape as FreeCamera's watchdog.
		events::GameUpdateEvent += MY_METHOD_HANDLER(CameraPath::OnPathWatchdog);
	}

	const FeatureGUIInfo& CameraPath::GetGUIInfo() const
	{
		// "Free Camera Path", not "Camera Path": the groups of a tab are a std::map, sorted by name, and this
		// name puts the panel right under Free Camera.
		TRANSLATED_GROUP_INFO("Free Camera Path", "Visuals");
		return info;
	}

	CameraPath& CameraPath::GetInstance()
	{
		static CameraPath instance;
		return instance;
	}

	// ------------------------------------------------------------------------------------------------
	// Requests (render / window thread -> game thread)
	// ------------------------------------------------------------------------------------------------

	static void Toast(const std::string& text);   // the hotkeys' feedback; defined with the game-thread code below

	void CameraPath::Post(Request kind, int index, std::string name)
	{
		if (kind == Request::Stop)
		{
			// Served right here, on whatever thread asked: StopPlayback is pure C++, and a Stop must work even
			// if the game-thread handler has been silenced by the fault guard mid-take.
			bool stopped = false;
			{
				std::lock_guard<std::mutex> lock(m_Lock);
				stopped = m_Play.phase != Phase::Idle;
				StopPlayback(_TR("stopped"));
			}
			if (stopped)
				Toast(_TR("stopped"));   // outside the lock, like every toast; only with the menu closed
			return;
		}
		std::lock_guard<std::mutex> lock(m_Lock);
		m_Requests.push_back(Pending{ kind, index, std::move(name) });
	}

	// The hotkeys fire on the window thread whatever has the keyboard - the F1 menu included - so they follow the
	// manager's own rule for feature toggles: inert while the menu is shown or owns the input, and while hotkeys
	// are switched off in Settings. The panel's buttons do the same things while the menu is open.
	bool CameraPath::HotkeysArmed()
	{
		if (CheatManagerBase::IsMenuShowed() || renderer::IsInputLocked())
			return false;
		return Settings::GetInstance().f_HotkeysEnabled;
	}

	void CameraPath::OnAddKey()
	{
		if (HotkeysArmed() && FreeCamera::GetInstance().f_Enabled->enabled())
			Post(Request::Capture);
	}

	void CameraPath::OnPlayKey()
	{
		if (!HotkeysArmed())
			return;
		// Play toggles: pressed during a playback it stops it.
		Post(m_PhaseView.load() == (int)Phase::Idle ? Request::Play : Request::Stop);
	}

	void CameraPath::OnGoStartKey()
	{
		if (HotkeysArmed() && FreeCamera::GetInstance().f_Enabled->enabled())
			Post(Request::GoStart);
	}

	void CameraPath::OnRemoveLastKey()
	{
		if (HotkeysArmed() && FreeCamera::GetInstance().f_Enabled->enabled())
			Post(Request::RemoveLast);
	}

	// ------------------------------------------------------------------------------------------------
	// Files: <dll dir>/campaths/<name>.json, like the teleports next door
	// ------------------------------------------------------------------------------------------------

	static bool ValidName(const std::string& name)
	{
		if (name.empty() || name.size() > 64)
			return false;
		if (name.find_first_of("\\/:*?\"<>|") != std::string::npos)
			return false;
		if (name[0] == '.' || name[0] == '_')   // '_' is the working files' prefix, hidden from the list
			return false;
		return true;
	}

	void CameraPath::RefreshFiles()
	{
		std::vector<std::string> names;
		try
		{
			if (std::filesystem::exists(m_Dir))
			{
				for (auto& entry : std::filesystem::directory_iterator(m_Dir))
				{
					if (!entry.is_regular_file())
						continue;
					auto ext = entry.path().extension().string();
					std::transform(ext.begin(), ext.end(), ext.begin(), [](unsigned char c) { return (char)std::tolower(c); });
					if (ext != ".json")
						continue;
					auto stem = StemUtf8(entry.path());
					if (stem.empty() || stem[0] == '_')
						continue;
					names.push_back(stem);
				}
			}
		}
		catch (const std::exception& e)
		{
			LOG_WARNING("[campath] could not list %s: %s", m_Dir.string().c_str(), e.what());
		}
		std::sort(names.begin(), names.end());
		std::lock_guard<std::mutex> lock(m_Lock);
		m_Files = std::move(names);
	}

	// Serialises under the lock, writes outside it. `autosave` names the working copy that follows every edit;
	// a named save also carries the shot's playback settings, so it replays as it was made.
	bool CameraPath::SaveTo(const std::string& name, bool autosave)
	{
		nlohmann::json j;
		{
			std::lock_guard<std::mutex> lock(m_Lock);
			j["format"] = 1;
			j["name"] = autosave ? m_LoadedName : name;
			j["scene"] = m_SceneId;
			j["game"] = RELIC_GAME_VERSION;
			auto& arr = j["keyframes"] = nlohmann::json::array();
			for (auto& k : m_Keys)
			{
				arr.push_back({
					{ "pos", { k.pose.position.x, k.pose.position.y, k.pose.position.z } },
					{ "rot", { k.pose.pitch, k.pose.yaw, k.pose.roll } },
					{ "fov", k.pose.fov },
					{ "hold", k.hold },
					{ "travel", k.travel },
					{ "ease", k.ease } });
			}
		}
		if (!autosave)
		{
			j["settings"] = {
				{ "stopAtKeyframes", (bool)f_StopAtKeyframes },
				{ "curved", (bool)f_Curved },
				{ "repeat", (int)f_Repeat },
				{ "speed", (float)f_Speed },
				{ "leadIn", (float)f_LeadIn },
				{ "aimAlongPath", (bool)f_AimAlongPath } };
		}
		try
		{
			std::filesystem::create_directories(m_Dir);
			auto path = NamePath(m_Dir, name, ".json");
			auto tmp = NamePath(m_Dir, name, ".json.tmp");
			{
				std::ofstream ofs(tmp, std::ios::binary | std::ios::trunc);
				if (!ofs)
					throw std::runtime_error("cannot open for writing");
				ofs << j.dump(1, '\t');
				ofs.close();   // flushes; a short write (disk full) lands in the stream state, and only a complete file may replace the last good one
				if (!ofs)
					throw std::runtime_error("write failed (disk full?)");
			}
			std::filesystem::rename(tmp, path);   // atomic on the same volume: a crash mid-write cannot leave a half file
		}
		catch (const std::exception& e)
		{
			LOG_ERROR("[campath] could not save '%s': %s", name.c_str(), e.what());
			return false;
		}
		if (!autosave)
		{
			std::lock_guard<std::mutex> lock(m_Lock);
			m_LoadedName = name;
			m_Dirty = true;          // the autosave carries the name; bring it up to date (not Touched(): nothing moved)
			m_DirtyMs = NowMs();
			LOG_INFO("[campath] saved '%s' (%zu keyframes)", name.c_str(), m_Keys.size());
		}
		return true;
	}

	bool CameraPath::LoadFrom(const std::string& name, bool autosave)
	{
		auto path = NamePath(m_Dir, name, ".json");
		nlohmann::json j;
		try
		{
			if (!std::filesystem::exists(path))
				return false;
			std::ifstream ifs(path, std::ios::binary);
			if (!ifs)
				throw std::runtime_error("cannot open");
			ifs >> j;
		}
		catch (const std::exception& e)
		{
			LOG_ERROR("[campath] could not read '%s': %s", name.c_str(), e.what());
			if (autosave)
			{
				// Keep it for a look, and out of the way: an empty path must not overwrite what may be a recoverable file.
				std::error_code ec;
				std::filesystem::rename(path, NamePath(m_Dir, name, ".corrupt.json"), ec);
			}
			return false;
		}

		std::vector<Keyframe> keys;
		uint32_t scene = 0;
		std::string loadedName;
		bool haveSettings = false;
		bool stopAt = true, curved = true, aim = false;
		int repeat = 0;
		float speed = 1.f, leadIn = 2.f;
		try
		{
			scene = j.value("scene", 0u);
			loadedName = j.value("name", std::string());
			auto& arr = j.at("keyframes");
			if (!arr.is_array())
				throw std::runtime_error("'keyframes' is not a list");
			if (arr.size() > (size_t)kMaxKeyframes)
				throw std::runtime_error(fmt::format("holds {} keyframes, more than the {} the panel can hold", arr.size(), kMaxKeyframes));
			for (auto& e : arr)
			{
				Keyframe k;
				auto& pos = e.at("pos");
				auto& rot = e.at("rot");
				k.pose.position = { pos.at(0).get<float>(), pos.at(1).get<float>(), pos.at(2).get<float>() };
				k.pose.pitch = rot.at(0).get<float>();
				k.pose.yaw = rot.at(1).get<float>();
				k.pose.roll = rot.at(2).get<float>();
				k.pose.fov = e.value("fov", 45.f);
				k.hold = e.value("hold", 0.f);
				k.travel = e.value("travel", 4.f);
				k.ease = e.value("ease", (int)EaseInOut);
				// A file is data from outside: keep every number where the panel could have put it.
				if (!std::isfinite(k.pose.position.x) || !std::isfinite(k.pose.position.y) || !std::isfinite(k.pose.position.z)
					|| !std::isfinite(k.pose.pitch) || !std::isfinite(k.pose.yaw) || !std::isfinite(k.pose.roll)
					|| !std::isfinite(k.pose.fov) || !std::isfinite(k.hold) || !std::isfinite(k.travel))
					throw std::runtime_error("a keyframe holds a value that is not a number");
				if (k.pose.fov < 1.f || k.pose.fov > 179.f) k.pose.fov = 45.f;
				if (k.hold < 0.f || k.hold > 3600.f) k.hold = 0.f;
				if (k.travel < kMinTravel || k.travel > 3600.f) k.travel = 4.f;
				if (k.ease < 0 || k.ease >= EaseCount) k.ease = EaseInOut;
				Canonical(k.pose);
				keys.push_back(k);
			}
			if (j.contains("settings") && j["settings"].is_object())
			{
				auto& s = j["settings"];
				haveSettings = true;
				stopAt = s.value("stopAtKeyframes", true);
				curved = s.value("curved", true);
				repeat = s.value("repeat", 0);
				speed = s.value("speed", 1.f);
				leadIn = s.value("leadIn", 2.f);
				aim = s.value("aimAlongPath", false);
				if (repeat < 0 || repeat >= RepeatCount) repeat = 0;
				if (!(speed >= 0.1f && speed <= 10.f)) speed = 1.f;
				if (!(leadIn >= 0.f && leadIn <= 60.f)) leadIn = 2.f;
			}
		}
		catch (const std::exception& e)
		{
			LOG_ERROR("[campath] '%s' is not a camera path file: %s", name.c_str(), e.what());
			return false;
		}

		{
			std::lock_guard<std::mutex> lock(m_Lock);
			m_Keys = std::move(keys);
			m_SceneId = scene;
			m_LoadedName = autosave ? loadedName : name;
			m_Selected = -1;
			Touched();
			if (!autosave)
				LOG_INFO("[campath] loaded '%s' (%zu keyframes)", name.c_str(), m_Keys.size());
		}
		if (haveSettings && !autosave)
		{
			// The shot's own settings come with it (plain field writes; the config save is debounced anyway).
			f_StopAtKeyframes = stopAt;
			f_Curved = curved;
			f_Repeat = repeat;
			f_Speed = speed;
			f_LeadIn = leadIn;
			f_AimAlongPath = aim;
		}
		return true;
	}

	void CameraPath::Autosave()
	{
		SaveTo(kAutosaveName, true);
	}

	// The keyframes changed (caller holds m_Lock): version them, schedule the autosave, drop the caches.
	void CameraPath::Touched()
	{
		m_KeysVersion++;
		m_Dirty = true;
		m_DirtyMs = NowMs();
		m_OverlayValid = false;
	}

	// ------------------------------------------------------------------------------------------------
	// Path geometry (C++ only - callers hold m_Lock)
	// ------------------------------------------------------------------------------------------------

	// The keyframe after `from` in direction `dir`, per the repeat mode. False at the end of a Once path and
	// at either end of a Back-and-forth one (the caller flips the direction).
	bool CameraPath::NextKeyframe(int from, int dir, int& next) const
	{
		const int n = (int)m_Keys.size();
		if (n < 2)
			return false;
		if (f_Repeat == RepeatLoop)
		{
			next = ((from + dir) % n + n) % n;
			return true;
		}
		next = from + dir;
		return next >= 0 && next < n;
	}

	// A move's timing belongs to the keyframe it leaves in FORWARD order (the lower index; in a loop the return
	// belongs to the last keyframe), so playing a path backwards rewinds the same motion.
	static bool ForwardOrder(int from, int to, int n, bool loop)
	{
		if (to == from + 1)
			return true;
		if (loop && from == n - 1 && to == 0)
			return true;   // the loop's return
		return false;
	}

	float CameraPath::SegmentTravel(int from, int to) const
	{
		const int n = (int)m_Keys.size();
		const int owner = ForwardOrder(from, to, n, f_Repeat == RepeatLoop) ? from : to;
		float t = m_Keys[owner].travel;
		return t < kMinTravel ? kMinTravel : t;
	}

	int CameraPath::SegmentEase(int from, int to) const
	{
		const int n = (int)m_Keys.size();
		const int owner = ForwardOrder(from, to, n, f_Repeat == RepeatLoop) ? from : to;
		return m_Keys[owner].ease;
	}

	// The raw position on the forward segment lo -> hi at s in [0, 1]: a straight line, or the centripetal
	// Catmull-Rom through the nearest DISTINCT positions on each side (a pan in place repeats a position; a
	// repeated knot would collapse a Catmull-Rom interval); with no neighbour, extrapolated so the curve leaves
	// straight. Two keyframes at the same spot give that spot.
	app::Vector3 CameraPath::CurvePosition(int lo, int hi, float s) const
	{
		const int n = (int)m_Keys.size();
		const Keyframe& a = m_Keys[lo];
		const Keyframe& b = m_Keys[hi];
		const app::Vector3 chord = b.pose.position - a.pose.position;
		if (GetVectorMagnitude(chord) < kSameSpot)
			return a.pose.position;
		if (!f_Curved)
			return Lerp(a.pose.position, b.pose.position, s);

		const bool loop = f_Repeat == RepeatLoop;
		app::Vector3 p0 = a.pose.position - chord;
		app::Vector3 p3 = b.pose.position + chord;
		for (int i = lo - 1, steps = 0; steps < n - 2; --i, ++steps)
		{
			if (i < 0) { if (!loop) break; i += n; }
			if (i == hi) break;
			if (GetVectorMagnitude(m_Keys[i].pose.position - a.pose.position) >= kSameSpot)
			{
				p0 = m_Keys[i].pose.position;
				break;
			}
		}
		for (int i = hi + 1, steps = 0; steps < n - 2; ++i, ++steps)
		{
			if (i >= n) { if (!loop) break; i -= n; }
			if (i == lo) break;
			if (GetVectorMagnitude(m_Keys[i].pose.position - b.pose.position) >= kSameSpot)
			{
				p3 = m_Keys[i].pose.position;
				break;
			}
		}
		return CatmullRom(p0, a.pose.position, b.pose.position, p3, s);
	}

	// The curve's parameter is not distance: without this the camera would speed up and slow down along a
	// bent segment. One cumulative-length table per forward segment, rebuilt when the keyframes or the shape
	// change (caller holds m_Lock; C++ only).
	void CameraPath::RebuildArcTable()
	{
		const int n = (int)m_Keys.size();
		const int segs = n < 2 ? 0 : (f_Repeat == RepeatLoop ? n : n - 1);
		m_Arc.assign(segs, ArcTable{});
		for (int lo = 0; lo < segs; ++lo)
		{
			const int hi = (lo + 1) % n;
			auto& t = m_Arc[lo];
			t.cum[0] = 0.f;
			app::Vector3 prev = CurvePosition(lo, hi, 0.f);
			for (int i = 1; i <= kArcSamples; ++i)
			{
				app::Vector3 p = CurvePosition(lo, hi, (float)i / kArcSamples);
				t.cum[i] = t.cum[i - 1] + GetVectorMagnitude(p - prev);
				prev = p;
			}
		}
		m_ArcVersion = m_KeysVersion;
		m_ArcRepeat = f_Repeat;
		m_ArcCurved = f_Curved;
	}

	// The pose on the move from keyframe `from` to keyframe `to`, `w` of the way (0 = at from, 1 = at to;
	// the caller has applied the easing already). Angles interpolate the short way round between the nearest
	// spellings, on the curve through a monotone cubic with the neighbouring keyframes' timing; the position
	// follows CurvePosition, re-timed by the arc table so the speed along a curved segment is even.
	bool CameraPath::Evaluate(int from, int to, float w, FreeCameraPose& out) const
	{
		const int n = (int)m_Keys.size();
		if (from < 0 || from >= n || to < 0 || to >= n)
			return false;
		w = Clamp01(w);

		const bool loop = f_Repeat == RepeatLoop;
		const bool forward = ForwardOrder(from, to, n, loop);
		const int lo = forward ? from : to;      // the segment in forward order: lo -> hi
		const int hi = forward ? to : from;
		float s = forward ? w : 1.f - w;         // position along lo -> hi
		const Keyframe& a = m_Keys[lo];
		const Keyframe& b = m_Keys[hi];

		// ---- position
		if (f_Curved && m_ArcCurved && lo < (int)m_Arc.size() && m_ArcVersion == m_KeysVersion)
		{
			// Even speed: find the curve parameter at which the length travelled is s of the whole.
			const auto& t = m_Arc[lo];
			const float total = t.cum[kArcSamples];
			if (total > 1e-3f)
			{
				const float target = s * total;
				int i = 0;
				while (i < kArcSamples - 1 && t.cum[i + 1] < target)
					++i;
				const float span = t.cum[i + 1] - t.cum[i];
				const float frac = span > 1e-6f ? (target - t.cum[i]) / span : 0.f;
				s = ((float)i + Clamp01(frac)) / kArcSamples;
			}
		}
		out.position = CurvePosition(lo, hi, s);

		// ---- angles and FOV: the forward neighbours, for the tangents (absent at the ends of an open path)
		int prev = -1, next = -1;
		if (loop && n > 2)
		{
			prev = (lo - 1 + n) % n;
			next = (hi + 1) % n;
		}
		else if (!loop)
		{
			if (lo - 1 >= 0) prev = lo - 1;
			if (hi + 1 < n) next = hi + 1;
		}
		// Every angle in the spelling nearest its neighbour along the segment (see Nearest) - except under "Aim
		// along the path", where pitch and yaw are replaced by the direction of travel (canonical range) and the
		// roll must then come from the canonical keyframes, the spelling the holds push (k.pose): paired with the
		// alt spelling's roll the image would flip by 180 on the first frame of a hold after a keyframe recorded
		// past the pole.
		const bool nearest = !f_AimAlongPath;
		const FreeCameraPose A = a.pose;
		const FreeCameraPose B = nearest ? Nearest(A, b.pose) : b.pose;
		const FreeCameraPose P = prev >= 0 ? (nearest ? Nearest(A, m_Keys[prev].pose) : m_Keys[prev].pose) : FreeCameraPose{};
		const FreeCameraPose N = next >= 0 ? (nearest ? Nearest(B, m_Keys[next].pose) : m_Keys[next].pose) : FreeCameraPose{};
		const float sr = forward ? w : 1.f - w;  // the angles follow the eased time, not the arc re-timing
		const float h = SegmentTravel(lo, hi);
		auto scalar = [&](float va, float vb, float vPrev, float vNext, bool angle) -> float
		{
			const float d = angle ? Wrap180(vb - va) : vb - va;
			if (!f_Curved)
				return va + d * sr;
			float sPrev = 0.f, sNext = 0.f, hPrev = 0.f, hNext = 0.f;
			const bool hasPrev = prev >= 0, hasNext = next >= 0;
			if (hasPrev)
			{
				hPrev = SegmentTravel(prev, lo);
				sPrev = (angle ? Wrap180(va - vPrev) : va - vPrev) / hPrev;
			}
			if (hasNext)
			{
				hNext = SegmentTravel(hi, next);
				sNext = (angle ? Wrap180(vNext - vb) : vNext - vb) / hNext;
			}
			const float sThis = d / h;
			const float m0 = MonotoneTangent(sPrev, sThis, hPrev, h, hasPrev, true);
			const float m1 = MonotoneTangent(sThis, sNext, h, hNext, true, hasNext);
			return Hermite(va, va + d, m0, m1, h, sr);
		};
		out.pitch = scalar(A.pitch, B.pitch, P.pitch, N.pitch, true);
		out.yaw = scalar(A.yaw, B.yaw, P.yaw, N.yaw, true);
		out.roll = scalar(A.roll, B.roll, P.roll, N.roll, true);
		out.fov = scalar(A.fov, B.fov, P.fov, N.fov, false);
		if (out.fov < 1.f) out.fov = 1.f;
		if (out.fov > 179.f) out.fov = 179.f;
		return true;
	}

	// "Aim along the path": the pitch and yaw the move leaving `from` in direction `dir` looks along at its
	// very start. False at the end of a path and for a move that stands still (a pan).
	bool CameraPath::DepartAim(int from, int dir, float& pitch, float& yaw) const
	{
		int to;
		if (!NextKeyframe(from, dir, to))
			return false;
		FreeCameraPose p0, p1;
		if (!Evaluate(from, to, 0.f, p0) || !Evaluate(from, to, 0.01f, p1))
			return false;
		return AimFromDirection(p1.position - p0.position, pitch, yaw);
	}

	// ------------------------------------------------------------------------------------------------
	// Playback (game thread unless noted; m_Lock held by the caller, no il2cpp inside)
	// ------------------------------------------------------------------------------------------------

	void CameraPath::StartPlayback()
	{
		auto& fc = FreeCamera::GetInstance();
		if (m_Keys.size() < 2)
		{
			if (m_Keys.size() == 1)
				StartGoto(0);
			m_Status = _TR("needs at least two keyframes");   // after the go-to, so this is what the user is told
			return;
		}
		if (!fc.f_Enabled->enabled())
		{
			// The free camera seats itself on the game camera at its next update; the countdown waits for it.
			fc.f_Enabled->set_enabled(true);
			fc.f_Enabled.FireChanged();
		}
		m_Play = Playback{};
		m_Play.phase = Phase::Countdown;
		m_Play.scene = m_CurrentScene;
		if (f_HideUI)
		{
			auto& hide = HideUI::GetInstance();
			m_Play.hideUiWasOn = hide.f_Enabled->enabled();
			if (!m_Play.hideUiWasOn)
			{
				// In memory only - no FireChanged: it would write the forced value to cfg.json within two seconds,
				// and a game closed or crashed mid-take would start its next session with the game UI hidden. The
				// restore in StopPlayback does fire it (the player's own value).
				hide.f_Enabled->set_enabled(true);
				m_Play.hideUiForced = true;
			}
		}
		m_PhaseView = (int)m_Play.phase;
		LOG_INFO("[campath] play: %zu keyframes, %s, %s, %s", m_Keys.size(), RepeatName(f_Repeat),
			f_Curved ? "curved" : "straight", f_StopAtKeyframes ? "stop at every keyframe" : "continuous runs");
	}

	// "Go to keyframe": a snap - unless the character is being carried, when a jump would trip the carry's
	// per-frame backstop (it re-measures the offset instead of following, and the character stays behind): then
	// a flight sized by the distance, which is the lead-in machinery with nothing after it. With the camera
	// off, the flight starts once the camera is seated (leadLen 0 = the start pose has not been taken yet).
	void CameraPath::StartGoto(int index)
	{
		auto& fc = FreeCamera::GetInstance();
		if (index < 0 || index >= (int)m_Keys.size())
			return;
		const Keyframe& k = m_Keys[index];
		if (!fc.f_Enabled->enabled())
		{
			fc.f_Enabled->set_enabled(true);
			fc.f_Enabled.FireChanged();
		}
		FreeCameraPose here;   // the camera's own spelling of its angles, so the flight (or the snap) continues it
		const bool havePose = fc.GetPose(here);
		const float dist = havePose ? GetVectorMagnitude(here.position - k.pose.position) : FLT_MAX;
		if (!havePose || (fc.f_MoveAvatar && dist > kSameSpot))
		{
			// A camera not seated (just switched on, or dead for a loading screen) cannot be pushed to blind: the
			// go-to becomes a take of its own that waits for it - scene-checked every tick, revoked by a Stop -
			// and snaps, or flies with the character in tow, once it is here (leadLen 0 = not started yet).
			m_Play = Playback{};
			m_Play.phase = Phase::LeadIn;
			m_Play.leadTarget = index;
			m_Play.leadTo = k.pose;
			m_Play.gotoOnly = true;
			m_Play.scene = m_CurrentScene;
			if (havePose)
			{
				m_Play.leadFrom = here;
				m_Play.leadTo = Nearest(here, k.pose);
				m_Play.leadLen = CarryFlightLen(dist) * m_Rate;   // the lead-in clock runs at the path's rate
			}
			m_PhaseView = (int)m_Play.phase;
			return;
		}
		// The camera is here: consumed by its next update.
		fc.PushPose(Nearest(here, k.pose));
		fc.f_FOV = k.pose.fov;
		m_Status = fmt::format("{} {}", _TR("at keyframe"), index + 1);
	}

	void CameraPath::StopPlayback(const char* why, bool dropPush)
	{
		if (m_Play.phase == Phase::Idle)
			return;
		if (dropPush)
			FreeCamera::GetInstance().DropPose();   // a pose no camera consumed yet must not seat the next one
		if (m_Play.hideUiForced)
		{
			auto& hide = HideUI::GetInstance();
			hide.f_Enabled->set_enabled(m_Play.hideUiWasOn);
			hide.f_Enabled.FireChanged();
		}
		if (m_Play.quiet)
			CheatManagerBase::SetOverlayQuiet(false);
		if (m_Play.hasLast)
		{
			// The free camera's FOV setting followed the path without the field-changed event (see
			// RelicTakePathPose); commit it once so the config and the panel agree with the camera.
			FreeCamera::GetInstance().f_FOV = m_Play.last.fov;
		}
		const bool wasGoto = m_Play.gotoOnly;
		const int target = m_Play.leadTarget;
		m_Play = Playback{};
		m_PhaseView = (int)Phase::Idle;
		if (wasGoto && why[0] == '\0')   // the flight completed; an aborted one keeps its reason
			m_Status = fmt::format("{} {}", _TR("at keyframe"), target + 1);
		else
		{
			m_Status = why;
			LOG_INFO("[campath] stop: %s", why);
		}
	}

	// The run of continuous motion that starts at keyframe `from`: how many segments and how long. One segment
	// with "Stop at every keyframe" on; otherwise a maximal stretch of segments of one kind - all moves, or all
	// pans in place - up to the next keyframe with a hold, the end of the path, or one full lap of a loop. A pan
	// is a run of its own so the move before it eases to a stop and the move after it eases away from rest.
	void CameraPath::BuildRun(int from, int dir, int& runSegs, float& runTotal, int& runEnd) const
	{
		const int n = (int)m_Keys.size();
		runSegs = 0;
		runTotal = 0.f;
		runEnd = from;
		int a = from;
		bool firstIsPan = false;
		for (;;)
		{
			int b = a;
			if (!NextKeyframe(a, dir, b))
				break;
			const bool pan = GetVectorMagnitude(m_Keys[b].pose.position - m_Keys[a].pose.position) < kSameSpot;
			if (runSegs == 0)
				firstIsPan = pan;
			else if (pan != firstIsPan)
				break;
			runSegs++;
			runTotal += SegmentTravel(a, b);
			runEnd = b;
			a = b;
			if (f_StopAtKeyframes || m_Keys[b].hold > 0.f || runSegs >= n)
				break;
		}
	}

	void CameraPath::Tick(float dt, float rawDt)
	{
		auto& fc = FreeCamera::GetInstance();
		const int n = (int)m_Keys.size();
		if (n < 1 || (n < 2 && !m_Play.gotoOnly))
		{
			StopPlayback(_TR("keyframes removed"));
			return;
		}

		FreeCameraPose pose = m_Play.hasLast ? m_Play.last : m_Keys[0].pose;
		bool havePose = false;
		char status[96] = {};

		// Every push goes through here. The free camera keeps CONTINUOUS Euler angles, so it is handed the spelling
		// of the pose nearest what it holds (last frame's push, or its own angles on the first): the view is the
		// same either way, but a take never leaves the camera folded - with the mouse's pitch inverted and the
		// Reset-roll key turning the image upside down. Only ever called with the camera seated.
		auto pushPose = [&](FreeCameraPose p)
		{
			FreeCameraPose ref;
			if (m_Play.hasLast)
				ref = m_Play.last;
			else if (!fc.GetPose(ref))
				ref = p;
			p = Nearest(ref, p);
			fc.PushPose(p);
			m_Play.last = p;
			m_Play.hasLast = true;
		};
		// Entering a hold: remember the aim the camera arrives with, for "Aim along the path" - in the canonical
		// spelling, the one the hold's roll (k.pose) is in, whatever spelling the arrival was pushed in.
		auto enterHold = [&](int at, FreeCameraPose arrived)
		{
			Canonical(arrived);
			m_Play.at = at;
			m_Play.phase = Phase::Hold;
			m_Play.holdPitch = arrived.pitch;
			m_Play.holdYaw = arrived.yaw;
			m_Play.holdAim = true;
		};
		// The end of a Once path: push the exact last pose first, so the camera ends ON the keyframe and not one
		// frame short of it (StopPlayback reads the last pose for the FOV commit, then resets the playback). A
		// camera not seated gets nothing, and the stop then revokes any push still standing.
		auto finish = [&]()
		{
			if (!m_Play.waiting)
				pushPose(pose);
			StopPlayback(_TR("finished"), m_Play.waiting);
		};

		// Zero-length phases (a 0 s countdown, a keyframe without a hold) chain within one tick, bounded.
		for (int step = 0; step < kMaxPhaseStepsPerTick; ++step)
		{
			bool advanced = false;
			switch (m_Play.phase)
			{
			case Phase::Countdown:
			{
				// Real seconds (rawDt): the operator's margin to hit record, never scaled by Speed or the game.
				const float wait = f_Countdown < 0.f ? 0.f : f_Countdown;
				m_Play.countdownLeft = wait - m_Play.elapsed;
				// The countdown only completes once the camera is seated (a lead-in from a camera that was just
				// switched on needs a pose to start from, and a shot must not begin on a loading screen) - and,
				// with the character carried, once a scene transmit of the carry is over, within reason.
				const bool carryBusy = fc.f_MoveAvatar && fc.IsCarryHeld() && m_Play.elapsed < wait + kCarryWait;
				if (m_Play.elapsed < wait || m_Play.waiting || carryBusy)
				{
					snprintf(status, sizeof(status), "%s %.1f s", _TR("starting in"), m_Play.countdownLeft > 0.f ? m_Play.countdownLeft : 0.f);
					m_Play.elapsed += rawDt;
					rawDt = 0.f;
					dt = 0.f;
					break;
				}
				m_Play.elapsed = 0.f;
				m_Play.countdownLeft = 0.f;
				m_Play.started = true;
				if (f_QuietOverlay)
				{
					CheatManagerBase::SetOverlayQuiet(true);
					m_Play.quiet = true;
				}
				CheatManagerBase::RequestHideMenu();   // started from the panel: the menu is not in the shot

				FreeCameraPose here;   // the camera's own spelling; the target is re-spelled to match before any comparison
				const bool haveHere = fc.GetPose(here);
				FreeCameraPose first = m_Keys[0].pose;
				float aimPitch, aimYaw;
				if (f_AimAlongPath && DepartAim(0, 1, aimPitch, aimYaw))
				{
					first.pitch = aimPitch;   // the lead-in already looks where the first move goes
					first.yaw = aimYaw;
				}
				if (haveHere)
					first = Nearest(here, first);
				float leadLen = f_LeadIn < 0.f ? 0.f : f_LeadIn;
				if (haveHere && fc.f_MoveAvatar)
				{
					// Never a jump cut with the character in tow - in path seconds, the clock the lead-in runs on.
					const float carryLen = CarryFlightLen(GetVectorMagnitude(here.position - first.position)) * m_Rate;
					if (leadLen < carryLen)
						leadLen = carryLen;
				}
				if (leadLen > 0.f && haveHere
					&& (GetVectorMagnitude(here.position - first.position) > kSameSpot
						|| fabsf(Wrap180(here.yaw - first.yaw)) > 0.5f || fabsf(Wrap180(here.pitch - first.pitch)) > 0.5f
						|| fabsf(Wrap180(here.roll - first.roll)) > 0.5f || fabsf(here.fov - first.fov) > 0.5f))
				{
					m_Play.leadFrom = here;
					m_Play.leadTo = first;
					m_Play.leadTarget = 0;
					m_Play.leadLen = leadLen;
					m_Play.phase = Phase::LeadIn;
				}
				else
				{
					enterHold(0, haveHere ? here : first);
				}
				advanced = true;
				break;
			}
			case Phase::LeadIn:
			{
				if (m_Play.leadTarget < 0 || m_Play.leadTarget >= n)
				{
					StopPlayback(_TR("keyframes removed"));
					return;
				}
				if (m_Play.leadLen <= 0.f)
				{
					// A Go-to started with the camera unseated: take the start pose now that it is here, then snap
					// (nothing carried) or fly (the character in tow).
					FreeCameraPose here;
					if (m_Play.waiting || !fc.GetPose(here))
					{
						snprintf(status, sizeof(status), "%s", _TR("waiting for the camera"));
						dt = 0.f;
						break;
					}
					m_Play.leadFrom = here;
					m_Play.leadTo = Nearest(here, m_Play.leadTo);
					const float dist = GetVectorMagnitude(here.position - m_Play.leadTo.position);
					if (!fc.f_MoveAvatar || dist <= kSameSpot)
					{
						pose = m_Play.leadTo;
						pushPose(pose);
						fc.f_FOV = pose.fov;
						StopPlayback("", false);
						return;
					}
					m_Play.leadLen = CarryFlightLen(dist) * m_Rate;
				}
				const float len = m_Play.leadLen < kMinTravel ? kMinTravel : m_Play.leadLen;
				const float u = m_Play.elapsed / len;
				const float w = Ease(EaseInOut, u);
				const auto& to = m_Play.leadTo;
				pose.position = Lerp(m_Play.leadFrom.position, to.position, w);
				pose.pitch = m_Play.leadFrom.pitch + Wrap180(to.pitch - m_Play.leadFrom.pitch) * w;
				pose.yaw = m_Play.leadFrom.yaw + Wrap180(to.yaw - m_Play.leadFrom.yaw) * w;
				pose.roll = m_Play.leadFrom.roll + Wrap180(to.roll - m_Play.leadFrom.roll) * w;
				pose.fov = Lerp(m_Play.leadFrom.fov, to.fov, w);
				havePose = true;
				snprintf(status, sizeof(status), "%s", m_Play.gotoOnly ? _TR("flying to the keyframe") : _TR("lead-in"));
				if (u >= 1.f)
				{
					pose = to;
					if (m_Play.gotoOnly)
					{
						// Like finish(): the exact pose only to a seated camera; a flight that ends on a loading
						// screen pushes nothing and the stop revokes any push still standing.
						if (!m_Play.waiting)
							pushPose(to);
						StopPlayback("", m_Play.waiting);
						return;
					}
					m_Play.elapsed -= len;
					enterHold(m_Play.leadTarget, to);
					advanced = true;
					break;
				}
				m_Play.elapsed += dt;
				dt = 0.f;
				break;
			}
			case Phase::Hold:
			{
				if (m_Play.at < 0 || m_Play.at >= n)
				{
					StopPlayback(_TR("keyframes removed"));
					return;
				}
				const Keyframe& k = m_Keys[m_Play.at];
				pose = k.pose;
				if (f_AimAlongPath && m_Play.holdAim)
				{
					// Turn from the aim the camera arrived with towards where the next move goes, over the hold.
					int ddir = m_Play.dir, nx;
					if (!NextKeyframe(m_Play.at, ddir, nx) && f_Repeat == RepeatPingPong)
						ddir = -ddir;
					float toPitch = m_Play.holdPitch, toYaw = m_Play.holdYaw;
					DepartAim(m_Play.at, ddir, toPitch, toYaw);
					const float t = k.hold > 0.f ? Ease(EaseInOut, m_Play.elapsed / k.hold) : 1.f;
					pose.pitch = m_Play.holdPitch + Wrap180(toPitch - m_Play.holdPitch) * t;
					pose.yaw = m_Play.holdYaw + Wrap180(toYaw - m_Play.holdYaw) * t;
				}
				havePose = true;
				if (m_Play.elapsed < k.hold)
				{
					snprintf(status, sizeof(status), "%s %d/%d  %.1f s", _TR("hold"), m_Play.at + 1, n, k.hold - m_Play.elapsed);
					m_Play.elapsed += dt;
					dt = 0.f;
					break;
				}
				m_Play.elapsed -= k.hold;
				if (m_Play.elapsed < 0.f) m_Play.elapsed = 0.f;

				int next;
				if (!NextKeyframe(m_Play.at, m_Play.dir, next))
				{
					if (f_Repeat == RepeatPingPong)
					{
						m_Play.dir = -m_Play.dir;
						if (!NextKeyframe(m_Play.at, m_Play.dir, next))
						{
							finish();
							return;
						}
					}
					else
					{
						finish();
						return;
					}
				}
				BuildRun(m_Play.at, m_Play.dir, m_Play.runSegs, m_Play.runTotal, m_Play.runEnd);
				if (m_Play.runSegs == 0)
				{
					finish();
					return;
				}
				m_Play.runFrom = m_Play.at;
				m_Play.phase = Phase::Travel;
				advanced = true;
				break;
			}
			case Phase::Travel:
			{
				const float total = m_Play.runTotal < kMinTravel ? kMinTravel : m_Play.runTotal;
				const float u = m_Play.elapsed / total;
				// One easing over the whole run; a run is one segment when every keyframe is a stop. Played
				// backwards (Back and forth) the rewind of an ease-in is an ease-out.
				int firstNext = m_Play.runFrom;
				if (m_Play.runFrom < 0 || m_Play.runFrom >= n || !NextKeyframe(m_Play.runFrom, m_Play.dir, firstNext))
				{
					StopPlayback(_TR("finished"));   // the repeat mode changed under a run that no longer exists
					return;
				}
				int ease = SegmentEase(m_Play.runFrom, firstNext);
				if (m_Play.dir < 0)
				{
					// The rewind of the FORWARD run (runEnd -> runFrom): its easing is the one leaving runEnd in
					// forward order - on a run of several segments another keyframe's than the one leaving runFrom.
					ease = m_Keys[m_Play.runEnd].ease;
					ease = ease == EaseIn ? EaseOut : (ease == EaseOut ? EaseIn : ease);
				}
				// A run that closes on itself - a Loop lap with no hold anywhere - has no ends to ease at: eased per
				// lap the camera would brake to a standstill at the first keyframe every lap. Even pace instead.
				if (f_Repeat == RepeatLoop && m_Play.runSegs >= n)
					ease = EaseLinear;
				const float easedTime = Ease(ease, u) * total;

				int a = m_Play.runFrom, b = firstNext;
				float acc = 0.f;
				float w = 1.f;
				for (int i = 0; i < m_Play.runSegs; ++i)
				{
					b = a;
					if (!NextKeyframe(a, m_Play.dir, b))
						break;                       // (same case) - hold the last reachable keyframe
					const float t = SegmentTravel(a, b);
					if (easedTime <= acc + t || i == m_Play.runSegs - 1)
					{
						w = Clamp01((easedTime - acc) / t);
						break;
					}
					acc += t;
					a = b;
				}
				Evaluate(a, b, w, pose);
				if (f_AimAlongPath)
				{
					// Pitch and yaw from the direction of travel, a hair ahead along the same move (or behind, at
					// its very end); a standstill keeps the previous aim.
					FreeCameraPose ahead;
					const float eps = 0.002f;
					app::Vector3 dir{};
					if (w + eps <= 1.f) { Evaluate(a, b, w + eps, ahead); dir = ahead.position - pose.position; }
					else { Evaluate(a, b, w - eps, ahead); dir = pose.position - ahead.position; }
					float aimPitch, aimYaw;
					if (AimFromDirection(dir, aimPitch, aimYaw))
					{
						pose.pitch = aimPitch;
						pose.yaw = aimYaw;
					}
					else if (m_Play.hasLast)
					{
						pose.pitch = m_Play.last.pitch;
						pose.yaw = m_Play.last.yaw;
					}
				}
				havePose = true;
				snprintf(status, sizeof(status), "%s %d/%d  %.1f s", _TR("playing"), m_Play.runEnd + 1, n, total - m_Play.elapsed);
				if (u >= 1.f)
				{
					m_Play.elapsed -= total;
					if (m_Play.elapsed < 0.f) m_Play.elapsed = 0.f;
					enterHold(m_Play.runEnd, pose);
					advanced = true;
					break;
				}
				m_Play.elapsed += dt;
				dt = 0.f;
				break;
			}
			default:
				return;
			}
			if (!advanced)
				break;
		}

		// Nothing is pushed while the camera is not seated: a pose belongs to the scene it was recorded in, and a
		// camera that comes back after a load is checked against that scene first.
		if (havePose && !m_Play.waiting)
			pushPose(pose);
		if (m_Play.waiting)
			m_Status = _TR("waiting for the camera");
		else
			m_Status = status;
	}

	// ------------------------------------------------------------------------------------------------
	// Serving the requests (game thread; m_Lock held, no il2cpp)
	// ------------------------------------------------------------------------------------------------

	void CameraPath::Serve(const Pending& req)
	{
		auto& fc = FreeCamera::GetInstance();
		const int n = (int)m_Keys.size();
		const bool playing = m_Play.phase != Phase::Idle;

		auto captureInto = [&](Keyframe& k, bool complain) -> bool
		{
			FreeCameraPose pose;
			if (!GetCanonicalPose(fc, pose))
			{
				if (complain)
					m_Status = _TR("turn the free camera on first");
				return false;
			}
			k.pose = pose;
			return true;
		};

		switch (req.kind)
		{
		case Request::Capture:
		{
			if (playing) break;
			if (n >= kMaxKeyframes) { m_Status = _TR("too many keyframes"); break; }
			Keyframe k;
			k.hold = f_DefaultHold;
			k.travel = f_DefaultTravel;
			k.ease = f_DefaultEase;
			if (!captureInto(k, true)) break;
			if (n == 0)
				m_SceneId = m_CurrentScene;
			m_Keys.push_back(k);
			Touched();
			m_Status = fmt::format("{} {}", _TR("keyframe"), n + 1);
			break;
		}
		case Request::Update:
		{
			if (playing || req.index < 0 || req.index >= n) break;
			if (!captureInto(m_Keys[req.index], true)) break;
			Touched();
			m_Status = fmt::format("{} {} {}", _TR("keyframe"), req.index + 1, _TR("updated"));
			break;
		}
		case Request::InsertAfter:
		{
			if (playing || req.index < 0 || req.index >= n || n >= kMaxKeyframes) break;
			Keyframe k = m_Keys[req.index];
			const bool fromCamera = captureInto(k, false);
			if (!fromCamera) k = m_Keys[req.index];   // no camera: a copy of the keyframe, edited by hand later
			m_Keys.insert(m_Keys.begin() + req.index + 1, k);
			if (m_Selected > req.index) m_Selected++;
			Touched();
			m_Status = fmt::format("{} {} {}", _TR("keyframe"), req.index + 2, fromCamera ? _TR("inserted") : _TR("copied"));
			break;
		}
		case Request::Goto:
		case Request::GoStart:
		{
			if (playing) break;
			StartGoto(req.kind == Request::GoStart ? 0 : req.index);
			break;
		}
		case Request::RemoveLast:
		{
			if (playing || n == 0) break;
			m_Keys.pop_back();
			if (m_Selected >= (int)m_Keys.size()) m_Selected = -1;
			Touched();
			m_Status = fmt::format("{} {}", _TR("keyframes:"), n - 1);
			break;
		}
		case Request::ClearAll:
		{
			if (playing) break;
			m_Keys.clear();
			m_SceneId = 0;
			m_LoadedName.clear();
			m_Selected = -1;
			Touched();
			m_Status.clear();
			break;
		}
		case Request::Play:
			if (!playing)
				StartPlayback();
			break;
		case Request::Stop:
			StopPlayback(_TR("stopped"));
			break;
		case Request::DistributeTime:
		{
			// Give every move a time proportional to its length, keeping the total - an even flight through
			// the whole path. Pans in place keep their own time.
			if (playing || n < 2) break;
			const int segs = f_Repeat == RepeatLoop ? n : n - 1;
			float total = 0.f, kept = 0.f, chordSum = 0.f;
			std::vector<float> chords(segs, 0.f);
			for (int i = 0; i < segs; ++i)
			{
				const int j = (i + 1) % n;
				chords[i] = GetVectorMagnitude(m_Keys[j].pose.position - m_Keys[i].pose.position);
				total += m_Keys[i].travel;
				if (chords[i] < kSameSpot) kept += m_Keys[i].travel;
				else chordSum += chords[i];
			}
			if (chordSum <= 0.f) break;
			float pool = total - kept;
			if (pool < 0.5f) pool = 0.5f;
			for (int i = 0; i < segs; ++i)
			{
				if (chords[i] < kSameSpot) continue;
				float t = pool * chords[i] / chordSum;
				m_Keys[i].travel = t < kMinTravel ? kMinTravel : t;
			}
			Touched();
			break;
		}
		case Request::Orbit:
		{
			// The classic orbit: keyframes on a circle around the point the camera looks at, at the camera's
			// height, each aimed at that point, starting where the camera is now. Looped, curved, continuous.
			if (playing) break;
			FreeCameraPose here;
			if (!GetCanonicalPose(fc, here)) { m_Status = _TR("turn the free camera on first"); break; }
			int count = f_OrbitKeys;
			if (count < 3) count = 3;
			if (count > 36) count = 36;
			float dist = f_OrbitDistance;
			if (dist < 0.5f) dist = 0.5f;
			const app::Vector3 fwd = Forward(here.pitch, here.yaw);
			const app::Vector3 center = here.position + fwd * dist;
			const float radius = sqrtf(fwd.x * fwd.x + fwd.z * fwd.z) * dist;   // the horizontal distance
			if (radius < kSameSpot) { m_Status = _TR("look at the point to orbit, not straight down"); break; }
			const float a0 = atan2f(here.position.x - center.x, here.position.z - center.z);
			m_Keys.clear();
			for (int i = 0; i < count; ++i)
			{
				const float a = a0 + (float)i / count * 2.f * 3.14159265f;
				Keyframe k;
				k.pose.position = { center.x + sinf(a) * radius, here.position.y, center.z + cosf(a) * radius };
				k.pose.yaw = atan2f(center.x - k.pose.position.x, center.z - k.pose.position.z) * kDegPerRad;
				k.pose.pitch = here.pitch;
				k.pose.roll = 0.f;
				k.pose.fov = here.fov;
				k.hold = 0.f;
				k.travel = f_DefaultTravel;
				k.ease = EaseLinear;
				m_Keys.push_back(k);
			}
			m_SceneId = m_CurrentScene;
			m_LoadedName.clear();
			m_Selected = -1;
			Touched();
			f_Repeat = (int)RepeatLoop;
			f_Curved = true;
			f_StopAtKeyframes = false;
			m_Status = fmt::format("{} {}", _TR("orbit of"), count);
			break;
		}
		default:
			break;   // Load / Save / Delete are served outside the lock (file IO) - see OnGameUpdate
		}
	}

	// ------------------------------------------------------------------------------------------------
	// Game thread
	// ------------------------------------------------------------------------------------------------

	static float SafeTimeScale()
	{
		float scale = 1.f;
		relic::Try([&]() { scale = app::Time_get_timeScale(nullptr); });
		if (!(scale >= 0.f) || scale > 100.f)
			scale = 1.f;
		return scale;
	}

	static uint32_t SafeSceneId()
	{
		uint32_t id = 0;
		relic::Try([&]() { id = game::GetCurrentPlayerSceneID(); });
		return id;
	}

	// The hotkeys' feedback with the menu closed - the manager's own idiom for its toggles (CheckToggles). Never
	// under m_Lock; hidden by the quiet gate during a take anyway.
	static void Toast(const std::string& text)
	{
		if (text.empty() || CheatManagerBase::IsMenuShowed())
			return;
		auto& settings = Settings::GetInstance();
		if (!settings.f_NotificationsShow)
			return;
		ImGuiToast toast(ImGuiToastType_None, settings.f_NotificationsDelay);
		toast.set_title("%s: %s", _TR("Camera Path"), text.c_str());
		ImGui::InsertNotification(toast);
	}

	void CameraPath::OnGameUpdate()
	{
		auto& fc = FreeCamera::GetInstance();
		m_Silence = 0;   // proof of life for OnPathWatchdog

		// ---- the working copy of the last session, once
		if (!m_AutosaveLoaded)
		{
			m_AutosaveLoaded = true;
			if (LoadFrom(kAutosaveName, true))
			{
				std::lock_guard<std::mutex> lock(m_Lock);
				m_Dirty = false;   // it is what the file holds
				if (!m_Keys.empty())
					LOG_INFO("[campath] %zu keyframes restored from the last session%s%s", m_Keys.size(),
						m_LoadedName.empty() ? "" : " - ", m_LoadedName.c_str());
			}
		}

		// ---- il2cpp samples first, with nothing locked (the scene every second, and every tick during a take:
		//      a camera that comes back after a load must be checked against the take's scene before any push)
		static int s_SceneTicks = 60;
		const bool taking = m_PhaseView.load() != (int)Phase::Idle;
		uint32_t scene = 0;
		bool sceneSampled = false;
		if (++s_SceneTicks >= 60 || taking)
		{
			s_SceneTicks = 0;
			scene = SafeSceneId();
			sceneSampled = true;
		}
		const float timeScale = f_GameTime ? SafeTimeScale() : 1.f;
		float speed = f_Speed;
		if (!(speed > 0.f)) speed = 1.f;
		const int64_t nowMs = NowMs();
		const bool running = fc.IsRunning();   // pure reads of the free camera's own flags

		// ---- requests
		std::vector<Pending> requests;
		{
			std::lock_guard<std::mutex> lock(m_Lock);
			if (sceneSampled)
				m_CurrentScene = scene;
			m_Rate = speed * timeScale;   // what the carry-safe flights are sized against
			requests.swap(m_Requests);
		}
		for (auto& req : requests)
		{
			if (req.kind == Request::Play)
			{
				// A capture (or a named save) in the second before Play would otherwise stay unsaved for the whole
				// take - the autosave waits for Idle. Flush it now, outside the lock, before the phase changes.
				bool flush = false;
				{
					std::lock_guard<std::mutex> lock(m_Lock);
					flush = m_Dirty && m_Play.phase == Phase::Idle;
					if (flush)
						m_Dirty = false;
				}
				if (flush)
					Autosave();
			}
			if (req.kind == Request::Load || req.kind == Request::Save || req.kind == Request::Delete)
			{
				// File IO, outside the lock; never while a path plays (the keyframes are being read).
				if (m_PhaseView.load() != (int)Phase::Idle)
					continue;
				if (req.kind == Request::Load)
				{
					if (!LoadFrom(req.name, false))
					{
						std::lock_guard<std::mutex> lock(m_Lock);
						m_Status = _TR("could not load that file");
					}
				}
				else if (req.kind == Request::Save)
				{
					if (!SaveTo(req.name, false))
					{
						std::lock_guard<std::mutex> lock(m_Lock);
						m_Status = _TR("could not save");
					}
				}
				else
				{
					std::error_code ec;
					std::filesystem::remove(NamePath(m_Dir, req.name, ".json"), ec);
					if (ec)
						LOG_WARNING("[campath] could not delete '%s': %s", req.name.c_str(), ec.message().c_str());
					std::lock_guard<std::mutex> lock(m_Lock);
					if (m_LoadedName == req.name)
					{
						m_LoadedName.clear();   // the keyframes stay; they no longer belong to a file
						m_Dirty = true;
						m_DirtyMs = nowMs;
					}
				}
				RefreshFiles();
				continue;
			}
			// The toast is the request's OWN output - what Serve wrote - so a repeated refusal is repeated; a request
			// that says nothing leaves the previous status line alone.
			std::string toast;
			{
				std::lock_guard<std::mutex> lock(m_Lock);
				const std::string kept = m_Status;
				m_Status.clear();
				Serve(req);
				if (m_Status.empty())
					m_Status = kept;
				else
					toast = m_Status;
			}
			Toast(toast);
		}

		// ---- playback
		bool wantAutosave = false;
		{
			std::lock_guard<std::mutex> lock(m_Lock);
			if (m_ArcVersion != m_KeysVersion || m_ArcRepeat != f_Repeat || m_ArcCurved != (bool)f_Curved)
				RebuildArcTable();
			if (m_Play.phase != Phase::Idle)
			{
				if (m_Play.scene == 0 && m_CurrentScene != 0)
					m_Play.scene = m_CurrentScene;   // the take started before the first sample of the session
				if (!fc.f_Enabled->enabled())
				{
					StopPlayback(_TR("free camera switched off"));
				}
				else if (m_Play.scene != 0 && m_CurrentScene != 0 && m_CurrentScene != m_Play.scene)
				{
					StopPlayback(_TR("scene changed"));   // the keyframes belong to the scene they were made in
				}
				else
				{
					float dt = 0.f;
					if (m_Play.lastTickMs > 0)
					{
						dt = (float)(nowMs - m_Play.lastTickMs) / 1000.f;
						if (dt < 0.f) dt = 0.f;
						if (dt > kMaxDt) dt = kMaxDt;
						if (fc.f_MoveAvatar && dt > kCarryMaxDt)
							dt = kCarryMaxDt;   // the carry's backstop is per frame: a hitch must not become a jump
					}
					m_Play.lastTickMs = nowMs;
					// A camera not seated - a loading screen, a fresh enable - pauses the clock and the pushes.
					m_Play.waiting = !running;
					if (m_Play.waiting)
						dt = 0.f;
					Tick(dt * speed * timeScale, dt);
				}
			}
			m_PhaseView = (int)m_Play.phase;
			if (m_Dirty && m_Play.phase == Phase::Idle && nowMs - m_DirtyMs >= 1000)
			{
				m_Dirty = false;   // a second of quiet after the last edit (a slider drag is not a save per frame)
				wantAutosave = true;
			}
		}
		if (wantAutosave)
			Autosave();

		// ---- the on-screen preview: project the keyframes and the curve while the path is not playing
		bool wantOverlay = false;
		{
			std::lock_guard<std::mutex> lock(m_Lock);
			wantOverlay = f_ShowPath && m_Play.phase == Phase::Idle && !m_Keys.empty()
				&& fc.f_Enabled->enabled() && running && m_OverlayFaults < kOverlayFaultLimit;
			if (!wantOverlay)
				m_OverlayValid = false;   // a loading screen must not keep last frame's dots on screen
		}
		if (!wantOverlay)
			return;

		// The world positions to project, built under the lock (C++ only)...
		{
			std::lock_guard<std::mutex> lock(m_Lock);
			const int n = (int)m_Keys.size();
			m_OverlayWorld.clear();
			for (int i = 0; i < n; ++i)
				m_OverlayWorld.push_back({ m_Keys[i].pose.position, i });
			const int segs = n < 2 ? 0 : (f_Repeat == RepeatLoop ? n : n - 1);
			for (int i = 0; i < segs; ++i)
			{
				const int j = (i + 1) % n;
				for (int s = 1; s < kCurveSamples; ++s)
				{
					FreeCameraPose p;
					if (Evaluate(i, j, (float)s / kCurveSamples, p))
						m_OverlayWorld.push_back({ p.position, -1 - i });   // -1 - segment: which segment the sample belongs to
				}
			}
			m_OverlayScratch.resize(m_OverlayWorld.size());
		}
		// ...projected outside it, into a pre-sized buffer, under the fault guard, with no object constructed
		// inside the guarded body (see the note on threads at the top).
		auto* cam = fc.CurrentCamera();
		if (cam == nullptr || cam->fields._._._.m_CachedPtr == nullptr)
		{
			std::lock_guard<std::mutex> lock(m_Lock);
			m_OverlayValid = false;
			return;
		}
		const size_t count = m_OverlayWorld.size();
		auto* world = m_OverlayWorld.data();
		auto* out = m_OverlayScratch.data();
		bool ok = relic::Try([&]()
		{
			const float screenW = (float)app::Screen_get_width(nullptr);
			const float screenH = (float)app::Screen_get_height(nullptr);
			const int pixelW = app::Camera_get_pixelWidth(cam, nullptr);
			const int pixelH = app::Camera_get_pixelHeight(cam, nullptr);
			const float sx = pixelW > 0 ? screenW / (float)pixelW : 1.f;
			const float sy = pixelH > 0 ? screenH / (float)pixelH : 1.f;
			for (size_t i = 0; i < count; ++i)
			{
				auto rel = app::WorldShiftManager_GetRelativePosition(world[i].position, nullptr);
				auto sp = app::Camera_WorldToScreenPoint(cam, rel, nullptr);
				out[i].x = sp.x * sx;
				out[i].y = screenH - sp.y * sy;
				out[i].visible = sp.z > 0.f;
				out[i].key = world[i].key;
			}
		});
		std::lock_guard<std::mutex> lock(m_Lock);
		if (!ok)
		{
			if (++m_OverlayFaults >= kOverlayFaultLimit)
				LOG_WARNING("[campath] the on-screen preview faulted %d times (0x%08x) - switched off for this session.",
					m_OverlayFaults, relic::g_lastGuardCode);
			m_OverlayValid = false;
			return;
		}
		m_OverlayFaults = 0;
		m_Overlay.assign(m_OverlayScratch.begin(), m_OverlayScratch.end());
		m_OverlayValid = true;
	}

	// The recovery for the take's side effects. If OnGameUpdate is silenced by the fault guard mid-take, the free
	// camera is back under the player's hand next frame on its own (a push is consumed once), but the game UI
	// would stay hidden and the mod's overlays quiet. This handler has its own fault budget and only ever
	// stops a take nobody is driving any more.
	void CameraPath::OnPathWatchdog()
	{
		if (++m_Silence <= 120)
			return;
		m_Silence = 0;
		if (m_PhaseView.load() == (int)Phase::Idle)
			return;
		LOG_WARNING("[campath] the path player has not run for 120 ticks - stopping the take.");
		std::lock_guard<std::mutex> lock(m_Lock);
		StopPlayback(_TR("the player stopped responding"));
	}

	// ------------------------------------------------------------------------------------------------
	// Render thread
	// ------------------------------------------------------------------------------------------------

	void CameraPath::DrawExternal()
	{
		std::vector<OverlayPoint> points;
		int n = 0, selected = -1;
		int phase = m_PhaseView.load();
		float countdown = 0.f;
		bool gotoOnly = false;
		{
			std::lock_guard<std::mutex> lock(m_Lock);
			n = (int)m_Keys.size();
			selected = m_Selected;
			countdown = m_Play.countdownLeft;
			gotoOnly = m_Play.gotoOnly;
			if (m_OverlayValid)
				points = m_Overlay;
		}

		auto* draw = ImGui::GetBackgroundDrawList();

		// The countdown, big and central - it runs before the shot, on frames that get trimmed anyway. The atlas
		// holds only the menu's size; a face baked at the digits' size (the ESP's font-token idiom) draws them
		// crisp, the menu font fills in for the frame before the atlas is rebuilt.
		if (phase == (int)Phase::Countdown && !gotoOnly && countdown > 0.f)
		{
			static renderer::FontToken s_Token = renderer::CreateFontToken(renderer::GetDefaultFontName(), kCountdownFontSize);
			if (renderer::GetTokenFontName(s_Token) != renderer::GetDefaultFontName())
				renderer::SetTokenFont(s_Token, renderer::GetDefaultFontName());
			if (renderer::GetTokenFontSize(s_Token) != kCountdownFontSize)
				renderer::SetTokenFontSize(s_Token, kCountdownFontSize);   // CreateFontToken ignores its size (the ESP does the same)
			ImFont* font = renderer::GetTokenFont(s_Token);
			if (font == nullptr)
				font = ImGui::GetFont();
			char text[16];
			snprintf(text, sizeof(text), "%d", (int)ceilf(countdown));
			const float size = kCountdownFontSize;
			const ImVec2 extent = font->CalcTextSizeA(size, FLT_MAX, 0.f, text);
			const ImVec2 screen = ImGui::GetIO().DisplaySize;
			const ImVec2 pos((screen.x - extent.x) * 0.5f, (screen.y - extent.y) * 0.5f);
			draw->AddText(font, size, ImVec2(pos.x + 3.f, pos.y + 3.f), ImColor(0, 0, 0, 180), text);
			draw->AddText(font, size, pos, ImColor(255, 255, 255, 230), text);
		}

		if (points.empty())
			return;

		const ImU32 lineColor = ImColor(0.35f, 0.85f, 1.0f, 0.75f);
		const ImU32 keyColor = ImColor(1.0f, 0.85f, 0.2f, 0.95f);
		const ImU32 firstColor = ImColor(0.3f, 1.0f, 0.4f, 0.95f);
		const ImU32 lastColor = ImColor(1.0f, 0.4f, 0.35f, 0.95f);
		const ImU32 selColor = ImColor(1.0f, 1.0f, 1.0f, 1.0f);

		// The curve: consecutive samples of one segment, keyframe to keyframe, both ends in front of the camera.
		// The samples are stored keyframes first, then per segment in order; a segment's polyline runs
		// key[i] -> samples(i) -> key[j].
		auto keyPoint = [&](int idx) -> const OverlayPoint* { return idx >= 0 && idx < n && idx < (int)points.size() ? &points[idx] : nullptr; };
		int segment = -1;
		const OverlayPoint* prev = nullptr;
		auto closeSegment = [&]()
		{
			if (segment < 0 || prev == nullptr || n < 1)
				return;
			const auto* end = keyPoint((segment + 1) % n);
			if (end != nullptr && end->visible && prev->visible)
				draw->AddLine(ImVec2(prev->x, prev->y), ImVec2(end->x, end->y), lineColor, 2.f);
		};
		for (size_t i = (size_t)n; i < points.size(); ++i)
		{
			const auto& p = points[i];
			const int seg = -1 - p.key;
			if (seg != segment)
			{
				closeSegment();
				segment = seg;
				prev = keyPoint(seg);
			}
			if (prev != nullptr && prev->visible && p.visible)
				draw->AddLine(ImVec2(prev->x, prev->y), ImVec2(p.x, p.y), lineColor, 2.f);
			prev = &p;
		}
		closeSegment();

		// The keyframes: numbered dots, the first green, the last red, the selected one ringed.
		for (int i = 0; i < n && i < (int)points.size(); ++i)
		{
			const auto& p = points[i];
			if (!p.visible)
				continue;
			ImU32 color = i == 0 ? firstColor : (i == n - 1 ? lastColor : keyColor);
			draw->AddCircleFilled(ImVec2(p.x, p.y), 5.f, color);
			draw->AddCircle(ImVec2(p.x, p.y), 5.f, ImColor(0, 0, 0, 200), 0, 1.5f);
			if (i == selected)
				draw->AddCircle(ImVec2(p.x, p.y), 9.f, selColor, 0, 2.f);
			char label[16];
			snprintf(label, sizeof(label), "%d", i + 1);
			DrawTextWithOutline(draw, ImVec2(p.x + 8.f, p.y - 8.f), label, ImColor(1.f, 1.f, 1.f, 1.f), 1.f);
		}
	}

	// In the status pane only where it cannot end up in a recording: during the countdown (before the shot) and
	// while the menu is open (nobody records with the menu up). The quiet gate hides the whole pane during a take
	// anyway when it is on.
	bool CameraPath::NeedStatusDraw() const
	{
		const int phase = m_PhaseView.load();
		if (phase == (int)Phase::Idle)
			return false;
		return phase == (int)Phase::Countdown || CheatManagerBase::IsMenuShowed();
	}

	void CameraPath::DrawStatus()
	{
		std::string status;
		{
			std::lock_guard<std::mutex> lock(m_Lock);
			status = m_Status;
		}
		ImGui::Text("%s: %s", _TR("Camera Path"), status.c_str());
	}

	// A combo over an int with translated names.
	static bool ComboNames(const char* label, int& value, int count, const char* (*name)(int), float width)
	{
		bool changed = false;
		if (value < 0 || value >= count) value = 0;
		ImGui::SetNextItemWidth(width);
		if (ImGui::BeginCombo(label, name(value)))
		{
			for (int i = 0; i < count; ++i)
			{
				const bool selected = i == value;
				if (ImGui::Selectable(name(i), selected))
				{
					value = i;
					changed = true;
				}
				if (selected)
					ImGui::SetItemDefaultFocus();
			}
			ImGui::EndCombo();
		}
		return changed;
	}

	static bool ComboField(const char* label, config::Field<int>& field, int count, const char* (*name)(int), float width, const char* desc = nullptr)
	{
		bool changed = ComboNames(label, field.value(), count, name, width);
		if (changed)
			field.FireChanged();
		if (desc != nullptr) { ImGui::SameLine(); HelpMarker(desc); }
		return changed;
	}

	void CameraPath::DrawMain()
	{
		auto& fc = FreeCamera::GetInstance();
		static std::string s_SaveName;
		static std::string s_FileSel;   // the chosen file by NAME: the list is re-sorted on every refresh

		// ---- a copy of the shared state; the panel draws from it and writes its edits back at the end
		std::vector<Keyframe> keys;
		std::vector<std::string> files;
		std::string status, loadedName;
		uint32_t version = 0, sceneId = 0, currentScene = 0;
		bool playing = false;
		int selected = -1;
		{
			std::lock_guard<std::mutex> lock(m_Lock);
			keys = m_Keys;
			files = m_Files;
			status = m_Status;
			loadedName = m_LoadedName;
			version = m_KeysVersion;
			sceneId = m_SceneId;
			currentScene = m_CurrentScene;
			playing = m_Play.phase != Phase::Idle;
			selected = m_Selected;
		}
		bool keysEdited = false;
		bool selectedChanged = false;
		std::vector<Pending> toPost;
		auto post = [&](Request kind, int index = -1, std::string name = {}) { toPost.push_back(Pending{ kind, index, std::move(name) }); };
		auto finite3 = [](const float* v) { return std::isfinite(v[0]) && std::isfinite(v[1]) && std::isfinite(v[2]); };

		const int n = (int)keys.size();
		const bool camOn = fc.f_Enabled->enabled();

		ImGui::TextWrapped("%s", _TR("Fly the free camera to a spot and add a keyframe; fly on and add the next. Play carries the "
			"camera through them - straight or on a smooth curve, eased in and out of every stop, pausing where you ask."));
		if (!camOn && !playing)
			ImGui::TextColored(ImVec4(1.f, 0.75f, 0.2f, 1.f), "%s", _TR("Turn the Free Camera on to record keyframes (Play turns it on by itself)."));

		// ---- the two things that matter
		ImGui::BeginDisabled(playing);
		if (ImGui::Button(_TR("Add keyframe")))
			post(Request::Capture);
		ImGui::EndDisabled();
		if (ImGui::IsItemHovered())
			ImGui::SetTooltip("%s: %s", _TR("Hotkey"), std::string(f_AddKey.value()).c_str());
		ImGui::SameLine();
		if (playing)
		{
			if (ImGui::Button(_TR("Stop")))
				post(Request::Stop);
		}
		else
		{
			ImGui::BeginDisabled(n < 2);
			if (ImGui::Button(_TR("Play")))
				post(Request::Play);
			ImGui::EndDisabled();
		}
		if (ImGui::IsItemHovered())
			ImGui::SetTooltip("%s: %s", _TR("Hotkey"), std::string(f_PlayKey.value()).c_str());
		ImGui::SameLine();
		ImGui::BeginDisabled(playing || n == 0);
		if (ImGui::Button(_TR("Go to start")))
			post(Request::GoStart);
		ImGui::EndDisabled();
		ImGui::SameLine();
		HelpMarker(_TR("Add keyframe records where the free camera is and where it looks (and its FOV).\n"
			"Play flies through the keyframes in order and closes this menu when the move starts;\n"
			"Stop leaves the camera where it is. Go to start puts the camera on the first keyframe.\n"
			"\n"
			"Two keyframes at the same spot with different angles are a pan in place.\n"
			"A hold pauses the camera at that keyframe; the time is the length of the move to the next one.\n"
			"Turns take the short way round: for a full spin add a keyframe half way.\n"
			"The hotkeys work while this menu is closed."));

		if (!status.empty())
			ImGui::TextDisabled("%s", status.c_str());
		if (n > 0 && sceneId != 0 && currentScene != 0 && sceneId != currentScene)
			ImGui::TextColored(ImVec4(1.f, 0.75f, 0.2f, 1.f), "%s %u, %s %u.", _TR("Recorded in scene"), sceneId, _TR("you are in scene"), currentScene);
		if (fc.f_MoveAvatar && n >= 2)
		{
			const int segs = f_Repeat == RepeatLoop ? n : n - 1;
			for (int i = 0; i < segs; ++i)
			{
				const int j = (i + 1) % n;
				const float chord = GetVectorMagnitude(keys[j].pose.position - keys[i].pose.position);
				const float t = keys[i].travel < kMinTravel ? kMinTravel : keys[i].travel;
				if (chord / t * (f_Speed > 0.f ? f_Speed : 1.f) > kCarrySafeSpeed)
				{
					ImGui::TextColored(ImVec4(1.f, 0.75f, 0.2f, 1.f), "%s %d %s", _TR("Move"), i + 1,
						_TR("is too fast for Move Character with Camera - the character will be left behind."));
					break;
				}
			}
		}

		// ---- the keyframes
		if (n > 0)
		{
			ImGui::BeginDisabled(playing);
			float easeWidth = 0.f;
			for (int e = 0; e < EaseCount; ++e)
			{
				const float w = ImGui::CalcTextSize(EaseName(e)).x;   // (Windows.h's max macro rules std::max out here)
				if (w > easeWidth)
					easeWidth = w;
			}
			easeWidth += ImGui::GetStyle().FramePadding.x * 2.f + ImGui::GetFrameHeight();   // text + padding + the arrow
			if (ImGui::BeginTable("CameraPathKeys", 5, ImGuiTableFlags_SizingFixedFit | ImGuiTableFlags_RowBg | ImGuiTableFlags_NoSavedSettings))
			{
				ImGui::TableSetupColumn("#", ImGuiTableColumnFlags_WidthFixed, 34.f);
				ImGui::TableSetupColumn(_TR("Hold"), ImGuiTableColumnFlags_WidthFixed, 50.f);
				ImGui::TableSetupColumn(_TR("Time"), ImGuiTableColumnFlags_WidthFixed, 50.f);
				ImGui::TableSetupColumn(_TR("Ease"), ImGuiTableColumnFlags_WidthFixed, easeWidth);
				ImGui::TableSetupColumn("", ImGuiTableColumnFlags_WidthStretch);
				ImGui::TableHeadersRow();
				if (ImGui::TableGetColumnFlags(1) & ImGuiTableColumnFlags_IsHovered) ImGui::SetTooltip("%s", _TR("Seconds the camera waits at this keyframe"));
				if (ImGui::TableGetColumnFlags(2) & ImGuiTableColumnFlags_IsHovered) ImGui::SetTooltip("%s", _TR("Seconds the move to the next keyframe takes (in Loop, the last one's is the return)"));
				if (ImGui::TableGetColumnFlags(3) & ImGuiTableColumnFlags_IsHovered) ImGui::SetTooltip("%s", _TR("How that move starts and ends"));

				int moveUp = -1, moveDown = -1, remove = -1;
				for (int i = 0; i < n; ++i)
				{
					auto& k = keys[i];
					ImGui::PushID(i);
					ImGui::TableNextRow();
					ImGui::TableNextColumn();
					const bool isSel = i == selected;
					const bool pan = i + 1 < n && GetVectorMagnitude(keys[i + 1].pose.position - k.pose.position) < kSameSpot;
					char num[12];
					snprintf(num, sizeof(num), "%d%s", i + 1, pan ? "*" : "");
					if (ImGui::Selectable(num, isSel, ImGuiSelectableFlags_None, ImVec2(0, 0)))
					{
						selected = isSel ? -1 : i;
						selectedChanged = true;
					}
					if (pan && ImGui::IsItemHovered())
						ImGui::SetTooltip("%s", _TR("* the next keyframe is at the same spot: this move is a pan in place"));
					ImGui::TableNextColumn();
					ImGui::SetNextItemWidth(-FLT_MIN);
					if (ImGui::DragFloat("##hold", &k.hold, 0.05f, 0.f, 600.f, "%.1f", ImGuiSliderFlags_AlwaysClamp)) keysEdited = true;
					ImGui::TableNextColumn();
					ImGui::SetNextItemWidth(-FLT_MIN);
					if (ImGui::DragFloat("##travel", &k.travel, 0.05f, kMinTravel, 600.f, "%.1f", ImGuiSliderFlags_AlwaysClamp)) keysEdited = true;
					ImGui::TableNextColumn();
					if (ComboNames("##ease", k.ease, EaseCount, EaseName, -FLT_MIN)) keysEdited = true;
					ImGui::TableNextColumn();
					ImGui::PushStyleVar(ImGuiStyleVar_ItemSpacing, ImVec2(3.f, ImGui::GetStyle().ItemSpacing.y));
					if (ImGui::SmallButton(_TR("Go"))) post(Request::Goto, i);
					if (ImGui::IsItemHovered()) ImGui::SetTooltip("%s", _TR("Put the camera on this keyframe (a short flight when the character is carried)"));
					ImGui::SameLine();
					if (ImGui::SmallButton("+")) post(Request::InsertAfter, i);
					if (ImGui::IsItemHovered()) ImGui::SetTooltip("%s", _TR("Insert a keyframe after this one (from the camera, else a copy)"));
					ImGui::SameLine();
					if (ImGui::SmallButton("^")) moveUp = i;
					ImGui::SameLine();
					if (ImGui::SmallButton("v")) moveDown = i;
					ImGui::SameLine();
					if (ImGui::SmallButton("x")) remove = i;
					if (ImGui::IsItemHovered()) ImGui::SetTooltip("%s", _TR("Delete this keyframe"));
					ImGui::PopStyleVar();
					ImGui::PopID();
				}
				ImGui::EndTable();

				// Edits of the list itself happen on the copy; the write-back at the end applies them. The
				// selection stays on the same keyframe through a move or a deletion above it.
				if (moveUp > 0)
				{
					std::swap(keys[moveUp], keys[moveUp - 1]); keysEdited = true;
					if (selected == moveUp) { selected--; selectedChanged = true; }
					else if (selected == moveUp - 1) { selected++; selectedChanged = true; }
				}
				if (moveDown >= 0 && moveDown < n - 1)
				{
					std::swap(keys[moveDown], keys[moveDown + 1]); keysEdited = true;
					if (selected == moveDown) { selected++; selectedChanged = true; }
					else if (selected == moveDown + 1) { selected--; selectedChanged = true; }
				}
				if (remove >= 0)
				{
					keys.erase(keys.begin() + remove); keysEdited = true;
					if (selected == remove) { selected = -1; selectedChanged = true; }
					else if (selected > remove) { selected--; selectedChanged = true; }
				}
			}

			// The selected keyframe, by the numbers.
			if (selected >= 0 && selected < (int)keys.size())
			{
				auto& k = keys[selected];
				ImGui::PushID("selected");
				ImGui::Text("%s %d", _TR("Keyframe"), selected + 1);
				ImGui::SameLine();
				if (ImGui::SmallButton(_TR("Set from camera"))) post(Request::Update, selected);
				if (ImGui::IsItemHovered()) ImGui::SetTooltip("%s", _TR("Re-record this keyframe from where the camera is now (its hold, time and ease stay)"));
				ImGui::SameLine();
				HelpMarker(_TR("The recorded pose, editable. Position is absolute (the world's own coordinates); pitch looks down\n"
					"for positive values, yaw turns right, roll tilts. Go shows the result."));
				float pos[3] = { k.pose.position.x, k.pose.position.y, k.pose.position.z };
				ImGui::SetNextItemWidth(-120.f);
				if (ImGui::DragFloat3(_TR("Position"), pos, 0.1f, 0.f, 0.f, "%.2f") && finite3(pos)) { k.pose.position = { pos[0], pos[1], pos[2] }; keysEdited = true; }
				float rot[3] = { k.pose.pitch, k.pose.yaw, k.pose.roll };
				ImGui::SetNextItemWidth(-120.f);
				if (ImGui::DragFloat3(_TR("Pitch/Yaw/Roll"), rot, 0.2f, 0.f, 0.f, "%.1f") && finite3(rot)) { k.pose.pitch = rot[0]; k.pose.yaw = rot[1]; k.pose.roll = rot[2]; Canonical(k.pose); keysEdited = true; }
				ImGui::SetNextItemWidth(120.f);
				if (ImGui::DragFloat(_TR("FOV"), &k.pose.fov, 0.1f, 1.f, 179.f, "%.1f", ImGuiSliderFlags_AlwaysClamp)) keysEdited = true;
				ImGui::PopID();
			}

			if (ImGui::Button(_TR("Even out times")))
				post(Request::DistributeTime);
			if (ImGui::IsItemHovered())
				ImGui::SetTooltip("%s", _TR("Gives every move a time in proportion to its length, keeping the total -\nan even flight through the whole path. Pans in place keep their own time."));
			ImGui::SameLine();
			if (ImGui::Button(_TR("Remove last")))
				post(Request::RemoveLast);
			ImGui::SameLine();
			if (ImGui::Button(_TR("Clear all")) && ImGui::GetIO().KeyCtrl)
				post(Request::ClearAll);
			if (ImGui::IsItemHovered())
				ImGui::SetTooltip("%s", _TR("Hold Ctrl and click to remove every keyframe"));
			ImGui::EndDisabled();
		}

		// ---- how it plays
		if (ImGui::BeginGroupPanel(_TR("Playback"), true))
		{
			// Read live by the tick (the curve, the run walk, the aim): locked for the take, like the keyframes.
			ImGui::BeginDisabled(playing);
			ConfigWidget(_TR("Stop at every keyframe"), f_StopAtKeyframes,
				_TR("On: every move eases into a stop at its keyframe (a series of shots).\n"
					"Off: keyframes without a hold are flown through without slowing - one continuous move\n"
					"from hold to hold, eased only at its ends (a pan in place is always its own move).\n"
					"Use Even out times for an even speed."));
			ConfigWidget(_TR("Smooth curve"), f_Curved,
				_TR("On: a smooth curve through the keyframes; the turns follow suit.\nOff: straight lines and plain turns."));
			ComboField(_TR("Repeat"), f_Repeat, RepeatCount, RepeatName, 150.f,
				_TR("Once: stop at the last keyframe.\nLoop: return to the first keyframe (its time is the last keyframe's) and go on.\nBack and forth: play it backwards to the start, and again."));
			ConfigWidget(_TR("Aim along the path"), f_AimAlongPath,
				_TR("Looks where the camera is going instead of at the recorded angles (roll and FOV still follow the keyframes).\nDuring a hold the camera turns towards the next move; a hold of 0 turns at once."));
			ImGui::EndDisabled();
			ImGui::SetNextItemWidth(90.f);
			ConfigWidget(_TR("Speed"), f_Speed, 0.05f, 0.1f, 10.f, _TR("Plays the whole path faster or slower than its times say (not the countdown)."));
			ImGui::SetNextItemWidth(90.f);
			ConfigWidget(_TR("Lead-in (s)"), f_LeadIn, 0.5f, 0.f, 60.f,
				_TR("Flies from wherever the camera is to the first keyframe in this many seconds.\n0 = start on the first keyframe (with the character carried the flight is always long enough\nfor it to follow: a jump would leave it behind)."));
			ImGui::TextDisabled("%s", _TR("These are saved with a named path and restored when it is loaded."));
		}
		ImGui::EndGroupPanel();

		if (ImGui::BeginGroupPanel(_TR("Recording"), true))
		{
			ImGui::SetNextItemWidth(90.f);
			ConfigWidget(_TR("Countdown (s)"), f_Countdown, 0.5f, 0.f, 60.f,
				_TR("Real seconds between Play and the first move - time to start the recording. Counted down on screen."));
			ConfigWidget(_TR("Hide the game UI while playing"), f_HideUI,
				_TR("Switches Hide UI on for the playback and back to how it was after."));
			ConfigWidget(_TR("Hide the menu's overlays while playing"), f_QuietOverlay,
				_TR("Nothing of this menu on screen during the shot: no status pane, info pane, FPS or notifications."));
			ConfigWidget(_TR("Follow game time"), f_GameTime,
				_TR("The camera's clock follows the game's time scale: slow motion slows the move too.\nOff: real time, whatever the game does. The countdown is real time either way."));
			ConfigWidget(_TR("Show the path on screen"), f_ShowPath,
				_TR("Draws the keyframes and the path while the free camera is on and nothing plays. Never during a playback."));
		}
		ImGui::EndGroupPanel();

		if (ImGui::BeginGroupPanel(_TR("New keyframes"), true))
		{
			ImGui::SetNextItemWidth(90.f);
			ConfigWidget(_TR("Time to next (s)"), f_DefaultTravel, 0.1f, kMinTravel, 600.f);
			ImGui::SetNextItemWidth(90.f);
			ConfigWidget(_TR("Hold (s)"), f_DefaultHold, 0.1f, 0.f, 600.f);
			ComboField(_TR("Ease"), f_DefaultEase, EaseCount, EaseName, 150.f);
			ImGui::Spacing();
			ImGui::BeginDisabled(playing);
			ImGui::SetNextItemWidth(70.f);
			ConfigWidget(_TR("Orbit keyframes"), f_OrbitKeys, 1, 3, 36);
			ImGui::SameLine();
			ImGui::SetNextItemWidth(70.f);
			ConfigWidget(_TR("at (m)"), f_OrbitDistance, 0.5f, 0.5f, 500.f);
			ImGui::SameLine();
			if (ImGui::Button(_TR("Orbit")) && (n == 0 || ImGui::GetIO().KeyCtrl))
				post(Request::Orbit);
			if (ImGui::IsItemHovered())
				ImGui::SetTooltip("%s", _TR("Replaces the path with a circle of keyframes around the point the camera looks at,\n"
					"this far ahead, at the camera's height, each looking at that point - a looped, smooth,\n"
					"continuous orbit starting where the camera is. Hold Ctrl to replace an existing path."));
			ImGui::EndDisabled();
		}
		ImGui::EndGroupPanel();

		// ---- files
		if (ImGui::BeginGroupPanel(_TR("Saved paths"), true))
		{
			// The file system decides what "exists" (NTFS is case-insensitive; a list compare is not, and 'Shot'
			// would replace 'shot.json' without Ctrl): probe the very path Save would rename onto. One stat per frame,
			// only while this panel is open and the name is valid.
			bool nameTaken = false;
			if (ValidName(s_SaveName))
			{
				std::error_code ec;
				nameTaken = std::filesystem::exists(NamePath(m_Dir, s_SaveName, ".json"), ec);
			}
			ImGui::SetNextItemWidth(170.f);
			ImGui::InputText(_TR("Name"), &s_SaveName);
			ImGui::SameLine();
			ImGui::BeginDisabled(playing || n == 0 || !ValidName(s_SaveName));
			if (ImGui::Button(_TR("Save")) && (!nameTaken || ImGui::GetIO().KeyCtrl))
				post(Request::Save, -1, s_SaveName);
			ImGui::EndDisabled();
			if (ImGui::IsItemHovered())
				ImGui::SetTooltip("%s", nameTaken ? _TR("A path of that name exists: hold Ctrl and click to replace it") : _TR("Saves the keyframes and the playback settings under this name"));
			if (!loadedName.empty())
				ImGui::TextDisabled("%s: %s", _TR("Current"), loadedName.c_str());

			int fileIndex = -1;
			for (int i = 0; i < (int)files.size(); ++i)
				if (files[i] == s_FileSel) { fileIndex = i; break; }
			const char* preview = fileIndex >= 0 ? files[fileIndex].c_str() : "";
			ImGui::SetNextItemWidth(170.f);
			if (ImGui::BeginCombo("##files", preview))
			{
				for (int i = 0; i < (int)files.size(); ++i)
				{
					if (ImGui::Selectable(files[i].c_str(), i == fileIndex))
						s_FileSel = files[i];
				}
				ImGui::EndCombo();
			}
			ImGui::SameLine();
			const bool haveFile = fileIndex >= 0;
			ImGui::BeginDisabled(playing || !haveFile);
			if (ImGui::Button(_TR("Load")))
			{
				post(Request::Load, -1, files[fileIndex]);
				s_SaveName = files[fileIndex];
			}
			ImGui::SameLine();
			if (ImGui::Button(_TR("Delete")) && ImGui::GetIO().KeyCtrl)
			{
				post(Request::Delete, -1, files[fileIndex]);
				s_FileSel.clear();
			}
			if (ImGui::IsItemHovered())
				ImGui::SetTooltip("%s", _TR("Hold Ctrl and click to delete the file"));
			ImGui::EndDisabled();
			if (ImGui::Button(_TR("Refresh")))
				m_FilesStale = true;
			ImGui::SameLine();
			if (ImGui::Button(_TR("Open folder")))
			{
				std::error_code ec;
				std::filesystem::create_directories(m_Dir, ec);
				ShellExecuteW(NULL, L"open", m_Dir.wstring().c_str(), NULL, NULL, SW_SHOW);
			}
			ImGui::TextDisabled("%s", _TR("The keyframes are also kept between sessions on their own (campaths/_autosave.json)."));
		}
		ImGui::EndGroupPanel();

		if (ImGui::BeginGroupPanel(_TR("Hotkeys"), true))
		{
			ConfigWidget(_TR("Add keyframe"), f_AddKey, true, _TR("Only while the free camera is on."));
			ConfigWidget(_TR("Play / Stop"), f_PlayKey, true, _TR("From anywhere: turns the free camera on if it is off."));
			ConfigWidget(_TR("Go to start"), f_GoStartKey, true);
			ConfigWidget(_TR("Remove last keyframe"), f_RemoveLastKey, true);
			ImGui::TextDisabled("%s", _TR("They work while this menu is closed (like every feature toggle)."));
		}
		ImGui::EndGroupPanel();

		// ---- write the edits back, unless the keyframes changed under us (a hotkey capture this very frame)
		{
			std::lock_guard<std::mutex> lock(m_Lock);
			const bool same = m_KeysVersion == version;
			if (keysEdited && same && m_Play.phase == Phase::Idle)
			{
				m_Keys = std::move(keys);
				Touched();
			}
			if (selectedChanged && same)
				m_Selected = selected;
		}
		for (auto& p : toPost)
			Post(p.kind, p.index, std::move(p.name));
		if (m_FilesStale)
		{
			m_FilesStale = false;
			RefreshFiles();
		}
	}
}
