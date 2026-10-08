#pragma once

// Relic: the pure half of the interactive map's progress features - which point a "Mark as done" key press
// lands on, and the explored-ground grid behind "Only where you have been". No il2cpp and no ImGui in here, so
// it can be exercised outside the game. Windows.h's min/max macros are live in this project: no std::min/max.

#include <Windows.h>   // VK_* for IsTypableCombo

#include <algorithm>
#include <charconv>
#include <cmath>
#include <cstdint>
#include <string>
#include <system_error>
#include <unordered_set>
#include <utility>
#include <vector>

namespace cheat::feature::mapprogress
{
	// ---- Mark as done ---------------------------------------------------------------------------------------

	constexpr int64_t kRecentMs = 60000;         // a point marked this recently answers "already marked" when it is the nearest...
	constexpr int64_t kPassRecentMs = 6000;      // ...and a second press this soon after that answer moves on to the next point
	constexpr int64_t kSkipAfterUndoMs = 60000;  // the next press this soon after an undo skips the point that was undone, once

	// Plants, ores, animals and enemies come back; everything else in the datasets (chests, oculi, puzzles,
	// challenges, waypoints...) is found once. The mark key prefers the latter, so a tracked flower next to the
	// chest you just opened cannot steal the mark. Matched on the category names of the datasets (Akebi's
	// Teyvat / Enkanomiya / Chasm files and the generated archipelago ones).
	inline bool IsRespawningCategory(const std::string& name)
	{
		if (name.rfind("Enemies", 0) == 0)   // "Enemies", "Enemies (Common)", "Enemies (Elite)", "Enemies (Boss)"
			return true;
		static const char* const kNames[] = {
			"Local Specialties", "Ores", "Materials", "Inventory / Materials", "Wood", "Fishing", "Animals",
		};
		for (const char* n : kNames)
			if (name == n)
				return true;
		return false;
	}

	// One key press, streamed over the candidate points: keeps the one the press lands on - the nearest point in
	// range that is not done yet, or that was marked less than kRecentMs ago (then the press only says so, and
	// `passRecent` - a second press - looks past those). Points that come back are looked at only when no
	// found-once point is in range. `range` 0 means no limit; `skip` is passed over.
	template <typename Point>
	class MarkPicker
	{
	public:
		MarkPicker(int64_t nowMs, float range, bool passRecent, const Point* skip)
			: m_Now(nowMs), m_Range(range), m_PassRecent(passRecent), m_Skip(skip) {}

		void Offer(Point* point, float distance, bool completed, int64_t completedAtMs, bool respawns)
		{
			if (point == nullptr || point == m_Skip || !(distance >= 0.f))   // !(>=) also drops a NaN distance
				return;
			if (m_Range > 0.f && distance > m_Range)
				return;
			bool recent = false;
			if (completed)
			{
				const int64_t age = m_Now - completedAtMs;
				recent = !m_PassRecent && completedAtMs > 0 && age >= 0 && age < kRecentMs;
				if (!recent)
					return;   // done a while ago: not a candidate at all
			}
			Best& best = m_Best[respawns ? 1 : 0];
			if (best.point == nullptr || distance < best.distance)
				best = { point, distance, recent };
		}

		Point* point() const { return result().point; }
		float distance() const { return result().distance; }
		bool alreadyDone() const { return result().alreadyDone; }

	private:
		struct Best
		{
			Point* point = nullptr;
			float distance = 0.f;
			bool alreadyDone = false;
		};

		const Best& result() const { return m_Best[0].point != nullptr ? m_Best[0] : m_Best[1]; }

		int64_t m_Now;
		float m_Range;
		bool m_PassRecent;
		const Point* m_Skip;
		Best m_Best[2];   // [0] found once, [1] comes back
	};

	// ---- Opened chests ---------------------------------------------------------------------------------------

