#include "pch-il2cpp.h"
#include "RenderDistance.h"

#include <math.h>
#include <stdio.h>
#include <string>
#include <vector>
#include <helpers.h>
#include <il2cpp-appdata.h>   // RELIC_STATIC_THIS (1.6 static-method ABI)
#include <cheat/events.h>
#include <cheat/game/EntityManager.h>
#include <cheat-base/relic-guard.h>

namespace cheat::feature
{
	// Relic: "render distance" on this engine is not Unity's. QualitySettings.set_lodBias and
	// Camera.layerCullDistances are stripped from both shipped builds, and the far clip plane already reaches the
	// horizon; what actually decides how far the world exists and how much detail it keeps is Genshin's own
	// streaming stack. Three levers, one multiplier, all verified in the 1.6 and 2.8 dumps and disassembled from
	// the shipped binaries (tools/callscan.py):
	//
	//   A. Mesh detail - the live SECTR_StreamingProfile, the ScriptableObject whose ratios the "Environment
	//      Detail" preset scales: lodRatio / HlodRatio / layerHlodRatio / sectorHlodRatio (where LODs and the merged
	//      far proxies switch), loadMaxDistance and oneFrameLimitCount. Its getters return `field x preset`, each
	//      has exactly one caller in the streamer / LOD evaluator, and those re-run at run time. The feature pokes
	//      the fields by offset - identical on both versions - and re-asserts them at 3 Hz, because
	//      SECTR_StreamingProfile.Init() reloads the asset whenever the quality name changes. The multiplier stops
	//      at 4: past 4 it moves only these
	//      ratios, and the 2.8 grass drop-out (lever B) comes straight back with them.
	//      layerLoadRatio is deliberately NOT scaled: it multiplies EVERY
	//      layer's load box, and the terrain-sized layers (Terrain / Persistence 6000 m, BigStones 4096 m, sectors of
	//      512-1024 m) already reach the horizon at the stock 0.55 - at 4x they become 13 km boxes, thousands of
	//      sectors, and the streamer's queue is saturated with them while the layers you can actually see wait.
	//      The per-layer radius lives in lever D instead.
	//   D. Streaming radius per layer - SECTR_SceneSplitterConfig.layerConfigs, one SECTR_LayerConfig per layer with
	//      its own loadSize (metres): MiniModel 128, SmallModel 256, MiddleModel 350, Tree 600, BigModel 750,
	//      HugeModel 1024, CityBigModel 2048 ... Every streaming job copies loadSize when it is created (1.6
	//      PFLMLHGJBIM.NHIEHNGEPPP @0x2B6A0A0 -> DKKCBPEDPHF.JGJBBMLAJFF(name) -> the LEVEL's own
	//      Dictionary<name, SECTR_LayerConfig>, which OICEICPMMLN.OAFMCJKHPCK @0xEF44B0 fills ONCE, when the level is
	//      built, from whatever SECTR_SceneSplitterConfig instance the static getter held at that moment -> [cfg+0x20];
	//      2.8 OONKBOHIFPO.LCNKHGBCNLI @0x1E87A80 -> DPGBNEHAGDM.FBCOLLBFGDP(hash) -> NJPNGIPLALM.PLDOADEOPIG @0x34A33C0
	//      builds the same tables) and multiplies it by layerLoadRatio x Random.Range(0.9, 1.1).
	//      The catch: a job is NOT transient - it is the
	//      per-sector object the level's sector tree is made of, its base box is copied from the config ONCE when
	//      the tree is built, and the candidate search only tests each job's stored box against the avatar (1.6
	//      OICEICPMMLN.LMFFBMOHEKN @0xEF3BF0 -> DKKCBPEDPHF.GLLGLBCDFFH). Poking the config in-level reaches nothing
	//      (every grass job keeps its base 80 whatever the config holds; rebuilding the level's tables changes nothing
	//      either - they only feed the keep check). So the radii are written BEFORE the level exists: the lever runs
	//      from the login screen on (the getter loads the BigWorld config on first use and the level creation
	//      reuses it when the path matches), and the by-path loader the level creation calls first (1.6
	//      JBPNDKLFIFM.KJLFHILAMPB @0x362E9C0, 2.8 KIFKAIFDIIC.FOEPCDHKLJB @0x362C310) is hooked to apply them to a
	//      freshly loaded config the moment it lands. A level already built keeps its stock boxes until it is built
	//      again (relog); the job-queue probe tells the two apart ("grass jobs ... base"), and while the grass
	//      sectors are stale the grass VIEW range is held at the game's: a view range beyond the load radius shows
	//      the +-10% jitter band flipping sectors in and out - the grass flashes.
	//      The game's own MonoInLevelDebugGrassViewRangeDialog writes the same field. The World
	//      lever scales loadSize x m up to a cap and never touches the layers whose stock radius already exceeds the
	//      cap; the Grass lever scales TerrainGrass by m x reach: grass has by far the smallest radius of any layer
	//      (80 m, ~44 m effective in stock, below its own 120 m view range) and the second-to-last sortOrder, which
	//      is why stock grass arrives last and pops in next to the character.
	//   B. Grass - UnityEngine.MiHoYoGrassGlobalConfigurator (native component on EnviroSky.mihoyoGrassConfig):
	//      view range (int[6]) and four per-LOD distance sets (float[6]). The game's quality applier and its own
	//      MonoInLevelDebugGrassViewRangeDialog apply exactly these setters, so they take effect live. The six
	//      entries are per quality level; the one the engine actually culls with is viewRange[idx] with idx from the
	//      manager's runtime quality level (2.8: level 4 -> idx 2 = 80 m in stock, 120 m is idx 0). Every element is
	//      scaled proportionally.
	//      2.8 ONLY (the whole-field flicker): 1.6 draws grass on the CPU, 2.8 through
	//      a GPU-driven vegetation module in UnityPlayer.dll with two FIXED budgets (read from the shipped binary with
	//      tools/native_xref.py): (1) residency - every
	//      MiHoYoGrassBlock of a streamed-in grass sector registers with one native manager (pool of 256) and must
	//      be uploaded into one of exactly 128 GPU block slots (a 128-slice height-map texture array + per-slot
	//      buffers; its non-empty layers into 352 layer slots). A block that finds no free slot is logged ONCE by the
	//      engine ("[Vegetation Module] No Slot for Uploading GPU Driven Grass Block" in output_log.txt),
	//      keeps slot -1, is never re-queued and is skipped by every frame's submit: a
	//      permanent hole until its sector reloads. A grass reach of 256 m (2 sectors) loads 45-60 sectors, i.e.
	//      more than 128 blocks; stock (80 m) loads 6-7. Slots are released two update passes after a block leaves,
	//      so churn briefly double-occupies - hence the 25% headroom. (2) per frame - the CPU submits EVERY slotted
	//      block every frame; all culling (frustum, the configurator's view range, LOD distances, previous-frame HiZ)
	//      runs in a compute shader whose visible-instance output is a fixed 32768-entry buffer; what the shader does
	//      past that is not in the DLL, but the visible ground area grows with the square of the view range (stock
	//      80 m ~ 15% of the budget at one blade per m2, 320 m ~ 250%) and the flicker follows exactly the amount of
	//      ground in view (camera pitch), which the slot holes cannot do. So on 2.8 the grass layer is capped at ONE
	//      sector (128 m, ~14 sectors), the grass view range gets its own multiplier (f_GrassView, default 2.0, live -
	//      no relog - so the threshold can be found by sliding), and a read-only probe of the manager logs blocks /
	//      slots / layers / dropped every ~10 s ("[renderdist] grass gpu: ...") and steps the reach down for the next
	//      level build when the slots run out. The probe is gated on the build signature of that UnityPlayer.dll.
	//      AND the grass layer's streaming radius: the view range only
	//      draws grass whose SECTR sectors are loaded, and TerrainGrass has by far the smallest load radius of any
	//      layer - SECTR_LayerConfig.loadSize 80 m against 128 (MiniModel), 350, 600 (Tree), 750, 1024, 2048, 6000
	//      (Terrain) - times the effective load ratio, the profile's layerLoadRatio 2.2 (measured on the jobs: base 80
	//      -> effective 165-193 m, not the 0.55 that 2.2 x a 0.25 preset would give), so stock grass
	//      streams within ~176 m while its live view range is 80-120 m. Every streaming job copies that loadSize at creation (1.6
	//      PFLMLHGJBIM.NHIEHNGEPPP @0x2B6A0A0 -> DKKCBPEDPHF.JGJBBMLAJFF(name) -> [cfg+0x20]/[cfg+0x24] ->
	//      JBPNDKLFIFM.GNFPHENGMCI; 2.8 OONKBOHIFPO.LCNKHGBCNLI @0x1E87A80 -> DPGBNEHAGDM.FBCOLLBFGDP(hash) ->
	//      KIFKAIFDIIC.CKEACDMFHJJ), multiplies it by layerLoadRatio x Random.Range(0.9, 1.1) and lives for about a
	//      second - the manager's loader lists are a churning job queue, not one object per layer, so poking a job is
	//      pointless. The game's own MonoInLevelDebugGrassViewRangeDialog writes loadSize/loadHeight into exactly
	//      that SECTR_LayerConfig, and so does this feature: the TerrainGrass entry (type 8) of the live
	//      SECTR_SceneSplitterConfig.layerConfigs, loadSize x "Grass reach" (the main multiplier still applies on top
	//      through the ratio when the World lever is on). Sector-quantised loading and the +-10% jitter are why one
	//      river bank can have grass where the other has none.
	//   C. Shadows - QualitySettings.set_shadowDistance AND set_shadowCascade4Split, both hooked (the distance getter
	//      is stripped), and the distance is capped at kMaxShadowMultiplier however high the main multiplier goes.
	//      Unity gives the whole shadow distance FOUR cascades of one shadow map: dividing the split by the
	//      multiplier keeps cascades 0-2 at exactly their stock metres, but the last one then has to cover from
	//      0.125 x 3200 = 400 m out to 3200 m - 2800 m of ground where the game gives it 400 - and at a seventh of
	//      the texel density the ground crawls between lit and shadowed, worst at low sun. 1.5x is about as far as
	//      four cascades stretch before that shows. NOTE: the whole-field
	//      2.8 grass flicker follows the camera pitch too, but it is not this: there
	//      the ENTIRE grass layer switches off, blades at the character's feet included, which shadows cannot do -
	//      the pitch dependence belongs to the GPU grass budget of lever B.
	//      The hooks (the RenderResolution pattern) substitute `distance x m` and `split / m` while the
	//      lever is on: Unity's four cascades are fractions of the shadow distance, so x4 on the distance alone gives
	//      the near cascades four times their metres at the same shadow-map size - shadow acne that crawls with the
	//      sun, worst at low sun angles (the ground flickers, usually when it gets dark).
	//      Divided by the same factor the first three
	//      cascades keep exactly their metres and their texel density; only the farthest one covers the added ground
	//      and gets softer. The game writes the pair from its quality applier (1.6 FPOAEFCHAMD.PEADMKMJBNF, 2.8
	//      EIMGBEKBIJE.GMJMABLPKFA) and the split alone from one more place per version, so both hooks stay
	//      stateless and cheap; 2.8's MoleMole.EnviroSky writes them on its mobile path only.
	//
	// The world streams around the LOCAL AVATAR, never the camera (see FreeCamera.cpp: the SECTR flush anchors
	// every layer on the avatar's position), so this widens the radius around the character; a flown-out free
	// camera still wants "Move Character with Camera". The pokes are in-memory only - the on-disk assets are
	// untouched and re-read on the next scene / quality change - so "nothing to restore" after a reload is safe.
	//
	// Every lever runs its own SEH guard with a 5-strike counter. The event guard cannot do that job here: it
	// resets its counter on every successful call, and a handler throttled to one tick in twenty succeeds the
	// other nineteen, so a persistent fault would never reach its limit and would log for the whole session.

#if RELIC_GAME_VERSION <= 16
	static constexpr size_t kSkyGrassConfigOffset = 0x4C8;   // MoleMole.EnviroSky.mihoyoGrassConfig (1.6 dump.cs:1941294)
#else
	static constexpr size_t kSkyGrassConfigOffset = 0x668;   // MoleMole.EnviroSky.mihoyoGrassConfig (2.8 dump.cs:1853745)
#endif
	static constexpr size_t kUnityCachedPtrOffset = 0x10;    // UnityEngine.Object.m_CachedPtr - the liveness idiom

	// SECTR_StreamingProfile fields the multiplier scales (1.6 dump.cs:1961476-1961485, 2.8 dump.cs:616033-616047).
	// layerLoadRatio (0x2C) and layerActiveRatio (0x30) are left alone - lever D scales the
	// radius per layer instead; colliderLoadRadius (0x44, physics) and hideHlodDelayTime (0x48, a time) too.
	static constexpr size_t kProfileFloatOffsets[] = {
		0x34,   // lodRatio           - LOD switch distances
		0x38,   // HlodRatio          - where the merged far proxies give way to real geometry
		0x3C,   // layerHlodRatio
		0x40,   // sectorHlodRatio
		0x50,   // loadMaxDistance    - the hard cap, scaled with the rest
	};
	static constexpr size_t kProfileIdxLod = 0, kProfileIdxHlod = 1, kProfileIdxMaxDist = 4;
	static constexpr size_t kProfileFloatCount = sizeof(kProfileFloatOffsets) / sizeof(kProfileFloatOffsets[0]);
	static constexpr size_t kProfileFrameLimitOffset = 0x4C;   // int oneFrameLimitCount - loads per frame

