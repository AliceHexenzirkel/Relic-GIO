#pragma once

// Relic: keeps the overlay's Present hook alive.
//
// The hook is a code patch in dxgi.dll. On this game the patch survives exactly one frame: something restores
// the original bytes right after the first Present, so with the patch alone the overlay appears only sometimes -
// it depends on WHEN the patch goes in. Instead of
// racing, we remember the bytes we installed and put them back whenever they change. Detours is not involved
// in the repair, so the trampoline it built (a copy of the original prologue) stays valid throughout.
namespace relic
{
	namespace hookwatch
	{
		// Remembers the patched bytes at `target` and starts the watcher on the first call.
		void protect(void* target, const char* name);

		// Restores every protected patch that no longer matches; returns how many it had to put back.
		int repair_now();

		// Stops repairing. Called once the overlay hangs off the swap chain's own vtable instead, where no
		// code patch can reach it: from then on fighting over dxgi's bytes would only cost frames.
		void stop();
	}
}
