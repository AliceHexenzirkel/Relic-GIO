#include "pch-il2cpp.h"
#include "CustomWeather.h"
#include <helpers.h>
#include <cheat/events.h>
#include <cheat/game/EntityManager.h>
#include <cheat/game/util.h>
#include <cheat/game/filters.h>
#include <cheat-base/relic-guard.h>
#include <cheat-base/util.h>

namespace cheat::feature
{
    static const std::map<CustomWeather::WeatherType, std::string> weather
    {
        { CustomWeather::WeatherType::ClearSky, "Data/Environment/Weather/BigWorld/Weather_ClearSky" },
        { CustomWeather::WeatherType::Cloudy, "Data/Environment/Weather/BigWorld/Weather_Cloudy" },
        { CustomWeather::WeatherType::Foggy, "Data/Environment/Weather/BigWorld/Weather_Foggy" },
        { CustomWeather::WeatherType::Storm, "Data/Environment/Weather/BigWorld/Weather_Storm" },
#if RELIC_GAME_VERSION <= 16
        // Relic: 1.6 has no Inazuma content (Dq = 稻妻/Daoqi, game 2.0+) - the client answers 0 for that path,
        // so that entry would be a guaranteed no-op. 1.6 ships its own heavy rain under a plain name, probed -> 1
        // with kind=3 (Rain). It carries no lightning (hasLightning=0); Weather_Storm is the lightning one.
        { CustomWeather::WeatherType::RainHeavy, "Data/Environment/Weather/BigWorld/Weather_Rain_Heavy" },
#else
        { CustomWeather::WeatherType::RainHeavy, "Data/Environment/Weather/BigWorld/Weather_Dq_Tabeisha_Rain_Heavy" },
#endif
        { CustomWeather::WeatherType::FountainRain, "Data/Environment/Weather/BigWorld/Weather_LY_Fountain_Rain" },
        { CustomWeather::WeatherType::SnowLight, "Data/Environment/Weather/BigWorld/Weather_Snowmountain_Snow_Light" },
        { CustomWeather::WeatherType::EastCoast, "Data/Environment/Weather/BigWorld/Weather_Snowmountain_EastCoast" },
    };


    // Relic: which weather the lightning branch belongs to is per game version. 2.8 gates it on the Inazuma
    // heavy rain (Dq = 稻妻/Daoqi), an asset that shipped with game 2.0: on 1.6 ChangeWeather answers 0 for it
    // so the gate could never open and the toggle would be a guaranteed
    // no-op. The branch does not CREATE lightning - it only moves EntityType::Lightning entities the weather
    // has already spawned onto nearby monsters - so on 1.6 it is left ungated: with no such entities the loop
    // does nothing at all, and whichever 1.6 weather does produce them then works without us having to guess
    // its asset name first. 1.6 does ship the gadget: server excel GadgetData_Level.txt row 70000009
    // "Storm_Lightning", entityType 39 = EntityType::Lightning, described 雷雨天的闪电 ("thunderstorm lightning").

    // Relic: appdata declares neither EnviroSky nor EnviroWeatherPreset, so the applied preset is read by raw
    // offset. Dump offsets are absolute from the object start:
    //   EnviroSky.Weather                 1.6 dump.cs:1941054 // 0x98    2.8 dump.cs:1853414 // 0xA8
    //     its EnviroWeatherPreset slots   +0x10 / +0x18 on both (1.6 DGDHAGGCGBN, 2.8 NGFKDPJEBFB)
    //   EnviroWeatherPreset.type          +0x18 on both   { ClearSky, Cloudy, Foggy, Rain, Snow, Storm, Skill }
    //   EnviroWeatherPreset.path          +0x30 on both
    //   EnviroWeatherPreset.hasLightning  1.6 +0xF1A (dump.cs:1942700)   2.8 +0x13D1 (dump.cs:614263)
    // hasLightning is the whole question for 1.6: a weather whose preset does not set it can never feed the
    // lightning branch. Every read goes through relic::Try, so a wrong offset costs one silent skip instead of
    // counting towards CustomWeather's own guard (20 consecutive faults switch a feature off for the session).
#if RELIC_GAME_VERSION <= 16
    static constexpr size_t kSkyWeatherOffset = 0x98;
    static constexpr size_t kPresetHasLightningOffset = 0xF1A;