	static constexpr float  kMinMultiplier = 1.0f;
	static constexpr float  kMaxMultiplier = 4.0f;     // past 4 only the LOD / HLOD switch distances scale, and the
	                                                   // 2.8 grass flicker comes back with them alone - so the whole-
	                                                   // field drop-out follows the GPU load of the frame, not the
	                                                   // grass instance count only.
	static constexpr float  kWarnMultiplier = 2.5f;
	static constexpr float  kMaxFrameLimitMultiplier = 4.0f;   // loads per frame grow with the radius, never past this
	static constexpr float  kMaxShadowMultiplier = 1.5f;   // see lever C: past this the far cascade crawls
	static constexpr size_t kMaxGrassArrayLen = 64;    // a getter answering more than this is not the array we think
	static constexpr int    kLeverFaultLimit = 5;      // consecutive faults before a lever is switched off
	static constexpr int    kTicksPerCheck = 20;       // ~3 Hz

	// The grass layer's streaming radius (lever B, second half): SECTR_SceneSplitterConfig.layerConfigs (0x30 on
	// both versions; 1.6 dump.cs:1961289, 2.8 dump.cs:615923) holds one SECTR_LayerConfig per layer (1.6
	// dump.cs:1960311, 2.8 dump.cs:1873100 - identical): layerName String* 0x10, sectorSize 0x18, sectorHeight 0x1C,
	// loadSize 0x20, loadHeight 0x24, levelSize 0x28, sortOrder 0x2C, unloadBuffer 0x30, type 0x34. The live config
	// comes from the holder's static getter (1.6 JBPNDKLFIFM.HAAKNBJALEP, 2.8 KIFKAIFDIIC.PKEAKDGLCKO) - the same
	// call the grass debug dialog makes (1.6 DAMGDLHDIMN.KLLPECJONLG @0x2966130 walks the list for type == 8).
	static constexpr size_t kSplitterLayerListOffset = 0x30;
	static constexpr size_t kLayerCfgNameOffset = 0x10;
	static constexpr size_t kLayerCfgSectorSizeOffset = 0x18;
	static constexpr size_t kLayerCfgLoadSizeOffset = 0x20;
	static constexpr size_t kLayerCfgLoadHeightOffset = 0x24;
	static constexpr size_t kLayerCfgLevelSizeOffset = 0x28;
	static constexpr size_t kLayerCfgSortOrderOffset = 0x2C;
	static constexpr size_t kLayerCfgUnloadBufferOffset = 0x30;
	static constexpr size_t kLayerCfgTypeOffset = 0x34;
	static constexpr int32_t kLayerTypeTerrainGrass = 8;         // IIGMGGBIAFO / KPKDEJLNHLG - same value on both
	static constexpr size_t  kMaxLayerConfigs = 64;              // a list longer than this is not the list we think
	// Radii are capped in SECTOR terms, never in bare metres: a layer's cost is (2 x radius / sectorSize)^2, so the
	// same 4x on MiniModel (32 m sectors) and on HugeModel (512 m sectors) differ by a factor of 256 in sectors
	// loaded. Stock radii, in units of the layer's own sector size: TerrainGrass 0.63, WaterTile 0.78, FogTile /
	// Volume / ReflectionProbe 1.5, HugeModel / SmallLight 2, Tree 2.3, CitySmallModel 2.5, MiddleModel 2.7,
	// BigModel / FarLight 2.9, CityMiddleModel 3, MiniModel / SmallModel / DynamicSmallLight 4, DynamicFarLight 5.1,
	// Terrain / Persistence 5.9, BigStones / CityBigModel 8. Grass is the outlier at less than ONE sector - which is
	// why it has always arrived at the character while props were already there.
	static constexpr int32_t kMaxSectorRadius = 6;               // no layer is pushed past this many of its sectors
#if RELIC_GAME_VERSION <= 16
	static constexpr float   kMaxGrassSectorRadius = 2.0f;       // grass: 2 sectors = 256 m, 3.2x its stock radius.
	                                                             // 960 m (7.5 sectors) makes the layer stop loading
	                                                             // ALTOGETHER when the level is built with it -
	                                                             // keep it small
#else
	static constexpr float   kMaxGrassSectorRadius = 1.0f;       // 2.8: ONE sector = 128 m (1.6x stock). This build
	                                                             // keeps at most 128 grass blocks on the GPU (lever B
	                                                             // note): 256 m loads 45-60 sectors and runs them out,
	                                                             // 128 m loads ~14, which fits even at 4 blocks a sector
#endif
	static constexpr float   kMinGrassSectorRadius = 0.625f;     // the stock 80 m: the session cap never goes below it
	static float s_GrassSectorCap = kMaxGrassSectorRadius;       // the session's grass cap in sectors; the 2.8 probe steps
	                                                             // it down for the next level build when the slots run out
	static float s_GrassCapBuilt = kMaxGrassSectorRadius;        // the cap the running level was built with (a step-down
	                                                             // is pending while s_GrassSectorCap is below it)
#if RELIC_GAME_VERSION > 16
	static bool  s_VegStepPending = false;                       // a lowered cap waits for the next level build
#endif
	static constexpr float   kGrowFromSectorRadius = 5.0f;       // layers the game already sends this far are left
	                                                             // alone (Terrain, Persistence, BigStones, ...)
	static constexpr float   kCostBudget = 1.5f;                 // projected sectors, against what the level's own
	                                                             // config projects (the streamer's queue, not video
	                                                             // memory, is what it protects)
	static constexpr float   kDefaultLoadRatio = 2.2f;           // profile layerLoadRatio: a job's box is
	                                                             // loadSize x this x Random.Range(0.9, 1.1)
	static constexpr size_t  kProfileLayerLoadRatioOffset = 0x2C;
	static constexpr float   kMinGrassReach = 1.0f;
	static constexpr float   kMaxGrassReach = 8.0f;
	// Layer types lever D never touches: Persistence 0 (always loaded), Navmesh 5 and Collider 6 (physics, and the
	// collider layer has its own radius on the profile), FogTexture 11, ReflectionProb 16 and Volume 17 (atmosphere
	// and probes, not geometry: their stock 1536 m already covers the visible range, and at 1024 m sectors every
	// step out costs the geometry layers a large share of the plan's budget).
	static bool LayerTypeIsScalable(int32_t type)
	{
		return type != 0 && type != 5 && type != 6 && type != 11 && type != 16 && type != 17;
	}

	// The job-queue probe: the streaming manager's loader lists are a churning queue of per-sector jobs; counting
	// them per layer type every few seconds shows whether one layer starves. Diagnostic only (never poked).
	//   1.6 DKKCBPEDPHF (dump.cs:1307552): List<PFLMLHGJBIM> at 0xC0 and 0x120; PFLMLHGJBIM (dump.cs:1304682):
	//       descriptor 0x20 (PDHCFAHFHPF: type at 0x20), base box Vector3 0x38, effective box 0xE8.
	//   2.8 DPGBNEHAGDM (dump.cs:1346728): List<OONKBOHIFPO> at 0xE0, 0x148, 0x150; OONKBOHIFPO (dump.cs:1342896):
	//       descriptor handle 0x20 -> native KMBLGAJKMCG (type at 0x8), base box 0x30, effective 0xE0.
#if RELIC_GAME_VERSION <= 16
	static constexpr size_t kMgrJobListOffsets[] = { 0xC0, 0x120 };
	static constexpr size_t kJobDescOffset = 0x20;
	static constexpr size_t kJobDescTypeOffset = 0x20;
	static constexpr size_t kJobBaseBoxOffset = 0x38;
	static constexpr size_t kJobEffBoxOffset = 0xE8;
#else
	static constexpr size_t kMgrJobListOffsets[] = { 0xE0, 0x148, 0x150 };
	static constexpr size_t kJobDescOffset = 0x20;
	static constexpr size_t kJobDescTypeOffset = 0x8;
	static constexpr size_t kJobBaseBoxOffset = 0x30;
	static constexpr size_t kJobEffBoxOffset = 0xE0;
#endif
	static constexpr size_t  kMaxJobs = 4096;   // per list; 512 would truncate the main list and the missing
	                                            // tail would be read as "no grass sectors"
	static constexpr int32_t kJobTypeCount = 20;
	static constexpr int     kProbeTicks = 30;   // ~10 s at 3 Hz

	// ---- values --------------------------------------------------------------------------------------------

	struct ProfileVals
	{
		float f[kProfileFloatCount] = {};
		int32_t limit = 0;

		bool operator==(const ProfileVals& o) const
		{
			for (size_t i = 0; i < kProfileFloatCount; i++)
				if (f[i] != o.f[i])
					return false;
			return limit == o.limit;
		}
		bool operator!=(const ProfileVals& o) const { return !(*this == o); }
	};

	struct GrassVals
	{
		std::vector<int32_t> view;
		std::vector<float> lod[4];

		bool operator==(const GrassVals& o) const
		{
			return view == o.view && lod[0] == o.lod[0] && lod[1] == o.lod[1] && lod[2] == o.lod[2] && lod[3] == o.lod[3];
		}
		bool operator!=(const GrassVals& o) const { return !(*this == o); }
	};

	// One lever's memory. `written` is the READ-BACK taken right after our own write, never the value we computed:
	// a setter that clamps or quantises our value must not look like the game rewriting it, or the clamped value
	// would be captured as the "baseline" and the real original lost.
	template <class Vals>
	struct LeverState
	{
		Vals base{};                 // the game's own values (meaningful while baseValid)
		Vals written{};              // what the object held after our last write (meaningful while writtenValid)
		void* owner = nullptr;       // the object base / written belong to - compared, never dereferenced
		bool baseValid = false;
		bool writtenValid = false;
		bool outstanding = false;    // our values are in the game right now
		int faults = 0;
		bool dead = false;           // faulted kLeverFaultLimit times in a row - off for this session
		bool warnedUnbound = false;
		bool warnedUnusable = false;
	};

	static LeverState<ProfileVals> s_World;
	static LeverState<GrassVals>   s_Grass;

	// Shadows: edge-driven, the hook is the only source of the game's value.
	static bool  s_ShadowSelf = false;    // our own write is going through the setter - do not substitute
	static bool  s_ShadowWant = false;    // read by the hook: substitute value x multiplier
	static float s_ShadowGame = 0.0f;     // the value the game last asked for
	static bool  s_ShadowKnown = false;
	static int   s_ShadowFaults = 0;
	static bool  s_ShadowDead = false;
	static app::Vector3 s_SplitGame{};   // the cascade split the game last asked for (fractions of the distance)
	static bool  s_SplitKnown = false;

	// Sampled on the game thread, drawn by DrawMain (which runs on the render thread and must not call il2cpp).
	static bool  s_DiagWorldBound = true;
	static bool  s_DiagGrassBound = true;
	static bool  s_DiagShadowBound = true;
	static bool  s_DiagWorldMissing = false;   // wanted, but no live profile this tick (outside a level)
	static bool  s_DiagGrassMissing = false;
	static float s_DiagWorldBase = 0.0f, s_DiagWorldNow = 0.0f;     // lodRatio
	static int   s_DiagGrassBase = 0,    s_DiagGrassNow = 0;        // viewRange[0]
	static float s_DiagShadowBase = 0.0f, s_DiagShadowNow = 0.0f;
	static app::Vector3 s_DiagSplitNow{};
	static bool  s_DiagLayersBound = true;
	static bool  s_DiagLayersMissing = false;    // wanted, but no splitter config this tick
	static int   s_DiagGrassBoxBase = 0, s_DiagGrassBoxNow = 0;   // TerrainGrass loadSize, metres
	static int   s_DiagLayersScaled = 0;         // layer configs currently holding our loadSize
	static bool  s_GrassJobsSeen = false;        // the probe found grass jobs in the loaded set
	static int   s_GrassJobCount = 0;
	static float s_GrassJobBaseMax = 0.0f;       // the widest base box among them (metres of loadSize)
	static float s_GrassJobEffMax = 0.0f;        // the widest EFFECTIVE box among them (measured metres: base x ratio x jitter)
	static float s_DiagGrassSectorMetres = 128.0f;   // the grass layer's sector size, from its config
	static int   s_DiagGrassViewIdx = 0;         // the view-range entry the engine culls with (2.8: from the quality level)
	static float s_DiagGrassViewScale = 0.0f;    // the scale the grass view range actually got (clamped to the
	                                             // radius its sectors are streamed to, and on 2.8 to f_GrassView)
	static float s_DiagGrassBoxCap = 0.0f;       // the clamp the loaded sectors impose on their own (m when none)
	static float s_LoadRatio = kDefaultLoadRatio;  // profile layerLoadRatio, read by the world lever

	// ---- helpers -------------------------------------------------------------------------------------------

	// The single place the effective multiplier is decided.
	static float EffectiveMultiplier()
	{
		float m = RenderDistance::GetInstance().f_Multiplier;
		if (!(m >= kMinMultiplier))   // also catches NaN
			m = kMinMultiplier;
		if (m > kMaxMultiplier)
			m = kMaxMultiplier;
		return m;
	}

	// Shadows get their own ceiling: four cascades over the whole distance, and the last one is already the
	// coarsest at stock.
	static float EffectiveShadowMultiplier()
	{
		float m = EffectiveMultiplier();
		return m < kMaxShadowMultiplier ? m : kMaxShadowMultiplier;
	}

	template <class T>
	static T& RawField(void* obj, size_t offset)
	{
		return *reinterpret_cast<T*>(reinterpret_cast<uint8_t*>(obj) + offset);
	}

	// Unity-alive, not merely non-null: a managed wrapper outlives its native object and every icall on it
	// raises (ESPRender / HideUI / CustomWeather use the same m_CachedPtr test).
	static bool IsUnityAlive(void* unityObject)
	{
		return unityObject != nullptr && RawField<void*>(unityObject, kUnityCachedPtrOffset) != nullptr;
	}

