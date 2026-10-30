#include "pch-il2cpp.h"
#include "DevTest.h"

#if RELIC_DEVTEST

#include "DevTestCapture.h"
#include "DevTestChannel.h"
#include "DevTestUi.h"

#include <helpers.h>
#include <cheat/events.h>
#include <cheat/game/EntityManager.h>
#include <cheat/game/util.h>
#include <cheat/teleport/MapTeleport.h>
#include <cheat/visuals/FreeCamera.h>
#include <cheat-base/relic-diag.h>
#include <cheat-base/relic-guard.h>
#include <cheat-base/render/backend/dx11-hook.h>

#include <algorithm>
#include <cmath>
#include <vector>

namespace cheat::feature
{
	// The dev-only test bed: a driver on this PC (build/devtest_driver.py) controls the running game and reads it,
	// in-process, through the game's own code. Never in a release: the whole feature is compiled only into the
	// DevTest configuration, whose DLL carries the marker every release gate refuses.
	//
	// FILES, under <mod folder>/devtest/ (<game>/ayy/anime/ee/devtest/ under Relic):
	//   cmd.json            one command written by the driver: {"id": n, "op": "...", ...}, n an integer other than 0,
	//                       at most 1 MiB. It is served once, by its id; the one that is already there when serving
	//                       begins is noted and never run (a driver takes the first fresh status.json as its leave to
	//                       write). A file that does not parse is read again at the next poll.
	//   results/<id>.json   the answer to that id, replaced whole: {"id", "op", "ok", "state": "running"|"done",
	//                       "outcome"?, "error"?, ...}. A long action answers "running" first and "done" under the
	//                       same id when it ends; every id has a file of its own, so no answer displaces another.
	//                       A cmd.json that is not a command is answered in results/0.json, once per time stamp.
	//   status.json         twice a second: the build (marker, game version, the DLL's sha256), the clocks, focus,
	//                       the place, the account, the camera, the UI switches, the actions in progress, the
	//                       overlay's counters.
	//   shots/<stem>.png    a screenshot, and shots/<stem>.json beside it (its moment and its timings).
	//   devtest.log         a line per event, appended whole from any thread but the render thread.
	// Every file is written as <file>.tmp and renamed over the old one; an answer the rename could not place yet is
	// tried again at every poll.
	//
	// THREADS. Every il2cpp call runs here on the game thread (OnGameUpdate, which the GameManager.Update detour
	// fires), under relic::Try, and this code holds no lock across one; the entities op and the avatar lookups go
	// through EntityManager, which holds its own cache lock across its il2cpp calls - on this thread only. The render
	// thread only copies a frame for a screenshot and the worker only encodes and writes it (DevTestCapture.cpp);
	// neither calls il2cpp or the mod's logger. DrawMain (the render thread) reads one note under a short lock.
	// Nothing reads input or touches a window: the game may sit in the background while a test runs. No C++
	// exception leaves a handler: the event's fault guard would count it as a fault and switch the handler off.
	//
	// ACTIONS that last (wait, shot, teleport, camera set/move/path, login, enter) are advanced every tick. While one
	// runs the scene is sampled every tick and a change ends it with the outcome "sceneChanged", unless it expects
	// one (a wait for a scene, the world or a page; a map teleport; login and enter). Nothing an action changes is
	// saved: the free camera and the feature switches are set without their changed event, and every one of them -
	// with the game's UI and the mod's quiet overlay - is put back at "camera release" / "feature restore", at quit,
	// when serving stops, and by OnWatchdog when OnGameUpdate has not run for 120 ticks (a handler the fault guard
	// silenced cannot clean up after itself).
	namespace
	{
		using namespace devtest;

		const char* const kMarker = "RELIC-DEVTEST-BUILD";
		const char* const kOps[] = { "ping", "status", "wait", "shot", "mark", "quit", "teleport", "camera", "entities",
			"avatar", "hud", "overlay", "feature", "ui.tree", "ui.click", "ui.text", "login", "enter" };

		nlohmann::json OpList()
		{
			nlohmann::json ops = nlohmann::json::array();
			for (const char* op : kOps)
				ops.push_back(op);
			return ops;
		}

		std::string Fault(const char* what)
		{
			return fmt::format("{} faulted (0x{:08X})", what, relic::g_lastGuardCode);
		}

		// ---- samples of the game, each under its own guard

		uint32_t SafeScene()
		{
			uint32_t id = 0;
			relic::Try([&]() { id = game::GetCurrentPlayerSceneID(); });
			return id;
		}

		bool SafeInGame()
		{
			bool loaded = false;
			relic::Try([&]() { loaded = game::IsInGame(); });
			return loaded;
		}

		uint32_t SafeUid()
		{
			uint32_t uid = 0;
			relic::Try([&]()
			{
				auto* account = game::GetAccountData();
				if (account != nullptr)
					uid = account->fields.userId;
			});
			return uid;
		}

		bool SafePos(app::Vector3& out)
		{
			return relic::Try([&]() { out = app::ActorUtils_GetAvatarPos(nullptr); });
		}

		nlohmann::json Vec(const app::Vector3& v)
		{
			return nlohmann::json::array({ v.x, v.y, v.z });
		}

		float Dist(const app::Vector3& a, const app::Vector3& b)
		{
			float dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
			return std::sqrt(dx * dx + dy * dy + dz * dz);
		}

		float DistXZ(const app::Vector3& a, const app::Vector3& b)
		{
			float dx = a.x - b.x, dz = a.z - b.z;
			return std::sqrt(dx * dx + dz * dz);
		}

		// ---- what finished during a tick, answered by the feature after it

		std::vector<std::pair<int64_t, nlohmann::json>> g_Finished;

		void Finish(int64_t id, nlohmann::json answer)
		{
			if (id == 0)
				return;
			answer["state"] = "done";
			g_Finished.emplace_back(id, std::move(answer));
		}

		nlohmann::json Head(int64_t id, const char* op)
		{
			return { { "id", id }, { "op", op }, { "ok", true } };
		}

		// ---- the camera: the free camera, driven through its pose interface

		enum class Ease { Linear, In, Out, InOut };

		Ease ReadEase(const nlohmann::json& j, Ease fallback)
		{
			std::string name = Arg<std::string>(j, "ease", std::string());
			if (name.empty())
				return fallback;
			if (name == "linear") return Ease::Linear;
			if (name == "in") return Ease::In;
			if (name == "out") return Ease::Out;
			if (name == "inOut") return Ease::InOut;
			throw std::invalid_argument("ease: linear, in, out or inOut");
		}

		float Eased(Ease e, float t)
		{
			t = std::clamp(t, 0.f, 1.f);
			switch (e)
			{
			case Ease::In: return t * t;
			case Ease::Out: return 1.f - (1.f - t) * (1.f - t);
			case Ease::InOut: return t * t * (3.f - 2.f * t);
			default: return t;
			}
		}

		// the shortest way from one angle to another, in degrees
		float Arc(float from, float to)
		{
			float d = std::fmod(to - from, 360.f);
			if (d > 180.f)
				d -= 360.f;
			else if (d < -180.f)
				d += 360.f;
			return d;
		}

		// A pose a command names; what it leaves out is the pose the camera starts from.
		struct PoseSpec
		{
			bool hasPos = false;
			float pos[3] = { 0, 0, 0 };
			bool has[4] = { false, false, false, false };   // pitch, yaw, roll, fov
			float value[4] = { 0, 0, 0, 0 };
		};

		PoseSpec ReadPose(const nlohmann::json& j)
		{
			if (!j.is_object())
				throw std::invalid_argument("a pose is an object: {pos: [x, y, z], pitch, yaw, roll, fov}");
			PoseSpec p;
			VecField pos = ReadVec(j, "pos", p.pos);
			if (pos == VecField::Bad || (pos == VecField::Good && !InWorld(p.pos)))
				throw std::invalid_argument(fmt::format("pos: [x, y, z] within {:g} m of the origin", WORLD_MAX));
			p.hasPos = pos == VecField::Good;
			const char* names[4] = { "pitch", "yaw", "roll", "fov" };
			for (int i = 0; i < 4; i++)
			{
				auto it = j.find(names[i]);
				if (it == j.end() || it->is_null())
					continue;
				if (!it->is_number())
					throw std::invalid_argument(fmt::format("{}: a number", names[i]));
				double v = it->get<double>();
				if (!(std::fabs(v) <= 1.0e6) || (i == 3 && !(v > 0.5 && v < 179.5)))
					throw std::invalid_argument(i == 3 ? "fov: degrees between 0.5 and 179.5" : fmt::format("{}: degrees", names[i]));
				p.has[i] = true;
				p.value[i] = static_cast<float>(v);
			}
			return p;
		}