	// The labels whose points stand for a chest. An opened chest is matched to its rarity label by its own filter;
	// the markers have no filter - the Teyvat data lists a sealed, a buried or a rock-covered chest only under one
	// of these, never under its rarity (their points lie a median 40-55 m from the nearest rarity chest: not
	// duplicates). "RockPile" is a generic "Experience" marker, not a chest.
	inline bool IsRarityChestLabel(const std::string& clearName)
	{
		return clearName == "CommonChest" || clearName == "ExquisiteChest" || clearName == "PreciousChest"
			|| clearName == "LuxuriousChest" || clearName == "RemarkableChest";
	}

	inline bool IsSealedChestLabel(const std::string& clearName)
	{
		return clearName == "SealedChest";
	}

	inline bool IsChestMarkerLabel(const std::string& clearName)
	{
		return IsSealedChestLabel(clearName) || clearName == "BuriedChest"
			|| clearName == "LargeRockPile" || clearName == "SmallRockPile";
	}

	constexpr float kSealedMarkerRange = 10.0f;   // a chest that was sealed takes a Sealed Chest marker this close first
	constexpr float kAnyChestRange = 6.0f;        // the last resort: a point of any chest label this close

	// Which point an opened chest marks. Every candidate comes with its distance to the chest and a role:
	//   Sealed - a Sealed Chest marker, offered only when the chest's prefab says it was sealed ("_Locked",
	//            "_Locker"); taken before anything else, within kSealedMarkerRange (and the detection range).
	//   Own    - a point of the rarity label the chest's own filter matched, or a chest marker: within `range`.
	//   Any    - a point of any other chest label, within kAnyChestRange only: for a chest whose name says nothing
	//            usable (a music-thorn or a reflection chest, a prefab numbered for another tier, an entity gone).
	// Own and Any compete by distance; the winner is returned whatever its state, because a chest that is marked
	// already must not pass its mark on to a neighbour.
	template <typename Point>
	class ChestPicker
	{
	public:
		enum Role { Sealed, Own, Any };

		explicit ChestPicker(float range) : m_Range(range) {}

		void Offer(Point* point, float distance, Role role)
		{
			if (point == nullptr || !(distance >= 0.f))   // !(>=) also drops a NaN distance
				return;
			float limit = m_Range;
			if (role == Sealed && kSealedMarkerRange < limit)
				limit = kSealedMarkerRange;
			else if (role == Any && kAnyChestRange < limit)
				limit = kAnyChestRange;
			if (distance > limit)
				return;
			Best& best = role == Sealed ? m_Sealed : m_Other;
			if (best.point == nullptr || distance < best.distance)
				best = { point, distance };
		}

		Point* point() const { return (m_Sealed.point != nullptr ? m_Sealed : m_Other).point; }
		float distance() const { return (m_Sealed.point != nullptr ? m_Sealed : m_Other).distance; }

	private:
		struct Best
		{
			Point* point = nullptr;
			float distance = 0.f;
		};

		float m_Range;
		Best m_Sealed;
		Best m_Other;
	};

	// Whether a chest seen by a rarity filter belongs to a chest marker instead - then the map neither adds it as a
	// new chest nor moves another chest's point onto it. `nearestOwn` is the nearest point of its rarity label.
	inline bool ClaimedByChestMarker(float nearestMarker, float nearestSealedMarker, bool looksSealed, float nearestOwn, float range)
	{
		if (looksSealed && nearestSealedMarker <= kSealedMarkerRange && nearestSealedMarker <= range)
			return true;
		return nearestMarker <= range && nearestMarker < nearestOwn;
	}