    // Relic: everything FireStormEffect @0x028496A0 touches before it will draw anything, read the way it reads
    // it so a session can say WHICH precondition refused instead of leaving a silent return.
    //   EnviroSky.CameraTrans      +0x48    Unity-alive, asked with Object.op_Equality @0x028497B1
    //   EnviroSky.levelEntity      +0x478   non-null @0x02849776 - the effect prefabs are spawned off it
    //   Weather -> applied preset  +0x18    the slot FireStormEffect itself reads
    //   preset.thunderSettings     +0xF20   dump.cs:1942701, non-null @0x028497BE
    // EnviroThunderSettings.Config is an INLINE struct at +0x18 (dump.cs:1940569), so its lightningBeginHeight
    // (+0x20) and lightningMaxHeight (+0x24) sit at +0x38 / +0x3C from the object - confirmed against the game's
    // own driver DEPJHAPCIBL @0x02844A10: mulss [rbx+0x40] (heightRatio), addss [rbx+0x38], clamp to [rbx+0x3C].
    static constexpr size_t kSkyCameraTransOffset = 0x48;
    static constexpr size_t kSkyLevelEntityOffset = 0x478;
    static constexpr size_t kWeatherAppliedPresetOffset = 0x18;
    static constexpr size_t kPresetThunderSettingsOffset = 0xF20;
    static constexpr size_t kThunderBeginHeightOffset = 0x38;
    static constexpr size_t kThunderMaxHeightOffset = 0x3C;
    static constexpr size_t kUnityCachedPtrOffset = 0x10;    // UnityEngine.Object.m_CachedPtr
    static constexpr float  kFallbackStrikeHeight = 60.0f;   // when the preset's own value is missing or absurd

    // Relic: the crash-damage arithmetic, read off the consumer AEOLLPABFCP$$LEACKKAHJNI @0x00A5E890:
    //     dealt = Mathf.Max(0, maxHp * (a1 - a2 / (a3 + velChange)))
    // a1..a3 are elements 1..3 of a ConstValue float[4] filled by DGEGKCBOKLG$$AAEFEPDOECF @0x02190480 from
    // const-value entry id 92, and 1.6's shipped row 92 is `10 | 0.4 | 4.2 | 2 | "1,0.9,1"` (the initializer
    // parses four floats then splits element 4 on ',', which pins the row to the code). So the inverse below
    // is exact and needs none of upstream KillAura's fudge ladder (KillAura.cpp:181-190).
    static constexpr float kCrashVelChange = 10000000.0f;
    static constexpr float kCrashA1 = 0.4f;
    static constexpr float kCrashA2 = 4.2f;
    static constexpr float kCrashA3 = 2.0f;
    static constexpr int64_t kDamageProbeDelayMs = 400;   // ~two EventManager ticks - the event is QUEUED
#else
    static constexpr size_t kSkyWeatherOffset = 0xA8;
    static constexpr size_t kPresetHasLightningOffset = 0x13D1;
#endif
    static constexpr float kStrikeRange = 30.0f;   // unchanged from the upstream entity-moving loop

    // Kept separate from `weather` on purpose: probing a name must never imply the feature offers it. The last
    // entries are speculative 1.6-era names - a miss costs one failed ConfigUtil.LoadConfig and writes nothing.
    static const std::vector<std::string> probeCandidates
    {
        "Data/Environment/Weather/BigWorld/Weather_ClearSky",
        "Data/Environment/Weather/BigWorld/Weather_Cloudy",
        "Data/Environment/Weather/BigWorld/Weather_Foggy",
        "Data/Environment/Weather/BigWorld/Weather_Storm",
        "Data/Environment/Weather/BigWorld/Weather_Dq_Tabeisha_Rain_Heavy",
        "Data/Environment/Weather/BigWorld/Weather_LY_Fountain_Rain",
        "Data/Environment/Weather/BigWorld/Weather_Snowmountain_Snow_Light",
        "Data/Environment/Weather/BigWorld/Weather_Snowmountain_EastCoast",
        "Data/Environment/Weather/BigWorld/Weather_Rain",
        "Data/Environment/Weather/BigWorld/Weather_Rain_Heavy",
        "Data/Environment/Weather/BigWorld/Weather_RainHeavy",
        "Data/Environment/Weather/BigWorld/Weather_ThunderStorm",
        "Data/Environment/Weather/BigWorld/Weather_Snow",
        "Data/Environment/Weather/BigWorld/Weather_MengDeInStorm",
    };

    // What the game itself would offer here. Short names, current area only - a hint, not an inventory.
    static void LogAreaWeatherList(void* Enviro)
    {
        if (app::EnviroSky_GetCurWeatherList == nullptr)
        {
            LOG_DEBUG("[weather] GetCurWeatherList is not resolved on this game build");
            return;
        }

        relic::Try([Enviro]()
        {
            auto* names = app::EnviroSky_GetCurWeatherList(Enviro, nullptr);
            if (names == nullptr || names->fields._items == nullptr || names->fields._size <= 0)
            {
                LOG_DEBUG("[weather] GetCurWeatherList returned nothing for this area");
                return;
            }
            for (int32_t i = 0; i < names->fields._size; i++)
            {
                auto* name = names->fields._items->vector[i];
                LOG_DEBUG("[weather] area offers 'Data/Environment/Weather/%s'",
                    name != nullptr ? il2cppi_to_string(name).c_str() : "(null)");
            }
        });
    }