		FreeCameraPose Apply(const PoseSpec& s, const FreeCameraPose& base)
		{
			FreeCameraPose p = base;
			if (s.hasPos)
				p.position = app::Vector3{ s.pos[0], s.pos[1], s.pos[2] };
			if (s.has[0]) p.pitch = s.value[0];
			if (s.has[1]) p.yaw = s.value[1];
			if (s.has[2]) p.roll = s.value[2];
			if (s.has[3]) p.fov = s.value[3];
			return p;
		}

		FreeCameraPose Lerp(const FreeCameraPose& a, const FreeCameraPose& b, float e)
		{
			FreeCameraPose p;
			p.position = app::Vector3{ a.position.x + (b.position.x - a.position.x) * e,
				a.position.y + (b.position.y - a.position.y) * e, a.position.z + (b.position.z - a.position.z) * e };
			p.pitch = a.pitch + Arc(a.pitch, b.pitch) * e;
			p.yaw = a.yaw + Arc(a.yaw, b.yaw) * e;
			p.roll = a.roll + Arc(a.roll, b.roll) * e;
			p.fov = a.fov + (b.fov - a.fov) * e;
			return p;
		}

		nlohmann::json PoseJson(const FreeCameraPose& p)
		{
			return { { "pos", Vec(p.position) }, { "pitch", p.pitch }, { "yaw", p.yaw }, { "roll", p.roll }, { "fov", p.fov } };
		}

		struct Key
		{
			PoseSpec pose;
			double seconds = 0;   // travel from the pose before
			double hold = 0;      // then stay
			Ease ease = Ease::InOut;
		};

		enum class CamMode { Idle, Set, Move, Path };

		const char* CamModeName(CamMode m)
		{
			switch (m)
			{
			case CamMode::Set: return "set";
			case CamMode::Move: return "move";
			case CamMode::Path: return "path";
			default: return "hold";
			}
		}

		struct CameraState
		{
			bool owned = false;            // the free camera is driven from here
			bool prevEnabled = false;      // the free camera's state before
			bool carrySaved = false, prevCarry = false;
			bool driveSaved = false, prevDrive = false;
			float prevFov = 45.f;
			bool haveHold = false;
			FreeCameraPose hold{};         // pushed every tick while nothing moves
			CamMode mode = CamMode::Idle;
			int64_t id = 0;                // the command of the action in progress
			int64_t deadlineMs = 0;        // the free camera has to be running by then
			PoseSpec target;               // set / move
			double seconds = 0, elapsed = 0;
			Ease ease = Ease::InOut;
			std::vector<Key> keys;
			size_t index = 0;
			bool loop = false, holding = false;
			double holdLeft = 0;
			FreeCameraPose from{};
			bool fromSet = false;
			uint32_t scene = 0;
			int64_t lastMs = 0;
			std::string lastEnd;
		};
		CameraState g_Cam;

		void CameraEndAction(const char* outcome, bool ok, const std::string& error = std::string())
		{
			if (g_Cam.id == 0)
				return;
			nlohmann::json a = Head(g_Cam.id, "camera");
			a["ok"] = ok;
			a["outcome"] = outcome;
			a["action"] = CamModeName(g_Cam.mode);
			if (!error.empty())
				a["error"] = error;
			if (g_Cam.haveHold)
				a["pose"] = PoseJson(g_Cam.hold);
			Finish(g_Cam.id, std::move(a));
			g_Cam.id = 0;
			g_Cam.mode = CamMode::Idle;
		}

		// The free camera back as it was: the pose revoked, its own switch, the options and its FOV as before.
		// `why` is the outcome an action in progress ends with: notStarted, freeCameraOff, released, sceneChanged,
		// faulted, quit, servingStopped, watchdog.
		void CameraRelease(const char* why, bool restoreEnable = true)
		{
			if (!g_Cam.owned)
				return;
			CameraEndAction(why, false, std::string("the camera was released (") + why + ")");
			relic::Try([&]()
			{
				auto& fc = FreeCamera::GetInstance();
				fc.DropPose();
				if (restoreEnable)
					fc.f_Enabled.value().set_enabled(g_Cam.prevEnabled, false);
				if (g_Cam.carrySaved)
					fc.f_MoveAvatar.value() = g_Cam.prevCarry;
				if (g_Cam.driveSaved)
					fc.f_DriveGameCamera.value() = g_Cam.prevDrive;
				fc.f_FOV.value() = g_Cam.prevFov;
			});
			g_Cam = CameraState{};
			g_Cam.lastEnd = why;
			LogLine("game", "camera released (%s)", why);
		}

		void CameraAdvance()
		{
			if (!g_Cam.owned)
				return;
			auto& fc = FreeCamera::GetInstance();
			if (!fc.f_Enabled.value().enabled())
			{
				CameraRelease("freeCameraOff", false);
				return;
			}
			const int64_t now = AwakeMs();
			float dt = g_Cam.lastMs > 0 ? static_cast<float>(now - g_Cam.lastMs) / 1000.f : 0.f;
			dt = std::clamp(dt, 0.f, 0.1f);   // a hitch must not skip a segment
			g_Cam.lastMs = now;
			if (!fc.IsRunning())
			{
				// not seated (a fresh enable, a loading screen): no push, the clock waits
				if (g_Cam.id != 0 && now > g_Cam.deadlineMs)
					CameraRelease("notStarted");
				return;
			}
			FreeCameraPose here{};
			fc.GetPose(here);
			switch (g_Cam.mode)
			{
			case CamMode::Set:
			{
				g_Cam.hold = Apply(g_Cam.target, g_Cam.haveHold ? g_Cam.hold : here);
				g_Cam.haveHold = true;
				fc.PushPose(g_Cam.hold);
				CameraEndAction("set", true);
				return;
			}
			case CamMode::Move:
			{
				if (!g_Cam.fromSet)
				{
					g_Cam.from = g_Cam.haveHold ? g_Cam.hold : here;
					g_Cam.fromSet = true;
					g_Cam.elapsed = 0;
				}
				FreeCameraPose to = Apply(g_Cam.target, g_Cam.from);
				g_Cam.elapsed += dt;
				float t = g_Cam.seconds > 0 ? static_cast<float>(g_Cam.elapsed / g_Cam.seconds) : 1.f;
				FreeCameraPose pose = Lerp(g_Cam.from, to, Eased(g_Cam.ease, t));
				fc.PushPose(pose);
				g_Cam.hold = pose;
				g_Cam.haveHold = true;
				if (t >= 1.f)
					CameraEndAction("arrived", true);
				return;
			}
			case CamMode::Path:
			{
				if (!g_Cam.fromSet)
				{
					g_Cam.from = g_Cam.haveHold ? g_Cam.hold : here;
					g_Cam.fromSet = true;
					g_Cam.index = 0;
					g_Cam.elapsed = 0;
					g_Cam.holding = false;
				}
				const Key& k = g_Cam.keys[g_Cam.index];
				if (!g_Cam.holding)
				{
					FreeCameraPose to = Apply(k.pose, g_Cam.from);
					g_Cam.elapsed += dt;
					float t = k.seconds > 0 ? static_cast<float>(g_Cam.elapsed / k.seconds) : 1.f;
					FreeCameraPose pose = Lerp(g_Cam.from, to, Eased(k.ease, t));
					fc.PushPose(pose);
					g_Cam.hold = pose;
					g_Cam.haveHold = true;
					if (t >= 1.f)
					{
						g_Cam.holding = true;
						g_Cam.holdLeft = k.hold;
						g_Cam.from = pose;   // the next segment starts from this key as the camera spells it
					}
					return;
				}
				fc.PushPose(g_Cam.hold);
				g_Cam.holdLeft -= dt;
				if (g_Cam.holdLeft > 0)
					return;
				g_Cam.holding = false;
				g_Cam.elapsed = 0;
				if (++g_Cam.index >= g_Cam.keys.size())
				{
					if (g_Cam.loop)
						g_Cam.index = 0;
					else
						CameraEndAction("finished", true);
				}
				return;
			}
			default:
				if (g_Cam.haveHold)
					fc.PushPose(g_Cam.hold);
				return;
			}
		}

		// ---- the other long actions

		struct WaitAction
		{
			bool active = false;
			int64_t id = 0;
			int inGame = -1;               // -1: not asked
			int64_t scene = -1, sceneNot = -1;
			bool uidSet = false;
			std::string page;
			int64_t frames = -1;
			double seconds = -1;
			double moved = -1;
			int64_t startMs = 0, deadlineMs = 0, startTicks = 0, pageCheckMs = 0;
			int pageState = 0;
			app::Vector3 startPos{};
			bool havePos = false;
			uint32_t scene0 = 0;
			bool expectsScene = false;
		};
		WaitAction g_Wait;

