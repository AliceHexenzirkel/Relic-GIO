#include "pch-il2cpp.h"
#include "NavBake.h"

#if RELIC_GAME_VERSION == 28

#include <helpers.h>
#include <cheat/events.h>
#include <cheat/game/EntityManager.h>
#include <cheat/game/util.h>
#include <cheat/teleport/MapTeleport.h>
#include <cheat-base/relic-guard.h>

#include <chrono>
#include <cmath>
#include <cstring>
#include <fstream>
#include <memory>
#include <stdexcept>

namespace cheat::feature
{
	// Relic: why the bake is done inside the game, and how.
	//
	// The pathfindingserver answers the client's path queries from navmesh files the server package ships, one
	// set per scene; the archipelago of this version has none, and nothing public offers one. A navmesh is baked
	// from collision geometry, and the only complete source of that geometry is the running game: the rocks and
	// structures the islands are made of are placed by the engine's own streaming data, which no extractor reads,
	// while PhysX holds every collider of the area the player stands in. So the bake happens here, with the
	// engine's own builder (UnityEngine.AI.NavMeshBuilder, kept by this build for the Serenitea Pot) and the
	// settings the vendor's files carry (agent radius 0.25, height 1.6, slope 60, climb 0.4, minimum region area
	// 36, voxel 0.125, tile 128 voxels), over a box of the world. The tiles that come out are of the server's own
	// version, 17, with the per-polygon region array at their end - but the builder leaves every entry of that
	// array unset (0xFFFFFFFF), and build/navmesh_tool.py fromunity computes the region ids.
	//
	// The builder's output lives in the native NavMeshData object (the managed one only wraps it), so the tiles
	// are read from the native object: its tile array is found by its content - the first tile's bytes start with
	// the 'DNAV' magic and the tile version - and every entry is checked before a byte is copied.
	//
	// Threads: everything that touches the game runs in OnGameUpdate, on the game thread. The hotkey only raises
	// a flag; DrawMain only reads a note. A bake runs on the engine's worker threads (UpdateNavMeshDataAsync) and
	// is polled, so the game keeps running meanwhile.
	//
	// Files, under <mod folder>/navbake/: cmd.json (one command, {"id": n, "op": "status" | "layers" | "navkey" |
	// "polygons" | "teleport" | "bake" | "peek", ...}, n an integer other than 0), result.json (the answer, with
	// the same id), status.json (the player's position and scene, twice a second), <stem>.tiles (int32 count,
	// then int32 size + bytes per tile - the fromunity input) and <stem>.json (the box, the player's position and
	// the counts of that bake); polygons_<scene>.json is the client's polygon table as the "polygons" op read it.
	//
	// A command is served once, and only when it was written while commands are being served. The cmd.json that
	// is there when serving begins - the game starts with the option on, or the box is ticked - was written for
	// an earlier session, another scene or another state of the islands: it is noted and never run. A driver
	// takes a fresh status.json as its leave to write, so the note is taken before the first status goes out.
	//
	// A bake's final answer and its <stem>.json say how it ended, in "outcome": "tiles"; "noSources" - the
	// collector found no collider in the box, and the unit is written empty; "noTileArray" - there were colliders
	// but no tile array is recognised in the native object, which is ground nobody can walk on or a layout this
	// code does not know: the unit is written empty and <stem>.native.bin holds the object's bytes; "truncated" -
	// more tiles than one bake carries: the answer is not ok and nothing is written; "failed". A unit is both of
	// its files or neither, and a bake that is not ok writes none.

	namespace
	{
		// ---- the engine API this build keeps (addresses from the 2.8 dump) ----
		struct Bounds { app::Vector3 center; app::Vector3 extents; };

		struct BuildSettings
		{
			int32_t agentTypeID;
			float agentRadius, agentHeight, agentSlope, agentClimb, ledgeDropHeight, maxJumpAcrossDistance, minRegionArea;
			int32_t overrideVoxelSize;
			float voxelSize;
			int32_t overrideTileSize, tileSize, accuratePlacement, generateDetailMap;
			uint8_t debugFlags;
			uint8_t pad[3];
		};
		static_assert(sizeof(BuildSettings) == 60, "NavMeshBuildSettings: 14 ints and floats plus the debug flags byte");

		using Fn_CollectSources  = void (*)(Bounds, int32_t, int32_t, int32_t, Il2CppObject*, Il2CppObject*, MethodInfo*);
		using Fn_UpdateAsync     = Il2CppObject* (*)(Il2CppObject*, BuildSettings, Il2CppObject*, Bounds, int32_t, MethodInfo*);
		using Fn_UpdateSync      = bool (*)(Il2CppObject*, BuildSettings, Il2CppObject*, Bounds, int32_t, MethodInfo*);
		using Fn_GetSettingsByID = BuildSettings (*)(int32_t, MethodInfo*);
		using Fn_IsDone          = bool (*)(Il2CppObject*, MethodInfo*);
		using Fn_LayerToName     = app::String* (*)(int32_t, MethodInfo*);
		using Fn_Destroy         = void (*)(Il2CppObject*, MethodInfo*);
		using Fn_Cancel          = void (*)(Il2CppObject*, MethodInfo*);
		using Fn_SetHideFlags    = void (*)(Il2CppObject*, int32_t, MethodInfo*);

		constexpr uintptr_t RVA_CollectSources  = 0x583E930;   // NavMeshBuilder.CollectSources(Bounds, int layerMask, NavMeshCollectGeometry, int defaultArea, List<NavMeshBuildMarkup>, List<NavMeshBuildSource>)
		constexpr uintptr_t RVA_UpdateAsync     = 0x583EDA0;   // NavMeshBuilder.UpdateNavMeshDataAsync(NavMeshData, NavMeshBuildSettings, List<NavMeshBuildSource>, Bounds, int cullingAreaMask)
		constexpr uintptr_t RVA_UpdateSync      = 0x583EF70;   // NavMeshBuilder.UpdateNavMeshData(the same arguments)
		constexpr uintptr_t RVA_Cancel          = 0x583E920;   // NavMeshBuilder.Cancel(NavMeshData)
		constexpr uintptr_t RVA_GetSettingsByID = 0x583F5F0;   // NavMesh.GetSettingsByID(int)
		constexpr uintptr_t RVA_IsDone          = 0x57991E0;   // AsyncOperation.get_isDone()
		constexpr uintptr_t RVA_LayerToName     = 0x583BB30;   // LayerMask.LayerToName(int)
		constexpr uintptr_t RVA_Destroy         = 0x57B6B80;   // Object.Destroy(Object)
		constexpr uintptr_t RVA_SetHideFlags    = 0x57B73E0;   // Object.set_hideFlags(HideFlags), an instance method: the object, then the value

		template <class T> T Rva(uintptr_t rva) { return reinterpret_cast<T>(il2cppi_get_base_address() + rva); }

		constexpr int32_t GEOMETRY_RENDER_MESHES = 0;        // NavMeshCollectGeometry.RenderMeshes (readable meshes only)
		constexpr int32_t GEOMETRY_PHYSICS_COLLIDERS = 1;    // NavMeshCollectGeometry.PhysicsColliders
		constexpr int32_t DEFAULT_AREA = 5;                  // every poly of the vendor's files carries area 5 (flags 1 << 5)
		constexpr uint32_t TILE_MAGIC = 0x444E4156;          // 'DNAV' as the tile stores it (bytes 56 41 4E 44)
		constexpr int32_t TILE_VERSION_UNITY = 16;
		constexpr int32_t TILE_VERSION_SERVER = 17;
		constexpr size_t TILE_HEADER = 72;
		constexpr size_t TILE_MAX = size_t(64) << 20;
		constexpr size_t TILES_CAP = 65536;
		constexpr int64_t BAKE_TIMEOUT_MS = 15 * 60 * 1000;
		constexpr size_t OBJECT_CACHED_PTR = 0x10;           // UnityEngine.Object.m_CachedPtr, the native object
		constexpr size_t LIST_SIZE = 0x18;                   // System.Collections.Generic.List<T>._size
		constexpr int32_t HIDE_DONT_UNLOAD_UNUSED_ASSET = 32;    // HideFlags.DontUnloadUnusedAsset
		constexpr float EXTENT_MAX_XZ = 2048.0f;             // half a box, in metres
		constexpr float EXTENT_MAX_Y = 5000.0f;
		constexpr float WORLD_MAX = 100000.0f;               // no scene reaches 100 km from its origin
		constexpr uintmax_t COMMAND_MAX = 1 << 20;           // bytes; a command is a few hundred
		constexpr size_t TEXT_MAX = 300;                     // characters of outside text a log line or an answer carries

		// The managed classes, looked up once per session.
		struct Classes
		{
			bool tried = false;
			Il2CppClass* navMeshData = nullptr;
			Il2CppClass* markupList = nullptr;
			Il2CppClass* sourceList = nullptr;
			std::string error;
		};
		Classes g_Classes;

		Il2CppClass* FindClass(const char* ns, const char* name)
		{
			size_t count = 0;
			const Il2CppAssembly** assemblies = il2cpp_domain_get_assemblies(il2cpp_domain_get(), &count);
			for (size_t i = 0; assemblies != nullptr && i < count; i++)
			{
				const Il2CppImage* image = il2cpp_assembly_get_image(assemblies[i]);
				if (image == nullptr)
					continue;
				Il2CppClass* klass = il2cpp_class_from_name(image, ns, name);
				if (klass != nullptr)
					return klass;
			}
			return nullptr;
		}