    // What actually got applied - and, the point of the exercise, whether that preset produces lightning.
    static void LogWeatherPreset(void* Enviro)
    {
        relic::Try([Enviro]()
        {
            auto* state = *reinterpret_cast<uint8_t**>(reinterpret_cast<uint8_t*>(Enviro) + kSkyWeatherOffset);
            if (state == nullptr)
            {
                LOG_DEBUG("[weather] no weather state on EnviroSky");
                return;
            }
            for (size_t slot : { static_cast<size_t>(0x10), static_cast<size_t>(0x18) })
            {
                auto* preset = *reinterpret_cast<uint8_t**>(state + slot);
                if (preset == nullptr)
                    continue;
                auto* path = *reinterpret_cast<app::String**>(preset + 0x30);
                LOG_DEBUG("[weather] preset@0x%zX path='%s' kind=%d hasLightning=%d", slot,
                    path != nullptr ? il2cppi_to_string(path).c_str() : "(null)",
                    *reinterpret_cast<int32_t*>(preset + 0x18),
                    *reinterpret_cast<bool*>(preset + kPresetHasLightningOffset) ? 1 : 0);
            }
        });
    }

#if RELIC_GAME_VERSION <= 16
    // Relic: upstream's Lightning toggle only MOVES EntityType::Lightning entities the weather has already
    // spawned. On 1.6 there are never any to move - the bolt is a server-spawned gadget (server excel
    // GadgetData_Level.txt row 70000009 "Storm_Lightning", entityType 39) and GIO never spawns it: the log
    // says `[weather] lightning entities: 0` under Weather_Storm and the count never changes.
    // FireStormEffect draws one itself, needing no server, no gadget and no entity - so on 1.6 the feature calls
    // it directly. Two consequences worth knowing: the entity count stays 0 even on complete success, and the
    // bolt is purely visual (in vanilla the damage comes from the gadget's ability, not from this call).
    struct StormReadiness
    {
        bool read = false;          // the whole walk completed without faulting
        bool hasWeather = false;    // EnviroSky.Weather - FireStormEffect dereferences this UNCHECKED
        bool hasPreset = false;     // its applied preset - unchecked too
        bool hasLightning = false;
        bool hasThunder = false;
        bool hasLevel = false;
        bool hasCamera = false;
        float beginHeight = 0.0f;
        float maxHeight = 0.0f;

        // The two the game does NOT check: calling with either null throws a managed NullReferenceException,
        // which is not something to hand to the SEH guard. Everything else only makes the call a silent no-op.
        bool safeToCall() const { return read && hasWeather && hasPreset; }
        bool ok() const { return safeToCall() && hasLightning && hasThunder && hasLevel && hasCamera; }
        int mask() const
        {
            return (read ? 1 : 0) | (hasWeather ? 2 : 0) | (hasPreset ? 4 : 0) | (hasLightning ? 8 : 0)
                 | (hasThunder ? 16 : 0) | (hasLevel ? 32 : 0) | (hasCamera ? 64 : 0);
        }
    };

    static StormReadiness ReadStormReadiness(void* Enviro)
    {
        StormReadiness state;
        state.read = relic::Try([Enviro, &state]()
        {
            auto* sky = reinterpret_cast<uint8_t*>(Enviro);
            state.hasLevel = *reinterpret_cast<void**>(sky + kSkyLevelEntityOffset) != nullptr;

            // Unity-alive, not merely non-null: op_Equality is true for a managed wrapper whose native side is
            // gone, and m_CachedPtr is the idiom ESPRender and HideUI already use for exactly that.
            auto* cameraTrans = *reinterpret_cast<uint8_t**>(sky + kSkyCameraTransOffset);
            state.hasCamera = cameraTrans != nullptr
                && *reinterpret_cast<void**>(cameraTrans + kUnityCachedPtrOffset) != nullptr;

            auto* weatherState = *reinterpret_cast<uint8_t**>(sky + kSkyWeatherOffset);
            if (weatherState == nullptr)
                return;
            state.hasWeather = true;

            auto* preset = *reinterpret_cast<uint8_t**>(weatherState + kWeatherAppliedPresetOffset);
            if (preset == nullptr)
                return;
            state.hasPreset = true;
            state.hasLightning = *reinterpret_cast<bool*>(preset + kPresetHasLightningOffset);

            auto* thunder = *reinterpret_cast<uint8_t**>(preset + kPresetThunderSettingsOffset);
            if (thunder == nullptr)
                return;
            state.hasThunder = true;
            state.beginHeight = *reinterpret_cast<float*>(thunder + kThunderBeginHeightOffset);
            state.maxHeight = *reinterpret_cast<float*>(thunder + kThunderMaxHeightOffset);
        });
        return state;
    }
#endif

    CustomWeather::CustomWeather() : Feature(),
        NFP(f_Enabled, "CustomWeather", "Custom weather", false),
        NFP(f_Lightning, "CustomWeather", "Lighting", false),
        NF(f_WeatherType, "CustomWeather", CustomWeather::WeatherType::ClearSky),
        NF(f_LightningDelay, "CustomWeather", 600),
        NF(f_LightningHeight, "CustomWeather", 0),
        NF(f_LightningDamage, "CustomWeather", false),
        NF(f_LightningDamageValue, "CustomWeather", 500.0f),
        NF(f_LightningElectro, "CustomWeather", false),
        NF(f_LightningElementGauge, "CustomWeather", 80.0f)
    {
        // Relic: cfg.json stores f_WeatherType as a raw integer, so a config written by another build can hold a
        // value this build has no path for. Clamp once here - std::map::at throwing inside OnGameUpdate is caught
        // by the per-handler SEH guard as a fault, and enough of those switch CustomWeather off for the session.
        if (weather.find(f_WeatherType.value()) == weather.end())
            f_WeatherType = CustomWeather::WeatherType::ClearSky;

        events::GameUpdateEvent += MY_METHOD_HANDLER(CustomWeather::OnGameUpdate);
    }

