#pragma once

// Relic: a counter board for the overlay path that does not go through the logger.
//
// When the menu stops appearing on a backported game version the log is useless: whatever swallowed the
// overlay may also have taken the logger's mutex with it, so a silent log says nothing about where
// the frame died. These counters are plain interlocked longs written by a watchdog thread of our own every
// two seconds into `relic-diag.txt` next to the DLL, so a frozen or skipped stage is visible as a counter
// that stopped moving. Nothing of cheat-base is included here — event.hpp pulls this in.
namespace relic
{
	namespace diag
	{
		enum Stage
		{
			PRESENT = 0,     // the DX11 Present detour ran
			PRESENT1,        // ... or the Present1 detour (flip-model swap chains)
			PRESENT_VT,      // ... or the swap chain's own vtable entry, which no code patch can undo
			RESTORES,        // times the overlay hook had to be put back after being overwritten
			DX11_IN,         // renderer::OnRenderDX11 entered (ImGui NewFrame done)
			DX11_OUT,        // ... and returned (ImGui Render done)
			RENDER_IN,       // CheatManagerBase::OnRender entered
			KEY_SEEN,        // the menu key was read without faulting
			TOGGLE,          // the menu was toggled
			EXTERNAL_OUT,    // DrawExternal returned
			MENU_OUT,        // the menu block returned
			RENDER_OUT,      // CheatManagerBase::OnRender returned
			HANDLER_RUN,     // an event handler was called
			HANDLER_SKIP,    // ... or skipped because it had faulted too often
			FAULTS,          // guarded faults so far
			COUNT
		};

		extern volatile long counters[COUNT];

		void tick(int stage);
		void note_fault(const char* what, unsigned code);
		void set_note(const char* text);   // one free-form line, shown on the board
		// A whole line of its own, appended to the board file at once (no logger, no shared mutex): a feature's
		// own findings that must survive with file logging off. The CALLER rate-limits it.
		void append_line(const char* text);
		void start();   // called once from CheatManagerBase::Init
	}
}

#define RELIC_DIAG_TICK(stage) ::relic::diag::tick(::relic::diag::stage)