		// The two List<T> classes are taken from the builder's own signature: CollectSources(..., List<NavMeshBuildMarkup>,
		// List<NavMeshBuildSource>) - the only place where those generic instances are certain to exist.
		bool ResolveClasses()
		{
			if (g_Classes.tried)
				return g_Classes.navMeshData != nullptr;
			g_Classes.tried = true;
			bool ok = relic::Try([&]()
			{
				Il2CppClass* builder = FindClass("UnityEngine.AI", "NavMeshBuilder");
				Il2CppClass* data = FindClass("UnityEngine.AI", "NavMeshData");
				if (builder == nullptr || data == nullptr)
				{
					g_Classes.error = "UnityEngine.AI.NavMeshBuilder / NavMeshData are not in this build";
					return;
				}
				const MethodInfo* collect = il2cpp_class_get_method_from_name(builder, "CollectSources", 6);
				if (collect == nullptr)
				{
					g_Classes.error = "NavMeshBuilder.CollectSources is not in this build";
					return;
				}
				const Il2CppType* markups = il2cpp_method_get_param(collect, 4);
				const Il2CppType* sources = il2cpp_method_get_param(collect, 5);
				Il2CppClass* markupList = markups ? il2cpp_class_from_il2cpp_type(markups) : nullptr;
				Il2CppClass* sourceList = sources ? il2cpp_class_from_il2cpp_type(sources) : nullptr;
				if (markupList == nullptr || sourceList == nullptr)
				{
					g_Classes.error = "the builder's list types could not be resolved";
					return;
				}
				g_Classes.markupList = markupList;
				g_Classes.sourceList = sourceList;
				g_Classes.navMeshData = data;
			});
			if (!ok)
				g_Classes.error = fmt::format("the class lookup faulted (0x{:08X})", relic::g_lastGuardCode);
			if (g_Classes.navMeshData == nullptr)
				LOG_WARNING("[navbake] %.300s", g_Classes.error.c_str());
			return g_Classes.navMeshData != nullptr;
		}

		// A new managed object, held by a GC handle from before its constructor runs: its only other reference
		// is kept in C++ memory the collector does not scan, and any allocation - the constructor's own, the next
		// object's - can start a collection.
		Il2CppObject* NewObject(Il2CppClass* klass, uint32_t& handle)
		{
			Il2CppObject* obj = il2cpp_object_new(klass);
			if (obj == nullptr)
				return nullptr;
			handle = il2cpp_gchandle_new(obj, false);
			il2cpp_runtime_object_init(obj);   // the parameterless constructor (NavMeshData's creates the native object)
			return obj;
		}

		int32_t ListCount(Il2CppObject* list)
		{
			return list == nullptr ? 0 : *reinterpret_cast<const int32_t*>(reinterpret_cast<const uint8_t*>(list) + LIST_SIZE);
		}

		bool Readable(const void* p, size_t n);

		// How many sources of each shape the collector found (NavMeshBuildSource: Matrix4x4, Vector3 size, int shape,
		// int area, int instance id, int component id = 92 bytes, inline in the list's array; shapes: 0 mesh, 1 terrain,
		// 2 box, 3 sphere, 4 capsule, 5 modifier box).
		nlohmann::json SourceShapes(Il2CppObject* list)
		{
			nlohmann::json out = nlohmann::json::object();
			if (list == nullptr)
				return out;
			const uint8_t* items = *reinterpret_cast<const uint8_t* const*>(reinterpret_cast<const uint8_t*>(list) + 0x10);
			int32_t count = ListCount(list);
			if (items == nullptr || count <= 0)
				return out;
			static const char* names[] = { "mesh", "terrain", "box", "sphere", "capsule", "modifierBox" };
			int counts[7] = { 0, 0, 0, 0, 0, 0, 0 };
			const size_t stride = 92, shapeOff = 76, header = 0x20;
			if (!Readable(items + header, stride * static_cast<size_t>(count)))
				return out;
			for (int32_t i = 0; i < count; i++)
			{
				int32_t shape = *reinterpret_cast<const int32_t*>(items + header + i * stride + shapeOff);
				counts[(shape >= 0 && shape < 6) ? shape : 6]++;
			}
			for (int i = 0; i < 6; i++)
				if (counts[i])
					out[names[i]] = counts[i];
			if (counts[6])
				out["other"] = counts[6];
			return out;
		}

		// ---- reading the tiles out of the native NavMeshData ----

		// Every byte of [p, p + n) is committed, readable memory of this process.
		bool Readable(const void* p, size_t n)
		{
			const uint8_t* cur = static_cast<const uint8_t*>(p);
			const uint8_t* end = cur + n;
			if (p == nullptr || n == 0 || end < cur)
				return false;
			while (cur < end)
			{
				MEMORY_BASIC_INFORMATION mbi;
				if (VirtualQuery(cur, &mbi, sizeof(mbi)) == 0)
					return false;
				if (mbi.State != MEM_COMMIT || (mbi.Protect & PAGE_GUARD) || (mbi.Protect & PAGE_NOACCESS))
					return false;
				DWORD access = mbi.Protect & 0xFF;
				if (access != PAGE_READONLY && access != PAGE_READWRITE && access != PAGE_WRITECOPY &&
					access != PAGE_EXECUTE_READ && access != PAGE_EXECUTE_READWRITE && access != PAGE_EXECUTE_WRITECOPY)
					return false;
				cur = static_cast<const uint8_t*>(mbi.BaseAddress) + mbi.RegionSize;
			}
			return true;
		}

		struct TileRef { const uint8_t* data; size_t size; };
		struct TileScan { size_t count = 0; size_t offset = 0; size_t label = 0; size_t stride = 0; size_t dataOff = 0; };

		// A tile of this engine: the magic and a version it writes (16 is stock Unity's; 17 - the server's, with the
		// per-polygon region array at the end - is what this build writes).
		bool TileAt(const uint8_t* data)
		{
			if (data == nullptr || !Readable(data, TILE_HEADER) || *reinterpret_cast<const uint32_t*>(data) != TILE_MAGIC)
				return false;
			int32_t version = *reinterpret_cast<const int32_t*>(data + 4);
			return version == TILE_VERSION_UNITY || version == TILE_VERSION_SERVER;
		}

		// Every entry of a candidate tile array checked: readable, the magic, the version, a sane size.
		size_t CheckEntries(const uint8_t* entries, size_t count, size_t stride, size_t dataOff, size_t sizeOff, TileRef* out, size_t cap)
		{
			if (count < 1 || count > 200000 || !Readable(entries, stride * count))
				return 0;
			size_t n = 0;
			for (size_t i = 0; i < count; i++)
			{
				const uint8_t* e = entries + i * stride;
				const uint8_t* data = *reinterpret_cast<const uint8_t* const*>(e + dataOff);
				size_t size = *reinterpret_cast<const size_t*>(e + sizeOff);
				if (size < TILE_HEADER || size > TILE_MAX || !TileAt(data) || !Readable(data, size))
					return 0;
				if (n < cap)
				{
					out[n].data = data;
					out[n].size = size;
				}
				n++;
			}
			return n;
		}

		// The native NavMeshData holds its tiles in a container of NavMeshTileData entries; the player build seen
		// keeps them as { begin, end, end of storage } (a vector), with every entry = the mesh data (its own
		// { data, label (8 bytes), size, capacity }) followed by a 16-byte Hash128 - 48 bytes. The dynamic_array
		// shape ({ data, label, size, capacity }), a 16-byte label and the hash-first order are tried as well. The
		// container is found by its content: a word of the object pointing at entries whose mesh data begins with
		// the tile magic and version; every entry is then checked before a byte is copied. Plain data only in here:
		// it runs under the SEH guard, and a build without /EHa does not unwind C++ objects past a fault.
		size_t FindTiles(const void* native, TileRef* out, size_t cap, TileScan& scan)
		{
			const uint8_t* base = static_cast<const uint8_t*>(native);
			for (size_t off = 0x10; off + 40 <= 0x400; off += 8)
			{
				if (!Readable(base + off, 40))
					break;
				const uint8_t* entries = *reinterpret_cast<const uint8_t* const*>(base + off);
				if (entries == nullptr || (reinterpret_cast<uintptr_t>(entries) & 7) != 0 || !Readable(entries, 56))
					continue;
				for (size_t dataOff = 0; dataOff <= 16; dataOff += 16)
				{
					if (!TileAt(*reinterpret_cast<const uint8_t* const*>(entries + dataOff)))
						continue;
					for (size_t label = 8; label <= 16; label += 8)
					{
						size_t stride = 16 + 8 + label + 16;
						size_t sizeOff = dataOff + 8 + label;
						// a vector: begin, end (the capacity pointer follows)
						const uint8_t* endp = *reinterpret_cast<const uint8_t* const*>(base + off + 8);
						if (endp > entries && (static_cast<size_t>(endp - entries) % stride) == 0)
						{
							size_t n = CheckEntries(entries, static_cast<size_t>(endp - entries) / stride, stride, dataOff, sizeOff, out, cap);
							if (n)
							{
								scan.count = n;
								scan.offset = off;
								scan.label = label;
								scan.stride = stride;
								scan.dataOff = dataOff;
								return n;
							}
						}
						// a dynamic_array: data, label, size, capacity
						size_t arraySize = *reinterpret_cast<const size_t*>(base + off + 8 + label);
						size_t capacity = *reinterpret_cast<const size_t*>(base + off + 16 + label);
						if (arraySize >= 1 && arraySize <= capacity && capacity <= 200000)
						{
							size_t n = CheckEntries(entries, arraySize, stride, dataOff, sizeOff, out, cap);
							if (n)
							{
								scan.count = n;
								scan.offset = off;
								scan.label = label;
								scan.stride = stride;
								scan.dataOff = dataOff;
								return n;
							}
						}
					}
				}
			}
			return 0;
		}