	static bool WorldBindingsPresent()
	{
#if RELIC_GAME_VERSION <= 16
		return app::SECTR_Streaming_get_CurrentProfile__RAW != nullptr;   // the plain name is the inline thunk on 1.6
#else
		return app::SECTR_Streaming_get_CurrentProfile != nullptr;
#endif
	}

	static bool GrassBindingsPresent()
	{
		return app::MiHoYoGrassGlobalConfigurator_GetViewRange != nullptr
			&& app::MiHoYoGrassGlobalConfigurator_SetViewRange != nullptr
			&& app::MiHoYoGrassGlobalConfigurator_GetLOD0DistanceSet != nullptr
			&& app::MiHoYoGrassGlobalConfigurator_SetLOD0DistanceSet != nullptr
			&& app::MiHoYoGrassGlobalConfigurator_GetLOD1DistanceSet != nullptr
			&& app::MiHoYoGrassGlobalConfigurator_SetLOD1DistanceSet != nullptr
			&& app::MiHoYoGrassGlobalConfigurator_GetLOD2DistanceSet != nullptr
			&& app::MiHoYoGrassGlobalConfigurator_SetLOD2DistanceSet != nullptr
			&& app::MiHoYoGrassGlobalConfigurator_GetLOD3DistanceSet != nullptr
			&& app::MiHoYoGrassGlobalConfigurator_SetLOD3DistanceSet != nullptr;
	}

	static bool ShadowBindingsPresent()
	{
		return app::QualitySettings_set_shadowDistance != nullptr;
	}

	// The hooked static keeps its name on 1.6 and has no thunk, so a direct call carries the dummy `this` itself.
	static void CallSetShadowDistance(float value)
	{
#if RELIC_GAME_VERSION <= 16
		app::QualitySettings_set_shadowDistance(nullptr, value, nullptr);
#else
		app::QualitySettings_set_shadowDistance(value, nullptr);
#endif
	}

	static bool SplitBindingsPresent()
	{
		return app::QualitySettings_set_shadowCascade4Split != nullptr;
	}

	static void CallSetShadowCascadeSplit(app::Vector3 value)
	{
#if RELIC_GAME_VERSION <= 16
		app::QualitySettings_set_shadowCascade4Split(nullptr, value, nullptr);
#else
		app::QualitySettings_set_shadowCascade4Split(value, nullptr);
#endif
	}

	// The split divided by the distance multiplier, kept strictly increasing and off zero (Unity clamps the rest):
	// the first three cascades keep their metres, the last one takes all the added distance.
	static app::Vector3 ScaleCascadeSplit(app::Vector3 s, float m)
	{
		const float kFloor = 0.002f;
		float k = m > 0.0f ? 1.0f / m : 1.0f;
		app::Vector3 r{ s.x * k, s.y * k, s.z * k };
		if (r.x < kFloor) r.x = kFloor;
		if (r.y < r.x + kFloor) r.y = r.x + kFloor;
		if (r.z < r.y + kFloor) r.z = r.y + kFloor;
		return r;
	}

	static bool SplitGetterPresent()
	{
#if RELIC_GAME_VERSION <= 16
		return app::QualitySettings_get_shadowCascade4Split__RAW != nullptr;   // the plain name is the inline thunk
#else
		return app::QualitySettings_get_shadowCascade4Split != nullptr;
#endif
	}

	// The streaming level (current level of the manager singleton) and its layer-table build - see the header.
	static void* SectrStreamingManager();
	static bool LevelBindingsPresent()
	{
		return app::SECTR_StreamingManager_get_CurrentLevel != nullptr
			&& app::SECTR_StreamingLevel_BuildLayerTables != nullptr
			&& app::Singleton_1_SectrStreamingManager__get_Instance__MethodInfo != nullptr;
	}

	template <class Vals>
	static bool LeverFaulted(LeverState<Vals>& L, const char* name)
	{
		L.faults++;
		if (L.faults >= kLeverFaultLimit)
		{
			L.dead = true;
			LOG_WARNING("[renderdist] %s lever faulted %d times in a row (last 0x%08X) - switched off for this session",
				name, L.faults, relic::g_lastGuardCode);
		}
		else
			LOG_DEBUG("[renderdist] %s lever faulted (0x%08X, %d in a row)", name, relic::g_lastGuardCode, L.faults);
		return false;
	}

	// ---- lever A: the streaming profile --------------------------------------------------------------------

	static void ReadProfile(void* profile, ProfileVals& out)
	{
		for (size_t i = 0; i < kProfileFloatCount; i++)
			out.f[i] = RawField<float>(profile, kProfileFloatOffsets[i]);
		out.limit = RawField<int32_t>(profile, kProfileFrameLimitOffset);
	}

	static void WriteProfile(void* profile, const ProfileVals& v)
	{
		for (size_t i = 0; i < kProfileFloatCount; i++)
			RawField<float>(profile, kProfileFloatOffsets[i]) = v.f[i];
		RawField<int32_t>(profile, kProfileFrameLimitOffset) = v.limit;
	}

	// A NaN or a negative ratio is not a baseline we want to remember or multiply.
	static bool ProfileSane(const ProfileVals& v)
	{
		for (size_t i = 0; i < kProfileFloatCount; i++)
			if (!(v.f[i] >= 0.0f) || v.f[i] > 1.0e6f)
				return false;
		return true;
	}

	// The three HLOD ratios (indices 1..3: HlodRatio, layerHlodRatio, sectorHlodRatio) decide how far out a sector
	// shows its real geometry instead of its baked impostor. They are the other thing in this feature that can
	// change the distant image rather than just extend it, so they have their own toggle.
	static ProfileVals ScaleProfile(const ProfileVals& base, float m, bool hlod)
	{
		ProfileVals t = base;
		for (size_t i = 0; i < kProfileFloatCount; i++)
			t.f[i] = (!hlod && i >= 1 && i <= 3) ? base.f[i] : base.f[i] * m;
		// Loads per frame: 0 or negative may mean "unlimited" - left alone; otherwise grow it with the radius so
		// the bigger area actually fills, at most x4 (longer frames during a streaming burst are the trade-off).
		if (base.limit > 0)
		{
			float fm = m < kMaxFrameLimitMultiplier ? m : kMaxFrameLimitMultiplier;
			int32_t grown = (int32_t)ceilf((float)base.limit * fm);
			t.limit = grown > base.limit ? grown : base.limit;
		}
		return t;
	}

	static void WorldTick(bool want, float m)
	{
		auto& L = s_World;
		if (L.dead)
			return;
		if (!WorldBindingsPresent())
		{
			s_DiagWorldBound = false;
			if (!L.warnedUnbound)
			{
				L.warnedUnbound = true;
				LOG_WARNING("[renderdist] the streaming profile getter is not resolved on this game build - world lever off");
			}
			return;
		}

		ProfileVals cur{};
		bool ok = relic::Try([&]()
		{
			void* p = app::SECTR_Streaming_get_CurrentProfile(nullptr);
			if (p == nullptr)
			{
				// Outside a level or mid-transition: touch nothing, keep `outstanding` - the same object may return.
				s_DiagWorldMissing = want;
				return;
			}
			s_DiagWorldMissing = false;
			ReadProfile(p, cur);

			// The factor every streaming job multiplies its layer's loadSize by (x Random.Range(0.9, 1.1)); the
			// planner and the grass view-range clamp both need the real metres, not the config number.
			float ratio = RawField<float>(p, kProfileLayerLoadRatioOffset);
			if (ratio > 0.05f && ratio < 100.0f)
				s_LoadRatio = ratio;

			if (!want)
			{
				if (L.outstanding)
				{
					if (p == L.owner && L.writtenValid && cur == L.written)
					{
						WriteProfile(p, L.base);
						LOG_DEBUG("[renderdist] world restored: lod %.2f hlod %.2f maxDist %.0f frameLimit %d",
							L.base.f[kProfileIdxLod], L.base.f[kProfileIdxHlod], L.base.f[kProfileIdxMaxDist], L.base.limit);
					}
					else
						LOG_DEBUG("[renderdist] world: the profile was reloaded or replaced - nothing to restore, the game's own values are in place");
					L.outstanding = false;
				}
				L.baseValid = false;
				return;
			}

			if (!ProfileSane(cur))
				return;   // leave it alone this tick

			bool rewritten = L.outstanding && L.writtenValid && cur != L.written;
			if (!L.baseValid || p != L.owner || rewritten)
			{
				if (L.writtenValid && cur == L.written)
				{
					// An object we already poked came back (quality toggled back and forth, or address reuse):
					// its values are ours, not a baseline.
				}
				else
				{
					L.base = cur;
					LOG_DEBUG("[renderdist] world baseline %s: lod %.2f hlod %.2f layerHlod %.2f sectorHlod %.2f maxDist %.0f frameLimit %d",
						L.baseValid ? "recaptured" : "captured",
						cur.f[0], cur.f[1], cur.f[2], cur.f[3], cur.f[4], cur.limit);
				}
				L.owner = p;
				L.baseValid = true;
			}

			ProfileVals target = ScaleProfile(L.base, m, RenderDistance::GetInstance().f_Hlod);
			if (!L.outstanding || cur != target)
			{
				WriteProfile(p, target);
				ReadProfile(p, L.written);
				L.writtenValid = true;
				L.outstanding = true;
				LOG_DEBUG("[renderdist] world applied x%.2f: lod %.2f hlod %.2f (%s) maxDist %.0f frameLimit %d",
					m, L.written.f[kProfileIdxLod], L.written.f[kProfileIdxHlod],
					RenderDistance::GetInstance().f_Hlod ? "scaled" : "the game's",
					L.written.f[kProfileIdxMaxDist], L.written.limit);
			}
			s_DiagWorldBase = L.base.f[kProfileIdxLod];
			s_DiagWorldNow = L.written.f[kProfileIdxLod];
		});

		if (!ok)
			LeverFaulted(L, "world");
		else
			L.faults = 0;
	}

	// ---- lever B: grass ------------------------------------------------------------------------------------

	typedef void* (*GrassGetFn)(void*, MethodInfo*);
	typedef void  (*GrassSetFn)(void*, void*, MethodInfo*);

	template <class T>
	static bool ArrayUsable(UniArray<T>* arr)
	{
		return arr != nullptr && arr->length() > 0 && arr->length() <= kMaxGrassArrayLen;
	}

	// The getters hand back fresh managed arrays (native -> managed copies), so reading them is side-effect free
	// and overwriting their elements before passing them to the setters touches nothing the game keeps.
	static bool ReadGrass(void* cfg, GrassVals& out)
	{
		auto* view = reinterpret_cast<UniArray<int32_t>*>(app::MiHoYoGrassGlobalConfigurator_GetViewRange(cfg, nullptr));
		if (!ArrayUsable(view))
			return false;
		out.view.assign(view->begin(), view->end());

		GrassGetFn getters[4] = {
			app::MiHoYoGrassGlobalConfigurator_GetLOD0DistanceSet, app::MiHoYoGrassGlobalConfigurator_GetLOD1DistanceSet,
			app::MiHoYoGrassGlobalConfigurator_GetLOD2DistanceSet, app::MiHoYoGrassGlobalConfigurator_GetLOD3DistanceSet };
		for (int i = 0; i < 4; i++)
		{
			auto* lod = reinterpret_cast<UniArray<float>*>(getters[i](cfg, nullptr));
			if (!ArrayUsable(lod))
				return false;
			out.lod[i].assign(lod->begin(), lod->end());
		}
		return true;
	}

	static bool WriteGrass(void* cfg, const GrassVals& v)
	{
		auto* view = reinterpret_cast<UniArray<int32_t>*>(app::MiHoYoGrassGlobalConfigurator_GetViewRange(cfg, nullptr));
		if (!ArrayUsable(view) || view->length() != v.view.size())
			return false;
		for (size_t i = 0; i < v.view.size(); i++)
			view->vector[i] = v.view[i];
		app::MiHoYoGrassGlobalConfigurator_SetViewRange(cfg, view, nullptr);

		GrassGetFn getters[4] = {
			app::MiHoYoGrassGlobalConfigurator_GetLOD0DistanceSet, app::MiHoYoGrassGlobalConfigurator_GetLOD1DistanceSet,
			app::MiHoYoGrassGlobalConfigurator_GetLOD2DistanceSet, app::MiHoYoGrassGlobalConfigurator_GetLOD3DistanceSet };
		GrassSetFn setters[4] = {
			app::MiHoYoGrassGlobalConfigurator_SetLOD0DistanceSet, app::MiHoYoGrassGlobalConfigurator_SetLOD1DistanceSet,
			app::MiHoYoGrassGlobalConfigurator_SetLOD2DistanceSet, app::MiHoYoGrassGlobalConfigurator_SetLOD3DistanceSet };
		for (int i = 0; i < 4; i++)
		{
			auto* lod = reinterpret_cast<UniArray<float>*>(getters[i](cfg, nullptr));
			if (!ArrayUsable(lod) || lod->length() != v.lod[i].size())
				return false;
			for (size_t k = 0; k < v.lod[i].size(); k++)
				lod->vector[k] = v.lod[i][k];
			setters[i](cfg, lod, nullptr);
		}
		return true;
	}

	static std::string JoinInts(const std::vector<int32_t>& v)
	{
		std::string out;
		char buf[16];
		for (size_t i = 0; i < v.size(); i++)
		{
			snprintf(buf, sizeof(buf), "%s%d", i ? "," : "", v[i]);
			out += buf;
		}
		return out;
	}

	static std::string JoinFloats(const std::vector<float>& v)
	{
		std::string out;
		char buf[24];
		for (size_t i = 0; i < v.size(); i++)
		{
			snprintf(buf, sizeof(buf), "%s%.0f", i ? "," : "", v[i]);
			out += buf;
		}
		return out;
	}

