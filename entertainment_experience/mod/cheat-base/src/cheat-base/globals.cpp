#include <pch.h>

#include "globals.h"

namespace events
{
	TCancelableEvent<short>& KeyUpEvent = *new TCancelableEvent<short>();  // Relic: immortal (never destructed at process exit)
	TCancelableEvent<HWND, UINT, WPARAM, LPARAM>& WndProcEvent = *new TCancelableEvent<HWND, UINT, WPARAM, LPARAM>();  // Relic: immortal (never destructed at process exit)
	TEvent<>& RenderEvent = *new TEvent<>();  // Relic: immortal (never destructed at process exit)
}