		// When no tile array is recognised: the object's first 0x400 bytes and, one level down, 0x100 bytes behind
		// every word of it that points at readable memory - enough to read the layout off line.
		// Format: "NAVB", u32 object bytes, the bytes; then records { u32 offset, u64 pointer, u32 n, n bytes }.
		void DumpNative(const void* native, std::string& out)
		{
			const uint8_t* base = static_cast<const uint8_t*>(native);
			size_t head = 0;
			while (head < 0x400 && Readable(base + head, 8))
				head += 8;
			out.append("NAVB", 4);
			uint32_t n = static_cast<uint32_t>(head);
			out.append(reinterpret_cast<const char*>(&n), 4);
			out.append(reinterpret_cast<const char*>(base), head);
			for (size_t off = 0; off + 8 <= head; off += 8)
			{
				const uint8_t* p = *reinterpret_cast<const uint8_t* const*>(base + off);
				if (p == nullptr || (reinterpret_cast<uintptr_t>(p) & 7) != 0 || !Readable(p, 0x100))
					continue;
				uint32_t o = static_cast<uint32_t>(off);
				uint64_t q = reinterpret_cast<uint64_t>(p);
				uint32_t len = 0x100;
				out.append(reinterpret_cast<const char*>(&o), 4);
				out.append(reinterpret_cast<const char*>(&q), 8);
				out.append(reinterpret_cast<const char*>(&len), 4);
				out.append(reinterpret_cast<const char*>(p), len);
			}
		}

		// ---- the client's own polygon table and the (polygon, scene tag hash) pair it sends ----
		// Two static classes of the game (obfuscated names, addresses from the 2.8 dump): the navmesh polygon
		// manager holds the loaded polygons of a polygon-mode scene (id, outline, height band, the scene tags that
		// belong to it and every tag hash a file may carry), the pathfinding plugin the pair last sent to the server.
		// Both are read only, by offset, after the class name proved the slot.
		constexpr uintptr_t RVA_TYPEINFO_POLYGONS = 0x09991AA0;      // KJABMFKPLCL__TypeInfo
		constexpr uintptr_t RVA_TYPEINFO_PATHFINDING = 0x09972E50;   // GAGNKDLKKFD__TypeInfo
		constexpr size_t CLASS_NAME = 0x10;            // Il2CppClass: image, gc_desc, name
		constexpr size_t CLASS_STATIC_FIELDS = 0xB8;   // Il2CppClass::static_fields
		constexpr size_t ARRAY_LENGTH = 0x18;          // an il2cpp array: klass, monitor, bounds, length, then the elements
		constexpr size_t ARRAY_DATA = 0x20;

		const uint8_t* ClassStatics(uintptr_t typeInfoRva, const char* expectedName)
		{
			Il2CppClass** slot = reinterpret_cast<Il2CppClass**>(il2cppi_get_base_address() + typeInfoRva);
			if (!Readable(slot, 8) || *slot == nullptr)
				return nullptr;
			const uint8_t* klass = reinterpret_cast<const uint8_t*>(*slot);
			if (!Readable(klass, CLASS_STATIC_FIELDS + 8))
				return nullptr;
			const char* name = *reinterpret_cast<const char* const*>(klass + CLASS_NAME);
			size_t len = strlen(expectedName);
			if (name == nullptr || !Readable(name, len + 1) || strncmp(name, expectedName, len + 1) != 0)
				return nullptr;
			const uint8_t* statics = *reinterpret_cast<const uint8_t* const*>(klass + CLASS_STATIC_FIELDS);
			return (statics != nullptr && Readable(statics, 0x40)) ? statics : nullptr;
		}

		// The polygon table stores its ids, tags and hashes as the game's obfuscated 4-byte integers; the game's own
		// getter turns one into its value (a pure call, safe on the game thread).
		uint32_t SafeValue(uint32_t raw)
		{
			app::SimpleSafeUInt32 v;
			v._value = raw;
			return app::MoleMole_SimpleSafeUInt32_get_Value(v, nullptr);
		}

		nlohmann::json UIntArray(const uint8_t* arr, size_t limit, bool obfuscated)
		{
			nlohmann::json out = nlohmann::json::array();
			if (arr == nullptr || !Readable(arr, ARRAY_DATA))
				return out;
			size_t n = *reinterpret_cast<const size_t*>(arr + ARRAY_LENGTH);
			if (n > limit || !Readable(arr + ARRAY_DATA, n * 4))
				return out;
			for (size_t i = 0; i < n; i++)
			{
				uint32_t raw = *reinterpret_cast<const uint32_t*>(arr + ARRAY_DATA + i * 4);
				out.push_back(obfuscated ? SafeValue(raw) : raw);
			}
			return out;
		}

		// The pair the client last sent (polygon id, scene tag hash) and its modes; empty when the class is not there.
		nlohmann::json ReadNavKey()
		{
			nlohmann::json out = nlohmann::json::object();
			const uint8_t* pf = ClassStatics(RVA_TYPEINFO_PATHFINDING, "GAGNKDLKKFD");
			if (pf != nullptr)
			{
				out["tagHash"] = *reinterpret_cast<const uint32_t*>(pf + 0x30);
				out["polygonId"] = *reinterpret_cast<const uint32_t*>(pf + 0x34);
				out["pathfindingMode"] = *reinterpret_cast<const int32_t*>(pf + 0x38);
				out["navmeshMode"] = *reinterpret_cast<const int32_t*>(pf + 0x3C);
			}
			const uint8_t* pm = ClassStatics(RVA_TYPEINFO_POLYGONS, "KJABMFKPLCL");
			if (pm != nullptr)
			{
				out["currentPolygonIndex"] = *reinterpret_cast<const int32_t*>(pm + 0x20);
				out["activeSceneTags"] = UIntArray(*reinterpret_cast<const uint8_t* const*>(pm + 0x28), 256, false);
			}
			return out;
		}

		// Every polygon of the loaded polygon-mode scene: id, outline (x, y, z of each point), height band, the
		// scene tags that belong to it and the tag hashes the client may send for it.
		nlohmann::json ReadPolygons(std::string& error)
		{
			const uint8_t* pm = ClassStatics(RVA_TYPEINFO_POLYGONS, "KJABMFKPLCL");
			if (pm == nullptr)
			{
				error = "the polygon manager class is not where this build expects it";
				return nlohmann::json();
			}
			const uint8_t* data = *reinterpret_cast<const uint8_t* const*>(pm);
			if (data == nullptr || !Readable(data, 0x20))
			{
				error = "no polygon data loaded (not in a polygon-mode scene?)";
				return nlohmann::json();
			}
			const uint8_t* arr = *reinterpret_cast<const uint8_t* const*>(data + 0x18);
			if (arr == nullptr || !Readable(arr, ARRAY_DATA))
			{
				error = "the polygon data holds no array";
				return nlohmann::json();
			}
			size_t n = *reinterpret_cast<const size_t*>(arr + ARRAY_LENGTH);
			if (n > 4096 || (n != 0 && !Readable(arr + ARRAY_DATA, n * 8)))
			{
				error = "the polygon array is not readable";
				return nlohmann::json();
			}
			// The scene id, the polygon ids, the tag ids and the hashes are obfuscated integers; the height band is
			// an obfuscated float this code does not decode (raw values kept for the record).
			nlohmann::json out = { { "scene", SafeValue(*reinterpret_cast<const uint32_t*>(data + 0x10)) }, { "polygons", nlohmann::json::array() } };
			for (size_t i = 0; i < n; i++)
			{
				const uint8_t* p = *reinterpret_cast<const uint8_t* const*>(arr + ARRAY_DATA + i * 8);
				if (p == nullptr || !Readable(p, 0x48))
					continue;
				nlohmann::json poly = {
					{ "id", SafeValue(*reinterpret_cast<const uint32_t*>(p + 0x10)) },
					{ "h0raw", *reinterpret_cast<const uint32_t*>(p + 0x20) },
					{ "h1raw", *reinterpret_cast<const uint32_t*>(p + 0x24) },
					{ "sceneTagIds", UIntArray(*reinterpret_cast<const uint8_t* const*>(p + 0x28), 256, true) },
					{ "validTagHashes", UIntArray(*reinterpret_cast<const uint8_t* const*>(p + 0x30), 4096, true) },
					{ "points", nlohmann::json::array() } };
				const uint8_t* pts = *reinterpret_cast<const uint8_t* const*>(p + 0x18);
				if (pts != nullptr && Readable(pts, ARRAY_DATA))
				{
					size_t m = *reinterpret_cast<const size_t*>(pts + ARRAY_LENGTH);
					if (m <= 100000 && Readable(pts + ARRAY_DATA, m * 12))
						for (size_t k = 0; k < m; k++)
						{
							const float* v = reinterpret_cast<const float*>(pts + ARRAY_DATA + k * 12);
							poly["points"].push_back(nlohmann::json::array({ v[0], v[1], v[2] }));
						}
				}
				out["polygons"].push_back(poly);
			}
			return out;
		}