	// A key the player could be typing into the game's chat: a letter, a digit, punctuation, space or a numpad key,
	// pressed without Ctrl or Alt. Such a key is ignored while the cursor is out (the chat or a game menu is open)
	// unless the big map is up. Function keys, arrows, Insert/Home/End... never type anything.
	inline bool IsTypableCombo(const std::vector<short>& keys)
	{
		bool typable = false;
		for (short k : keys)
		{
			switch (k)
			{
			case VK_CONTROL: case VK_LCONTROL: case VK_RCONTROL:
			case VK_MENU: case VK_LMENU: case VK_RMENU:
				return false;   // a Ctrl / Alt combination types nothing
			default:
				break;
			}
			if ((k >= 'A' && k <= 'Z') || (k >= '0' && k <= '9') || k == VK_SPACE
				|| (k >= VK_NUMPAD0 && k <= VK_DIVIDE)    // 0x60-0x6F: numpad digits and operators
				|| (k >= VK_OEM_1 && k <= VK_OEM_3)       // 0xBA-0xC0: semicolon, plus, comma, minus, period, slash, grave
				|| (k >= VK_OEM_4 && k <= VK_OEM_8)       // 0xDB-0xDF: brackets, backslash, quote, OEM_8
				|| k == VK_OEM_102)                       // the extra key of ISO keyboards
				typable = true;
		}
		return typable;
	}

	// ---- Only where you have been ---------------------------------------------------------------------------

	constexpr float kExploreCell = 25.0f;           // level units (metres) per side of an explored cell
	constexpr float kExploreMinRadius = 25.0f;      // the reveal radius slider's range...
	constexpr float kExploreMaxRadius = 200.0f;     // ...whose top is also the grids' margin
	constexpr int32_t kExploreMaxSide = 2048;       // cells per grid side (51 km): bounds a runaway dataset extent

	inline int32_t CellOf(float v)
	{
		return static_cast<int32_t>(std::floor(v / kExploreCell));
	}

	// Unsigned packing, so negative cells round-trip without relying on signed shifts.
	inline int64_t CellKey(int32_t cx, int32_t cy)
	{
		return static_cast<int64_t>((static_cast<uint64_t>(static_cast<uint32_t>(cx)) << 32) | static_cast<uint32_t>(cy));
	}

	inline int32_t CellX(int64_t key)
	{
		return static_cast<int32_t>(static_cast<uint32_t>(static_cast<uint64_t>(key) >> 32));
	}

	inline int32_t CellY(int64_t key)
	{
		return static_cast<int32_t>(static_cast<uint32_t>(static_cast<uint64_t>(key) & 0xFFFFFFFFull));
	}

	inline int64_t CellKeyAt(float x, float y)
	{
		return CellKey(CellOf(x), CellOf(y));
	}

	inline double ExploredKm2(size_t cells)
	{
		return static_cast<double>(cells) * kExploreCell * kExploreCell / 1e6;
	}

	inline float ClampRadius(float radius)
	{
		if (!(radius >= kExploreMinRadius))   // NaN included
			return kExploreMinRadius;
		return radius > kExploreMaxRadius ? kExploreMaxRadius : radius;
	}

	// Which cells of one scene count as seen: every cell whose centre lies within the reveal radius (plus half a
	// cell - where the character stood inside its cell is not known) of a cell the character has stood in. One
	// byte per cell over the dataset's extent plus a margin, so the draw loops test a point with one index.
	class ExploreGrid
	{
	public:
		// The extent in level units - the dataset's points. The margin keeps a revealed ring whole at the edges.
		void SetBounds(float minX, float minY, float maxX, float maxY)
		{
			const float margin = kExploreMaxRadius + kExploreCell;
			m_MinCx = CellOf(minX - margin);
			m_MinCy = CellOf(minY - margin);
			m_Width = std::clamp(CellOf(maxX + margin) - m_MinCx + 1, 1, kExploreMaxSide);
			m_Height = std::clamp(CellOf(maxY + margin) - m_MinCy + 1, 1, kExploreMaxSide);
			m_Cells.assign(static_cast<size_t>(m_Width) * static_cast<size_t>(m_Height), uint8_t(0));
			m_Radius = -1.f;   // nothing stamped yet: the next Rebuild sets the radius
			m_Mask.clear();
		}

		bool Valid() const { return !m_Cells.empty(); }
		float Radius() const { return m_Radius; }
		size_t Width() const { return static_cast<size_t>(m_Width); }
		size_t Height() const { return static_cast<size_t>(m_Height); }

