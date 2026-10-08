#pragma once

#include "eventhandlerptr.h"


namespace events::handlers 
{
	template<class ...TParams>
	class AbstractEventHandler
	{
		using MyType = AbstractEventHandler<TParams...>;

	public:

		virtual ~AbstractEventHandler() {}

		virtual void call(TParams... params) = 0;

		// Relic: how often calling this handler faulted (wrong offsets for the running game version). The
		// dispatcher stops calling it after a few faults instead of paying an exception per frame forever.
		int relicFaults = 0;

		bool operator==(const MyType& other) const noexcept
		{
			return isEquals(other);
		}
		bool operator!=(const MyType& other) const noexcept
		{
			return !(*this == other);
		}

	protected:

		AbstractEventHandler() {}

		virtual bool isEquals(const MyType& other) const noexcept = 0;
	};
}