	static bool GrassSane(const GrassVals& v)
	{
		for (int32_t x : v.view)
			if (x < 0 || x > 100000)
				return false;
		for (int i = 0; i < 4; i++)
			for (float x : v.lod[i])
				if (!(x >= 0.0f) || x > 1.0e6f)
					return false;
		return true;
	}

	static void ScaleGrass(const GrassVals& base, float m, GrassVals& out)
	{
		out.view.resize(base.view.size());
		for (size_t i = 0; i < base.view.size(); i++)
			out.view[i] = (int32_t)lroundf((float)base.view[i] * m);
		for (int i = 0; i < 4; i++)
		{
			out.lod[i].resize(base.lod[i].size());
			for (size_t k = 0; k < base.lod[i].size(); k++)
				out.lod[i][k] = base.lod[i][k] * m;
		}
	}

	// The grass VIEW range may not reach past the radius its sectors are actually streamed to: beyond that the
	// streamer's +-10% box jitter flips whole sectors in and out in plain sight - the grass flashes half
	// transparent. The honest radius is the EFFECTIVE box the loaded grass jobs carry (measured by
	// the probe: loadSize x layerLoadRatio x jitter), and the config's own loadSize x the profile's ratio until the
	// probe has seen one.
	static float GrassLoadedReachMetres(bool* measured)
	{
		if (s_GrassJobsSeen && s_GrassJobEffMax > 0.0f)
		{
			if (measured != nullptr)
				*measured = true;
			return s_GrassJobEffMax;
		}
		if (measured != nullptr)
			*measured = false;
		return (float)s_DiagGrassBoxNow * s_LoadRatio;
	}

	// Which of the six view-range entries the engine culls with: 2.8's vegetation manager picks viewRange[idx] from
	// its runtime quality level (read by the probe; stock level 4 -> index 2 = 80 m). [0] when that is not known.
	static size_t GrassLiveViewIndex(const GrassVals& base);

	// The multiplier the loaded sectors allow on the entry the engine uses (m itself when they allow more, or when
	// nothing is known yet).
	static float GrassBoxViewCap(float m, const GrassVals& base)
	{
		size_t idx = GrassLiveViewIndex(base);
		int32_t view0 = idx < base.view.size() ? base.view[idx] : 0;
		float reach = GrassLoadedReachMetres(nullptr);
		if (view0 <= 0 || reach <= 0.0f)
			return m;
		float cap = reach / (float)view0;
		if (cap < 1.0f)
			cap = 1.0f;
		return m < cap ? m : cap;
	}

#if RELIC_GAME_VERSION > 16
	// 2.8: the grass view range has its own multiplier under the main one (lever B note: the GPU culls the whole
	// field into a fixed per-frame budget, and the visible ground grows with the square of the range).
	static float EffectiveGrassView()
	{
		float v = RenderDistance::GetInstance().f_GrassView;
		if (!(v >= kMinMultiplier))
			v = kMinMultiplier;
		if (v > kMaxMultiplier)
			v = kMaxMultiplier;
		return v;
	}
#endif

	static void GrassTick(bool want, float m)
	{
		auto& L = s_Grass;
		if (L.dead)
			return;
		if (!GrassBindingsPresent())
		{
			s_DiagGrassBound = false;
			if (!L.warnedUnbound)
			{
				L.warnedUnbound = true;
				LOG_WARNING("[renderdist] the grass configurator bindings are not resolved on this game build - grass lever off");
			}
			return;
		}

		// The vectors live OUTSIDE the guarded body: SEH does not run destructors, so a fault inside the lambda
		// must not leave freshly constructed C++ objects behind.
		static GrassVals s_cur;
		static GrassVals s_target;
		bool ok = relic::Try([&]()
		{
			void* sky = app::EnviroSky_get_Instance(nullptr);
			void* cfg = sky != nullptr ? RawField<void*>(sky, kSkyGrassConfigOffset) : nullptr;
			if (!IsUnityAlive(cfg))
			{
				// No EnviroSky, or a component whose native side is gone: never hand it to an icall.
				s_DiagGrassMissing = want;
				if (!want && L.outstanding)
				{
					LOG_DEBUG("[renderdist] grass: the configurator is gone - nothing to restore");
					L.outstanding = false;
					L.baseValid = false;
				}
				return;
			}
			s_DiagGrassMissing = false;

			if (!ReadGrass(cfg, s_cur))
			{
				if (!L.warnedUnusable)
				{
					L.warnedUnusable = true;
					LOG_WARNING("[renderdist] grass: a getter answered no usable array (null, empty or > %zu elements) - grass lever idle", kMaxGrassArrayLen);
				}
				return;
			}

			if (!want)
			{
				if (L.outstanding)
				{
					if (cfg == L.owner && L.writtenValid && s_cur == L.written)
					{
						WriteGrass(cfg, L.base);
						LOG_DEBUG("[renderdist] grass restored: view[0] %d", L.base.view.empty() ? -1 : L.base.view[0]);
					}
					else
						LOG_DEBUG("[renderdist] grass: the configurator was re-applied or replaced - nothing to restore");
					L.outstanding = false;
				}
				L.baseValid = false;
				return;
			}

			if (!GrassSane(s_cur))
				return;

			// The game re-applies the VIEW RANGE alone when a level finishes loading (its quality applier calls
			// SetViewRange, not the LOD setters), so each array keeps its own baseline: an array that still holds
			// what we wrote is ours, an array that differs is the game's new value for that array only.
			bool fresh = !L.baseValid || cfg != L.owner;
			bool any = false;
			auto take = [&](auto& baseArr, const auto& curArr, const auto& writtenArr)
			{
				if (fresh || (L.outstanding && L.writtenValid && curArr != writtenArr))
				{
					baseArr = curArr;
					any = true;
				}
			};
			take(L.base.view, s_cur.view, L.written.view);
			for (int i = 0; i < 4; i++)
				take(L.base.lod[i], s_cur.lod[i], L.written.lod[i]);
			if (any)
			{
				LOG_DEBUG("[renderdist] grass baseline %s: view %s | lod0 %s | lod1 %s | lod2 %s | lod3 %s",
					fresh ? "captured" : "recaptured",
					JoinInts(L.base.view).c_str(), JoinFloats(L.base.lod[0]).c_str(), JoinFloats(L.base.lod[1]).c_str(),
					JoinFloats(L.base.lod[2]).c_str(), JoinFloats(L.base.lod[3]).c_str());
			}
			L.owner = cfg;
			L.baseValid = true;

			float boxCap = GrassBoxViewCap(m, L.base);
			size_t viewIdx = GrassLiveViewIndex(L.base);
			if (boxCap + 0.005f < m && fabsf(boxCap - s_DiagGrassBoxCap) > 0.01f)
			{
				bool measured = false;
				float reach = GrassLoadedReachMetres(&measured);
				LOG_DEBUG("[renderdist] grass view range held at x%.2f instead of x%.2f: its sectors only reach %.0f m (%s) against the live entry [%zu] = %d m",
					boxCap, m, reach, measured ? "measured" : "estimated", viewIdx,
					viewIdx < L.base.view.size() ? L.base.view[viewIdx] : 0);
			}
			s_DiagGrassBoxCap = boxCap;
			s_DiagGrassViewIdx = (int)viewIdx;
			float gm = boxCap;
#if RELIC_GAME_VERSION > 16
			float userCap = EffectiveGrassView();
			if (gm > userCap)
				gm = userCap;
#endif
			s_DiagGrassViewScale = gm;

			ScaleGrass(L.base, gm, s_target);
			if (!L.outstanding || s_cur != s_target)
			{
				if (!WriteGrass(cfg, s_target))
					return;   // an array changed shape between the read and the write - next tick recaptures
				if (!ReadGrass(cfg, L.written))
					return;
				L.writtenValid = true;
				L.outstanding = true;
				LOG_DEBUG("[renderdist] grass applied x%.2f: view %s | lod0 %s", gm,
					JoinInts(L.written.view).c_str(), JoinFloats(L.written.lod[0]).c_str());
			}
			s_DiagGrassBase = viewIdx < L.base.view.size() ? L.base.view[viewIdx] : 0;
			s_DiagGrassNow = viewIdx < L.written.view.size() ? L.written.view[viewIdx] : 0;
		});

		if (!ok)
			LeverFaulted(L, "grass");
		else
			L.faults = 0;
	}

	// ---- lever D: the streaming radius of every layer (World) and of the grass layer (Grass reach) -----------

	struct LayerCfgVals
	{
		int32_t loadSize = 0;
		int32_t loadHeight = 0;
		bool operator==(const LayerCfgVals& o) const { return loadSize == o.loadSize && loadHeight == o.loadHeight; }
		bool operator!=(const LayerCfgVals& o) const { return !(*this == o); }
	};

	// One baseline per SECTR_LayerConfig object (owner = the config); the lever-wide fault budget is in the first
	// slot's fault fields.
	static LeverState<LayerCfgVals> s_Layers[kMaxLayerConfigs];
	static int32_t s_LayerTypes[kMaxLayerConfigs] = {};
	static int   s_LayersFaults = 0;
	static bool  s_LayersDead = false;
	static bool  s_LayersWarnedUnbound = false;
	static bool  s_LevelWarnedUnbound = false;
	static void* s_LayersTableFor = nullptr;      // the splitter config whose layer table was last logged

	static bool LayersBindingsPresent()
	{
#if RELIC_GAME_VERSION <= 16
		return app::SECTR_Streaming_get_SceneSplitterConfig__RAW != nullptr;   // the plain name is the inline thunk
#else
		return app::SECTR_Streaming_get_SceneSplitterConfig != nullptr;
#endif
	}

	static float EffectiveGrassReach()
	{
		float r = RenderDistance::GetInstance().f_GrassReach;
		if (!(r >= kMinGrassReach))
			r = kMinGrassReach;
		if (r > kMaxGrassReach)
			r = kMaxGrassReach;
		return r;
	}

	static float EffectiveStreamBudget()
	{
		return kCostBudget;
	}

	static std::string LayerCfgName(void* cfg)
	{
		auto* str = RawField<app::String*>(cfg, kLayerCfgNameOffset);
		return str != nullptr ? il2cppi_to_string(str) : "(null)";
	}

	// Every SECTR_LayerConfig of the live splitter config, in list order, capped so a wrong offset cannot walk forever.
	template <class Fn>
	static void ForEachLayerConfig(void* splitter, Fn fn)
	{
		auto* list = RawField<UniList<void*>*>(splitter, kSplitterLayerListOffset);
		if (list == nullptr || list->store == nullptr)
			return;
		uint32_t n = list->size;
		if (n > kMaxLayerConfigs)
			n = kMaxLayerConfigs;
		for (uint32_t i = 0; i < n; i++)
		{
			void* cfg = *(*list->store)[i];
			if (cfg != nullptr)
				fn(cfg);
		}
	}

	static void ReadLayerCfg(void* cfg, LayerCfgVals& out)
	{
		out.loadSize = RawField<int32_t>(cfg, kLayerCfgLoadSizeOffset);
		out.loadHeight = RawField<int32_t>(cfg, kLayerCfgLoadHeightOffset);
	}

	static void WriteLayerCfg(void* cfg, const LayerCfgVals& v)
	{
		RawField<int32_t>(cfg, kLayerCfgLoadSizeOffset) = v.loadSize;
		RawField<int32_t>(cfg, kLayerCfgLoadHeightOffset) = v.loadHeight;
	}

	// SECTR_LayerSizeType is Size_1 .. Size_1024: the enum value is the power of two, in metres.
	static float SectorMetres(int32_t sectorSizeEnum)
	{
		if (sectorSizeEnum < 0)
			sectorSizeEnum = 0;
		if (sectorSizeEnum > 20)
			sectorSizeEnum = 20;
		return (float)(1 << sectorSizeEnum);
	}

	// A proportional estimate of the sectors a layer keeps loaded: the real count is far lower (only sectors that
	// hold content of that layer exist), but the ratio between two configs is what the planner needs.
	static float SectorCost(float loadSize, float sectorMetres)
	{
		if (sectorMetres <= 0.0f || loadSize <= 0.0f)
			return 0.0f;
		float n = 2.0f * loadSize * s_LoadRatio / sectorMetres + 1.0f;
		return n * n;
	}

	struct LayerPlan
	{
		void*    cfg = nullptr;
		int32_t  type = 0;
		float    sector = 128.0f;
		int32_t  base = 0;
		int32_t  target = 0;
		bool     grass = false;
		bool     want = false;
		LeverState<LayerCfgVals>* slot = nullptr;
	};

	// Every layer is capped at kMaxSectorRadius of its OWN sector size, layers the game already sends
	// kGrowFromSectorRadius sectors out are left alone, and then the whole config's projected cost is held to
	// kCostBudget x the level's own by pulling every layer's growth back with one common factor. Grass is exempt
	// from the pull-back: it is the layer the eye actually misses, and even at its cap it is a small share of the
	// total.
	static void PlanLayerRadii(LayerPlan* p, size_t n, float m, float reach, float budgetMul, float& costStockOut, float& costPlanOut)
	{
		float costStock = 0.0f;
		for (size_t i = 0; i < n; i++)
		{
			LayerPlan& L = p[i];
			L.target = L.base;
			if (L.base > 0)
				costStock += SectorCost((float)L.base, L.sector);
			if (!L.want || L.base <= 0 || L.sector <= 0.0f)
				continue;
			if ((float)L.base / L.sector >= kGrowFromSectorRadius && !L.grass)
			{
				L.want = false;   // already at the horizon for its own grain
				continue;
			}
			float wanted = L.grass ? (float)L.base * m * reach : (float)L.base * m;
			float cap = L.sector * (L.grass ? s_GrassSectorCap : (float)kMaxSectorRadius);
			if (wanted > cap)
				wanted = cap;
			int32_t t = (int32_t)lroundf(wanted);
			L.target = t > L.base ? t : L.base;
		}

		auto costAt = [&](float k)
		{
			float c = 0.0f;
			for (size_t i = 0; i < n; i++)
			{
				const LayerPlan& L = p[i];
				if (L.base <= 0)
					continue;
				float r = (float)L.base;
				if (L.want && L.target > L.base)
					r += (L.grass ? 1.0f : k) * (float)(L.target - L.base);
				c += SectorCost(r, L.sector);
			}
			return c;
		};

		costStockOut = costStock;
		float budget = costStock * budgetMul;
		float full = costAt(1.0f);
		if (costStock <= 0.0f || full <= budget)
		{
			costPlanOut = full;
			return;
		}

		float lo = 0.0f, hi = 1.0f;
		for (int it = 0; it < 20; it++)
		{
			float k = 0.5f * (lo + hi);
			if (costAt(k) > budget)
				hi = k;
			else
				lo = k;
		}
		for (size_t i = 0; i < n; i++)
		{
			LayerPlan& L = p[i];
			if (!L.want || L.grass || L.target <= L.base)
				continue;
			int32_t t = (int32_t)lroundf((float)L.base + lo * (float)(L.target - L.base));
			L.target = t > L.base ? t : L.base;
		}
		costPlanOut = costAt(lo);
	}