		std::string Utf8(const std::filesystem::path& p)
		{
			auto s = p.u8string();
			return std::string(s.begin(), s.end());
		}

		// Outside text - a parse error that quotes the file, a system message - cut to what a log line and an
		// answer carry.
		std::string Brief(const std::string& text)
		{
			return text.size() <= TEXT_MAX ? text : text.substr(0, TEXT_MAX) + "...";
		}

		// The text of a JSON file. A string that is not UTF-8 - a path or a system message in the machine's code
		// page - is written with replacement characters instead of failing the whole document.
		std::string Dump(const nlohmann::json& j, int indent = -1)
		{
			return j.dump(indent, ' ', false, nlohmann::json::error_handler_t::replace);
		}

		// A whole-or-nothing write: the bytes go to <path>.tmp, which then takes the file's place, so a reader
		// never sees a half file. A reader that has the file open at that moment can make the replace fail; the
		// caller decides whether to try again.
		bool WriteFileAtomic(const std::filesystem::path& path, const std::string& bytes, std::string& error)
		{
			std::filesystem::path tmp = path;
			tmp += ".tmp";
			std::error_code ec, ignored;
			{
				std::ofstream out(tmp, std::ios::binary | std::ios::trunc);
				if (!out)
				{
					error = "cannot write " + Utf8(tmp);
					return false;
				}
				out.write(bytes.data(), static_cast<std::streamsize>(bytes.size()));
				out.close();   // the last buffer goes out here, and a full disk may show only here
				if (!out)
				{
					std::filesystem::remove(tmp, ignored);
					error = "write failed: " + Utf8(tmp);
					return false;
				}
			}
			std::filesystem::rename(tmp, path, ec);
			if (ec.value() == ERROR_ACCESS_DENIED)
			{
				// The file is read-only, or a process has it open. Removing it first gets past a read-only file
				// and past a reader that shares the deletion; a reader that does not - the driver's - refuses
				// both, and the file stays as it is. Any other failure is not about the file in the way, which
				// then must not be removed.
				std::filesystem::remove(path, ignored);
				std::filesystem::rename(tmp, path, ec);
			}
			if (ec)
			{
				error = fmt::format("cannot replace {}: error {} ({})", Utf8(path), ec.value(), ec.message());
				std::filesystem::remove(tmp, ignored);
				return false;
			}
			return true;
		}

		int64_t NowMs()
		{
			return static_cast<int64_t>(GetTickCount64());
		}

		// The clock a bake's time-out runs on: the milliseconds the system has been awake. A machine that was
		// suspended in the middle of a bake has not been building for that long.
		int64_t AwakeMs()
		{
			ULONGLONG ticks = 0;   // of 100 ns
			if (!QueryUnbiasedInterruptTime(&ticks))
				return NowMs();
			return static_cast<int64_t>(ticks / 10000);
		}

		nlohmann::json Vec(const app::Vector3& v)
		{
			return nlohmann::json::array({ v.x, v.y, v.z });
		}

		nlohmann::json Vec(const float v[3])
		{
			return nlohmann::json::array({ v[0], v[1], v[2] });
		}

		enum class VecField { Absent, Good, Bad };

		// [x, y, z] under `key`: three numbers a float holds.
		VecField ReadVec(const nlohmann::json& j, const char* key, float out[3])
		{
			auto it = j.find(key);
			if (it == j.end())
				return VecField::Absent;
			if (!it->is_array() || it->size() != 3)
				return VecField::Bad;
			for (size_t i = 0; i < 3; i++)
			{
				const nlohmann::json& v = (*it)[i];
				if (!v.is_number())
					return VecField::Bad;
				double d = v.get<double>();
				if (!(std::fabs(d) <= 1.0e30))
					return VecField::Bad;
				out[i] = static_cast<float>(d);
			}
			return VecField::Good;
		}

		// A place a scene can have. The engine turns a position into voxel and tile indices, and what it does
		// with one that lies far outside every scene is not known.
		bool InWorld(const float p[3])
		{
			for (int i = 0; i < 3; i++)
				if (!(std::fabs(p[i]) <= WORLD_MAX))   // a NaN fails the comparison too
					return false;
			return true;
		}

		// The box of a bake: at a place a scene can have, of a size the builder can be asked for.
		bool CheckBox(const float center[3], const float extents[3], std::string& error)
		{
			if (!InWorld(center))
			{
				error = fmt::format("center: within {:g} m of the origin on every axis", WORLD_MAX);
				return false;
			}
			for (int i = 0; i < 3; i++)
				if (!(extents[i] > 0.0f && extents[i] <= (i == 1 ? EXTENT_MAX_Y : EXTENT_MAX_XZ)))
				{
					error = fmt::format("extents: positive, at most {:g} m on x and z and {:g} m on y", EXTENT_MAX_XZ, EXTENT_MAX_Y);
					return false;
				}
			return true;
		}

		// A field of a command, or `fallback` when the command does not carry it. A value of another type is
		// refused by the key's name, which the JSON library's own message leaves out.
		template <class T>
		T Field(const nlohmann::json& cmd, const char* key, const T& fallback)
		{
			auto it = cmd.find(key);
			if (it == cmd.end())
				return fallback;
			try
			{
				return it->get<T>();
			}
			catch (const nlohmann::json::exception& e)
			{
				throw std::invalid_argument(fmt::format("{}: {}", key, e.what()));
			}
		}