		struct ShotAction
		{
			bool active = false;
			int64_t id = 0;
			int64_t deadlineMs = 0;
			bool hidHud = false;
			std::string file, sidecar;
			bool overlay = false;
			uint32_t scene0 = 0;
			std::string ending;            // set when the shot is to end without a picture (cancel pending)
		};
		ShotAction g_Shot;

		struct TeleportAction
		{
			bool active = false;
			int64_t id = 0;
			app::Vector3 target{};
			bool viaMap = false;
			float arrive = 12.f;
			double hold = 0;
			int64_t deadlineMs = 0, arrivedMs = 0, lastReapplyMs = 0;
			int insideTicks = 0, reapplied = 0, left = 0;
			uint32_t scene0 = 0;
		};
		TeleportAction g_Teleport;

		struct LoginAction
		{
			bool active = false;
			int64_t id = 0;
			bool enter = false;            // the door page; else the SDK form
			int64_t deadlineMs = 0;
			nlohmann::json found;
			// The door page takes a press while it is still loading its bundles and does nothing with it: the enter
			// action presses again while the button can still be pressed and no world is loading.
			std::string button;
			int64_t lastPressMs = 0;
			int presses = 0;
			std::string lastRepress;       // why the last press again was not made ("" = it was made)
		};
		constexpr int64_t kEnterRepressMs = 8000;
		constexpr int kEnterMaxPresses = 8;
		LoginAction g_Login;

		bool g_QuitPending = false;
		int64_t g_QuitId = 0;
		bool g_QuietByUs = false;
		bool g_MenuByUs = false;
		uint32_t g_HudScene = 0;

		// ---- transient feature switches

		struct Original
		{
			std::string section, name, type;
			nlohmann::json value;
		};
		std::map<std::string, Original> g_Originals;

		template <class T>
		config::Field<T>* FindField(const std::string& section, const std::string& name)
		{
			for (auto& field : config::GetFields<T>())
				if (field.section() == section && field.name() == name)
					return &field;
			return nullptr;
		}

		// The value of a field of one of the kinds this op sets, as JSON; false when there is none of that name.
		bool ReadField(const std::string& section, const std::string& name, std::string& type, nlohmann::json& value)
		{
			if (auto* f = FindField<bool>(section, name)) { type = "bool"; value = f->value(); return true; }
			if (auto* f = FindField<int>(section, name)) { type = "int"; value = f->value(); return true; }
			if (auto* f = FindField<float>(section, name)) { type = "float"; value = f->value(); return true; }
			if (auto* f = FindField<TranslatedHotkey>(section, name)) { type = "toggle"; value = f->value().enabled(); return true; }
			if (auto* f = FindField<config::Toggle<Hotkey>>(section, name)) { type = "toggle"; value = f->value().enabled(); return true; }
			return false;
		}

		// Writes the value without the field's changed event, so nothing is saved.
		bool WriteField(const std::string& section, const std::string& name, const nlohmann::json& value, std::string& error)
		{
			try
			{
				if (auto* f = FindField<bool>(section, name)) { f->value() = value.get<bool>(); return true; }
				if (auto* f = FindField<int>(section, name)) { f->value() = value.get<int>(); return true; }
				if (auto* f = FindField<float>(section, name)) { f->value() = value.get<float>(); return true; }
				if (auto* f = FindField<TranslatedHotkey>(section, name)) { f->value().set_enabled(value.get<bool>(), false); return true; }
				if (auto* f = FindField<config::Toggle<Hotkey>>(section, name)) { f->value().set_enabled(value.get<bool>(), false); return true; }
			}
			catch (const nlohmann::json::exception& e)
			{
				error = Brief(std::string("value: ") + e.what());
				return false;
			}
			error = "no such field";
			return false;
		}

		int RestoreFeatures()
		{
			int restored = 0;
			for (auto& [key, original] : g_Originals)
			{
				std::string error;
				if (WriteField(original.section, original.name, original.value, error))
					restored++;
			}
			g_Originals.clear();
			return restored;
		}

		// ---- the actions' ticks

		void WaitAdvance(uint32_t scene, int64_t ticks)
		{
			WaitAction& w = g_Wait;
			const int64_t now = AwakeMs();
			if (!w.expectsScene && scene != w.scene0)
			{
				nlohmann::json a = Head(w.id, "wait");
				a["ok"] = false;
				a["outcome"] = "sceneChanged";
				a["scene"] = scene;
				Finish(w.id, std::move(a));
				w = WaitAction{};
				return;
			}
			bool met = true;
			if (w.inGame >= 0)
				met = met && SafeInGame() == (w.inGame == 1);
			if (w.scene >= 0)
				met = met && scene == static_cast<uint32_t>(w.scene);
			if (w.sceneNot >= 0)
				met = met && scene != static_cast<uint32_t>(w.sceneNot);
			if (w.uidSet)
				met = met && SafeUid() != 0;
			if (w.frames >= 0)
				met = met && ticks - w.startTicks >= w.frames;
			if (w.seconds >= 0)
				met = met && static_cast<double>(now - w.startMs) / 1000.0 >= w.seconds;
			app::Vector3 pos{};
			bool havePos = SafePos(pos);
			if (w.moved >= 0)
				met = met && w.havePos && havePos && Dist(pos, w.startPos) >= w.moved;
			if (!w.page.empty())
			{
				if (now >= w.pageCheckMs)
				{
					w.pageCheckMs = now + 250;   // GameObject.Find searches the scene: four times a second is plenty
					w.pageState = ui::PageActive(w.page);
				}
				met = met && w.pageState == 1;
			}
			if (!met && now < w.deadlineMs)
				return;
			nlohmann::json a = Head(w.id, "wait");
			a["ok"] = met;
			a["outcome"] = met ? "met" : "timeout";
			a["waitedMs"] = now - w.startMs;
			a["scene"] = scene;
			a["inGame"] = SafeInGame();
			a["uid"] = SafeUid();
			if (havePos)
				a["pos"] = Vec(pos);
			if (!met)
				a["error"] = "the condition was not met in time";
			Finish(w.id, std::move(a));
			w = WaitAction{};
		}

		void ShotEnd(nlohmann::json a)
		{
			if (g_Shot.hidHud)
				ui::RestoreHud();
			Finish(g_Shot.id, std::move(a));
			g_Shot = ShotAction{};
		}

		void ShotAdvance(uint32_t scene)
		{
			ShotAction& s = g_Shot;
			const int64_t now = AwakeMs();
			if (s.ending.empty() && scene != s.scene0)
				s.ending = "sceneChanged";
			capture::Phase phase = capture::Poll();
			if (phase == capture::Phase::Done || phase == capture::Phase::Failed)
			{
				capture::Result r;
				capture::Take(r);
				nlohmann::json a = Head(s.id, "shot");
				a["width"] = r.width;
				a["height"] = r.height;
				a["format"] = r.format;
				a["samples"] = r.samples;
				a["copyUs"] = r.copyUs;
				a["mapWaitFrames"] = r.mapWaitFrames;
				a["memcpyUs"] = r.memcpyUs;
				a["encodeMs"] = r.encodeMs;
				a["overlay"] = s.overlay;
				if (r.error.empty() && s.ending.empty())
				{
					a["outcome"] = "saved";
					a["file"] = s.file;
					a["sidecar"] = s.sidecar;
					a["bytes"] = r.bytes;
				}
				else
				{
					a["ok"] = false;
					a["outcome"] = s.ending.empty() ? "failed" : s.ending;
					a["error"] = r.error.empty() ? std::string("ended: ") + s.ending : r.error;
				}
				ShotEnd(std::move(a));
				return;
			}
			if (s.ending.empty())
			{
				if (phase == capture::Phase::Waiting && now > s.deadlineMs)
					s.ending = "noFrame";
				else if (phase == capture::Phase::Copied && now > s.deadlineMs + 20000)
					s.ending = "failed";
				else if (phase == capture::Phase::Idle)
					s.ending = "failed";
			}
			if (s.ending.empty())
				return;
			if (!capture::Cancel())
				return;   // another thread holds the job for the moment: at the next tick
			nlohmann::json a = Head(s.id, "shot");
			a["ok"] = false;
			a["outcome"] = s.ending;
			a["error"] = s.ending == "noFrame" ? "no frame was presented within the time-out (is the game minimised?)"
				: s.ending == "sceneChanged" ? "the scene changed before the frame was taken"
				: "the frame was not written in time";
			ShotEnd(std::move(a));
		}