	static float s_LastPlanCost = -1.0f;

	static void LayersApply(bool wantWorld, bool wantGrass, float m, bool rebuildTables)
	{
		if (s_LayersDead)
			return;
		if (!LayersBindingsPresent())
		{
			s_DiagLayersBound = false;
			if (!s_LayersWarnedUnbound)
			{
				s_LayersWarnedUnbound = true;
				LOG_WARNING("[renderdist] the scene splitter config getter is not resolved on this game build - layer radii off");
			}
			return;
		}

		float reach = EffectiveGrassReach();
		float budgetMul = EffectiveStreamBudget();
		LayerPlan plan[kMaxLayerConfigs];   // POD: safe to hold across the SEH guard
		size_t nplan = 0;
		int scaled = 0;
		bool wrote = false;
		float costStock = 0.0f, costPlan = 0.0f;
		bool ok = relic::Try([&]()
		{
			void* splitter = app::SECTR_Streaming_get_SceneSplitterConfig(nullptr);
			if (splitter == nullptr)
			{
				s_DiagLayersMissing = wantWorld || wantGrass;
				return;
			}
			s_DiagLayersMissing = false;

			// Keep the load ratio current even when the world lever is off: the planner works in real metres.
			if (WorldBindingsPresent())
			{
				void* prof = app::SECTR_Streaming_get_CurrentProfile(nullptr);
				if (prof != nullptr)
				{
					float ratio = RawField<float>(prof, kProfileLayerLoadRatioOffset);
					if (ratio > 0.05f && ratio < 100.0f)
						s_LoadRatio = ratio;
				}
			}

			ForEachLayerConfig(splitter, [&](void* cfg)
			{
				if (nplan >= kMaxLayerConfigs)
					return;
				LayerPlan& L = plan[nplan];
				L.cfg = cfg;
				L.type = RawField<int32_t>(cfg, kLayerCfgTypeOffset);
				L.sector = SectorMetres(RawField<int32_t>(cfg, kLayerCfgSectorSizeOffset));
				L.grass = L.type == kLayerTypeTerrainGrass;
				L.want = L.grass ? wantGrass : (wantWorld && LayerTypeIsScalable(L.type));
				L.base = 0;
				L.target = 0;
				L.slot = nullptr;
				nplan++;
			});

			// The layer table, once per config object: this is the real streaming budget of the level. A config seen
			// for the first time is one no level has been built from yet: the level it builds carries the cap of this
			// moment (the loader hook below stamps it too, at the exact moment).
			if (splitter != s_LayersTableFor)
			{
				s_LayersTableFor = splitter;
				s_GrassCapBuilt = s_GrassSectorCap;
#if RELIC_GAME_VERSION > 16
				s_VegStepPending = false;
#endif
				for (size_t i = 0; i < nplan; i++)
				{
					void* cfg = plan[i].cfg;
					LOG_DEBUG("[renderdist] layer config %zu: type %d name %s sector %.0f m loadSize %d (%.2f sectors) loadHeight %d levelSize %d sortOrder %d unloadBuffer %.2f",
						i, plan[i].type, LayerCfgName(cfg).c_str(), plan[i].sector,
						RawField<int32_t>(cfg, kLayerCfgLoadSizeOffset),
						(float)RawField<int32_t>(cfg, kLayerCfgLoadSizeOffset) / plan[i].sector,
						RawField<int32_t>(cfg, kLayerCfgLoadHeightOffset), RawField<int32_t>(cfg, kLayerCfgLevelSizeOffset),
						RawField<int32_t>(cfg, kLayerCfgSortOrderOffset), RawField<float>(cfg, kLayerCfgUnloadBufferOffset));
				}
			}

			// Slots whose config object is gone (another level's splitter config): nothing to restore.
			for (size_t k = 0; k < kMaxLayerConfigs; k++)
			{
				auto& S = s_Layers[k];
				if (S.owner == nullptr)
					continue;
				bool present = false;
				for (size_t i = 0; i < nplan && !present; i++)
					present = plan[i].cfg == S.owner;
				if (!present)
				{
					if (S.outstanding)
						LOG_DEBUG("[renderdist] layer radius: config %p (type %d) is gone - nothing to restore", S.owner, s_LayerTypes[k]);
					S.owner = nullptr;
					S.outstanding = false;
					S.baseValid = false;
				}
			}

			// Baselines first: the plan is made from the game's own values, never from ours.
			for (size_t i = 0; i < nplan; i++)
			{
				LayerPlan& L = plan[i];
				LayerCfgVals cur{};
				ReadLayerCfg(L.cfg, cur);
				L.base = cur.loadSize;

				LeverState<LayerCfgVals>* slot = nullptr;
				for (size_t k = 0; k < kMaxLayerConfigs && slot == nullptr; k++)
					if (s_Layers[k].owner == L.cfg)
						slot = &s_Layers[k];

				if (!L.want)
				{
					if (slot != nullptr)
					{
						if (slot->outstanding)
						{
							if (slot->writtenValid && cur == slot->written)
							{
								WriteLayerCfg(L.cfg, slot->base);
								wrote = true;
								L.base = slot->base.loadSize;
								LOG_DEBUG("[renderdist] layer radius restored: %s loadSize %d", LayerCfgName(L.cfg).c_str(), slot->base.loadSize);
							}
							else
								LOG_DEBUG("[renderdist] layer radius: %s was reloaded - nothing to restore", LayerCfgName(L.cfg).c_str());
							slot->outstanding = false;
						}
						slot->owner = nullptr;
						slot->baseValid = false;
					}
					continue;
				}

				if (cur.loadSize <= 0 || cur.loadSize > 100000)
				{
					L.want = false;   // not a value to remember or multiply
					continue;
				}

				if (slot == nullptr)
					for (size_t k = 0; k < kMaxLayerConfigs && slot == nullptr; k++)
						if (s_Layers[k].owner == nullptr)
						{
							slot = &s_Layers[k];
							s_LayerTypes[k] = L.type;
						}
				if (slot == nullptr)
				{
					L.want = false;
					continue;
				}

				bool rewritten = slot->outstanding && slot->writtenValid && cur != slot->written;
				if (!slot->baseValid || L.cfg != slot->owner || rewritten)
				{
					if (slot->writtenValid && cur == slot->written)
					{
						// the same values came back on this object - still ours, keep the baseline
					}
					else
					{
						slot->base = cur;
						LOG_DEBUG("[renderdist] layer radius baseline %s: %s (type %d) loadSize %d loadHeight %d",
							slot->baseValid ? "recaptured" : "captured", LayerCfgName(L.cfg).c_str(), L.type, cur.loadSize, cur.loadHeight);
					}
					slot->owner = L.cfg;
					slot->baseValid = true;
				}
				L.slot = slot;
				L.base = slot->base.loadSize;
			}

			PlanLayerRadii(plan, nplan, m, reach, budgetMul, costStock, costPlan);

			for (size_t i = 0; i < nplan; i++)
			{
				LayerPlan& L = plan[i];
				if (!L.want || L.slot == nullptr)
					continue;
				auto& S = *L.slot;
				LayerCfgVals cur{};
				ReadLayerCfg(L.cfg, cur);

				LayerCfgVals target = S.base;
				target.loadSize = L.target;
				if (target.loadSize == S.base.loadSize)
				{
					if (S.outstanding && cur != S.base && S.writtenValid && cur == S.written)
					{
						WriteLayerCfg(L.cfg, S.base);
						wrote = true;
						LOG_DEBUG("[renderdist] layer radius back to the game's: %s loadSize %d", LayerCfgName(L.cfg).c_str(), S.base.loadSize);
					}
					S.outstanding = false;
					continue;
				}
				if (!S.outstanding || cur != target)
				{
					WriteLayerCfg(L.cfg, target);
					wrote = true;
					ReadLayerCfg(L.cfg, S.written);
					S.writtenValid = true;
					S.outstanding = true;
					LOG_DEBUG("[renderdist] layer radius applied: %s loadSize %d -> %d (%.1f -> %.1f sectors of %.0f m)%s",
						LayerCfgName(L.cfg).c_str(), S.base.loadSize, S.written.loadSize,
						(float)S.base.loadSize / L.sector, (float)S.written.loadSize / L.sector, L.sector,
						L.grass ? " (grass reach)" : "");
				}
				scaled++;
				if (L.grass)
				{
					s_DiagGrassBoxBase = S.base.loadSize;
					s_DiagGrassBoxNow = S.written.loadSize;
					s_DiagGrassSectorMetres = L.sector;
				}
			}
			s_DiagLayersScaled = scaled;

			if (costPlan > 0.0f && fabsf(costPlan - s_LastPlanCost) > 1.0f)
			{
				s_LastPlanCost = costPlan;
				LOG_DEBUG("[renderdist] layer budget: projected sectors %.0f -> %.0f (cap %.0f at x%.2f, load ratio %.2f)",
					costStock, costPlan, costStock * budgetMul, budgetMul, s_LoadRatio);
			}

			// The level's own tables (name -> config, the cached per-layer radii the keep check reads) are built once
			// from the config; refresh them from the configs just written. Not from the loader hook: the level that
			// will use this config does not exist yet.
			if (wrote && rebuildTables)
			{
				if (!LevelBindingsPresent())
				{
					if (!s_LevelWarnedUnbound)
					{
						s_LevelWarnedUnbound = true;
						LOG_WARNING("[renderdist] the streaming level accessor is not resolved on this game build - the written radii reach the jobs only after the next level build");
					}
				}
				else
				{
					void* mgr = SectrStreamingManager();
					void* level = mgr != nullptr ? app::SECTR_StreamingManager_get_CurrentLevel(mgr, nullptr) : nullptr;
					if (level != nullptr)
						app::SECTR_StreamingLevel_BuildLayerTables(level, nullptr);
				}
			}
		});

		if (!ok)
		{
			if (++s_LayersFaults >= kLeverFaultLimit)
			{
				s_LayersDead = true;
				LOG_WARNING("[renderdist] layer radius lever faulted %d times in a row (last 0x%08X) - switched off for this session",
					s_LayersFaults, relic::g_lastGuardCode);
			}
			else
				LOG_DEBUG("[renderdist] layer radius lever faulted (0x%08X, %d in a row)", relic::g_lastGuardCode, s_LayersFaults);
			return;
		}
		s_LayersFaults = 0;
	}

	static void LayersTick(bool wantWorld, bool wantGrass, float m)
	{
		LayersApply(wantWorld, wantGrass, m, true);
	}

	// ---- the splitter-config loader: the radii must be in the config before the level's sector tree is built ----

	static void SECTR_Streaming_LoadSplitterConfig_Hook(RELIC_STATIC_THIS app::String* path, MethodInfo* method)
	{
		CALL_ORIGIN(SECTR_Streaming_LoadSplitterConfig_Hook, RELIC_STATIC_THIS_ARG path, method);
		auto& self = RenderDistance::GetInstance();
		if (!self.f_Enabled)
			return;
		LOG_DEBUG("[renderdist] splitter config loaded (%s) - applying the layer radii before the level is built",
			path != nullptr ? il2cppi_to_string(path).c_str() : "?");
		LayersApply(self.f_World, self.f_Grass, EffectiveMultiplier(), false);
		// The level built from this config carries today's grass cap: a pending step-down is now applied.
		s_GrassCapBuilt = s_GrassSectorCap;
#if RELIC_GAME_VERSION > 16
		s_VegStepPending = false;
#endif
	}

	// ---- the job-queue probe (diagnostic, and the grass verdict) --------------------------------------------------

	static int  s_ProbeFaults = 0;
	static bool s_ProbeDead = false;

	static void* SectrStreamingManager()
	{
		if (app::Singleton_1_SectrStreamingManager__get_Instance__MethodInfo == nullptr)
			return nullptr;
		MethodInfo* mi = *app::Singleton_1_SectrStreamingManager__get_Instance__MethodInfo;
		if (mi == nullptr)
			return nullptr;
		return app::Singleton_GetInstance(mi);
	}

