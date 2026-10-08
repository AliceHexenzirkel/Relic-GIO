#pragma once

#include <map>
#include <type_traits>
#include <detours.h>
#include <cheat-base/Logger.h>
#include <cheat-base/relic-guard.h>
#include <cheat-base/relic-diag.h>

#define CALL_ORIGIN(function, ...) \
	HookManager::call(function, __func__, __VA_ARGS__)

class HookManager
{
public:
	template <typename Fn>
	static void install(Fn func, Fn handler, const char* name = nullptr)
	{
		if (reinterpret_cast<void*>(func) == nullptr)
		{
			// Relic: an unresolved (0x0) offset for this game build - leave the feature unhooked instead of crashing.
			LOG_WARNING("Hook target is null for %s (offset not resolved for this game build) - hook skipped.",
				name == nullptr ? "<unnamed>" : name);
			return;
		}
		enable(func, handler);
		holderMap[reinterpret_cast<void*>(handler)] = reinterpret_cast<void*>(func);
	}

	// Relic: attaches `wrapper` to the game but records the origin under `handler`, so CALL_ORIGIN inside the
	// hook still finds it. That is what lets a hook body run under a guard without the feature code changing.
	template <typename Fn>
	static void installGuarded(Fn func, Fn handler, Fn wrapper, const char* name)
	{
		if (reinterpret_cast<void*>(func) == nullptr)
		{
			LOG_WARNING("Hook target is null for %s (offset not resolved for this game build) - hook skipped.",
				name == nullptr ? "<unnamed>" : name);
			return;
		}
		enable(func, wrapper);
		holderMap[reinterpret_cast<void*>(handler)] = reinterpret_cast<void*>(func);
	}

	template <typename Fn>
	static Fn getOrigin(Fn handler, const char* callerName = nullptr) noexcept
	{
		if (holderMap.count(reinterpret_cast<void*>(handler)) == 0) {
			LOG_WARNING("Origin not found for handler: %s. Maybe racing bug.", callerName == nullptr ? "<Unknown>" : callerName);
			return nullptr;
		}
		return reinterpret_cast<Fn>(holderMap[reinterpret_cast<void*>(handler)]);
	}

	template <typename Fn>
	static void detach(Fn handler) noexcept 
	{
		disable(handler);
		holderMap.erase(reinterpret_cast<void*>(handler));
	}

	// I don't know why
#ifdef _WIN64

	template <typename RType, typename... Params>
	static RType call(RType(*handler)(Params...), const char* callerName = nullptr, Params... params)
	{
		auto origin = getOrigin(handler, callerName);
		if (origin != nullptr)
			return origin(params...);

		return RType();
	}

#else

	template <typename RType, typename... Params>
	static RType call(RType(__cdecl *handler)(Params...), const char* callerName = nullptr, Params... params)
	{
		auto origin = getOrigin(handler, callerName);
		if (origin != nullptr)
			return origin(params...);

		return RType();
	}

	template <typename RType, typename... Params>
	static RType call(RType(__stdcall *handler)(Params...), const char* callerName = nullptr, Params... params)
	{
		auto origin = getOrigin(handler, callerName);
		if (origin != nullptr)
			return origin(params...);

		return RType();
	}

#endif

	static void detachAll() noexcept
	{
		for (const auto &[key, value] : holderMap) 
		{
			disable(key);
		}
		holderMap.clear();
	}

private:
	inline static std::map<void*, void*> holderMap{};

	template <typename Fn>
	static void disable(Fn handler)
	{
		Fn origin = getOrigin(handler);
		DetourTransactionBegin();
		DetourUpdateThread(GetCurrentThread());
		DetourDetach(&(PVOID&)origin, handler);
		DetourTransactionCommit();
	}

	template <typename Fn>
	static void enable(Fn& func, Fn handler)
	{
		DetourTransactionBegin();
		DetourUpdateThread(GetCurrentThread());
		DetourAttach(&(PVOID&)func, handler);
		DetourTransactionCommit();
	}
};

// Relic: a hook is called by the GAME, not through our event system, so none of the guards around features
// and rendering can see it. On a backported build a hook body is the most dangerous code we have - it walks
// game objects using offsets and struct layouts reconstructed from a dump, and one wrong field takes the
// whole game down with no log and no dump.
//
// HookGuard wraps a hook in the same SEH guard the rest of the mod uses. On a fault the call is dropped (the
// origin is NOT called again - it may already have run, and running game logic twice is worse than skipping
// it once) and, once a hook has faulted too often in a row, it turns into a plain pass-through to the game's
// own function. The feature then behaves as if it were switched off, and everything else keeps working.
namespace relic
{
	template <auto Handler>
	struct HookGuard;

	template <class R, class... A, R(*Handler)(A...)>
	struct HookGuard<Handler>
	{
		static inline const char* s_name = "hook";
		static inline int s_faults = 0;

		static R call(A... args)
		{
			if (s_faults >= relic::kGuardFaultLimit)
				return HookManager::call(Handler, s_name, args...);

			if constexpr (std::is_void_v<R>)
			{
				if (relic::Try([&]() { Handler(args...); }))
				{
					s_faults = 0;
					return;
				}
			}
			else
			{
				R result{};
				if (relic::Try([&]() { result = Handler(args...); }))
				{
					s_faults = 0;
					return result;
				}
			}

			s_faults++;
			relic::diag::note_fault(s_name, relic::g_lastGuardCode);
			relic::guard_report_fault(s_name, relic::g_lastGuardCode, s_faults,
				s_faults >= relic::kGuardFaultLimit);

			if constexpr (!std::is_void_v<R>)
				return R{};
		}
	};
}

// Installs a hook whose body runs under the guard above. Use this for every hook into the game.
#define INSTALL_HOOK(target, handler)                                            \
	do {                                                                         \
		relic::HookGuard<handler>::s_name = #handler;                            \
		HookManager::installGuarded(target, handler,                             \
			&relic::HookGuard<handler>::call, #handler);                         \
	} while (0)