    const FeatureGUIInfo& CustomWeather::GetGUIInfo() const
    {
        TRANSLATED_GROUP_INFO("Custom Weather", "Visuals");
        return info;
    }

    void CustomWeather::DrawMain()
    {
        ConfigWidget(_TR("Enabled"), f_Enabled, _TR("Custom Weather."));
        if (f_Enabled->enabled())
        {
            ConfigWidget(_TR("Weather type"), f_WeatherType, _TR("Select weather type."));
            // Relic: ChangeWeather answers whether this client actually ships the asset, and which weathers a
            // build has is per game version. Saying so beats retrying a missing one ten times a second in silence.
            if (m_Availability.load() == Availability::Missing)
                ImGui::TextColored(ImColor(255, 165, 0, 255), _TR("This game version does not have that weather."));
        }
#if RELIC_GAME_VERSION <= 16
        ConfigWidget(_TR("Lightning"), f_Lightning, _TR("Calls lightning down on nearby enemies. Needs Custom "
            "Weather on and a weather whose preset produces lightning - on game 1.6 that is Storm. The bolt is "
            "visual on its own; switch on Damage enemies below to make it hurt."));
        if (f_Lightning->enabled())
        {
            ImGui::Indent();
            ConfigWidget(_TR("Strike delay (ms)"), f_LightningDelay, 10, 100, 5000,
                _TR("Time between two strikes. One per interval, cycling through the enemies in range."));
            ConfigWidget(_TR("Bolt height"), f_LightningHeight, 1, 0, 300,
                _TR("How far above the enemy the bolt starts, in world units. 0 = use the weather's own value."));
            ConfigWidget(_TR("Damage enemies"), f_LightningDamage, _TR("Makes the bolt hurt. By default the "
                "damage is the game's own FALL damage: a plain white number, credited to nobody, with no "
                "element and no crit. Switch on Electro below for a real elemental hit. Neither kind gets "
                "through shields, and neither does anything to invincible or HP-locked enemies."));
            if (f_LightningDamage.value())
            {
                ConfigWidget(_TR("Damage per bolt"), f_LightningDamageValue, 10.0f, 1.0f, 10000000.0f,
                    _TR("Damage one bolt deals. Flat - it does not scale with your character."));
                ConfigWidget(_TR("Electro damage (experimental)"), f_LightningElectro, _TR("Turns the bolt "
                    "into a real Electro hit instead of fall damage: a purple number, an Electro aura on the "
                    "enemy, and elemental reactions - Electro-Charged on a wet target, Overloaded on a "
                    "burning one, Superconduct on a frozen one. The hit is credited to your active "
                    "character. The damage is still flat: it does not use your stats and cannot crit. "
                    "EXPERIMENTAL - unlike the plain damage this has not been proven against your server, "
                    "and the game handles the hit one frame later, outside the mod's crash guard. If the "
                    "game closes when you switch this on, leave it off and use the plain damage."));
                if (f_LightningElectro.value())
                    ConfigWidget(_TR("Electro gauge"), f_LightningElementGauge, 1.0f, 0.0f, 200.0f,
                        _TR("How much Electro one bolt applies. 80 is the value the game itself uses for its "
                            "own weather element effects. Higher means the aura lasts longer."));
            }
            switch (m_LightningState.load())
            {
            case LightningState::NoLightning:
                ImGui::TextColored(ImColor(255, 165, 0, 255), _TR("This weather produces no lightning."));
                break;
            case LightningState::Blocked:
                ImGui::TextColored(ImColor(255, 165, 0, 255), _TR("The game cannot draw lightning right now."));
                break;
            default:
                break;
            }
            ImGui::Unindent();
        }
#else
        ConfigWidget(_TR("Lightning"), f_Lightning, _TR("Lightning target enemy, works with RainHeavy weather."));
#endif
    }

    bool CustomWeather::NeedStatusDraw() const
    {
        return f_Enabled->enabled();
    }

    void CustomWeather::DrawStatus()
    {
        ImGui::Text(_TR("Custom Weather"));
        if (f_Lightning->enabled())
            ImGui::Text(_TR("Lightning"));
    }

    CustomWeather& CustomWeather::GetInstance()
    {
        static CustomWeather instance;
        return instance;
    }

    void CustomWeather::StartAssetProbe()
    {
        m_ProbeIndex = 0;
    }

    bool CustomWeather::AssetProbeRunning() const
    {
        return m_ProbeIndex.load() >= 0;
    }