	static void JobProbe()
	{
		if (s_ProbeDead)
			return;
		int32_t counts[kJobTypeCount] = {};
		int32_t other = 0;
		size_t total = 0;
		float grassMin = 0.0f, grassMax = 0.0f, grassEff = 0.0f, grassEffMax = 0.0f;
		int32_t grassCount = 0;
		static constexpr size_t kMaxJobLists = 4;
		int32_t listSizes[kMaxJobLists] = {};
		bool truncated = false;
		bool ok = relic::Try([&]()
		{
			void* mgr = SectrStreamingManager();
			if (mgr == nullptr)
				return;
			for (size_t li = 0; li < sizeof(kMgrJobListOffsets) / sizeof(kMgrJobListOffsets[0]); li++)
			{
				auto* list = RawField<UniList<void*>*>(mgr, kMgrJobListOffsets[li]);
				if (list == nullptr || list->store == nullptr)
					continue;
				uint32_t n = list->size;
				if (li < kMaxJobLists)
					listSizes[li] = (int32_t)n;
				if (n > kMaxJobs)
				{
					truncated = true;
					n = kMaxJobs;
				}
				for (uint32_t i = 0; i < n; i++)
				{
					void* job = *(*list->store)[i];
					if (job == nullptr)
						continue;
					total++;
					void* desc = RawField<void*>(job, kJobDescOffset);
					int32_t type = desc != nullptr ? RawField<int32_t>(desc, kJobDescTypeOffset) : -1;
					if (type >= 0 && type < kJobTypeCount)
						counts[type]++;
					else
						other++;
					if (type == kLayerTypeTerrainGrass)
					{
						float b = RawField<float>(job, kJobBaseBoxOffset);
						if (grassCount == 0 || b < grassMin)
							grassMin = b;
						float e = RawField<float>(job, kJobEffBoxOffset);
						if (grassCount == 0 || b > grassMax)
						{
							grassMax = b;
							grassEff = e;
						}
						if (grassCount == 0 || e > grassEffMax)
							grassEffMax = e;
						grassCount++;
					}
				}
			}
		});
		if (!ok)
		{
			if (++s_ProbeFaults >= kLeverFaultLimit)
			{
				s_ProbeDead = true;
				LOG_WARNING("[renderdist] job probe faulted %d times in a row (last 0x%08X) - off for this session", s_ProbeFaults, relic::g_lastGuardCode);
			}
			return;
		}
		s_ProbeFaults = 0;
		// The grass verdict is published even when the lists are empty (a level transition): an empty queue means
		// "no grass sectors known", and the view-range clamp falls back to the config's own radius.
		s_GrassJobsSeen = grassCount > 0;
		s_GrassJobCount = grassCount;
		s_GrassJobBaseMax = grassMax;
		s_GrassJobEffMax = grassEffMax > 0.0f && grassEffMax < 100000.0f ? grassEffMax : 0.0f;
		if (total == 0)
			return;
		static const char* kTypeNames[kJobTypeCount] = {
			"Persist", "Terrain", "Static", "Stone", "Dynamic", "Navmesh", "Collider", "VeryHigh", "Grass", "Tree",
			"Water", "Fog", "SmallLight", "FarLight", "DynSmallLight", "DynFarLight", "ReflProbe", "Volume", "HeightGrass", "?" };
		std::string line;
		char buf[64];
		for (int t = 0; t < kJobTypeCount; t++)
		{
			if (counts[t] == 0)
				continue;
			snprintf(buf, sizeof(buf), " %s %d", kTypeNames[t], counts[t]);
			line += buf;
		}
		if (other)
		{
			snprintf(buf, sizeof(buf), " other %d", other);
			line += buf;
		}
		char lists[64];
		int used = snprintf(lists, sizeof(lists), "%d", listSizes[0]);
		for (size_t li = 1; li < sizeof(kMgrJobListOffsets) / sizeof(kMgrJobListOffsets[0]) && li < kMaxJobLists; li++)
		{
			if (used < 0 || (size_t)used >= sizeof(lists))
				break;
			int n = snprintf(lists + used, sizeof(lists) - (size_t)used, "/%d", listSizes[li]);
			if (n < 0)
				break;
			used += n;
		}
		if (grassCount > 0)
			LOG_DEBUG("[renderdist] jobs %zu (lists %s%s):%s | grass jobs %d base %.0f-%.0f effective %.0f",
				total, lists, truncated ? ", TRUNCATED" : "", line.c_str(), grassCount, grassMin, grassMax, grassEff);
		else
			LOG_DEBUG("[renderdist] jobs %zu (lists %s%s):%s | no grass job in the loaded set",
				total, lists, truncated ? ", TRUNCATED" : "", line.c_str());
	}

#if RELIC_GAME_VERSION > 16
	// ---- the vegetation manager probe (2.8 only, read-only; the lever B note in the header) ----------------------
	// Everything here was read from the shipped UnityPlayer.dll of 2.8 (PE timestamp 0x629E2EBC, SizeOfImage
	// 0x2222000; tools/native_xref.py) and the probe checks that signature plus two code bytes and one vtable entry
	// first: on any other build it stays off. The manager is mutated only on the main thread (the Behaviour's
	// AddToManager / RemoveFromManager and the once-per-frame update under PlayerLoop "UpdateAllRenderers"), the
	// thread OnGameUpdate runs on, so plain reads are consistent. It never writes and never calls into the engine.
	static constexpr uint32_t kVegPeTimeStamp     = 0x629E2EBC;
	static constexpr uint32_t kVegPeSizeOfImage   = 0x02222000;
	static constexpr size_t   kVegGetterRva       = 0xEB9430;             // `mov rax,[rip+..]; ret` = the manager getter
	static constexpr uint64_t kVegGetterBytes     = 0xC300CACB59058B48ull;
	static constexpr size_t   kVegSlotCmpImmRva   = 0xEB8FE4;             // the 0x80 of `cmp eax,0x80` in the slot allocator
	static constexpr size_t   kVegSingletonRva    = 0x1B65F90;            // MiHoYoVegetationManager* (null until created)
	static constexpr size_t   kVegBlockVtableRva  = 0x1855090;            // MiHoYoGrassBlock vtable ...
	static constexpr size_t   kVegVtableAddSlotRva = 0x18551B8;           // ... whose AddToManager entry (slot 37) ...
	static constexpr size_t   kVegAddToManagerRva = 0xEB6710;             // ... points here
	// manager fields
	static constexpr size_t kVegLiveViewOffset        = 0xB8;    // int32  the view range the culling uses (viewRange[idx])
	static constexpr size_t kVegViewOverrideOffset    = 0xBC;    // int32  > 0 replaces it
	static constexpr size_t kVegQualityOffset         = 0xE8;    // int32  runtime quality level (stock 4 -> idx 2)
	static constexpr size_t kVegBlocksUsedOffset      = 0x108;   // uint64 used pool entries
	static constexpr size_t kVegPoolOffset            = 0x110;   // Entry* 256 entries of 0x58 bytes, DENSE: removal
	                                                             // swap-moves the last used entry into the hole
	static constexpr size_t kVegPoolSizeOffset        = 0x120;   // uint64 always 256 (the ctor pre-fills the array)
	static constexpr size_t kVegSlotBitmapOffset      = 0x1E8;   // uint32[4]  128 block slots
	static constexpr size_t kVegLayerBitmapOffset     = 0x1F8;   // uint32[11] 352 layer slots
	static constexpr size_t kVegPendingOffset         = 0x248;   // uint32* pool indices waiting for a slot
	static constexpr size_t kVegPendingCountOffset    = 0x258;   // uint64
	static constexpr size_t kVegReleasingBlocksOffset = 0x278;   // uint64 block slots on the delayed-release list
	static constexpr size_t kVegReleasingLayersOffset = 0x298;   // uint64 layer slots on it
	// pool entry / block object
	static constexpr size_t kVegEntryStride = 0x58, kVegEntryObjectOffset = 0x08, kVegEntrySlotOffset = 0x10;
	static constexpr size_t kVegBlockWidthOffset = 0xF0, kVegBlockDepthOffset = 0xF4;   // int32, metres
	static constexpr int   kVegBlockSlots = 128, kVegLayerSlots = 352, kVegPoolMax = 256;
	static constexpr int   kVegSlotHold  = 120;     // slots + releasing + pending here or past = the budget is reached
	static constexpr int   kVegSlotClear = 64;      // ... and below this the episode is over (another level, far away)
	static constexpr int   kVegMaxPending = 4096;   // a longer queue is not the queue we think
	static constexpr float kVegStepDown  = 0.75f;   // the reach cap for the next level build, once per built level
	static constexpr int   kVegHotTicksNeeded = 6;  // ~2 s at 3 Hz with the load at the hold line = sustained, not a spike

	static bool s_VegChecked = false, s_VegOk = false, s_VegDead = false, s_VegEpisode = false, s_VegWarnedOdd = false;
	static int  s_VegFaults = 0, s_VegHotTicks = 0;
	// Sampled on the game thread for DrawMain.
	static int  s_DiagVegBlocks = -1;    // -1 = the manager does not exist yet
	static int  s_DiagVegSlots = 0, s_DiagVegLayers = 0, s_DiagVegPending = 0, s_DiagVegReleasing = 0, s_DiagVegDropped = 0;
	static int  s_DiagVegLiveView = 0, s_DiagVegQuality = 0;
	static float s_DiagVegBlocksPerSector = 0.0f;
	static int  s_DiagVegSideMin = 0, s_DiagVegSideMax = 0;

	static int Popcount(const uint32_t* words, size_t n)
	{
		int c = 0;
		for (size_t i = 0; i < n; i++)
		{
			uint32_t v = words[i];
			while (v)
			{
				v &= v - 1;
				c++;
			}
		}
		return c;
	}

	static uint8_t* VegBase()
	{
		return reinterpret_cast<uint8_t*>(il2cppi_get_unity_address());
	}

	// The build signature: the PE header stamps plus the instructions and the vtable entry the recipe depends on.
	static bool VegBuildMatches(uint8_t* base)
	{
		if (base == nullptr)
			return false;
		int32_t e = RawField<int32_t>(base, 0x3C);
		if (e < 0x40 || e > 0x1000)
			return false;
		if (RawField<uint32_t>(base, (size_t)e + 8) != kVegPeTimeStamp)
			return false;
		if (RawField<uint32_t>(base, (size_t)e + 0x18 + 0x38) != kVegPeSizeOfImage)
			return false;
		if (RawField<uint64_t>(base, kVegGetterRva) != kVegGetterBytes)
			return false;
		if (RawField<uint32_t>(base, kVegSlotCmpImmRva) != (uint32_t)kVegBlockSlots)
			return false;
		if (RawField<uint64_t>(base, kVegVtableAddSlotRva) != (uint64_t)(uintptr_t)(base + kVegAddToManagerRva))
			return false;
		return true;
	}

	static bool VegReady()
	{
		if (s_VegDead)
			return false;
		if (!s_VegChecked)
		{
			s_VegChecked = true;
			uint8_t* base = VegBase();
			bool matches = false;
			bool ok = relic::Try([&]() { matches = VegBuildMatches(base); });
			s_VegOk = ok && matches;
			if (s_VegOk)
				LOG_DEBUG("[renderdist] vegetation probe: UnityPlayer.dll build 0x%08X recognised - the grass GPU budget is watched", kVegPeTimeStamp);
			else
				LOG_WARNING("[renderdist] vegetation probe: this UnityPlayer.dll is not the build the probe was read from - the grass GPU budget is not watched (the static caps still apply)");
		}
		return s_VegOk;
	}

	static void VegFaulted()
	{
		if (++s_VegFaults >= kLeverFaultLimit)
		{
			s_VegDead = true;
			LOG_WARNING("[renderdist] vegetation probe faulted %d times in a row (last 0x%08X) - off for this session", s_VegFaults, relic::g_lastGuardCode);
		}
	}

	struct VegScalars
	{
		int blocks = -1, slots = 0, layers = 0, pending = 0, relBlocks = 0, relLayers = 0, liveView = 0, quality = 0;
	};

	// The manager's counters; POD in, POD out, no engine call. Returns the manager (null = not created yet).
	static uint8_t* VegReadScalars(uint8_t* base, VegScalars& s)
	{
		uint8_t* mgr = RawField<uint8_t*>(base, kVegSingletonRva);
		if (mgr == nullptr)
			return nullptr;
		uint64_t used = RawField<uint64_t>(mgr, kVegBlocksUsedOffset);
		s.blocks = used > (uint64_t)kVegPoolMax ? kVegPoolMax : (int)used;
		s.slots = Popcount(&RawField<uint32_t>(mgr, kVegSlotBitmapOffset), 4);
		s.layers = Popcount(&RawField<uint32_t>(mgr, kVegLayerBitmapOffset), 11);
		uint64_t p = RawField<uint64_t>(mgr, kVegPendingCountOffset);
		s.pending = p > (uint64_t)kVegMaxPending ? kVegMaxPending : (int)p;
		uint64_t rb = RawField<uint64_t>(mgr, kVegReleasingBlocksOffset);
		s.relBlocks = rb > (uint64_t)kVegBlockSlots ? kVegBlockSlots : (int)rb;
		uint64_t rl = RawField<uint64_t>(mgr, kVegReleasingLayersOffset);
		s.relLayers = rl > (uint64_t)kVegLayerSlots ? kVegLayerSlots : (int)rl;
		int32_t ov = RawField<int32_t>(mgr, kVegViewOverrideOffset);
		s.liveView = ov > 0 ? ov : RawField<int32_t>(mgr, kVegLiveViewOffset);
		s.quality = RawField<int32_t>(mgr, kVegQualityOffset);
		return mgr;
	}

	static void VegPublish(const VegScalars& s)
	{
		s_DiagVegBlocks = s.blocks;
		s_DiagVegSlots = s.slots;
		s_DiagVegLayers = s.layers;
		s_DiagVegPending = s.pending;
		s_DiagVegReleasing = s.relBlocks;
		s_DiagVegLiveView = s.liveView;
		s_DiagVegQuality = s.quality;
	}