		void TeleportAdvance(uint32_t scene)
		{
			TeleportAction& t = g_Teleport;
			const int64_t now = AwakeMs();
			if (!t.viaMap && scene != t.scene0)
			{
				nlohmann::json a = Head(t.id, "teleport");
				a["ok"] = false;
				a["outcome"] = "sceneChanged";
				a["scene"] = scene;
				Finish(t.id, std::move(a));
				t = TeleportAction{};
				return;
			}
			app::Vector3 pos{};
			bool havePos = SafePos(pos) && SafeInGame();
			float d = havePos ? DistXZ(pos, t.target) : -1.f;
			if (t.arrivedMs == 0)
			{
				t.insideTicks = havePos && d <= t.arrive ? t.insideTicks + 1 : 0;
				if (t.insideTicks >= 2)
					t.arrivedMs = now;   // decided here, two ticks in a row within `arrive` on x and z
			}
			else if (havePos && d > t.arrive)
			{
				// the server pulled the character back while it was held there
				t.left++;
				if (!t.viaMap && now - t.lastReapplyMs >= 1000)
				{
					t.lastReapplyMs = now;
					relic::Try([&]()
					{
						auto avatar = game::EntityManager::instance().avatar();
						if (avatar != nullptr && avatar->raw() != nullptr)
						{
							avatar->setAbsolutePosition(t.target);
							t.reapplied++;
						}
					});
				}
			}
			const bool held = t.arrivedMs != 0 && static_cast<double>(now - t.arrivedMs) / 1000.0 >= t.hold;
			if (!held && now < t.deadlineMs)
				return;
			nlohmann::json a = Head(t.id, "teleport");
			a["via"] = t.viaMap ? "map" : "direct";
			a["ok"] = held;
			a["outcome"] = held ? "arrived" : "timeout";
			if (havePos)
			{
				a["pos"] = Vec(pos);
				a["distance"] = d;
			}
			a["reapplied"] = t.reapplied;
			a["leftTicks"] = t.left;
			if (!held)
				a["error"] = t.arrivedMs == 0 ? "the character did not arrive in time" : "the character did not stay";
			Finish(t.id, std::move(a));
			t = TeleportAction{};
		}

		void LoginAdvance(uint32_t scene)
		{
			LoginAction& l = g_Login;
			const int64_t now = AwakeMs();
			bool done = false;
			nlohmann::json a = Head(l.id, l.enter ? "enter" : "login");
			if (l.enter)
			{
				if (SafeInGame() && scene != 0)
				{
					done = true;
					a["outcome"] = "entered";
				}
				else if (scene == 0 && now - l.lastPressMs >= kEnterRepressMs && l.presses < kEnterMaxPresses)
				{
					// A press the page did not act on leaves the button there and pressable; one it acted on hides it
					// (the press below then refuses: the page is gone, or the button inactive).
					l.lastPressMs = now;
					nlohmann::json again = { { "ok", true } };
					ui::EnterStart(l.button, again);
					if (again.value("ok", false))
					{
						l.presses++;
						l.lastRepress.clear();
						LOG_DEBUG("[devtest] enter: the door page is still there - pressed again (%d)", l.presses);
					}
					else
						l.lastRepress = again.value("error", std::string("refused"));
				}
			}
			else if (ui::LoginFormOpen() == 0)
			{
				done = true;
				a["outcome"] = "formClosed";
			}
			if (!done && now < l.deadlineMs)
				return;
			if (!done)
			{
				a["ok"] = false;
				a["outcome"] = "timeout";
				a["error"] = l.enter ? "the world did not load in time" : "the login form did not close in time";
			}
			a["found"] = l.found;
			if (l.enter)
			{
				a["presses"] = l.presses;
				if (!l.lastRepress.empty())
					a["lastRepress"] = l.lastRepress;
			}
			a["scene"] = scene;
			a["uid"] = SafeUid();
			Finish(l.id, std::move(a));
			l = LoginAction{};
		}

		// Ends every action and puts back everything this feature changed.
		void EndActions(const char* why)
		{
			auto failed = [&](int64_t id, const char* op)
			{
				nlohmann::json a = Head(id, op);
				a["ok"] = false;
				a["outcome"] = why;
				a["error"] = std::string("ended: ") + why;
				Finish(id, std::move(a));
			};
			if (g_Wait.active)
				failed(g_Wait.id, "wait");
			g_Wait = WaitAction{};
			if (g_Teleport.active)
				failed(g_Teleport.id, "teleport");
			g_Teleport = TeleportAction{};
			if (g_Login.active)
				failed(g_Login.id, g_Login.enter ? "enter" : "login");
			g_Login = LoginAction{};
			if (g_Shot.active)
			{
				capture::Cancel();   // best effort: a job a thread holds ends on its own and is dropped at the next shot
				if (g_Shot.hidHud)
					ui::RestoreHud();
				failed(g_Shot.id, "shot");
			}
			g_Shot = ShotAction{};
			CameraRelease(why);
		}
	}

	DevTest::DevTest() : Feature(),
		NF(f_Enabled, "Debug::DevTest", true),
		m_Dir(util::GetCurrentPath() / "devtest")
	{
		std::error_code ec;
		std::filesystem::create_directories(m_Dir, ec);
		SetLogPath(m_Dir / "devtest.log");
		LogLine("game", "DevTest loaded: game version %d, folder %s", RELIC_GAME_VERSION, Utf8(m_Dir).c_str());
		// Never FreeCamera::GetInstance() or CameraPath::GetInstance() here: constructing either first would put
		// its handlers ahead of these, and the pose pushed below has to reach the free camera in the same tick.
		events::GameUpdateEvent += MY_METHOD_HANDLER(DevTest::OnGameUpdate);
		// Its own subscription, its own fault budget: it puts everything back if OnGameUpdate is ever silenced.
		events::GameUpdateEvent += MY_METHOD_HANDLER(DevTest::OnWatchdog);
		backend::DX11Events::DevPresentEvent += FUNCTION_HANDLER(devtest::capture::OnPresent);
		devtest::capture::Start();
	}

	const FeatureGUIInfo& DevTest::GetGUIInfo() const
	{
		TRANSLATED_GROUP_INFO("Dev Test", "Debug");
		return info;
	}

	DevTest& DevTest::GetInstance()
	{
		static DevTest instance;
		return instance;
	}

	void DevTest::Note(const std::string& text)
	{
		std::lock_guard<std::mutex> lock(m_NoteMutex);
		m_Note = text;
	}

	// ------------------------------------------------------------------------------------------------ UI (render thread)

	void DevTest::DrawMain()
	{
		ConfigWidget(_TR("Serve commands"), f_Enabled,
			_TR("The dev-only test bed of this DevTest build: devtest/cmd.json next to the mod is read and answered in devtest/results/<id>.json, devtest/status.json is written twice a second. A command that is already in the folder when this is ticked is not run."));
		ImGui::TextWrapped("%s", (_TR("Folder: ") + Utf8(m_Dir)).c_str());
		std::string note;
		{
			std::lock_guard<std::mutex> lock(m_NoteMutex);
			note = m_Note;
		}
		if (!note.empty())
			ImGui::TextWrapped("%s", note.c_str());
	}

	// ------------------------------------------------------------------------------------------------ game thread

	void DevTest::OnGameUpdate()
	{
		m_Silence = 0;   // proof of life for OnWatchdog
		m_Ticks++;
		try
		{
			// the long actions, every tick: the camera's push is consumed by the free camera later in this tick
			const bool active = g_Wait.active || g_Shot.active || g_Teleport.active || g_Login.active || g_Cam.owned || ui::HudHidden();
			if (active)
			{
				const uint32_t scene = SafeScene();
				if (g_Wait.active)
					WaitAdvance(scene, m_Ticks);
				if (g_Shot.active)
					ShotAdvance(scene);
				if (g_Teleport.active)
					TeleportAdvance(scene);
				if (g_Login.active)
					LoginAdvance(scene);
				if (g_Cam.owned && scene != g_Cam.scene)
					CameraRelease("sceneChanged");
				CameraAdvance();
				if (ui::HudHidden() && !g_Shot.active && scene != g_HudScene)
				{
					ui::RestoreHud();
					LogLine("game", "the game's UI is shown again: the scene changed");
				}
			}
			if (g_QuitPending)
			{
				g_QuitPending = false;
				EndActions("quit");
				RestoreAll("quit");
				AnswerFinished();
				std::string error;
				LogLine("game", "quitting through Application.Quit (command %lld)", static_cast<long long>(g_QuitId));
				if (!ui::Quit(error))
				{
					LOG_WARNING("[devtest] quit: %.300s", error.c_str());
					nlohmann::json a = Head(g_QuitId, "quit");
					a["ok"] = false;
					a["outcome"] = "failed";
					a["error"] = error;
					Finish(g_QuitId, std::move(a));
				}
			}
		}
		catch (const std::exception& e)
		{
			LOG_WARNING("[devtest] tick: %.300s", e.what());
		}
		try
		{
			AnswerFinished();
			Poll();
		}
		catch (const std::exception& e)
		{
			LOG_WARNING("[devtest] poll: %.300s", e.what());
		}
	}