    // Walks the candidate list one entry at a time, dwelling long enough for the weather to actually apply, then
    // logs which preset the client ended up with. Returns true while it owns the weather, so OnGameUpdate skips
    // its normal apply. ChangeWeather writes nothing on a miss, and the player's own selection is restored at the
    // end (the normal path re-asserts it every 100 ms anyway).
    bool CustomWeather::RunAssetProbe(void* Enviro)
    {
        int probe = m_ProbeIndex.load();
        if (probe < 0)
            return false;

        const int dwellTicks = 15;   // ~1.5 s at UPDATE_DELAY(100); ChangeWeather blends over about a second
        int index = probe / dwellTicks;
        int phase = probe % dwellTicks;

        if (probe == 0)
        {
            m_ProbeRestore = f_WeatherType.value();
            LOG_DEBUG("[weather] probe start (%zu candidates)", probeCandidates.size());
            LogAreaWeatherList(Enviro);
        }

        if (index < static_cast<int>(probeCandidates.size()))
        {
            if (phase == 0)
            {
                const std::string& candidate = probeCandidates[index];
                bool ok = app::EnviroSky_ChangeWeather(Enviro, string_to_il2cppi(candidate), 1, 1, nullptr);
                LOG_DEBUG("[weather] probe '%s' -> %d", candidate.c_str(), ok ? 1 : 0);
            }
            else if (phase == dwellTicks - 1)
            {
                LogWeatherPreset(Enviro);
            }
            m_ProbeIndex = probe + 1;
            return true;
        }

        LOG_DEBUG("[weather] probe done");
        m_ProbeIndex = -1;
        f_WeatherType = m_ProbeRestore;
        return false;
    }

    // Relic: one bolt per interval, round-robin over the monsters in range. Rate limiting is not cosmetic -
    // every call spawns up to three prefabs and restarts the full-screen lightning flash, so a call per monster
    // per tick would be a strobe. KillAura's idiom: a local next-time plus util::GetCurrentTimeMillisec, with
    // UPDATE_DELAY still owning the outer 10 Hz tick.
#if RELIC_GAME_VERSION <= 16
    // Relic: the only damage source the mod can raise without fabricating a managed object. It is the game's own
    // fall-crash event, and this sequence is the one the game's own velocity detector DNBGMBAPINL$$CNJGADBJJME
    // @0x01970630 runs (a second producer, HBFKKCMPNOG$$OJDGFACDKIL @0x036AA7E0, repeats it):
    // EventHelper.Allocate<EvtCrash> @0x0303C370 (MethodInfo slot 0x08FE8850, the <MFKLHADCFAO> instantiation)
    // -> EvtCrash.Init @0x023EEC30 -> EventManager.FireEvent @0x01718600.
    //
    // It is NOT an elemental hit. EvtCrash's only fields are velChange / maxHp / hitPos, so it carries no element,
    // no attacker and no AbilityIdentifier - the tooltip says so and must keep saying so.
    //
    // immediately = false, like both of the game's producers: FireEvent only ENQUEUES, and the handler runs on the
    // EventManager's next tick - OUTSIDE our per-handler SEH guard. That is why nothing here writes a field it has
    // not read out of the consumer, and why the whole thing is opt-in and off by default. The pooled event is
    // recycled by the dispatcher (GNMJCKLPHJA @0x01716D10), so crashEvt must never be touched after the call.
    static bool FireCrashDamage(game::Entity* target, float damage)
    {
        if (target == nullptr || !target->isLoaded() || damage <= 0.0f)
            return false;

        auto eventManager = GET_SINGLETON(MoleMole_EventManager);
        if (eventManager == nullptr
            || app::MoleMole_EventHelper_Allocate_103__MethodInfo == nullptr
            || *app::MoleMole_EventHelper_Allocate_103__MethodInfo == nullptr)
            return false;   // 1.6 fills singleton and metadata slots late - a null here is transient, not a bug

        uint32_t targetID = target->runtimeID();
        app::Vector3 hitPos = target->absolutePosition();

        return relic::Try([eventManager, targetID, hitPos, damage]()
        {
            auto* crashEvt = app::MoleMole_EventHelper_Allocate_103(*app::MoleMole_EventHelper_Allocate_103__MethodInfo);
            if (crashEvt == nullptr)
                return;

            app::MoleMole_EvtCrash_Init(crashEvt, targetID, nullptr);
            crashEvt->fields.velChange = kCrashVelChange;
            crashEvt->fields.maxHp = damage / (kCrashA1 - kCrashA2 / (kCrashA3 + kCrashVelChange));
            crashEvt->fields.hitPos = hitPos;

            app::MoleMole_EventManager_FireEvent(eventManager, reinterpret_cast<app::BaseEvent*>(crashEvt), false, nullptr);
        });
    }

