#include "pch-il2cpp.h"
#include "events.h"

namespace cheat::events
{
	TEvent<>& GameUpdateEvent = *new TEvent<>();  // Relic: immortal (never destructed at process exit)
	TEvent<uint32_t>& AccountChangedEvent = *new TEvent<uint32_t>();  // Relic: immortal (never destructed at process exit)
	TEvent<uint32_t, app::MotionInfo*>& MoveSyncEvent = *new TEvent<uint32_t, app::MotionInfo*>();  // Relic: immortal (never destructed at process exit)
}