	// The budget verdict. Proof of an overload is the engine's own failure (a dropped block) or the slots sitting at
	// the hold line for a couple of seconds - one sample is not: a waypoint teleport releases and queues a whole grass
	// set at once and the releasing slots free before the queued ones need them. One warning per episode. The reach
	// cap steps down at most once per BUILT level (a step already waiting for the next build is not compounded), and
	// only when the running level carries OUR radius: a level built at the game's 80 m that still overloads is not
	// the reach's doing, and the advice there is the view range, not a relog. Nothing can be done for the level that
	// is already built - its sector radii were copied when it was made - and a block the engine dropped comes back
	// only when its sector reloads.
	static void VegBudgetCheck(const VegScalars& s, int dropped, bool sampleIsTick)
	{
		int load = s.slots + s.relBlocks + s.pending;
		if (sampleIsTick)
			s_VegHotTicks = load >= kVegSlotHold ? s_VegHotTicks + 1 : 0;
		bool over = dropped > 0 || s_VegHotTicks >= kVegHotTicksNeeded;
		if (!over)
		{
			if (load < kVegSlotClear && dropped == 0)
				s_VegEpisode = false;
			return;
		}
		if (s_VegEpisode)
			return;
		s_VegEpisode = true;

		char head[256];
		snprintf(head, sizeof(head), "[renderdist] grass GPU budget: %d/%d block slots in use (+%d releasing, %d pending), %d block%s dropped by the engine%s",
			s.slots, kVegBlockSlots, s.relBlocks, s.pending, dropped, dropped == 1 ? "" : "s",
			dropped > 0 ? " (holes until their sectors reload)" : " so far");
		bool ours = RenderDistance::GetInstance().f_Grass && s_GrassJobsSeen && s_DiagGrassBoxBase > 0
			&& s_GrassJobBaseMax > (float)s_DiagGrassBoxBase + 1.0f;
		if (!ours)
			LOG_WARNING("%s - the loaded grass sectors carry the game's own radius, not the reach: the cap is left alone (lower Grass view range or the Multiplier instead)", head);
		else if (s_GrassSectorCap < s_GrassCapBuilt - 0.001f)
			LOG_WARNING("%s - a lower reach cap (%.3f sectors) is already waiting for the next level build (relog to apply it)", head, s_GrassSectorCap);
		else if (s_GrassSectorCap <= kMinGrassSectorRadius + 0.001f)
			LOG_WARNING("%s - the grass reach is already at the game's own radius, so the reach is not the cause (lower Grass view range or the Multiplier instead)", head);
		else
		{
			float before = s_GrassSectorCap;
			float after = before * kVegStepDown;
			if (after < kMinGrassSectorRadius)
				after = kMinGrassSectorRadius;
			s_GrassSectorCap = after;
			s_VegStepPending = true;
			LOG_WARNING("%s - the grass reach cap for the next level build goes %.3f -> %.3f sectors (%.0f -> %.0f m; relog to apply)",
				head, before, after, before * s_DiagGrassSectorMetres, after * s_DiagGrassSectorMetres);
		}
	}

	// Every tick (3 Hz): the counters only.
	static void VegProbeTick()
	{
		if (!VegReady())
			return;
		uint8_t* base = VegBase();
		VegScalars s;
		bool ok = relic::Try([&]() { VegReadScalars(base, s); });
		if (!ok)
		{
			VegFaulted();
			return;
		}
		s_VegFaults = 0;
		VegPublish(s);
		if (s.blocks >= 0)
			VegBudgetCheck(s, s_DiagVegDropped, true);
	}

	// With the job probe (~10 s): the pool walk - which blocks the engine dropped, and what a block is (its side in
	// metres, blocks per loaded grass sector) so the caps can be judged against real data.
	static void VegProbeBlocks()
	{
		if (!VegReady())
			return;
		uint8_t* base = VegBase();
		VegScalars s;
		int valid = 0, slotted = 0, dropped = 0, sideMin = 0, sideMax = 0, sideN = 0, odd = 0;
		bool poolOdd = false;
		bool ok = relic::Try([&]()
		{
			uint8_t* mgr = VegReadScalars(base, s);
			if (mgr == nullptr)
				return;
			uint8_t* pool = RawField<uint8_t*>(mgr, kVegPoolOffset);
			const uint32_t* pend = RawField<const uint32_t*>(mgr, kVegPendingOffset);
			if (pool == nullptr || RawField<uint64_t>(mgr, kVegPoolSizeOffset) != (uint64_t)kVegPoolMax)
			{
				poolOdd = true;   // not the pool we know - walk nothing
				return;
			}
			void* vt = base + kVegBlockVtableRva;
			// The pool is dense (entries 0..used-1 are the live blocks), so the used count bounds the walk.
			for (int i = 0; i < s.blocks; i++)
			{
				uint8_t* e = pool + (size_t)i * kVegEntryStride;
				uint8_t* obj = RawField<uint8_t*>(e, kVegEntryObjectOffset);
				if (obj == nullptr || RawField<void*>(obj, 0) != vt)
				{
					odd++;   // a used entry without a MiHoYoGrassBlock: not the layout we know - never read into it
					continue;
				}
				valid++;
				int32_t slot = RawField<int32_t>(e, kVegEntrySlotOffset);
				if (slot >= 0)
					slotted++;
				else
				{
					bool queued = false;
					for (int k = 0; k < s.pending && pend != nullptr && !queued; k++)
						queued = pend[k] == (uint32_t)i;
					if (!queued)
						dropped++;
				}
				int32_t w = RawField<int32_t>(obj, kVegBlockWidthOffset);
				int32_t d = RawField<int32_t>(obj, kVegBlockDepthOffset);
				int32_t side = w > d ? w : d;
				if (side > 0 && side <= 4096)
				{
					if (sideN == 0 || side < sideMin)
						sideMin = side;
					if (side > sideMax)
						sideMax = side;
					sideN++;
				}
			}
		});
		if (!ok)
		{
			VegFaulted();
			return;
		}
		s_VegFaults = 0;
		VegPublish(s);
		s_DiagVegDropped = dropped;
		s_DiagVegSideMin = sideMin;
		s_DiagVegSideMax = sideMax;
		s_DiagVegBlocksPerSector = (s_GrassJobCount > 0 && valid > 0) ? (float)valid / (float)s_GrassJobCount : 0.0f;
		if (s.blocks < 0)
		{
			LOG_DEBUG("[renderdist] grass gpu: the vegetation manager does not exist yet");
			return;
		}
		if ((poolOdd || odd > 0) && !s_VegWarnedOdd)
		{
			s_VegWarnedOdd = true;
			LOG_WARNING("[renderdist] grass gpu: the block pool is not laid out as expected (%s%d used entries without a grass block) - the per-block counts are not trusted this session",
				poolOdd ? "pool size != 256; " : "", odd);
		}
		LOG_DEBUG("[renderdist] grass gpu: blocks %d/%d (%d valid, %d slotted, %d dropped) slots %d/%d (+%d releasing, %d pending) layers %d/%d (+%d releasing) | %.1f blocks per loaded grass sector (%d sectors), block side %d-%d m | live view %d m (quality %d -> entry [%d]) | reach cap %.3f sectors, built with %.3f",
			s.blocks, kVegPoolMax, valid, slotted, dropped, s.slots, kVegBlockSlots, s.relBlocks, s.pending,
			s.layers, kVegLayerSlots, s.relLayers, s_DiagVegBlocksPerSector, s_GrassJobCount, sideMin, sideMax,
			s.liveView, s.quality, s_DiagGrassViewIdx, s_GrassSectorCap, s_GrassCapBuilt);
		VegBudgetCheck(s, (poolOdd || odd > 0) ? 0 : dropped, false);
	}
#endif

	static size_t GrassLiveViewIndex(const GrassVals& base)
	{
#if RELIC_GAME_VERSION > 16
		// The engine's map from its runtime quality level to the entry (read from the shipped 2.8 UnityPlayer.dll:
		// level <= 1 -> 5, 2 -> 4, 3 -> 3, 4 -> 2, 5 -> 1, >= 6 -> 0); the probe reads the level.
		if (s_VegOk && s_DiagVegBlocks >= 0 && !base.view.empty())
		{
			int q = s_DiagVegQuality;
			size_t idx = q <= 1 ? 5 : q == 2 ? 4 : q == 3 ? 3 : q == 4 ? 2 : q == 5 ? 1 : 0;
			return idx < base.view.size() ? idx : base.view.size() - 1;
		}
#else
		(void)base;
#endif
		return 0;
	}

	// ---- lever C: shadows ----------------------------------------------------------------------------------

	static void QualitySettings_set_shadowDistance_Hook(RELIC_STATIC_THIS float value, MethodInfo* method)
	{
		if (!s_ShadowSelf)
		{
			s_ShadowGame = value;     // one global - remembered whether or not we are in a level
			s_ShadowKnown = true;
			if (s_ShadowWant)
				value *= EffectiveShadowMultiplier();
		}
		CALL_ORIGIN(QualitySettings_set_shadowDistance_Hook, RELIC_STATIC_THIS_ARG value, method);
	}

	static void QualitySettings_set_shadowCascade4Split_Hook(RELIC_STATIC_THIS app::Vector3 value, MethodInfo* method)
	{
		if (!s_ShadowSelf)
		{
			s_SplitGame = value;
			s_SplitKnown = true;
			if (s_ShadowWant)
				value = ScaleCascadeSplit(value, EffectiveShadowMultiplier());
		}
		CALL_ORIGIN(QualitySettings_set_shadowCascade4Split_Hook, RELIC_STATIC_THIS_ARG value, method);
	}

	static void ShadowTick(bool want, float m)
	{
		static bool  s_wasWant = false;
		static float s_wasMul = 0.0f;

		if (s_ShadowDead)
			return;
		if (!ShadowBindingsPresent())
		{
			s_DiagShadowBound = false;
			return;   // installGuarded already logged the skipped hook
		}

		s_ShadowWant = want;   // from here on the hooks substitute on the game's own writes
		if (want == s_wasWant && (!want || m == s_wasMul))
			return;
		s_wasWant = want;
		s_wasMul = m;

		if (!s_ShadowKnown)
		{
			if (want)
				LOG_DEBUG("[renderdist] shadow: the game has not set its shadow distance yet - it is scaled at its next write");
			return;
		}

		if (!s_SplitKnown && SplitGetterPresent())
		{
			// The game wrote the split before the hook existed (or only the distance since): read it once.
			app::Vector3 cur{};
			bool got = relic::Try([&]() { cur = app::QualitySettings_get_shadowCascade4Split(nullptr); });
			if (got && cur.x > 0.0f && cur.y > cur.x && cur.z > cur.y && cur.z < 1.0f)
			{
				s_SplitGame = cur;
				s_SplitKnown = true;
				LOG_DEBUG("[renderdist] shadow cascade split read from the game: %.3f/%.3f/%.3f", cur.x, cur.y, cur.z);
			}
			else
				LOG_DEBUG("[renderdist] shadow cascade split: getter %s (%.3f/%.3f/%.3f) - divided at the game's next write",
					got ? "answered an unusable split" : "faulted", cur.x, cur.y, cur.z);
		}

		float target = want ? s_ShadowGame * m : s_ShadowGame;
		bool splitToo = s_SplitKnown && SplitBindingsPresent();
		app::Vector3 split = want ? ScaleCascadeSplit(s_SplitGame, m) : s_SplitGame;
		bool ok = relic::Try([&]()
		{
			s_ShadowSelf = true;
			CallSetShadowDistance(target);
			if (splitToo)
				CallSetShadowCascadeSplit(split);
			s_ShadowSelf = false;
		});
		s_ShadowSelf = false;
		if (!ok)
		{
			if (++s_ShadowFaults >= kLeverFaultLimit)
			{
				s_ShadowDead = true;
				LOG_WARNING("[renderdist] shadow lever faulted %d times in a row (last 0x%08X) - switched off for this session",
					s_ShadowFaults, relic::g_lastGuardCode);
			}
			return;
		}
		s_ShadowFaults = 0;
		s_DiagShadowBase = s_ShadowGame;
		s_DiagShadowNow = target;
		s_DiagSplitNow = splitToo ? split : s_SplitGame;
		if (splitToo)
			LOG_DEBUG("[renderdist] shadow %s: %.0f -> %.0f | cascades %.3f/%.3f/%.3f -> %.3f/%.3f/%.3f",
				want ? "applied" : "restored", s_ShadowGame, target,
				s_SplitGame.x, s_SplitGame.y, s_SplitGame.z, split.x, split.y, split.z);
		else
			LOG_DEBUG("[renderdist] shadow %s: %.0f -> %.0f (cascade split not seen yet - divided at the game's next write)",
				want ? "applied" : "restored", s_ShadowGame, target);
	}

	// ---- the feature ---------------------------------------------------------------------------------------

	RenderDistance::RenderDistance() : Feature(),
		NF(f_Enabled, "Visuals::RenderDistance", false),
		NF(f_Multiplier, "Visuals::RenderDistance", 2.0f),
		NF(f_World, "Visuals::RenderDistance", true),
		NF(f_Hlod, "Visuals::RenderDistance", true),
		NF(f_Grass, "Visuals::RenderDistance", true),
		NF(f_GrassReach, "Visuals::RenderDistance", 3.0f),
#if RELIC_GAME_VERSION > 16
		NF(f_GrassView, "Visuals::RenderDistance", 2.0f),
#endif
		NF(f_Shadows, "Visuals::RenderDistance", true)
	{
		INSTALL_HOOK(app::QualitySettings_set_shadowDistance, QualitySettings_set_shadowDistance_Hook);
		INSTALL_HOOK(app::QualitySettings_set_shadowCascade4Split, QualitySettings_set_shadowCascade4Split_Hook);
		INSTALL_HOOK(app::SECTR_Streaming_LoadSplitterConfig, SECTR_Streaming_LoadSplitterConfig_Hook);
		events::GameUpdateEvent += MY_METHOD_HANDLER(RenderDistance::OnGameUpdate);
	}