    // Relic: current HP, every hop null-checked - the entity can be unloaded between the strike and the readback.
    static bool ReadHp(game::Entity* entity, float& hp)
    {
        hp = -1.0f;
        if (entity == nullptr || !entity->isLoaded())
            return false;

        relic::Try([entity, &hp]()
        {
            auto* combat = entity->combat();
            if (combat == nullptr)
                return;
            auto* prop = combat->fields._combatProperty_k__BackingField;
            if (prop == nullptr)
                return;
            hp = app::MoleMole_SafeFloat_get_Value(prop->fields.HP, nullptr);
        });
        return hp >= 0.0f;
    }

    // Relic: the Electro half. It hands the game a managed AttackResult - but not a fabricated one:
    // MoleMole.AttackResult.CreateElementAttack @0x021D77D0 is the game's OWN factory (pool-allocate ->
    // Reset() -> _origElementType -> _origElementDurability), the same call the game's Cryo weather mixin
    // makes with (Ice, 80.0f) at 0x02C302DC.
    //
    // Why the element sticks without touching anything shared: get_ElementType @0x021D74E0 reads
    // modifiedAttackProperty (+0x110) -> _attackerAttackProperty (+0x108) -> _origElementType (+0x118), and the
    // factory leaves the first two NULL. So the element rides on this one attack, and the shared
    // ConfigAttackProperty that RapidFire.cpp:384 corrupts is never written.
    //
    // The one field the factory leaves null that the pipeline needs is defenseCombatProperty (+0x18): the
    // damage handler PMBPGLIKLGN$$BLKPKLBJACI @0x0179B9D0 opens with
    //     mov rax,[ar+0x18]; test rax,rax; je <ret>; cmp qword [rax+0x4C0],0; je <ret>   (+0x4C0 = isInvincible)
    // - so a null there is a silent no-hit - and the visual combat component FNNDKCJBJDB$$KNDJNEFFICJ
    // @0x018DD7C0 reads the same field at 0x018DD84B and THROWS on null. Both are answered by filling it from
    // the live target. Every other pointer field the factory leaves null was checked against all 43 consumers
    // of EvtBeingHit.get_attackResult @0x023673E0 and is read behind a real guard.
    //
    // Lifetime: EvtBeingHit.Init @0x02367510 Retains the AttackResult and the event's recycle Releases it
    // (-> Reset + ObjectPoolUtility.Deallocate), so the count balances exactly. Never Retain, never Release,
    // and never touch `ar` after the call - it belongs to the game from that moment.
    //
    // Same caveat as FireCrashDamage: FireBeingHitEvent ends in FireEvent(immediately = false), so the
    // consumers run on the EventManager's next tick, OUTSIDE our guard. Hence opt-in and off by default.
    static bool FireElectroDamage(game::Entity* attacker, game::Entity* target, float damage, float gauge)
    {
        static bool s_disabled = false;   // one readback mismatch switches the mode off for the session
        if (s_disabled || attacker == nullptr || target == nullptr || !target->isLoaded() || damage <= 0.0f)
            return false;
        if (app::MoleMole_AttackResult_CreateElementAttack__RAW == nullptr
            || app::MoleMole_LCBaseCombat_FireBeingHitEvent == nullptr)
            return false;   // not resolved on this build - the caller falls back to the crash event

        uint32_t targetID = target->runtimeID();
        uint32_t attackerID = attacker->runtimeID();
        bool fired = false;

        relic::Try([&]()
        {
            auto* attackerCombat = attacker->combat();
            auto* targetCombat = target->combat();
            if (attackerCombat == nullptr || targetCombat == nullptr)
                return;
            auto* attackerProp = attackerCombat->fields._combatProperty_k__BackingField;
            auto* targetProp = targetCombat->fields._combatProperty_k__BackingField;
            if (attackerProp == nullptr || targetProp == nullptr)
                return;   // never fire without a defenseCombatProperty - see the note above

            auto* ar = app::MoleMole_AttackResult_CreateElementAttack(
                app::ElementType__Enum::Electric, gauge, nullptr);
            if (ar == nullptr)
                return;

            // Zero-risk proof that 0x021D77D0 really is that factory: nothing has been fired yet, so a wrong
            // match costs one log line and this mode for the session instead of a hit in the dark.
            if (ar->fields._origElementType != app::ElementType__Enum::Electric
                || ar->fields._origElementDurability != gauge)
            {
                s_disabled = true;
                LOG_DEBUG("[weather] electro refused: factory answered elem=%d gauge=%.1f (wanted 4 / %.1f)",
                    static_cast<int>(ar->fields._origElementType), ar->fields._origElementDurability, gauge);
                return;
            }

            ar->fields.damage = damage;
            ar->fields.attackerCombatProperty = attackerProp;
            ar->fields.defenseCombatProperty = targetProp;

            // AttackResult.Reset @0x021D67C0 does not cover these three, so a pooled instance would otherwise
            // carry whatever the previous attack left in them. No consumer was found reading them, so this is
            // determinism rather than safety - and the POINTER fields Reset also misses are deliberately left
            // alone: a stale pointer there is still a valid object, a null one is what a read would fault on.
            ar->fields.criticalRand = 0;
            ar->fields.elementReactionType = app::ElementReactionType__Enum::None;
            ar->fields.endureDelta = 0.0f;

            // Logged BEFORE the call on purpose: the dispatch is queued, so if a consumer takes the game down,
            // the last line in the log names the target that did it.
            static int s_logged = 0;
            if (s_logged < 10)
            {
                s_logged++;
                LOG_DEBUG("[weather] electro fire id=%u attacker=%u dmg=%.1f gauge=%.1f ar=%p",
                    targetID, attackerID, damage, gauge, ar);
            }

            app::MoleMole_LCBaseCombat_FireBeingHitEvent(attackerCombat, targetID, ar, nullptr);
            fired = true;   // `ar` belongs to the game from here on
        });

        return fired;
    }
#endif