		// Everything revealed from scratch: a new radius, a load, a reset.
		void Rebuild(const std::unordered_set<int64_t>& explored, float radius)
		{
			if (!Valid())
				return;
			std::fill(m_Cells.begin(), m_Cells.end(), uint8_t(0));
			SetRadius(ClampRadius(radius));
			for (int64_t key : explored)
				Stamp(key);
		}

		// One more explored cell, at the radius of the last Rebuild.
		void Add(int64_t key)
		{
			if (Valid())
				Stamp(key);
		}

		bool IsRevealed(float x, float y) const
		{
			if (!Valid() || !std::isfinite(x) || !std::isfinite(y))
				return false;
			const int64_t cx = static_cast<int64_t>(CellOf(x)) - m_MinCx;
			const int64_t cy = static_cast<int64_t>(CellOf(y)) - m_MinCy;
			if (cx < 0 || cy < 0 || cx >= m_Width || cy >= m_Height)
				return false;
			return m_Cells[static_cast<size_t>(cy) * static_cast<size_t>(m_Width) + static_cast<size_t>(cx)] != 0;
		}

	private:
		void SetRadius(float radius)
		{
			m_Radius = radius;
			m_Mask.clear();
			const float r = radius / kExploreCell + 0.5f;
			const int reach = static_cast<int>(std::ceil(r));
			for (int dy = -reach; dy <= reach; dy++)
				for (int dx = -reach; dx <= reach; dx++)
					if (static_cast<float>(dx * dx + dy * dy) <= r * r)
						m_Mask.emplace_back(dx, dy);
		}

		void Stamp(int64_t key)
		{
			const int64_t baseX = static_cast<int64_t>(CellX(key)) - m_MinCx;
			const int64_t baseY = static_cast<int64_t>(CellY(key)) - m_MinCy;
			for (const auto& [dx, dy] : m_Mask)
			{
				const int64_t x = baseX + dx;
				const int64_t y = baseY + dy;
				if (x < 0 || y < 0 || x >= m_Width || y >= m_Height)
					continue;
				m_Cells[static_cast<size_t>(y) * static_cast<size_t>(m_Width) + static_cast<size_t>(x)] = 1;
			}
		}

		int32_t m_MinCx = 0;
		int32_t m_MinCy = 0;
		int32_t m_Width = 0;
		int32_t m_Height = 0;
		float m_Radius = -1.f;
		std::vector<std::pair<int, int>> m_Mask;
		std::vector<uint8_t> m_Cells;
	};

	// The saved form of a scene's explored cells: "x,y;x,y;..." in cell units, sorted so the text is stable.
	inline std::string EncodeCells(const std::unordered_set<int64_t>& cells)
	{
		std::vector<int64_t> sorted(cells.begin(), cells.end());
		std::sort(sorted.begin(), sorted.end());
		std::string out;
		out.reserve(sorted.size() * 9);
		for (int64_t key : sorted)
		{
			if (!out.empty())
				out += ';';
			out += std::to_string(CellX(key));
			out += ',';
			out += std::to_string(CellY(key));
		}
		return out;
	}

	// ...and back. Anything malformed is skipped rather than failing the whole scene. Returns the cells added.
	inline size_t DecodeCells(const std::string& text, std::unordered_set<int64_t>& out)
	{
		size_t added = 0;
		const char* p = text.data();
		const char* const end = p + text.size();
		while (p < end)
		{
			const char* const sep = std::find(p, end, ';');
			const char* const comma = std::find(p, sep, ',');
			if (comma != sep)
			{
				int32_t x = 0, y = 0;
				const auto rx = std::from_chars(p, comma, x);
				const auto ry = std::from_chars(comma + 1, sep, y);
				if (rx.ec == std::errc() && rx.ptr == comma && ry.ec == std::errc() && ry.ptr == sep)
					added += out.insert(CellKey(x, y)).second ? 1 : 0;
			}
			p = sep == end ? end : sep + 1;
		}
		return added;
	}
}