		// A file stem a command may name: letters, digits, '_' and '-', none of the names Windows keeps for its
		// devices whatever extension follows them, and none of the channel's own files - <stem>.json would be one.
		bool SafeStem(const std::string& s)
		{
			if (s.empty() || s.size() > 64)
				return false;
			std::string upper;
			for (char c : s)
			{
				bool lower = c >= 'a' && c <= 'z';
				if (!(lower || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-'))
					return false;
				upper.push_back(lower ? static_cast<char>(c - 'a' + 'A') : c);
			}
			if (upper == "CON" || upper == "PRN" || upper == "AUX" || upper == "NUL")
				return false;
			if (upper.size() == 4 && (upper.compare(0, 3, "COM") == 0 || upper.compare(0, 3, "LPT") == 0) && upper[3] >= '0' && upper[3] <= '9')
				return false;
			if (upper == "CMD" || upper == "RESULT" || upper == "STATUS" || upper.compare(0, 9, "POLYGONS_") == 0)
				return false;
			return true;
		}

		enum class CommandFile { Ready, Later, Refused };

		// cmd.json as a poll finds it. Ready: a command - an object with an integer id other than 0. Later:
		// nothing whole to read yet - the file is empty, held by its writer or cut off in the middle; `problem`
		// carries the parser's words when there is text. Refused: a whole document that is not a command.
		CommandFile ReadCommand(const std::filesystem::path& path, nlohmann::json& cmd, std::string& problem)
		{
			problem.clear();
			std::error_code ec;
			uintmax_t size = std::filesystem::file_size(path, ec);
			if (ec)
				return CommandFile::Later;
			if (size > COMMAND_MAX)
			{
				problem = "cmd.json is larger than a command can be";
				return CommandFile::Refused;
			}
			std::string text;
			{
				std::ifstream in(path, std::ios::binary);
				if (!in)
					return CommandFile::Later;
				text.assign(std::istreambuf_iterator<char>(in), std::istreambuf_iterator<char>());
			}
			if (text.empty())
				return CommandFile::Later;
			try
			{
				cmd = nlohmann::json::parse(text);
			}
			catch (const std::exception& e)
			{
				problem = Brief(std::string("cmd.json: ") + e.what());
				return CommandFile::Later;
			}
			if (!cmd.is_object() || !cmd.contains("id") || !cmd["id"].is_number_integer() || cmd["id"].get<int64_t>() == 0)
			{
				problem = "a command is an object with an integer id other than 0 and an op";
				return CommandFile::Refused;
			}
			return CommandFile::Ready;
		}

		// The first manual_<n>, from `from` on, that no file of the folder carries: a key press never writes over
		// a bake that is already there, whichever session made it. 0 when every number is taken.
		int FreeManualNumber(const std::filesystem::path& dir, int from)
		{
			static const char* const kinds[] = { ".tiles", ".json", ".native.bin" };
			for (int n = from < 1 ? 1 : from; n <= 999999; n++)
			{
				std::string stem = fmt::format("manual_{:03d}", n);
				bool taken = false;
				for (const char* kind : kinds)
				{
					std::error_code ec;
					if (std::filesystem::exists(dir / (stem + kind), ec))
						taken = true;
				}
				if (!taken)
					return n;
			}
			return 0;
		}

		// A build that is given up while the engine's threads may still be writing into its NavMeshData is
		// cancelled first: the object is destroyed later. NavMeshBuilder.Cancel is what the game's own navmesh
		// plugin calls on its data when it shuts down. False when the call faulted.
		bool CancelBuild(Il2CppObject* data)
		{
			return relic::Try([&]() { Rva<Fn_Cancel>(RVA_Cancel)(data, nullptr); });
		}

		// How a bake ended, as its answer and its sidecar name it.
		enum class Outcome { Failed, Tiles, NoSources, NoTileArray, Truncated };

		const char* OutcomeName(Outcome outcome)
		{
			switch (outcome)
			{
			case Outcome::Tiles: return "tiles";
			case Outcome::NoSources: return "noSources";
			case Outcome::NoTileArray: return "noTileArray";
			case Outcome::Truncated: return "truncated";
			default: return "failed";
			}
		}
	}

	// The bake in flight: the managed objects (held by GC handles while the engine's threads use them), the
	// settings and the counts the result reports.
	struct NavBake::Inflight
	{
		BakeRequest req;
		Il2CppObject* markups = nullptr;
		Il2CppObject* sources = nullptr;
		Il2CppObject* data = nullptr;
		Il2CppObject* op = nullptr;
		uint32_t handles[4] = { 0, 0, 0, 0 };
		int32_t sourceCount = 0;
		nlohmann::json shapes;
		bool building = false;   // the engine was asked to build into `data`
		bool syncDone = false;
		bool syncResult = false;
		BuildSettings settings{};
		app::Vector3 player{};
		uint32_t scene = 0;
		int64_t startedMs = 0;
	};

	// How a bake ended: what its answer, its sidecar, the log and the note say.
	struct NavBake::BakeReport
	{
		bool ok = false;
		std::string error;
		Outcome outcome = Outcome::Failed;
		size_t found = 0;          // tiles in the native object
		size_t tiles = 0;          // tiles in the unit that was written
		size_t bytes = 0;
		std::string file;          // <stem>.tiles, once the unit is whole
		std::string dump;          // <stem>.native.bin, when one was written
		TileScan scan;
		const void* native = nullptr;
		int64_t elapsedMs = 0;
	};

	NavBake::NavBake() : Feature(),
		NF(f_Enabled, "World::NavBake", false),
		NF(f_BakeKey, "World::NavBake", Hotkey()),
		NF(f_BoxSize, "World::NavBake", 256.0f),
		NF(f_BoxHeight, "World::NavBake", 600.0f),
		NF(f_LayerMask, "World::NavBake", GAME_BAKE_LAYERS),
		NF(f_CullMask, "World::NavBake", 0),
		m_Dir(util::GetCurrentPath() / "navbake")
	{
		f_BakeKey.value().PressedEvent += MY_METHOD_HANDLER(NavBake::OnBakeKey);
		events::GameUpdateEvent += MY_METHOD_HANDLER(NavBake::OnGameUpdate);
	}

	const FeatureGUIInfo& NavBake::GetGUIInfo() const
	{
		TRANSLATED_GROUP_INFO("Navmesh Bake", "World");
		return info;
	}

	NavBake& NavBake::GetInstance()
	{
		static NavBake instance;
		return instance;
	}

	// window thread
	void NavBake::OnBakeKey()
	{
		m_KeyPressed = true;
	}

	void NavBake::Note(std::string text)
	{
		std::lock_guard<std::mutex> lock(m_NoteMutex);
		m_LastNote = std::move(text);
	}

	// ------------------------------------------------------------------------------------------------ UI

	void NavBake::DrawMain()
	{
		ConfigWidget(_TR("Serve commands"), f_Enabled,
			_TR("Read navbake/cmd.json next to the mod and answer in result.json; status.json carries the player's position. A command that is already in the folder when this is ticked is not run."));
		ConfigWidget(_TR("Bake here"), f_BakeKey, true,
			_TR("Bake the box around the player into navbake/manual_<n>.tiles."));
		ConfigWidget(_TR("Box size (m)"), f_BoxSize, 16.0f, 32.0f, 1100.0f, _TR("The box the key bakes, on x and z."));
		ConfigWidget(_TR("Box height (m)"), f_BoxHeight, 50.0f, 50.0f, 3000.0f, _TR("...and on y."));
		ConfigWidget(_TR("Layer mask"), f_LayerMask, 1, INT_MIN, INT_MAX,
			_TR("The colliders' layers the key bakes. 2099456 = Terrain, SceneProp and ScenePropIgnoreCamera, the layers of the game's own bake; -1 = every layer, characters and monsters included."));
		ConfigWidget(_TR("Culling area mask"), f_CullMask, 1, INT_MIN, INT_MAX, _TR("Handed to the builder as it is; 0 keeps everything."));
		ImGui::TextWrapped("%s", (_TR("Folder: ") + Utf8(m_Dir)).c_str());
		std::string note;
		{
			std::lock_guard<std::mutex> lock(m_NoteMutex);
			note = m_LastNote;
		}
		if (!note.empty())
			ImGui::TextWrapped("%s", note.c_str());
	}

	bool NavBake::NeedStatusDraw() const
	{
		return m_Busy.load();
	}

	void NavBake::DrawStatus()
	{
		ImGui::Text("%s", _TR("Navmesh bake running"));
	}

	// ------------------------------------------------------------------------------------------------ game thread

	void NavBake::OnGameUpdate()
	{
		PollBake();
		ServeHotkey();

		int64_t now = NowMs();
		if (now - m_LastPollMs < 500)
			return;
		m_LastPollMs = now;
		FlushResult();
		if (!f_Enabled)
		{
			m_Serving = false;
			return;
		}

		std::error_code ec;
		std::filesystem::create_directories(m_Dir, ec);
		if (!m_Serving)
		{
			NoteStaleCommand();   // before the first status: a fresh status.json is a driver's leave to write
			m_Serving = true;
		}
		WriteStatus();
		ServeCommands();
	}

	void NavBake::ServeHotkey()
	{
		if (!m_KeyPressed.exchange(false))
			return;
		app::Vector3 pos{};
		if (!relic::Try([&]() { pos = app::ActorUtils_GetAvatarPos(nullptr); }))
		{
			Note(fmt::format("Bake failed to start: the position lookup faulted (0x{:08X})", relic::g_lastGuardCode));
			return;
		}
		std::error_code ec;
		std::filesystem::create_directories(m_Dir, ec);
		int number = FreeManualNumber(m_Dir, m_ManualNext);
		if (number == 0)
		{
			Note("Bake failed to start: the folder holds every manual_<n> there is");
			return;
		}
		m_ManualNext = number;   // taken again when this bake writes nothing
		BakeRequest req;
		req.id = -static_cast<int64_t>(number);
		req.key = true;
		req.center[0] = pos.x;
		req.center[1] = pos.y;
		req.center[2] = pos.z;
		req.extents[0] = f_BoxSize / 2.0f;
		req.extents[1] = f_BoxHeight / 2.0f;
		req.extents[2] = f_BoxSize / 2.0f;
		req.layerMask = f_LayerMask;
		req.cullMask = f_CullMask;
		req.out = fmt::format("manual_{:03d}", number);
		std::string error;
		if (!StartBake(req, error))
			Note("Bake failed to start: " + error);
	}

	void NavBake::WriteStatus()
	{
		nlohmann::json st;
		bool ok = relic::Try([&]()
		{
			st["pos"] = Vec(app::ActorUtils_GetAvatarPos(nullptr));
			st["scene"] = game::GetCurrentPlayerSceneID();
		});
		if (!ok)
			st["error"] = fmt::format("the position lookup faulted (0x{:08X})", relic::g_lastGuardCode);
		relic::Try([&]()
		{
			nlohmann::json key = ReadNavKey();
			if (key.contains("polygonId"))
			{
				st["polygonId"] = key["polygonId"];
				st["tagHash"] = key["tagHash"];
			}
		});
		st["busy"] = m_Inflight != nullptr;
		st["bakeId"] = m_Inflight ? m_Inflight->req.id : 0;
		st["lastCommandId"] = m_LastCommandId;
		st["tick"] = NowMs();
		std::string error;
		WriteFileAtomic(m_Dir / "status.json", Dump(st), error);
	}

	// The answer to a command, or the end of a bake. result.json is replaced whole, and a reader that has it open
	// at that moment makes the replace fail, so the text is kept until the file has it; a later answer takes an
	// unwritten one's place.
	void NavBake::WriteResult(const nlohmann::json& result)
	{
		m_PendingResult = Dump(result, 2);
		m_PendingResultLogged = false;
		FlushResult();
	}

	// Tried when the answer is made and at every poll after that, until result.json has it.
	void NavBake::FlushResult()
	{
		if (m_PendingResult.empty())
			return;
		std::string error;
		if (WriteFileAtomic(m_Dir / "result.json", m_PendingResult, error))
		{
			m_PendingResult.clear();
			return;
		}
		if (!m_PendingResultLogged)
		{
			m_PendingResultLogged = true;
			LOG_WARNING("[navbake] %.300s - trying again at every poll", error.c_str());
		}
	}

	// Serving begins: the cmd.json that is already there is noted - its id when it holds a command, its time
	// stamp for whatever is wrong with it - so that ServeCommands runs and reports only what is written from
	// here on.
	void NavBake::NoteStaleCommand()
	{
		std::filesystem::path cmdPath = m_Dir / "cmd.json";
		std::error_code ec;
		auto stamp = std::filesystem::last_write_time(cmdPath, ec);
		if (ec)
			return;   // no file: the first one written is a command to serve
		m_ReportedStamp = stamp;
		nlohmann::json cmd;
		std::string problem;
		if (ReadCommand(cmdPath, cmd, problem) == CommandFile::Ready)
			m_LastCommandId = cmd["id"].get<int64_t>();
	}

	// One command per file: cmd.json is read at every poll that finds it and served when its id is new - the id
	// alone tells two commands apart, a file system's time stamps may give two files written in a row the same
	// one. A file that does not parse may be one its writer has not finished: it is read again at the next poll,
	// and its problem is reported once per time stamp.
	void NavBake::ServeCommands()
	{
		std::filesystem::path cmdPath = m_Dir / "cmd.json";
		std::error_code ec;
		auto stamp = std::filesystem::last_write_time(cmdPath, ec);
		if (ec)
			return;

		nlohmann::json cmd;
		std::string problem;
		CommandFile state = ReadCommand(cmdPath, cmd, problem);
		if (state != CommandFile::Ready)
		{
			if (!problem.empty() && stamp != m_ReportedStamp)
			{
				m_ReportedStamp = stamp;
				LOG_WARNING("[navbake] %.300s", problem.c_str());
				if (m_PendingResult.empty())   // never in the place of an answer that is still to be written
					WriteResult({ { "id", 0 }, { "ok", false }, { "error", problem } });
			}
			return;
		}
		int64_t id = cmd["id"].get<int64_t>();
		if (id == m_LastCommandId)
			return;
		m_LastCommandId = id;
		try
		{
			Execute(cmd);
		}
		catch (const std::exception& e)
		{
			// A field of a type the op cannot read, or an answer that could not be put together. The command is
			// answered all the same - unless it is a bake that did start: that one answers when it ends.
			LOG_WARNING("[navbake] command %lld: %.300s", static_cast<long long>(id), e.what());
			if (m_Inflight == nullptr || m_Inflight->req.id != id)
				WriteResult({ { "id", id }, { "ok", false }, { "error", Brief(e.what()) } });
		}
	}

	void NavBake::Execute(const nlohmann::json& cmd)
	{
		int64_t id = cmd["id"].get<int64_t>();
		std::string op = Field(cmd, "op", std::string());
		nlohmann::json result = { { "id", id }, { "op", op }, { "ok", true } };

		if (op == "status" || op == "ping")
		{
			bool ok = relic::Try([&]()
			{
				result["pos"] = Vec(app::ActorUtils_GetAvatarPos(nullptr));
				result["scene"] = game::GetCurrentPlayerSceneID();
			});
			if (!ok)
			{
				result["ok"] = false;
				result["error"] = fmt::format("the position lookup faulted (0x{:08X})", relic::g_lastGuardCode);
			}
			result["busy"] = m_Inflight != nullptr;
		}
		else if (op == "navkey")
		{
			nlohmann::json key;
			bool ok = relic::Try([&]() { key = ReadNavKey(); });
			if (!ok)
			{
				result["ok"] = false;
				result["error"] = fmt::format("reading the pathfinding statics faulted (0x{:08X})", relic::g_lastGuardCode);
			}
			else
				result["navkey"] = key;
		}
		else if (op == "polygons")
		{
			nlohmann::json polys;
			std::string error;
			bool ok = relic::Try([&]() { polys = ReadPolygons(error); });
			if (!ok)
			{
				result["ok"] = false;
				result["error"] = fmt::format("reading the polygon table faulted (0x{:08X})", relic::g_lastGuardCode);
			}
			else if (!error.empty())
			{
				result["ok"] = false;
				result["error"] = error;
			}
			else
			{
				uint32_t scene = polys["scene"].get<uint32_t>();
				std::string name = fmt::format("polygons_{}.json", scene);
				std::string werr;
				if (WriteFileAtomic(m_Dir / name, Dump(polys, 1), werr))
					result["file"] = name;
				else
				{
					result["ok"] = false;
					result["error"] = werr;
				}
				result["scene"] = scene;
				result["count"] = polys["polygons"].size();
				nlohmann::json ids = nlohmann::json::array();
				for (auto& p : polys["polygons"])
					ids.push_back({ { "id", p["id"] }, { "points", p["points"].size() }, { "tags", p["sceneTagIds"] }, { "hashes", p["validTagHashes"].size() } });
				result["polygons"] = ids;
			}
		}
		else if (op == "peek")
		{
			// Diagnostics: a hex dump of readable process memory ({"addr": "0x...", "size": n}, at most 64 KB).
			std::string a = Field(cmd, "addr", std::string());
			int64_t size = Field(cmd, "size", int64_t(256));
			uint64_t addr = 0;
			try { addr = std::stoull(a, nullptr, 0); } catch (const std::exception&) { addr = 0; }
			const uint8_t* p = reinterpret_cast<const uint8_t*>(static_cast<uintptr_t>(addr));
			if (addr == 0 || size <= 0 || size > 65536 || !Readable(p, static_cast<size_t>(size)))
			{
				result["ok"] = false;
				result["error"] = "addr/size do not name readable memory";
			}
			else
			{
				static const char* digits = "0123456789abcdef";
				std::string hex;
				hex.reserve(static_cast<size_t>(size) * 2);
				bool read = relic::Try([&]()
				{
					for (int64_t i = 0; i < size; i++)
					{
						hex.push_back(digits[p[i] >> 4]);
						hex.push_back(digits[p[i] & 15]);
					}
				});
				if (read)
				{
					result["addr"] = a;
					result["hex"] = hex;
				}
				else
				{
					// readable when asked, gone while it was copied
					result["ok"] = false;
					result["error"] = fmt::format("reading the memory faulted (0x{:08X}) after {} bytes", relic::g_lastGuardCode, hex.size() / 2);
				}
			}
		}
		else if (op == "layers")
		{
			nlohmann::json names = nlohmann::json::array();
			bool ok = relic::Try([&]()
			{
				auto toName = Rva<Fn_LayerToName>(RVA_LayerToName);
				for (int32_t i = 0; i < 32; i++)
				{
					app::String* s = toName(i, nullptr);
					names.push_back(s ? il2cppi_to_string(s) : std::string());
				}
			});
			if (!ok)
			{
				result["ok"] = false;
				result["error"] = fmt::format("LayerMask.LayerToName faulted (0x{:08X})", relic::g_lastGuardCode);
			}
			result["layers"] = names;
		}
		else if (op == "teleport")
		{
			float pos[3];
			std::string mode = Field(cmd, "mode", std::string("auto"));
			if (ReadVec(cmd, "pos", pos) != VecField::Good)
			{
				result["ok"] = false;
				result["error"] = "teleport needs pos: [x, y, z]";
			}
			else if (!InWorld(pos))
			{
				result["ok"] = false;
				result["error"] = fmt::format("pos: within {:g} m of the origin on every axis", WORLD_MAX);
			}
			else
			{
				bool ok = relic::Try([&]()
				{
					auto& manager = game::EntityManager::instance();
					auto avatar = manager.avatar();
					if (avatar == nullptr || avatar->moveComponent() == nullptr)
					{
						result["ok"] = false;
						result["error"] = "no avatar - is the scene loaded?";
						return;
					}
					app::Vector3 target{ pos[0], pos[1], pos[2] };
					app::Vector3 cur = app::ActorUtils_GetAvatarPos(nullptr);
					float dist = app::Vector3_Distance(cur, target, nullptr);
					result["distance"] = dist;
					bool viaMap = mode == "map" || (mode != "direct" && dist > 60.0f);
					if (viaMap)
					{
						bool started = MapTeleport::GetInstance().TeleportTo(target);
						result["via"] = "map";
						if (!started)
						{
							result["ok"] = false;
							result["error"] = "no unlocked waypoint to go through";
						}
					}
					else
					{
						avatar->setAbsolutePosition(target);
						result["via"] = "direct";
					}
				});
				if (!ok)
				{
					result["ok"] = false;
					result["error"] = fmt::format("the teleport faulted (0x{:08X})", relic::g_lastGuardCode);
				}
			}
		}
		else if (op == "bake")
		{
			// center: the player's position when the command names none; extents and cull: the fields of the
			// panel; layerMask: the layers of the game's own bake, whatever the panel holds - every other mask
			// is asked for by its number; geometry: the colliders unless the render meshes are asked for.
			BakeRequest req;
			req.id = id;
			req.layerMask = Field(cmd, "layerMask", GAME_BAKE_LAYERS);
			req.cullMask = Field(cmd, "cull", f_CullMask.value());
			req.geometry = Field(cmd, "geometry", GEOMETRY_PHYSICS_COLLIDERS);
			req.sync = Field(cmd, "sync", false);
			req.out = Field(cmd, "out", fmt::format("bake_{}", id));
			VecField center = ReadVec(cmd, "center", req.center);
			VecField extents = ReadVec(cmd, "extents", req.extents);
			std::string error;
			if (center == VecField::Bad)
				error = "center: [x, y, z]";
			else if (extents == VecField::Bad)
				error = "extents: [x, y, z]";
			else if (req.geometry != GEOMETRY_PHYSICS_COLLIDERS && req.geometry != GEOMETRY_RENDER_MESHES)
				error = "geometry: 1 (the physics colliders) or 0 (the render meshes)";
			else if (!SafeStem(req.out))
				error = "out: letters, digits, '_' and '-' only, at most 64 characters, not a device name (CON, NUL, COM1...) "
					"and not a file of the channel (cmd, result, status, polygons_<scene>)";
			else if (center == VecField::Absent)
			{
				app::Vector3 pos{};
				if (relic::Try([&]() { pos = app::ActorUtils_GetAvatarPos(nullptr); }))
				{
					req.center[0] = pos.x;
					req.center[1] = pos.y;
					req.center[2] = pos.z;
				}
				else
					error = "the player's position is not available and no center was given";
			}
			if (extents == VecField::Absent)
			{
				req.extents[0] = f_BoxSize / 2.0f;
				req.extents[1] = f_BoxHeight / 2.0f;
				req.extents[2] = f_BoxSize / 2.0f;
			}
			if (error.empty() && StartBake(req, error))
			{
				result["state"] = m_Inflight ? "running" : "done";
				result["sources"] = m_Inflight ? m_Inflight->sourceCount : 0;
				result["center"] = Vec(req.center);
				result["extents"] = Vec(req.extents);
				WriteResult(result);
				return;   // the final answer follows when the bake ends
			}
			result["ok"] = false;
			result["error"] = error;
		}
		else
		{
			result["ok"] = false;
			result["error"] = "unknown op (status, layers, navkey, polygons, teleport, bake, peek)";
		}
		WriteResult(result);
	}

	bool NavBake::StartBake(const BakeRequest& req, std::string& error)
	{
		if (m_Inflight != nullptr)
		{
			error = fmt::format("bake {} is still running", m_Inflight->req.id);
			return false;
		}
		if (!CheckBox(req.center, req.extents, error))
			return false;
		if (!ResolveClasses())
		{
			error = g_Classes.error;
			return false;
		}
		app::Vector3 player{};
		uint32_t scene = 0;
		bool looked = relic::Try([&]()
		{
			player = app::ActorUtils_GetAvatarPos(nullptr);
			scene = game::GetCurrentPlayerSceneID();
		});
		if (!looked)
		{
			error = fmt::format("the scene lookup faulted (0x{:08X})", relic::g_lastGuardCode);
			return false;
		}
		// Without a scene there are no colliders: the builder would come back with nothing, and that nothing
		// would be written over the unit the stem holds.
		if (scene == 0)
		{
			error = "no scene is loaded - the player has to be in the world";
			return false;
		}

		auto f = std::make_unique<Inflight>();
		f->req = req;
		f->player = player;
		f->scene = scene;
		f->startedMs = AwakeMs();
		bool created = false;
		bool flagged = true;
		unsigned flagCode = 0;
		bool ok = relic::Try([&]()
		{
			f->markups = NewObject(g_Classes.markupList, f->handles[0]);
			f->sources = NewObject(g_Classes.sourceList, f->handles[1]);
			f->data = NewObject(g_Classes.navMeshData, f->handles[2]);
			if (f->markups == nullptr || f->sources == nullptr || f->data == nullptr)
				return;
			created = true;

			// Nothing of a scene refers to this NavMeshData, so the engine's sweep of unused assets may unload
			// it; the flag keeps it until Object.Destroy. A setter that faults costs the flag, not the bake.
			flagged = relic::Try([&]() { Rva<Fn_SetHideFlags>(RVA_SetHideFlags)(f->data, HIDE_DONT_UNLOAD_UNUSED_ASSET, nullptr); });
			if (!flagged)
				flagCode = relic::g_lastGuardCode;

			Bounds box{ { req.center[0], req.center[1], req.center[2] }, { req.extents[0], req.extents[1], req.extents[2] } };
			Rva<Fn_CollectSources>(RVA_CollectSources)(box, req.layerMask, req.geometry, DEFAULT_AREA, f->markups, f->sources, nullptr);
			f->sourceCount = ListCount(f->sources);
			f->shapes = SourceShapes(f->sources);

			// The vendor's settings on the default agent type, whatever this build's defaults are.
			BuildSettings s = Rva<Fn_GetSettingsByID>(RVA_GetSettingsByID)(0, nullptr);
			s.agentTypeID = 0;
			s.agentRadius = 0.25f;
			s.agentHeight = 1.6f;
			s.agentSlope = 60.0f;
			s.agentClimb = 0.4f;
			s.ledgeDropHeight = 0.0f;
			s.maxJumpAcrossDistance = 0.0f;
			s.minRegionArea = 36.0f;
			s.overrideVoxelSize = 1;
			s.voxelSize = 0.125f;
			s.overrideTileSize = 1;
			s.tileSize = 128;
			s.accuratePlacement = 0;
			s.generateDetailMap = 1;
			s.debugFlags = 0;
			f->settings = s;

			f->building = true;
			if (req.sync)
			{
				f->syncResult = Rva<Fn_UpdateSync>(RVA_UpdateSync)(f->data, s, f->sources, box, req.cullMask, nullptr);
				f->syncDone = true;
			}
			else
			{
				f->op = Rva<Fn_UpdateAsync>(RVA_UpdateAsync)(f->data, s, f->sources, box, req.cullMask, nullptr);
				if (f->op != nullptr)
					f->handles[3] = il2cpp_gchandle_new(f->op, false);
			}
		});
		unsigned code = relic::g_lastGuardCode;
		if (!flagged)
			LOG_WARNING("[navbake] bake %lld: Object.set_hideFlags faulted (0x%08X), the NavMeshData is not kept from the sweep of unused assets",
				static_cast<long long>(req.id), flagCode);
		if (!ok || !created || (!req.sync && f->op == nullptr))
		{
			DiscardBake(*f);
			if (!ok)
				error = fmt::format("the builder faulted (0x{:08X})", code);
			else if (!created)
				error = "the navmesh objects could not be created";
			else
				error = "UpdateNavMeshDataAsync returned no operation";
			LOG_WARNING("[navbake] bake %lld not started: %.300s", static_cast<long long>(req.id), error.c_str());
			return false;
		}
		m_Inflight = f.release();
		m_Busy = true;
		LOG_INFO("[navbake] bake %lld: box (%.1f, %.1f, %.1f) +- (%.1f, %.1f, %.1f), %d sources, layers 0x%08X, cull 0x%08X, %s, %s",
			static_cast<long long>(req.id), req.center[0], req.center[1], req.center[2], req.extents[0], req.extents[1], req.extents[2],
			m_Inflight->sourceCount, static_cast<unsigned>(req.layerMask), static_cast<unsigned>(req.cullMask),
			req.geometry == GEOMETRY_RENDER_MESHES ? "render meshes" : "physics colliders", req.sync ? "sync" : "async");
		Note(fmt::format("Bake {} running: {} sources", req.id, m_Inflight->sourceCount));
		return true;
	}

	// A bake that did not start: a build the engine may have begun is cancelled, the NavMeshData is destroyed
	// while its handle still holds it, and the handles go.
	void NavBake::DiscardBake(Inflight& f)
	{
		if (f.building && !f.req.sync && f.data != nullptr)
			CancelBuild(f.data);
		if (f.data != nullptr)
			relic::Try([&]() { Rva<Fn_Destroy>(RVA_Destroy)(f.data, nullptr); });
		relic::Try([&]()
		{
			for (uint32_t& h : f.handles)
				if (h != 0)
				{
					il2cpp_gchandle_free(h);
					h = 0;
				}
		});
	}

	void NavBake::PollBake()
	{
		Inflight* f = m_Inflight;
		if (f == nullptr)
			return;
		if (f->syncDone)
		{
			FinishBake(f->syncResult, f->syncResult ? std::string() : "UpdateNavMeshData returned false");
			return;
		}
		bool done = false;
		bool ok = relic::Try([&]() { done = Rva<Fn_IsDone>(RVA_IsDone)(f->op, nullptr); });
		std::string error;
		if (!ok)
			error = fmt::format("AsyncOperation.isDone faulted (0x{:08X})", relic::g_lastGuardCode);
		else if (!done && AwakeMs() - f->startedMs > BAKE_TIMEOUT_MS)
			error = "the bake did not finish in 15 minutes";
		if (!error.empty())
		{
			if (!CancelBuild(f->data))
				error += fmt::format("; NavMeshBuilder.Cancel faulted (0x{:08X})", relic::g_lastGuardCode);
			FinishBake(false, error);
		}
		else if (done)
			FinishBake(true, std::string());
	}

	// The lists and the operation go. The NavMeshData stays alive, held by its handle, for `peek` diagnostics
	// until the next bake ends, which destroys it - the collector does not free the native object.
	void NavBake::ReleaseBake(Inflight& f)
	{
		relic::Try([&]()
		{
			for (int i = 0; i < 4; i++)
				if (i != 2 && f.handles[i] != 0)
				{
					il2cpp_gchandle_free(f.handles[i]);
					f.handles[i] = 0;
				}
		});
		if (m_LastData != nullptr)
		{
			relic::Try([&]() { Rva<Fn_Destroy>(RVA_Destroy)(m_LastData, nullptr); });
			relic::Try([&]() { il2cpp_gchandle_free(m_LastDataHandle); });
		}
		m_LastData = f.data;
		m_LastDataHandle = f.handles[2];
		f.handles[2] = 0;
	}

	// The tiles are read out of the native object and the unit is written: <stem>.tiles and its sidecar, both or
	// neither. It may throw - a box of many tiles needs their bytes twice over - and FinishBake answers for that.
	void NavBake::SaveBake(Inflight& f, BakeReport& report)
	{
		std::vector<TileRef> refs(TILES_CAP);
		bool read = relic::Try([&]() { report.found = FindTiles(report.native, refs.data(), refs.size(), report.scan); });
		if (!read)
		{
			report.ok = false;
			report.error = fmt::format("reading the native NavMeshData faulted (0x{:08X})", relic::g_lastGuardCode);
			return;
		}
		if (report.found > refs.size())
		{
			// A unit that lacks some of its tiles would pass for a whole one: nothing is written.
			report.ok = false;
			report.outcome = Outcome::Truncated;
			report.error = fmt::format("the box holds {} tiles, more than the {} one bake carries - bake a smaller box", report.found, refs.size());
			return;
		}
		if (report.found != 0)
			report.outcome = Outcome::Tiles;
		else if (f.sourceCount == 0)
			report.outcome = Outcome::NoSources;
		else
		{
			// Colliders went in and no tile array is recognised: ground nobody can walk on, or a layout of the
			// native object this code does not know. The object's bytes tell which, off line.
			report.outcome = Outcome::NoTileArray;
			std::string dump;
			if (relic::Try([&]() { DumpNative(report.native, dump); }) && !dump.empty())
			{
				std::string name = f.req.out + ".native.bin";
				std::string dumpError;
				if (WriteFileAtomic(m_Dir / name, dump, dumpError))
					report.dump = name;
				else
					LOG_WARNING("[navbake] %.300s", dumpError.c_str());
			}
			if (!report.dump.empty())
				LOG_WARNING("[navbake] bake %lld: %d sources, but no tile array recognised in the native NavMeshData at %p - %zu bytes dumped to %.80s",
					static_cast<long long>(f.req.id), f.sourceCount, report.native, dump.size(), report.dump.c_str());
			else
				LOG_WARNING("[navbake] bake %lld: %d sources, but no tile array recognised in the native NavMeshData at %p",
					static_cast<long long>(f.req.id), f.sourceCount, report.native);
		}

		std::vector<std::vector<uint8_t>> tiles(report.found);
		for (size_t i = 0; i < tiles.size(); i++)
			tiles[i].resize(refs[i].size);
		bool copied = relic::Try([&]()
		{
			for (size_t i = 0; i < tiles.size(); i++)
				memcpy(tiles[i].data(), refs[i].data, refs[i].size);
		});
		if (!copied)
		{
			report.ok = false;
			report.error = fmt::format("copying the tiles faulted (0x{:08X})", relic::g_lastGuardCode);
			return;
		}

		size_t bytes = 0;
		for (auto& t : tiles)
			bytes += t.size();
		std::string blob;
		blob.reserve(4 + tiles.size() * 4 + bytes);
		int32_t count = static_cast<int32_t>(tiles.size());
		blob.append(reinterpret_cast<const char*>(&count), 4);
		for (auto& t : tiles)
		{
			int32_t size = static_cast<int32_t>(t.size());
			blob.append(reinterpret_cast<const char*>(&size), 4);
			blob.append(reinterpret_cast<const char*>(t.data()), t.size());
		}
		nlohmann::json side = {
			{ "id", f.req.id }, { "outcome", OutcomeName(report.outcome) }, { "center", Vec(f.req.center) }, { "extents", Vec(f.req.extents) },
			{ "player", Vec(f.player) }, { "scene", f.scene }, { "tiles", tiles.size() }, { "bytes", bytes },
			{ "sources", f.sourceCount }, { "shapes", f.shapes }, { "layerMask", f.req.layerMask }, { "cull", f.req.cullMask },
			{ "geometry", f.req.geometry },
			{ "elapsedMs", report.elapsedMs }, { "tileVersion", tiles.empty() ? 0 : *reinterpret_cast<const int32_t*>(tiles[0].data() + 4) },
			{ "native", fmt::format("0x{:x}", reinterpret_cast<uintptr_t>(report.native)) },
			{ "settings", { { "agentRadius", f.settings.agentRadius }, { "agentHeight", f.settings.agentHeight },
				{ "agentSlope", f.settings.agentSlope }, { "agentClimb", f.settings.agentClimb },
				{ "minRegionArea", f.settings.minRegionArea }, { "voxelSize", f.settings.voxelSize }, { "tileSize", f.settings.tileSize } } },
			{ "nativeScan", { { "offset", report.scan.offset }, { "label", report.scan.label }, { "stride", report.scan.stride }, { "dataOff", report.scan.dataOff } } } };
		if (!report.dump.empty())
			side["nativeDump"] = report.dump;
		// Everything the sidecar needs is made before the tiles are put in place: between the two files only
		// the second write can still fail.
		std::string sideText = Dump(side, 2);
		std::filesystem::path tilesPath = m_Dir / (f.req.out + ".tiles");
		std::filesystem::path sidePath = m_Dir / (f.req.out + ".json");
		std::string file = f.req.out + ".tiles";

		if (!WriteFileAtomic(tilesPath, blob, report.error))
		{
			report.ok = false;
			return;
		}
		std::string sideError;
		bool whole = false;
		try
		{
			whole = WriteFileAtomic(sidePath, sideText, sideError);
		}
		catch (const std::exception& e)
		{
			sideError = e.what();
		}
		if (!whole)
		{
			// Tiles beside an earlier bake's sidecar, or beside none, would pass for a unit: they are taken back.
			std::error_code ec;
			bool removed = std::filesystem::remove(tilesPath, ec);
			report.ok = false;
			report.error = Brief(sideError) + (removed ? " - the tiles were taken back, no unit was written" : " - and the tiles written before it could not be taken back");
			return;
		}
		report.tiles = tiles.size();
		report.bytes = bytes;
		report.file = std::move(file);
	}

	// The end of a bake, good or bad: the managed objects are released, the tiles are read out of the native
	// object and written, and result.json gets the final answer - whatever one of those steps throws.
	void NavBake::FinishBake(bool ok, const std::string& errorIn)
	{
		std::unique_ptr<Inflight> f(m_Inflight);
		m_Inflight = nullptr;
		m_Busy = false;
		ReleaseBake(*f);

		const BakeRequest& req = f->req;
		// Nobody waits for the end of a key's bake: while commands are served it stays out of result.json, where it
		// would take the place of an answer a driver has not read yet. The note and the sidecar say how it ended.
		const bool toFile = !(req.key && m_Serving);
		BakeReport report;
		report.ok = ok;
		report.error = errorIn;
		bool answered = false;
		try
		{
			report.elapsedMs = AwakeMs() - f->startedMs;
			relic::Try([&]() { report.native = *reinterpret_cast<const void* const*>(reinterpret_cast<const uint8_t*>(f->data) + OBJECT_CACHED_PTR); });
			if (report.ok && report.native == nullptr)
			{
				report.ok = false;
				report.error = "the NavMeshData has no native object";
			}
			if (report.ok)
			{
				try
				{
					SaveBake(*f, report);
				}
				catch (const std::exception& e)
				{
					report.ok = false;
					report.error = Brief(std::string("the bake's result could not be written: ") + e.what());
				}
			}
			if (!report.ok && report.outcome != Outcome::Truncated)
				report.outcome = Outcome::Failed;

			nlohmann::json result = { { "id", req.id }, { "op", "bake" }, { "ok", report.ok }, { "state", "done" },
				{ "outcome", OutcomeName(report.outcome) }, { "tiles", report.tiles }, { "bytes", report.bytes },
				{ "sources", f->sourceCount }, { "shapes", f->shapes }, { "geometry", req.geometry }, { "elapsedMs", report.elapsedMs }, { "center", Vec(req.center) },
				{ "extents", Vec(req.extents) }, { "player", Vec(f->player) }, { "scene", f->scene },
				{ "native", fmt::format("0x{:x}", reinterpret_cast<uintptr_t>(report.native)) } };
			if (report.found != report.tiles)
				result["tilesFound"] = report.found;
			if (!report.file.empty())
				result["file"] = report.file;
			if (!report.dump.empty())
				result["nativeDump"] = report.dump;
			if (!report.error.empty())
				result["error"] = report.error;
			if (toFile)
				WriteResult(result);
			answered = true;

			if (!report.ok)
			{
				LOG_WARNING("[navbake] bake %lld failed after %lld ms: %.300s", static_cast<long long>(req.id), static_cast<long long>(report.elapsedMs), report.error.c_str());
				Note(fmt::format("Bake {} failed: {}", req.id, report.error));
				return;
			}
			LOG_INFO("[navbake] bake %lld done (%s): %zu tiles, %zu bytes, %d sources, %lld ms -> %.80s (tile array at +0x%zx, label %zu)",
				static_cast<long long>(req.id), OutcomeName(report.outcome), report.tiles, report.bytes, f->sourceCount,
				static_cast<long long>(report.elapsedMs), report.file.c_str(), report.scan.offset, report.scan.label);
			if (report.outcome == Outcome::Tiles)
				Note(fmt::format("Bake {} done: {} tiles ({} KB) from {} sources in {} s -> {}", req.id, report.tiles, report.bytes / 1024, f->sourceCount, report.elapsedMs / 1000, report.file));
			else if (report.outcome == Outcome::NoSources)
				Note(fmt::format("Bake {} done: no collider in the box, an empty unit -> {}", req.id, report.file));
			else
				Note(fmt::format("Bake {} done: {} sources but no tiles recognised, an empty unit -> {}", req.id, f->sourceCount, report.file));
		}
		catch (const std::exception& e)
		{
			LOG_WARNING("[navbake] bake %lld: its end could not be reported: %.300s", static_cast<long long>(req.id), e.what());
			if (answered || !toFile)
				return;
			try
			{
				WriteResult({ { "id", req.id }, { "op", "bake" }, { "ok", false }, { "state", "done" }, { "outcome", OutcomeName(Outcome::Failed) },
					{ "error", Brief(std::string("the bake ended, but its answer could not be put together: ") + e.what()) } });
			}
			catch (const std::exception&)
			{
			}
		}
	}
}

#endif