	// The recovery for everything this feature changes. If OnGameUpdate is silenced by the fault guard, nothing
	// else would put the camera, the game's UI, the overlay and the switches back. This handler has its own fault
	// budget and does nothing but count while OnGameUpdate is alive.
	void DevTest::OnWatchdog()
	{
		if (++m_Silence <= 120)
			return;
		m_Silence = 0;   // and again in about 2 s if something could not be reached
		const bool armed = g_Cam.owned || ui::HudHidden() || g_QuietByUs || g_MenuByUs || !g_Originals.empty()
			|| g_Shot.active || g_Wait.active || g_Teleport.active || g_Login.active;
		if (!armed)
			return;
		LOG_WARNING("[devtest] the command handler has not run for 120 ticks - putting the camera, the UI and the switches back.");
		LogLine("game", "watchdog: the command handler has not run for 120 ticks");
		try
		{
			EndActions("watchdog");
			RestoreAll("watchdog");
			AnswerFinished();
			FlushAnswers();
		}
		catch (const std::exception& e)
		{
			LOG_WARNING("[devtest] watchdog: %.300s", e.what());
		}
	}

	void DevTest::RestoreAll(const char* why)
	{
		CameraRelease(why);
		ui::RestoreHud();
		if (g_QuietByUs)
		{
			CheatManagerBase::SetOverlayQuiet(false);
			g_QuietByUs = false;
		}
		if (g_MenuByUs)
		{
			CheatManagerBase::RequestHideMenu();
			g_MenuByUs = false;
		}
		int restored = RestoreFeatures();
		LogLine("game", "put back (%s): camera, UI, overlay, %d feature switch(es)", why, restored);
	}

	void DevTest::Poll()
	{
		const int64_t now = NowMs();
		if (now - m_LastPollMs < 500)
			return;
		m_LastPollMs = now;
		FlushAnswers();
		if (!f_Enabled)
		{
			if (m_Serving)
			{
				m_Serving = false;
				EndActions("servingStopped");
				RestoreAll("servingStopped");
				AnswerFinished();
				LogLine("game", "serving stopped");
				Note("Not serving.");
			}
			return;
		}
		std::error_code ec;
		std::filesystem::create_directories(m_Dir / "results", ec);
		std::filesystem::create_directories(m_Dir / "shots", ec);
		if (!m_Serving)
		{
			NoteStaleCommand();   // before the first status: a fresh status.json is a driver's leave to write
			m_Serving = true;
			LogLine("game", "serving %s (the command already there: %lld)", Utf8(m_Dir).c_str(), static_cast<long long>(m_LastCommandId));
			Note("Serving.");
		}
		WriteStatus();
		ServeCommands();
	}

	void DevTest::NoteStaleCommand()
	{
		std::filesystem::path cmdPath = m_Dir / "cmd.json";
		std::error_code ec;
		auto stamp = std::filesystem::last_write_time(cmdPath, ec);
		if (ec)
			return;   // no file: the first one written is a command to serve
		m_ReportedStamp = stamp;
		nlohmann::json cmd;
		std::string problem;
		if (ReadCommand(cmdPath, cmd, problem) == CommandFile::Ready)
			m_LastCommandId = cmd["id"].get<int64_t>();
	}

	void DevTest::ServeCommands()
	{
		std::filesystem::path cmdPath = m_Dir / "cmd.json";
		std::error_code ec;
		auto stamp = std::filesystem::last_write_time(cmdPath, ec);
		if (ec)
			return;
		nlohmann::json cmd;
		std::string problem;
		CommandFile state = ReadCommand(cmdPath, cmd, problem);
		if (state != CommandFile::Ready)
		{
			if (!problem.empty() && stamp != m_ReportedStamp)
			{
				m_ReportedStamp = stamp;
				LOG_WARNING("[devtest] %.300s", problem.c_str());
				Answer(0, { { "id", 0 }, { "ok", false }, { "state", "done" }, { "error", problem } });
			}
			return;
		}
		int64_t id = cmd["id"].get<int64_t>();
		if (id == m_LastCommandId)
			return;
		m_LastCommandId = id;   // before it runs: a command that faults is never run again
		// read without a throw: an op that is not a string is answered by Execute below, like any wrong field
		auto opField = cmd.find("op");
		const std::string op = opField != cmd.end() && opField->is_string() ? opField->get<std::string>() : std::string();
		LogLine("game", "command %lld: %.60s", static_cast<long long>(id), op.c_str());
		Note(fmt::format("Serving. Last command: {} ({})", id, Brief(op)));
		try
		{
			Execute(cmd);
		}
		catch (const std::exception& e)
		{
			// A field of a type the op cannot read, or an answer that could not be put together.
			LOG_WARNING("[devtest] command %lld: %.300s", static_cast<long long>(id), e.what());
			Answer(id, { { "id", id }, { "op", op }, { "ok", false }, { "state", "done" }, { "error", Brief(e.what()) } });
		}
	}

	void DevTest::Answer(int64_t id, const nlohmann::json& answer)
	{
		m_PendingAnswers[id] = Dump(answer, 2);
		m_PendingLogged = false;
		FlushAnswers();
	}

	void DevTest::FlushAnswers()
	{
		for (auto it = m_PendingAnswers.begin(); it != m_PendingAnswers.end();)
		{
			std::string error;
			if (WriteFileAtomic(m_Dir / "results" / (std::to_string(it->first) + ".json"), it->second, error))
			{
				it = m_PendingAnswers.erase(it);
				continue;
			}
			if (!m_PendingLogged)
			{
				m_PendingLogged = true;
				LOG_WARNING("[devtest] %.300s - trying again at every poll", error.c_str());
			}
			++it;
		}
	}

	void DevTest::AnswerFinished()
	{
		if (g_Finished.empty())
			return;
		auto finished = std::move(g_Finished);
		g_Finished.clear();
		for (auto& [id, answer] : finished)
		{
			LogLine("game", "command %lld done: %.40s", static_cast<long long>(id), answer.value("outcome", std::string()).c_str());
			Answer(id, answer);
		}
	}

	nlohmann::json DevTest::StatusFields()
	{
		nlohmann::json st;
		st["marker"] = kMarker;
		st["gameVersion"] = RELIC_GAME_VERSION;
		st["dllSha256"] = capture::DllSha256();
		st["seq"] = ++m_Seq;
		st["wallMs"] = WallMs();
		st["tick"] = NowMs();
		st["gameTicks"] = m_Ticks;
		std::string error;
		bool focused = false;
		if (relic::Try([&]() { focused = app::Application_get_IsFocused(nullptr); }))
			st["focused"] = focused;
		else
			st["focused"] = nullptr;
		bool background = false;
		if (ui::RunInBackground(background))
			st["runInBackground"] = background;
		else
			st["runInBackground"] = nullptr;
		st["screen"] = ui::ScreenState();   // a test that needs a window sees here whether it got one
		st["inGame"] = SafeInGame();
		uint32_t mapScene = 0;
		relic::Try([&]() { mapScene = game::GetCurrentMapSceneID(); });
		st["scene"] = SafeScene();
		st["mapScene"] = mapScene;
		app::Vector3 pos{};
		if (SafePos(pos))
			st["pos"] = Vec(pos);
		else
			error = Fault("the position lookup");
		st["uid"] = SafeUid();
		uint32_t configId = 0;
		relic::Try([&]()
		{
			auto avatar = game::EntityManager::instance().avatar();
			if (avatar != nullptr && avatar->raw() != nullptr)
				configId = avatar->raw()->fields._configID_k__BackingField;
		});
		st["avatarConfigId"] = configId;
		bool running = false;
		relic::Try([&]() { running = FreeCamera::GetInstance().IsRunning(); });
		nlohmann::json camera = { { "held", g_Cam.owned }, { "running", running }, { "action", CamModeName(g_Cam.mode) } };
		if (!g_Cam.lastEnd.empty())
			camera["lastEnd"] = g_Cam.lastEnd;
		st["camera"] = camera;
		st["hudHidden"] = ui::HudHidden();
		st["overlayQuiet"] = CheatManagerBase::IsOverlayQuiet();
		st["menu"] = CheatManagerBase::IsMenuShowed();
		nlohmann::json busy = nlohmann::json::array();
		if (g_Wait.active) busy.push_back({ { "id", g_Wait.id }, { "op", "wait" } });
		if (g_Shot.active) busy.push_back({ { "id", g_Shot.id }, { "op", "shot" } });
		if (g_Teleport.active) busy.push_back({ { "id", g_Teleport.id }, { "op", "teleport" } });
		if (g_Cam.id != 0) busy.push_back({ { "id", g_Cam.id }, { "op", "camera" } });
		if (g_Login.active) busy.push_back({ { "id", g_Login.id }, { "op", g_Login.enter ? "enter" : "login" } });
		st["busy"] = busy;
		st["lastCommandId"] = m_LastCommandId;
		auto counter = [](int stage) { return static_cast<long>(relic::diag::counters[stage]); };
		st["counters"] = {
			{ "present", counter(relic::diag::PRESENT) },
			{ "present1", counter(relic::diag::PRESENT1) },
			{ "presentVt", counter(relic::diag::PRESENT_VT) },
			{ "handlerRun", counter(relic::diag::HANDLER_RUN) },
			{ "faults", counter(relic::diag::FAULTS) },
		};
		if (!error.empty())
			st["error"] = error;
		return st;
	}

