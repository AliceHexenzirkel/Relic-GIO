#pragma once

#include "FieldEntry.h"
#include <cheat-base/config/converters.h>

namespace config::internal
{
	namespace CHECK
	{
		// Relic: C++20 rewritten comparisons make upstream's SFINAE trick (a template operator== returning No)
		// ambiguous on MSVC 14.5x; a requires-expression asks the same question without declaring anything.
		template<typename T, typename Arg = T>
		struct EqualExists
		{
			static constexpr bool value = requires(T& a, Arg& b) { a == b; };
		};
	}

	template<typename T>
	class FieldSerialize : public FieldEntry
	{
	public:
		FieldSerialize(const std::string& name, const std::string& sectionName, const T& defaultValue, bool multiProfile = false) :
			FieldEntry(name, sectionName, multiProfile), m_Value(defaultValue), m_DefaultValue(defaultValue) { }

		nlohmann::json ToJson() override
		{
			if constexpr (CHECK::EqualExists<T>::value)
			{
				if (m_Value == m_DefaultValue)
					return {};
			}

			return converters::ToJson(m_Value);
		}

		void FromJson(const nlohmann::json& jObject) override
		{
			if (jObject.empty())
			{
				m_Value = m_DefaultValue;
				return;
			}

			converters::FromJson(m_Value, jObject);
		}
		
		void Reset() override
		{
			m_Value = m_DefaultValue;
		}

		T m_Value;
		T m_DefaultValue;
	};
}