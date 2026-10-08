#pragma once
#include <windows.h>

// Relic: the 1.6 offsets and struct layouts are reconstructed from a dump by structural matching, so a wrong
// match shows up as an access violation deep inside the game rather than as a compile error. A faulting
// FEATURE must never take the game down with it: Guard runs a piece of work under SEH and, on the first
// fault, logs it once and answers false so the caller can skip that feature for the rest of the session.
//
// __try cannot live in a function that needs C++ unwinding, hence the two-step shape: the guarded body is a
// lambda called through a plain function pointer inside guard_call, which itself owns no objects. Nothing of
// cheat-base is included here on purpose — this header is pulled in by events/event.hpp, which Logger.h
// includes itself; the message goes through the out-of-line helper in relic-guard.cpp instead.
namespace relic
{
	typedef void (*GuardBody)(void* ctx);

	void guard_report(const char* what, unsigned code);   // relic-guard.cpp

	inline bool guard_call(GuardBody body, void* ctx, const char* what, bool* reported)
	{
		__try
		{
			body(ctx);
			return true;
		}
		__except (EXCEPTION_EXECUTE_HANDLER)
		{
			if (reported == nullptr || !*reported)
			{
				if (reported != nullptr)
					*reported = true;
				guard_report(what, (unsigned)GetExceptionCode());
			}
			return false;
		}
	}

	// The silent variant: the last exception code is kept so the caller can log it after the fact.
	inline thread_local unsigned g_lastGuardCode = 0;

	inline bool guard_call_silent(GuardBody body, void* ctx)
	{
		__try
		{
			body(ctx);
			return true;
		}
		__except (EXCEPTION_EXECUTE_HANDLER)
		{
			g_lastGuardCode = (unsigned)GetExceptionCode();
			return false;
		}
	}

	// Runs `fn` under SEH. `what` names it in the log; the fault is logged only the first time per `reported`
	// flag (pass a static bool per call site so a per-frame path cannot flood the log).
	template <class TFunc>
	inline bool Guard(const char* what, bool* reported, TFunc&& fn)
	{
		GuardBody body = [](void* ctx) { (*static_cast<TFunc*>(ctx))(); };
		return guard_call(body, &fn, what, reported);
	}

	// Same, but silent: the caller decides what (and whether) to log. Returns false when `fn` faulted.
	template <class TFunc>
	inline bool Try(TFunc&& fn)
	{
		GuardBody body = [](void* ctx) { (*static_cast<TFunc*>(ctx))(); };
		return guard_call_silent(body, &fn);
	}

	// How many CONSECUTIVE faults a repeating call site tolerates before it is switched off for the session.
	// Counted consecutively on purpose: on 1.6 a singleton or a metadata slot is simply not resolved yet during
	// the first frames, so an early fault is transient and must not disable a working feature for good.
	const int kGuardFaultLimit = 20;

	void guard_report_fault(const char* what, unsigned code, int count, bool disabled);   // relic-guard.cpp
}

// Runs the following block under SEH; on a fault it is logged once and skipped.
#define RELIC_GUARD(what, ...) \
	do { static bool s_relicGuardReported = false; \
	     relic::Guard(what, &s_relicGuardReported, [&]() __VA_ARGS__); } while (0)