    void CustomWeather::StrikeNearbyMonsters(void* Enviro)
    {
#if RELIC_GAME_VERSION <= 16
        if (app::EnviroSky_FireStormEffect == nullptr)
        {
            static bool s_told = false;
            if (!s_told)
            {
                s_told = true;
                LOG_DEBUG("[weather] FireStormEffect is not resolved on this game build");
            }
            return;
        }

        StormReadiness ready = ReadStormReadiness(Enviro);
        m_LightningState = !ready.read         ? LightningState::Unknown
                         : !ready.hasLightning ? LightningState::NoLightning
                         : ready.ok()          ? LightningState::Armed
                                               : LightningState::Blocked;

        static int s_lastMask = -1;
        if (ready.mask() != s_lastMask)
        {
            s_lastMask = ready.mask();
            LOG_DEBUG("[weather] storm readiness: read=%d weather=%d preset=%d hasLightning=%d thunder=%d "
                "level=%d camera=%d beginHeight=%.1f maxHeight=%.1f",
                ready.read ? 1 : 0, ready.hasWeather ? 1 : 0, ready.hasPreset ? 1 : 0,
                ready.hasLightning ? 1 : 0, ready.hasThunder ? 1 : 0, ready.hasLevel ? 1 : 0,
                ready.hasCamera ? 1 : 0, ready.beginHeight, ready.maxHeight);
        }

        if (!ready.ok())
            return;

        auto currentTime = util::GetCurrentTimeMillisec();
        auto& manager = game::EntityManager::instance();

        // Relic: proof of damage. One pending observation at a time - HP at the strike, HP again once the queued
        // event has been drained - so a session says whether the crash event landed, whether the number matches
        // the slider, and whether the server let it stand. Capped: a first-session diagnostic, not telemetry.
        struct DamageProbe { uint32_t id; float hp; int64_t due; const char* mode; };
        static DamageProbe s_probe { 0, 0.0f, 0, "crash" };
        static int s_probesLogged = 0;
        if (s_probe.id != 0 && currentTime >= s_probe.due)
        {
            float hpNow = -1.0f;
            bool present = ReadHp(manager.entity(s_probe.id), hpNow);
            LOG_DEBUG("[weather] %s damage id=%u wanted=%.1f hpBefore=%.1f hpAfter=%.1f delta=%.1f%s",
                s_probe.mode, s_probe.id, f_LightningDamageValue.value(), s_probe.hp, hpNow,
                present ? s_probe.hp - hpNow : -1.0f, present ? "" : "  (target gone - dead or unloaded)");
            s_probe.id = 0;
        }

        static int64_t s_nextStrikeTime = 0;
        if (currentTime < s_nextStrikeTime)
            return;

        std::vector<game::Entity*> targets;
        for (const auto& monster : manager.entities(game::filters::combined::Monsters))
        {
            if (manager.avatar()->distance(monster) < kStrikeRange)
                targets.push_back(monster);
        }
        if (targets.empty())
            return;

        int delay = f_LightningDelay.value();
        s_nextStrikeTime = currentTime + (delay < 100 ? 100 : delay);

        static size_t s_cursor = 0;   // round robin, so every enemy in range gets a turn without per-entity state
        auto* target = targets[s_cursor++ % targets.size()];

        float height = static_cast<float>(f_LightningHeight.value());
        if (height <= 0.0f)
            height = (ready.beginHeight > 1.0f && ready.beginHeight < 2000.0f) ? ready.beginHeight
                                                                              : kFallbackStrikeHeight;

        // ABSOLUTE, not relative. FireStormEffect applies no world-shift conversion of its own and hands the
        // point straight to the effect spawner - and that spawner demonstrably consumes world-shift-ABSOLUTE
        // coordinates: the game's own driver DEPJHAPCIBL @0x02844A10 raycasts in Unity space and then converts
        // BOTH endpoints with WorldShiftManager.GetAbsolutePosition (@0x0284502D, @0x02845068) immediately
        // before calling it. If the bolt ever lands a whole world-shift away, this is the one call to flip -
        // the abs/rel pair logged below is exactly that delta.
        app::Vector3 at = target->absolutePosition();
        bool fired = relic::Try([Enviro, at, height]()
        {
            app::EnviroSky_FireStormEffect(Enviro, at, height, nullptr);
        });

        // Relic: the damage half - deliberately on the SAME target and the SAME tick as the visual, so the bolt
        // and the number cannot drift apart. Off by default: its dispatch runs outside our guard.
        const char* damageState = "off";
        const char* damageMode = "crash";
        float hpBefore = -1.0f;
        if (f_LightningDamage.value())
        {
            bool hadHp = ReadHp(target, hpBefore);
            bool damaged = false;
            if (f_LightningElectro.value())
            {
                damageMode = "electro";
                damaged = FireElectroDamage(manager.avatar(), target, f_LightningDamageValue.value(),
                    f_LightningElementGauge.value());
                if (!damaged)
                {
                    // The Electro route refusing must never mean the bolt stops hurting: fall back to the
                    // proven crash event, and say so, so a session can tell "refused" from "never tried".
                    damageMode = "electro->crash";
                    damaged = FireCrashDamage(target, f_LightningDamageValue.value());
                }
            }
            else
            {
                damaged = FireCrashDamage(target, f_LightningDamageValue.value());
            }
            damageState = damaged ? "fired" : "refused";
            if (damaged && hadHp && s_probe.id == 0 && s_probesLogged++ < 10)
            {
                s_probe.id = target->runtimeID();
                s_probe.hp = hpBefore;
                s_probe.due = currentTime + kDamageProbeDelayMs;
                s_probe.mode = damageMode;
            }
        }

        static int s_logged = 0;
        if (s_logged < 5)
        {
            s_logged++;
            app::Vector3 rel = target->relativePosition();
            LOG_DEBUG("[weather] FireStormEffect -> %s h=%.1f targets=%zu abs=(%.1f,%.1f,%.1f) rel=(%.1f,%.1f,%.1f) "
                "dmg=%s/%s hp=%.1f",
                fired ? "ok" : "faulted", height, targets.size(),
                at.x, at.y, at.z, rel.x, rel.y, rel.z, damageState, damageMode, hpBefore);
        }
#else
        (void)Enviro;   // 2.8 keeps the upstream entity-moving behaviour exactly as it was
#endif
    }