	const FeatureGUIInfo& RenderDistance::GetGUIInfo() const
	{
		TRANSLATED_GROUP_INFO("Render Distance", "Visuals");
		return info;
	}

	void RenderDistance::DrawMain()
	{
		ConfigWidget(_TR("Enabled"), f_Enabled,
			_TR("Pushes the world's streaming and LOD radius, the grass draw distance and the shadow distance\n"
				"out by the multiplier - past the game's own 'Highest' environment detail.\n"
				"The world streams around your CHARACTER, not the camera: for far shots pair this with\n"
				"Free Camera > 'Move Character with Camera'. Expect a few seconds of stutter while the extra\n"
				"sectors load, and a few more for them to unload again after switching it off."));

		if (!f_Enabled)
			return;

		ImGui::Indent();
		ConfigWidget(_TR("Multiplier"), f_Multiplier, 0.1f, kMinMultiplier, kMaxMultiplier,
			_TR("2.0 = everything loads, and keeps its detail, at twice the distance. 4.0 is the ceiling: the\n"
				"mesh detail past it (LOD and HLOD distances alone) brought the 2.8 grass drop-out back."));

		float m = EffectiveMultiplier();
		if (m > kWarnMultiplier)
			ImGui::TextColored(ImVec4(1.f, 0.6f, 0.2f, 1.f), "%s",
				_TR("Every mesh in the loaded area at a finer level, and more of the world in memory: GPU and\n"
					"streaming load grow fast from here. Running out of video memory shows as a black screen."));

		ConfigWidget(_TR("World and props"), f_World,
			_TR("LOD and HLOD switch distances and the per-frame load budget (the SECTR streaming profile the\n"
				"game's Environment Detail preset scales), plus every streaming layer's own load radius up to a\n"
				"cap. The terrain-sized layers already reach the horizon and are left alone, which keeps the\n"
				"streamer's queue free for the props, trees and grass you can actually see."));
		if (f_World)
		{
			ImGui::Indent();
			ConfigWidget(_TR("Distant geometry (HLOD)"), f_Hlod,
				_TR("Also push out the distance at which a sector drops its real geometry for the baked impostor\n"
					"the game draws behind it. This is the part that changes how the distance LOOKS rather than\n"
					"how far it loads - switch it off if distant ground or buildings shimmer."));
			ImGui::Unindent();
		}
		ConfigWidget(_TR("Grass"), f_Grass,
			_TR("Grass view range and the per-LOD grass distances, plus the grass layer's own streaming radius\n"
				"(the reach below): grass is the layer the game streams in closest by far - 80 m against\n"
				"128-1024 m for props - which is why it used to arrive last and pop in near the character."));
		if (f_Grass)
		{
			ImGui::Indent();
#if RELIC_GAME_VERSION <= 16
			ConfigWidget(_TR("Grass reach"), f_GrassReach, 0.25f, kMinGrassReach, kMaxGrassReach,
				_TR("Multiplies the grass layer's streaming radius (the game's 80 m) so its sectors are already\n"
					"loaded before the camera can see them; the main multiplier still applies on top when World\n"
					"is on. 3.0 keeps up with the grass view range at 4.0x. Grass data is dense: raise it a step\n"
					"at a time and watch memory.\n"
					"Capped at two grass sectors (256 m): a level built with much more than that stops streaming\n"
					"grass altogether. The radius is baked into a level when the level is built, so keep this on\n"
					"when you log in, or relog after changing it."));
#else
			ConfigWidget(_TR("Grass reach"), f_GrassReach, 0.25f, kMinGrassReach, kMaxGrassReach,
				_TR("Multiplies the grass layer's streaming radius (the game's 80 m) so its sectors are already\n"
					"loaded before the camera can see them; the main multiplier still applies on top when World\n"
					"is on. The radius is baked into a level when the level is built, so keep this on when you\n"
					"log in, or relog after changing it.\n"
					"Capped at one grass sector (128 m) on 2.8: this build keeps at most 128 grass blocks on the\n"
					"GPU and drops every further one for good (a hole until its sector reloads); at 256 m the\n"
					"engine ran out of slots. When the probe sees the slots run out, the cap steps down for the\n"
					"next level build - the 'Grass GPU' lines below show slots in use, blocks dropped and the cap."));
			ConfigWidget(_TR("Grass view range"), f_GrassView, 0.1f, kMinMultiplier, kMaxMultiplier,
				_TR("2.8 only: the grass draw distance gets its own multiplier, under the main one and under the\n"
					"radius its sectors are streamed to (the status shows what applied). This build's grass\n"
					"culling writes its visible blades into a fixed 32768-entry GPU buffer; what happens past it\n"
					"is not in the engine binary, but the ground in view grows with the square of the range (2.0 =\n"
					"four times the area of the game's live 80 m at the default quality) and the pitch-dependent\n"
					"flicker of the earlier tests fits that buffer overflowing. Applies live - no relog: raise it\n"
					"a step at a time until the flicker starts, then back off one."));
#endif
			float wantView = EffectiveMultiplier();
#if RELIC_GAME_VERSION > 16
			if (EffectiveGrassView() < wantView)
				wantView = EffectiveGrassView();
#endif
			if (s_DiagGrassBoxCap > 0.0f && s_DiagGrassBoxCap + 0.01f < wantView)
				ImGui::TextColored(ImVec4(1.f, 0.6f, 0.2f, 1.f), "%s",
					_TR("The grass view range is held below the multiplier: grass may not be drawn further than its\n"
						"sectors are streamed, or whole sectors flash in and out. A level bakes the radius when it\n"
						"is built, so relog after raising the reach."));
			ImGui::Unindent();
		}
		ConfigWidget(_TR("Shadows"), f_Shadows,
			_TR("Shadow distance x the multiplier, with the cascade split divided by it so the near shadows keep\n"
				"their sharpness. Capped at 1.5x however high the multiplier goes: the whole distance is covered\n"
				"by four cascades of ONE shadow map, and stretched further the last one runs out of texels and the\n"
				"distant ground crawls between lit and shadowed - worst at low sun, and only where you are looking\n"
				"towards the horizon."));

		// Status, from values sampled on the game thread.
		if (!s_DiagWorldBound)
			ImGui::TextDisabled("%s", _TR("World: not resolved on this game build."));
		else if (s_World.dead)
			ImGui::TextDisabled("%s", _TR("World: switched off after repeated faults - see the log."));
		else if (f_World && s_DiagWorldMissing)
			ImGui::TextDisabled("%s", _TR("World: waiting for a level."));

		if (!s_DiagGrassBound)
			ImGui::TextDisabled("%s", _TR("Grass: not resolved on this game build."));
		else if (s_Grass.dead)
			ImGui::TextDisabled("%s", _TR("Grass: switched off after repeated faults - see the log."));
		else if (f_Grass && s_DiagGrassMissing)
			ImGui::TextDisabled("%s", _TR("Grass: waiting for a level."));

		if (!s_DiagLayersBound)
			ImGui::TextDisabled("%s", _TR("Layer radii: not resolved on this game build."));
		else if (s_LayersDead)
			ImGui::TextDisabled("%s", _TR("Layer radii: switched off after repeated faults - see the log."));
		else if ((f_World || f_Grass) && s_DiagLayersMissing)
			ImGui::TextDisabled("%s", _TR("Layer radii: waiting for a level."));

		if (!s_DiagShadowBound)
			ImGui::TextDisabled("%s", _TR("Shadows: not resolved on this game build."));
		else if (s_ShadowDead)
			ImGui::TextDisabled("%s", _TR("Shadows: switched off after repeated faults - see the log."));
		else if (f_Shadows && !s_ShadowKnown)
			ImGui::TextDisabled("%s", _TR("Shadows: takes effect after the game next applies its graphics settings."));

#if RELIC_GAME_VERSION > 16
		// The grass GPU budget (sampled on the game thread by the vegetation probe).
		if (s_VegChecked && !s_VegOk)
			ImGui::TextDisabled("%s", _TR("Grass GPU: this UnityPlayer.dll build is not the one the probe knows - the budget is not watched."));
		else if (s_VegDead)
			ImGui::TextDisabled("%s", _TR("Grass GPU: the probe switched off after repeated faults - see the log."));
		else if (s_DiagVegBlocks < 0)
			ImGui::TextDisabled("%s", _TR("Grass GPU: waiting for the vegetation manager."));
		else
		{
			bool hot = s_DiagVegSlots + s_DiagVegReleasing + s_DiagVegPending >= kVegSlotHold || s_DiagVegDropped > 0;
			ImVec4 col = hot ? ImVec4(1.f, 0.6f, 0.2f, 1.f) : ImGui::GetStyleColorVec4(ImGuiCol_TextDisabled);
			ImGui::TextColored(col, "%s %d/%d | %s %d/%d (+%d %s, %d %s) | %s %d/%d | %s %d",
				_TR("Grass GPU: blocks"), s_DiagVegBlocks, kVegPoolMax,
				_TR("slots"), s_DiagVegSlots, kVegBlockSlots, s_DiagVegReleasing, _TR("releasing"), s_DiagVegPending, _TR("pending"),
				_TR("layers"), s_DiagVegLayers, kVegLayerSlots,
				_TR("dropped"), s_DiagVegDropped);
			ImGui::TextColored(col, "%.1f %s | %s %d-%d m | %s %d m (%s %d, %s [%d]) | %s %.3f %s (%.0f m)",
				s_DiagVegBlocksPerSector, _TR("blocks per loaded grass sector"),
				_TR("block side"), s_DiagVegSideMin, s_DiagVegSideMax,
				_TR("live view"), s_DiagVegLiveView, _TR("quality"), s_DiagVegQuality, _TR("entry"), s_DiagGrassViewIdx,
				_TR("reach cap"), s_GrassSectorCap, _TR("sectors"), s_GrassSectorCap * s_DiagGrassSectorMetres);
			if (hot)
			{
				const char* what = s_DiagVegDropped > 0
					? _TR("Blocks past the 128 GPU slots were dropped by the engine (holes until their sectors reload).")
					: _TR("The 128 GPU block slots are nearly full.");
				const char* then = s_VegStepPending
					? _TR("The reach cap for the next level build was lowered - relog to apply it.")
					: (s_GrassSectorCap <= kMinGrassSectorRadius + 0.001f
						? _TR("The reach is already at the game's own radius - lower Grass view range or the Multiplier.")
						: _TR("If it persists, lower Grass reach and relog."));
				ImGui::TextColored(ImVec4(1.f, 0.6f, 0.2f, 1.f), "%s\n%s", what, then);
			}
		}
#endif

		ImGui::TextDisabled("%s %.2f -> %.2f | %s %d | %s %.0f -> %.0f | %s %.3f/%.3f/%.3f",
			_TR("LOD ratio"), s_DiagWorldBase, s_DiagWorldNow,
			_TR("Layers scaled"), s_DiagLayersScaled,
			_TR("Shadow distance"), s_DiagShadowBase, s_DiagShadowNow,
			_TR("Cascades"), s_DiagSplitNow.x, s_DiagSplitNow.y, s_DiagSplitNow.z);
		ImGui::TextDisabled("%s x%.2f (%s [%d] %d -> %d m) | %s %d -> %d m (%d %s, %s %.0f m)",
			_TR("Grass view"), s_DiagGrassViewScale, _TR("entry"), s_DiagGrassViewIdx, s_DiagGrassBase, s_DiagGrassNow,
			_TR("Grass reach"), s_DiagGrassBoxBase, s_DiagGrassBoxNow, s_GrassJobCount, _TR("sectors loaded"),
			_TR("their base"), s_GrassJobBaseMax);
		ImGui::Unindent();
	}

	bool RenderDistance::NeedStatusDraw() const
	{
		return f_Enabled;
	}

	void RenderDistance::DrawStatus()
	{
		ImGui::Text("%s [%.1fx]", _TR("Render Distance"), EffectiveMultiplier());
	}

	RenderDistance& RenderDistance::GetInstance()
	{
		static RenderDistance instance;
		return instance;
	}

	void RenderDistance::OnGameUpdate()
	{
		auto& manager = game::EntityManager::instance();
		bool inLevel = manager.avatar()->raw() != nullptr;

		// The game applies its own values on scene load and on a settings change, never per frame; 3 Hz is enough
		// to take over promptly and to re-assert after the game re-applied its own.
		static int s_Ticks = 0;
		if (++s_Ticks < kTicksPerCheck)
			return;
		s_Ticks = 0;

		// A config saved with a higher ceiling would show its 8 or 12 in the slider forever while
		// the effective value is 4: write the ceiling back once.
		if (f_Multiplier > kMaxMultiplier)
			f_Multiplier = kMaxMultiplier;

		float m = EffectiveMultiplier();
		bool wantAny = f_Enabled && inLevel;

		// The layer radii are copied into a level's sector jobs when the level is built, so this lever is not
		// gated on being in a level: it writes the config from the login screen on (and the loader hook covers
		// every later config load).
		LayersTick(f_Enabled && f_World, f_Enabled && f_Grass, m);

		// The probe runs at once when the lever comes on, then every ~10 s; its grass verdict gates the grass
		// view range below.
		static int s_ProbeCountdown = kProbeTicks;
		if (wantAny)
		{
			if (++s_ProbeCountdown >= kProbeTicks)
			{
				s_ProbeCountdown = 0;
				JobProbe();
#if RELIC_GAME_VERSION > 16
				VegProbeBlocks();   // after JobProbe: it relates the blocks to the loaded grass sectors
#endif
			}
#if RELIC_GAME_VERSION > 16
			else
				VegProbeTick();
#endif
		}
		else
			s_ProbeCountdown = kProbeTicks;

		WorldTick(wantAny && f_World, m);
		GrassTick(wantAny && f_Grass, m);
		ShadowTick(wantAny && f_Shadows, EffectiveShadowMultiplier());
	}
}
