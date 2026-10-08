#include "pch-il2cpp.h"
#include "RenderResolution.h"

#include <math.h>
#include <helpers.h>
#include <cheat/events.h>
#include <cheat/game/EntityManager.h>

namespace cheat::feature
{
	// Relic: the game's own "Render Resolution" slider stops at 1.5. That ceiling is not a property of the
	// renderer - it is the last entry of a data-driven option table, sitting on top of a Mathf.Min(v, 2.0f)
	// and a desktop-resolution gate that forces the scale down again above 2560x1440. The value all three
	// of them are guarding ends up in exactly one place:
	//
	//     PostProcessLayer.innerResolutionScale
	//       -> SetupInnerResolution -> UpdateInnerTarget
	//       -> RenderTexture.GetTemporary(FloorToInt(w * scale * ratio), FloorToInt(h * scale)) as the
	//          camera's target texture
	//
	// so a value above 1.0 is genuine supersampling: the frame is rendered larger than the window and
	// resolved down, which is the sharpest anti-aliasing there is (and the most expensive). We hook the
	// setter - the only code path that writes the field, below every clamp - and substitute our value.
	//
	// A bare field poke is NOT a shortcut here: UpdateInnerTarget only runs behind the layer's
	// cameraBufferDirty flag, so writing the field without going through the setter leaves the render
	// target stale.
	static void PostProcessLayer_set_innerResolutionScale_Hook(void* __this, float value, MethodInfo* method);

	static bool  s_InLevel = false;     // written once per frame by OnGameUpdate, read by the hook
	static bool  s_SelfApply = false;   // our own write is going through the setter - do not substitute
	static float s_GameScale = 1.0f;    // the value the game last asked for, restored when we switch off
	static bool  s_GameScaleKnown = false;
	static int   s_ScreenW = 0;         // sampled on the game thread, drawn by DrawMain (which is not on it)
	static int   s_ScreenH = 0;
	static bool  s_WarnedNoLayer = false;

	// The single place the effective scale is decided. f_Scale keeps whatever the player last dragged it to,
	// so without this an old 4.0 would still be applied after "Allow above 2.0" was switched back off.
	static float EffectiveScale()
	{
		auto& f = RenderResolution::GetInstance();
		float scale = f.f_Scale;
		if (!f.f_AllowExtreme && scale > 2.0f)
			scale = 2.0f;
		if (scale < 0.6f)
			scale = 0.6f;
		return scale;
	}

	RenderResolution::RenderResolution() : Feature(),
		NF(f_Enabled, "Visuals::RenderResolution", false),
		NF(f_Scale, "Visuals::RenderResolution", 2.0f),
		NF(f_AllowExtreme, "Visuals::RenderResolution", false)
	{
		INSTALL_HOOK(app::PostProcessLayer_set_innerResolutionScale,
			PostProcessLayer_set_innerResolutionScale_Hook);
		events::GameUpdateEvent += MY_METHOD_HANDLER(RenderResolution::OnGameUpdate);
	}

	const FeatureGUIInfo& RenderResolution::GetGUIInfo() const
	{
		TRANSLATED_GROUP_INFO("Render Resolution", "Visuals");
		return info;
	}

	void RenderResolution::DrawMain()
	{
		ConfigWidget(_TR("Enabled"), f_Enabled,
			_TR("Raises the game's Render Resolution past the 1.5 its own settings menu allows.\n"
				"Above 1.0 the frame is rendered larger than the window and resolved down - the cleanest\n"
				"anti-aliasing there is, and the most expensive one."));

		if (f_Enabled)
		{
			ConfigWidget(_TR("Render Scale"), f_Scale, 0.05f, 0.6f, f_AllowExtreme ? 4.0f : 2.0f,
				_TR("2.0 renders four times the pixels of 1.0 - 1080p at 2.0 costs about as much as native 4K,\n"
					"and every full-size buffer in the post-processing stack grows with it."));

			float shown = f_Scale;
			if (!f_AllowExtreme && shown > 2.0f)
				shown = 2.0f;
			if (s_ScreenW > 0 && s_ScreenH > 0)
				ImGui::Text("%s: ~%d x %d", _TR("Rendering at"),
					(int)(s_ScreenW * shown), (int)(s_ScreenH * shown));

			ConfigWidget(_TR("Allow above 2.0"), f_AllowExtreme,
				_TR("Unlocks the slider past the engine's own 2.0 ceiling. Running out of video memory shows up\n"
					"as a black screen rather than an error, so raise it one step at a time."));

			if (shown > 2.0f)
				ImGui::TextColored(ImVec4(1.f, 0.6f, 0.2f, 1.f), "%s",
					_TR("Above 2.0 is past anything the engine ships. Expect heavy video memory use."));

			ImGui::TextDisabled("%s", _TR("Note: on a desktop above 2560x1440 the game forces the scale back down\n"
				"on its own; this option overrides that too, so it costs even more there."));
		}
	}