	void DevTest::WriteStatus()
	{
		std::string error;
		WriteFileAtomic(m_Dir / "status.json", Dump(StatusFields()), error);   // a failed one: the next is 500 ms away
	}

	void DevTest::Execute(const nlohmann::json& cmd)
	{
		const int64_t id = cmd["id"].get<int64_t>();
		const std::string op = Arg(cmd, "op", std::string());
		nlohmann::json a = { { "id", id }, { "op", op }, { "ok", true }, { "state", "done" } };

		if (op == "ping" || op == "status")
		{
			nlohmann::json st = StatusFields();
			for (auto it = st.begin(); it != st.end(); ++it)
				a[it.key()] = it.value();
			if (op == "ping")
			{
				a["ops"] = OpList();
				a["resolved"] = ui::Probe();
				a["dllPath"] = capture::DllPath();
				a["folder"] = Utf8(m_Dir);
			}
		}
		else if (op == "wait")
		{
			auto until = cmd.find("until");
			if (until == cmd.end() || !until->is_object())
				throw std::invalid_argument("wait needs until: {inGame, scene, sceneNot, uidSet, page, frames, seconds, moved}");
			for (auto it = until->begin(); it != until->end(); ++it)
			{
				const std::string& key = it.key();
				if (key != "inGame" && key != "scene" && key != "sceneNot" && key != "uidSet" && key != "page" && key != "frames"
					&& key != "seconds" && key != "moved")
					throw std::invalid_argument("until." + key + ": not a condition (inGame, scene, sceneNot, uidSet, page, frames, seconds, moved)");
			}
			WaitAction w;
			const nlohmann::json& u = *until;
			if (u.contains("inGame")) w.inGame = Arg<bool>(u, "inGame", false) ? 1 : 0;
			if (u.contains("scene")) w.scene = Arg<uint32_t>(u, "scene", 0);
			if (u.contains("sceneNot")) w.sceneNot = Arg<uint32_t>(u, "sceneNot", 0);
			w.uidSet = Arg<bool>(u, "uidSet", false);
			w.page = Arg<std::string>(u, "page", std::string());
			w.frames = u.contains("frames") ? std::clamp<int64_t>(Arg<int64_t>(u, "frames", 0), 0, 1000000) : -1;
			w.seconds = u.contains("seconds") ? std::clamp(Arg<double>(u, "seconds", 0.0), 0.0, 86400.0) : -1.0;
			w.moved = u.contains("moved") ? std::clamp(Arg<double>(u, "moved", 0.0), 0.0, 100000.0) : -1.0;
			double timeout = std::clamp(Arg<double>(cmd, "timeout", 60.0), 0.0, 86400.0);
			if (g_Wait.active)
			{
				nlohmann::json old = Head(g_Wait.id, "wait");
				old["ok"] = false;
				old["outcome"] = "replaced";
				Finish(g_Wait.id, std::move(old));
			}
			w.active = true;
			w.id = id;
			w.startMs = AwakeMs();
			w.deadlineMs = w.startMs + static_cast<int64_t>(timeout * 1000.0);
			w.startTicks = m_Ticks;
			w.havePos = SafePos(w.startPos);
			w.scene0 = SafeScene();
			w.expectsScene = w.inGame >= 0 || w.scene >= 0 || w.sceneNot >= 0 || w.uidSet || !w.page.empty();
			g_Wait = w;
			a["state"] = "running";
			a["timeout"] = timeout;
		}
		else if (op == "shot")
		{
			bool overlay = Arg<bool>(cmd, "overlay", false);
			std::string hud = Arg<std::string>(cmd, "hud", std::string("keep"));
			int afterFrames = std::clamp(Arg<int>(cmd, "afterFrames", 2), 0, 600);
			std::string stem = Arg<std::string>(cmd, "out", fmt::format("shot_{}", id));
			double timeout = std::clamp(Arg<double>(cmd, "timeout", 10.0), 0.1, 120.0);
			if (hud != "keep" && hud != "hide")
				throw std::invalid_argument("hud: keep or hide");
			if (!SafeStem(stem))
				throw std::invalid_argument("out: letters, digits, '_' and '-' only, at most 64 characters, not a device name (CON, NUL, COM1...)");
			if (g_Shot.active)
			{
				a["ok"] = false;
				a["error"] = fmt::format("a shot is in flight (command {})", g_Shot.id);
			}
			else if (!capture::Cancel())
			{
				// a shot that was ended while a thread still held its frame (quit, a scene change) is finishing
				a["ok"] = false;
				a["error"] = "the previous shot is still being written - try again in a moment";
			}
			else
			{
				ShotAction s;
				if (hud == "hide" && !ui::HudHidden())
				{
					nlohmann::json h = { { "ok", true } };
					ui::Hud(false, h);
					s.hidHud = h.value("ok", false);
					if (!s.hidHud)
						a["hudError"] = h.value("error", std::string());
					else
						g_HudScene = SafeScene();
				}
				app::Vector3 pos{};
				nlohmann::json base = { { "id", id }, { "gameTick", m_Ticks }, { "wallMs", WallMs() }, { "scene", SafeScene() },
					{ "inGame", SafeInGame() }, { "overlay", overlay }, { "hudHidden", ui::HudHidden() }, { "afterFrames", afterFrames } };
				if (SafePos(pos))
					base["pos"] = Vec(pos);
				relic::Try([&]()
				{
					FreeCameraPose pose{};
					if (FreeCamera::GetInstance().GetPose(pose))
						base["camera"] = PoseJson(pose);
				});
				capture::Request req;
				req.id = id;
				req.afterOverlay = overlay;
				req.afterFrames = afterFrames;
				req.png = m_Dir / "shots" / (stem + ".png");
				req.sidecar = m_Dir / "shots" / (stem + ".json");
				req.sidecarBase = Dump(base);
				std::string error;
				if (!capture::Arm(req, error))
				{
					if (s.hidHud)
						ui::RestoreHud();
					a["ok"] = false;
					a["error"] = error;
				}
				else
				{
					s.active = true;
					s.id = id;
					s.deadlineMs = AwakeMs() + static_cast<int64_t>(timeout * 1000.0);
					s.file = "shots/" + stem + ".png";
					s.sidecar = "shots/" + stem + ".json";
					s.overlay = overlay;
					s.scene0 = SafeScene();
					g_Shot = s;
					a["state"] = "running";
					a["file"] = s.file;
					a["overlay"] = overlay;
					a["afterFrames"] = afterFrames;
					a["hudHidden"] = ui::HudHidden();
				}
			}
		}
		else if (op == "mark")
		{
			std::string text = Brief(Arg<std::string>(cmd, "text", std::string()));
			LogLine("game", "mark %lld: %s", static_cast<long long>(id), text.c_str());
			relic::diag::append_line(("[devtest] mark " + std::to_string(id) + ": " + text).c_str());
			a["text"] = text;
		}
		else if (op == "quit")
		{
			std::string error;
			if (!ui::CanQuit(error))
			{
				a["ok"] = false;
				a["error"] = error;
			}
			else
			{
				g_QuitPending = true;   // answered first: Application.Quit runs at the next tick
				g_QuitId = id;
				a["quitting"] = true;
			}
		}
		else if (op == "teleport")
		{
			float pos[3];
			std::string mode = Arg<std::string>(cmd, "mode", std::string("auto"));
			float arrive = std::clamp(Arg<float>(cmd, "arrive", 12.0f), 0.5f, 1000.0f);
			double timeout = std::clamp(Arg<double>(cmd, "timeout", 60.0), 1.0, 3600.0);
			double hold = std::clamp(Arg<double>(cmd, "hold", 0.0), 0.0, 600.0);
			if (mode != "auto" && mode != "direct" && mode != "map")
				throw std::invalid_argument("mode: auto, direct or map");
			if (ReadVec(cmd, "pos", pos) != VecField::Good)
				throw std::invalid_argument("teleport needs pos: [x, y, z]");
			if (!InWorld(pos))
				throw std::invalid_argument(fmt::format("pos: within {:g} m of the origin on every axis", WORLD_MAX));
			if (g_Teleport.active)
			{
				nlohmann::json old = Head(g_Teleport.id, "teleport");
				old["ok"] = false;
				old["outcome"] = "replaced";
				Finish(g_Teleport.id, std::move(old));
				g_Teleport = TeleportAction{};
			}
			TeleportAction t;
			t.target = app::Vector3{ pos[0], pos[1], pos[2] };
			std::string error;
			bool ok = relic::Try([&]()
			{
				auto& manager = game::EntityManager::instance();
				auto avatar = manager.avatar();
				if (avatar == nullptr || avatar->raw() == nullptr || avatar->moveComponent() == nullptr)
				{
					error = "no avatar - is the scene loaded?";
					return;
				}
				app::Vector3 cur = app::ActorUtils_GetAvatarPos(nullptr);
				float dist = Dist(cur, t.target);
				a["distance"] = dist;
				t.viaMap = mode == "map" || (mode != "direct" && dist > 60.0f);
				if (t.viaMap)
				{
					if (!MapTeleport::GetInstance().TeleportTo(t.target))
						error = "no unlocked waypoint to go through";
				}
				else
					avatar->setAbsolutePosition(t.target);
			});
			a["via"] = t.viaMap ? "map" : "direct";
			if (!ok)
				error = Fault("the teleport");
			if (!error.empty())
			{
				a["ok"] = false;
				a["error"] = error;
			}
			else
			{
				t.active = true;
				t.id = id;
				t.arrive = arrive;
				t.hold = hold;
				t.deadlineMs = AwakeMs() + static_cast<int64_t>(timeout * 1000.0);
				t.scene0 = SafeScene();
				g_Teleport = t;
				a["state"] = "running";
			}
		}
		else if (op == "camera")
		{
			int asked = (cmd.contains("set") ? 1 : 0) + (cmd.contains("move") ? 1 : 0) + (cmd.contains("path") ? 1 : 0)
				+ (Arg<bool>(cmd, "release", false) ? 1 : 0);
			if (asked > 1)
				throw std::invalid_argument("camera: one of set, move, path or release");
			auto& fc = FreeCamera::GetInstance();
			if (Arg<bool>(cmd, "release", false))
			{
				a["released"] = g_Cam.owned;
				if (!g_Cam.owned)
					a["note"] = "the camera was not held";
				CameraRelease("released");
			}
			else if (asked == 0)
			{
				bool running = false;
				FreeCameraPose pose{};
				bool have = false;
				relic::Try([&]() { running = fc.IsRunning(); have = fc.GetPose(pose); });
				a["held"] = g_Cam.owned;
				a["running"] = running;
				a["action"] = CamModeName(g_Cam.mode);
				if (have)
					a["pose"] = PoseJson(pose);
			}
			else
			{
				// everything is read before anything changes
				CamMode mode = CamMode::Idle;
				PoseSpec target;
				double seconds = 0;
				Ease ease = Ease::InOut;
				std::vector<Key> keys;
				bool loop = false;
				if (cmd.contains("set"))
				{
					mode = CamMode::Set;
					target = ReadPose(cmd["set"]);
				}
				else if (cmd.contains("move"))
				{
					mode = CamMode::Move;
					const nlohmann::json& m = cmd["move"];
					if (!m.is_object() || !m.contains("to"))
						throw std::invalid_argument("move: {to: {pos, pitch, yaw, roll, fov}, seconds, ease}");
					target = ReadPose(m["to"]);
					seconds = Arg<double>(m, "seconds", 0.0);
					if (!(seconds > 0.0 && seconds <= 600.0))
						throw std::invalid_argument("move.seconds: more than 0, at most 600");
					ease = ReadEase(m, Ease::InOut);
				}
				else
				{
					mode = CamMode::Path;
					const nlohmann::json& p = cmd["path"];
					if (!p.is_object() || !p.contains("keys") || !p["keys"].is_array() || p["keys"].empty() || p["keys"].size() > 256)
						throw std::invalid_argument("path: {keys: [{pos, pitch, yaw, roll, fov, seconds, hold, ease}, ...] (1 to 256), loop}");
					for (const auto& kj : p["keys"])
					{
						Key k;
						k.pose = ReadPose(kj);
						k.seconds = std::clamp(Arg<double>(kj, "seconds", 2.0), 0.0, 600.0);
						k.hold = std::clamp(Arg<double>(kj, "hold", 0.0), 0.0, 600.0);
						k.ease = ReadEase(kj, Ease::InOut);
						keys.push_back(k);
					}
					loop = Arg<bool>(p, "loop", false);
				}
				bool hasCarry = cmd.contains("carry"), hasDrive = cmd.contains("drive");
				bool carry = Arg<bool>(cmd, "carry", false), drive = Arg<bool>(cmd, "drive", false);

				if (g_Cam.id != 0)
					CameraEndAction("replaced", false);
				std::string error;
				bool ok = relic::Try([&]()
				{
					if (!g_Cam.owned)
					{
						g_Cam.owned = true;
						g_Cam.prevEnabled = fc.f_Enabled.value().enabled();
						g_Cam.prevFov = fc.f_FOV.value();
						g_Cam.scene = SafeScene();
						g_Cam.lastEnd.clear();
					}
					fc.f_Enabled.value().set_enabled(true, false);   // no changed event: nothing is saved
					if (hasCarry)
					{
						if (!g_Cam.carrySaved)
						{
							g_Cam.prevCarry = fc.f_MoveAvatar.value();
							g_Cam.carrySaved = true;
						}
						fc.f_MoveAvatar.value() = carry;
					}
					if (hasDrive)
					{
						if (!g_Cam.driveSaved)
						{
							g_Cam.prevDrive = fc.f_DriveGameCamera.value();
							g_Cam.driveSaved = true;
						}
						fc.f_DriveGameCamera.value() = drive;
					}
				});
				if (!ok)
				{
					a["ok"] = false;
					a["error"] = Fault("taking the free camera");
					CameraRelease("faulted");
				}
				else
				{
					g_Cam.mode = mode;
					g_Cam.id = id;
					g_Cam.deadlineMs = AwakeMs() + 15000;
					g_Cam.target = target;
					g_Cam.seconds = seconds;
					g_Cam.ease = ease;
					g_Cam.keys = std::move(keys);
					g_Cam.loop = loop;
					g_Cam.fromSet = false;
					g_Cam.holding = false;
					g_Cam.elapsed = 0;
					a["state"] = "running";
					a["action"] = CamModeName(mode);
#if RELIC_GAME_VERSION == 16
					if (hasDrive)
						a["note"] = "1.6 always drives the game's own camera";
#elif RELIC_GAME_VERSION == 32
					if (hasDrive)
						a["note"] = "3.2 has no game-camera drive (no FlushStateData hook): the free camera runs on its clone";
#endif
				}
			}
		}
		else if (op == "entities")
		{
			float radius = std::clamp(Arg<float>(cmd, "radius", 100.0f), 0.0f, 100000.0f);
			int limit = std::clamp(Arg<int>(cmd, "limit", 200), 1, 5000);
			std::vector<std::string> kinds = Arg<std::vector<std::string>>(cmd, "kinds", { "monster", "gadget", "avatar", "npc" });
			auto wanted = [&](const char* kind) { return std::find(kinds.begin(), kinds.end(), kind) != kinds.end(); };
			for (const auto& k : kinds)
				if (k != "monster" && k != "gadget" && k != "avatar" && k != "npc")
					throw std::invalid_argument("kinds: monster, gadget, avatar, npc");
			struct Row { uint32_t runtimeId = 0, configId = 0; std::string kind, name; app::Vector3 pos{}; float dist = 0; bool combat = false; float hp = 0, maxHp = 0, level = 0; };
			std::vector<Row> rows;
			app::Vector3 me{};
			bool haveMe = SafePos(me);
			std::vector<game::Entity*> entities;
			bool listed = relic::Try([&]() { entities = game::EntityManager::instance().entities(); });
			if (!listed || !haveMe)
			{
				a["ok"] = false;
				a["error"] = !haveMe ? Fault("the position lookup") : Fault("listing the entities");
			}
			else
			{
				int faults = 0;
				for (game::Entity* e : entities)
				{
					Row row;
					bool keep = false;
					bool ok = relic::Try([&]()
					{
						if (e == nullptr || e->raw() == nullptr)
							return;
						auto type = e->raw()->fields.entityType;
						const char* kind = type == app::EntityType__Enum_1::Avatar ? "avatar"
							: type == app::EntityType__Enum_1::Monster ? "monster"
							: type == app::EntityType__Enum_1::NPC ? "npc"
							: e->isGadget() ? "gadget" : nullptr;
						if (kind == nullptr || !wanted(kind))
							return;
						row.pos = e->absolutePosition();
						row.dist = Dist(row.pos, me);
						if (row.dist > radius)
							return;
						row.kind = kind;
						row.runtimeId = e->runtimeID();
						row.configId = e->raw()->fields._configID_k__BackingField;
						keep = true;
					});
					if (!ok)
					{
						faults++;
						continue;
					}
					if (!keep)
						continue;
					relic::Try([&]() { row.name = Brief(e->name()); });
					relic::Try([&]()
					{
						auto* combat = e->combat();
						auto* prop = combat != nullptr ? combat->fields._combatProperty_k__BackingField : nullptr;
						if (prop == nullptr)
							return;
						row.hp = app::MoleMole_SafeFloat_get_Value(prop->fields.HP, nullptr);
						row.maxHp = app::MoleMole_SafeFloat_get_Value(prop->fields.maxHP, nullptr);
#if RELIC_GAME_VERSION != 32   // the 3.2 CombatProperty names no level field (answered as null)
						row.level = app::MoleMole_SafeFloat_get_Value(prop->fields.level, nullptr);
#endif
						row.combat = true;
					});
					rows.push_back(std::move(row));
				}
				std::sort(rows.begin(), rows.end(), [](const Row& x, const Row& y) { return x.dist < y.dist; });
				nlohmann::json list = nlohmann::json::array();
				for (size_t i = 0; i < rows.size() && static_cast<int>(i) < limit; i++)
				{
					const Row& r = rows[i];
					nlohmann::json j = { { "runtimeId", r.runtimeId }, { "configId", r.configId }, { "kind", r.kind }, { "name", r.name },
						{ "pos", Vec(r.pos) }, { "dist", r.dist } };
					if (r.combat)
					{
						j["hp"] = r.hp;
						j["maxHp"] = r.maxHp;
#if RELIC_GAME_VERSION == 32
						j["level"] = nullptr;
#else
						j["level"] = r.level;
#endif
					}
					list.push_back(std::move(j));
				}
				a["entities"] = std::move(list);
				a["total"] = rows.size();
				a["truncated"] = static_cast<int>(rows.size()) > limit;
				if (faults > 0)
					a["faults"] = faults;
			}
		}
		else if (op == "avatar")
		{
			bool found = false;
			bool ok = relic::Try([&]()
			{
				auto avatar = game::EntityManager::instance().avatar();
				if (avatar == nullptr || avatar->raw() == nullptr)
					return;
				found = true;
				a["configId"] = avatar->raw()->fields._configID_k__BackingField;
				a["runtimeId"] = avatar->runtimeID();
				a["pos"] = Vec(avatar->absolutePosition());
			});
			if (found)
				relic::Try([&]() { a["name"] = Brief(game::EntityManager::instance().avatar()->name()); });
			a["team"] = ui::Team();
			if (!ok || !found)
			{
				a["ok"] = false;
				a["error"] = !ok ? Fault("the avatar lookup") : "no avatar - is the scene loaded?";
			}
		}
		else if (op == "hud")
		{
			auto it = cmd.find("visible");
			if (it == cmd.end() || !it->is_boolean())
				throw std::invalid_argument("hud needs visible: true or false");
			bool visible = it->get<bool>();
			ui::Hud(visible, a);
			if (!visible && a.value("ok", false))
				g_HudScene = SafeScene();
		}
		else if (op == "overlay")
		{
			auto quiet = cmd.find("quiet");
			std::string menu = Arg<std::string>(cmd, "menu", std::string());
			if (quiet != cmd.end() && !quiet->is_boolean())
				throw std::invalid_argument("quiet: true or false");
			if (!menu.empty() && menu != "hide" && menu != "show")
				throw std::invalid_argument("menu: hide or show");
			if (quiet != cmd.end())
			{
				CheatManagerBase::SetOverlayQuiet(quiet->get<bool>());
				g_QuietByUs = quiet->get<bool>();
			}
			// Both requests are served at the next render, and the later one wins. Only a menu this test bed opens
			// is closed again by RestoreAll: one that was already open stays as it was.
			if (menu == "show")
			{
				if (!CheatManagerBase::IsMenuShowed())
					g_MenuByUs = true;
				CheatManagerBase::RequestShowMenu();
			}
			else if (menu == "hide")
			{
				CheatManagerBase::RequestHideMenu();
				g_MenuByUs = false;
			}
			a["quiet"] = CheatManagerBase::IsOverlayQuiet();
			a["menu"] = CheatManagerBase::IsMenuShowed();
			if (!menu.empty())
				a["menuRequested"] = menu;
		}
		else if (op == "feature")
		{
			bool restore = Arg<bool>(cmd, "restore", false);
			std::string section = Arg<std::string>(cmd, "section", std::string());
			std::string name = Arg<std::string>(cmd, "name", std::string());
			if (restore && name.empty())
			{
				a["restored"] = RestoreFeatures();
			}
			else
			{
				if (section.empty() || name.empty())
					throw std::invalid_argument("feature needs section and name (as cfg.json spells them, e.g. Visuals::FreeCamera / Enabled), and value or restore");
				std::string type;
				nlohmann::json current;
				if (!ReadField(section, name, type, current))
				{
					a["ok"] = false;
					a["error"] = "no field " + Brief(section) + " / " + Brief(name) + " of a kind this op sets (bool, int, float, toggle)";
				}
				else
				{
					const std::string key = section + "\n" + name;
					a["section"] = section;
					a["name"] = name;
					a["type"] = type;
					a["old"] = current;
					if (restore)
					{
						auto it = g_Originals.find(key);
						std::string error;
						if (it != g_Originals.end() && WriteField(section, name, it->second.value, error))
						{
							a["new"] = it->second.value;
							g_Originals.erase(it);
						}
						else
						{
							a["new"] = current;
							a["note"] = "not changed by this test bed";
						}
					}
					else
					{
						auto value = cmd.find("value");
						if (value == cmd.end())
							throw std::invalid_argument("feature needs value (or restore: true)");
						if (g_Originals.find(key) == g_Originals.end())
							g_Originals[key] = Original{ section, name, type, current };
						std::string error;
						if (!WriteField(section, name, *value, error))
						{
							a["ok"] = false;
							a["error"] = error;
						}
						else
						{
							ReadField(section, name, type, current);
							a["new"] = current;
						}
					}
				}
			}
		}
		else if (op == "ui.tree")
		{
			ui::Tree(cmd, a);
		}
		else if (op == "ui.click")
		{
			ui::Click(cmd, a);
		}
		else if (op == "ui.text")
		{
			ui::SetText(cmd, a);
		}
		else if (op == "login" || op == "enter")
		{
			const bool enter = op == "enter";
			std::string account = Arg<std::string>(cmd, "account", std::string());
			double timeout = std::clamp(Arg<double>(cmd, "timeout", enter ? 180.0 : 60.0), 1.0, 3600.0);
			if (!enter && account.empty())
				throw std::invalid_argument("login needs account (and password, unless the form's own one is kept)");
			if (g_Login.active)
			{
				a["ok"] = false;
				a["error"] = fmt::format("a {} is in progress (command {})", g_Login.enter ? "enter" : "login", g_Login.id);
			}
			else if (!enter && Arg<bool>(cmd, "ifShown", false) && ui::LoginFormOpen() == 0)
			{
				// The SDK logs in by itself with the account it remembers: there is no form to fill.
				a["outcome"] = "notShown";
			}
			else
			{
				if (enter)
					ui::EnterStart(Arg<std::string>(cmd, "button", std::string()), a);
				else
				{
					auto pw = cmd.find("password");
					std::string password = pw != cmd.end() && pw->is_string() ? pw->get<std::string>() : std::string();
					ui::LoginStart(account, pw != cmd.end() && pw->is_string() ? &password : nullptr, a);
				}
				if (a.value("ok", false))
				{
					LoginAction l;
					l.active = true;
					l.id = id;
					l.enter = enter;
					l.deadlineMs = AwakeMs() + static_cast<int64_t>(timeout * 1000.0);
					l.found = a.value("found", nlohmann::json::object());
					if (enter)
					{
						l.button = Arg<std::string>(cmd, "button", std::string());
						l.lastPressMs = AwakeMs();
						l.presses = 1;
					}
					g_Login = l;
					a["state"] = "running";
				}
			}
		}
		else
		{
			a["ok"] = false;
			a["error"] = "unknown op";
			a["ops"] = OpList();
		}
		Answer(id, a);
	}
}
#endif
