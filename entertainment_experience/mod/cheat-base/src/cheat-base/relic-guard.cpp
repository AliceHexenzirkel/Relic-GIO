#include "pch.h"
#include <cheat-base/relic-guard.h>
#include <cheat-base/Logger.h>

namespace relic
{
	void guard_report(const char* what, unsigned code)
	{
		LOG_WARNING("%s faulted (0x%08X) - this build does not have the right offsets for it on this game version, "
			"so it is skipped. The rest of the menu keeps working.", what, code);
	}

	void guard_report_fault(const char* what, unsigned code, int count, bool disabled)
	{
		if (disabled)
			LOG_WARNING("%s faulted %d times (last 0x%08X) - switching it off for this session. Everything else keeps "
				"working; this feature needs its offsets fixed for this game version.", what, count, code);
		else if (count == 1 || count == 5)
			LOG_WARNING("%s faulted (0x%08X, %d in a row) - wrong offsets for this game version, or the game object it "
				"needs does not exist yet; the call was skipped.", what, code, count);
	}
}