    void CustomWeather::OnGameUpdate()
    {
        if (!f_Enabled->enabled())
            return;

        UPDATE_DELAY(100);

        auto Enviro = app::EnviroSky_get_Instance(nullptr);
        if (Enviro != nullptr)
        {
            // Relic: ChangeWeather answers whether the client actually has that weather asset, and the answer was
            // being thrown away - a weather this build does not ship fails silently and is retried ten times a
            // second. Logged once per change so a session says exactly which of the eight paths a version has.
            if (RunAssetProbe(Enviro))
                return;   // the probe owns the weather for a few seconds

            auto weatherEntry = weather.find(f_WeatherType.value());
            if (weatherEntry == weather.end())
                return;

            const std::string& weatherPath = weatherEntry->second;
            bool weatherChanged = app::EnviroSky_ChangeWeather(Enviro, string_to_il2cppi(weatherPath), 1, 1, nullptr);
            m_Availability = weatherChanged ? Availability::Present : Availability::Missing;
            static std::string s_lastWeatherPath;
            static bool s_lastWeatherOk = false;
            if (weatherPath != s_lastWeatherPath || weatherChanged != s_lastWeatherOk)
            {
                s_lastWeatherPath = weatherPath;
                s_lastWeatherOk = weatherChanged;
                LOG_DEBUG("[weather] ChangeWeather('%s') -> %d", weatherPath.c_str(), weatherChanged ? 1 : 0);
            }

#if RELIC_GAME_VERSION <= 16
            if (f_Lightning->enabled())   // ungated on 1.6 - see the note above the offsets
#else
            if (f_Lightning->enabled() && f_WeatherType.value() == CustomWeather::WeatherType::RainHeavy)
#endif
            {
                auto& manager = game::EntityManager::instance();

                // Hoisted out of the monster loop: entities() is a full walk of the game's entity list and would
                // run there once per monster within 30 m, ten times a second. The count is also the diagnostic that
                // separates "this weather produces no lightning" from "the targeting does not work".
                // Upstream behaviour, unchanged: move whatever lightning the weather already spawned onto the
                // monsters. The count stays as the running proof of whether the server ever spawns gadget
                // 70000009 - it is NOT the success signal for the strike below, which creates no entity.
                auto lightning = manager.entities(game::filters::combined::Lightning);
                static size_t s_lastLightningCount = SIZE_MAX;
                if (lightning.size() != s_lastLightningCount)
                {
                    s_lastLightningCount = lightning.size();
                    LOG_DEBUG("[weather] lightning entities: %zu", lightning.size());
                }

                if (!lightning.empty())
                {
                    for (auto& Monsters : manager.entities(game::filters::combined::Monsters))
                    {
                        if (manager.avatar()->distance(Monsters) >= kStrikeRange)
                            continue;

                        for (auto& entity : lightning)
                        {
                            entity->setRelativePosition(Monsters->relativePosition());
                        }
                    }
                }

                StrikeNearbyMonsters(Enviro);   // and, on 1.6, draw one ourselves
            }
        }
    }
}