	bool RenderResolution::NeedStatusDraw() const
	{
		return f_Enabled;
	}

	void RenderResolution::DrawStatus()
	{
		float shown = f_Scale;
		if (!f_AllowExtreme && shown > 2.0f)
			shown = 2.0f;                        // the same clamp EffectiveScale applies
		ImGui::Text("%s [%.2fx]", _TR("Render Resolution"), shown);
	}

	RenderResolution& RenderResolution::GetInstance()
	{
		static RenderResolution instance;
		return instance;
	}

	static void PostProcessLayer_set_innerResolutionScale_Hook(void* __this, float value, MethodInfo* method)
	{
		if (!s_SelfApply && __this != nullptr)
		{
			auto& f = RenderResolution::GetInstance();

			// The apply funnel also runs for the login, character-select and gacha cameras; gate on being in
			// level so the menus are not supersampled - and so the value we remember for the restore is the
			// one that belongs to the in-level layer, not whatever a menu camera happened to ask for.
			if (s_InLevel)
			{
				s_GameScale = value;
				s_GameScaleKnown = true;

				if (f.f_Enabled)
					value = EffectiveScale();
			}
		}

		CALL_ORIGIN(PostProcessLayer_set_innerResolutionScale_Hook, __this, value, method);
	}

	// Resolve the live layer fresh every time - never remember an il2cpp object pointer across frames.
	//
	// GameObject.Find only sees ACTIVE objects, and on 2.8 the free camera deactivates the real main camera
	// and renders through a clone of it - which is exactly when this feature is wanted. Object.Instantiate
	// names the clone "<original>(Clone)" and parents it to nothing, so the second lookup finds it by name.
	// (On 1.6 the free camera drives the game's own camera, so the first lookup always wins.)
	static void* ResolveLayer()
	{
		auto* cam = app::GameObject_Find(string_to_il2cppi("/EntityRoot/MainCamera(Clone)"), nullptr);
		if (cam == nullptr)
			cam = app::GameObject_Find(string_to_il2cppi("MainCamera(Clone)(Clone)"), nullptr);
		if (cam == nullptr)
			return nullptr;
		return app::GameObject_GetComponentByName(cam, string_to_il2cppi("PostProcessLayer"), nullptr);
	}

	void RenderResolution::OnGameUpdate()
	{
		auto& manager = game::EntityManager::instance();
		s_InLevel = manager.avatar()->raw() != nullptr;

		// The game applies the setting on scene load and on a settings change, never per frame, so this
		// low-frequency watchdog is what makes the slider take effect immediately - and what re-asserts our
		// value after something else re-applied the game's own.
		static int  s_Ticks = 0;
		static bool s_WasEnabled = false;
		static float s_WasScale = 0.f;

		if (++s_Ticks < 20)
			return;
		s_Ticks = 0;

		// Re-sampled every tick, not once: the player can resize the window or switch to full screen, and
		// DrawMain (which runs on the render thread and must not call il2cpp) reads these.
		s_ScreenW = app::Screen_get_width(nullptr);
		s_ScreenH = app::Screen_get_height(nullptr);

		bool want = f_Enabled && s_InLevel;
		float scale = EffectiveScale();

		if (!want && !s_WasEnabled)
			return;

		void* layer = ResolveLayer();
		if (layer == nullptr)
		{
			if (want && !s_WarnedNoLayer)
			{
				s_WarnedNoLayer = true;
				LOG_WARNING("[renderres] no PostProcessLayer on the main camera - the scale will only apply "
					"when the game itself re-applies its graphics settings.");
			}
			return;
		}
		s_WarnedNoLayer = false;   // it resolved; let the warning fire again if it ever stops

		float now = app::PostProcessLayer_get_innerResolutionScale(layer, nullptr);

		if (want && !s_WasEnabled && !s_GameScaleKnown)
		{
			// First time we take over and the hook has not seen a game write yet: whatever is on the layer
			// right now IS the player's own setting.
			s_GameScale = now;
			s_GameScaleKnown = true;
		}

		float target = want ? scale : s_GameScale;

		if (want != s_WasEnabled || scale != s_WasScale || fabsf(now - target) > 0.001f)
		{
			s_SelfApply = true;
			app::PostProcessLayer_set_innerResolutionScale(layer, target, nullptr);
			s_SelfApply = false;
			LOG_DEBUG("[renderres] applied %.2f (the game asked for %.2f)", target, s_GameScale);
		}

		s_WasEnabled = want;
		s_WasScale = scale;
	}
}
