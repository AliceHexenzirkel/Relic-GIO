"""Helper modules of build/make_tweaks_catalog.py (the generator of agent/payloads/<version>/tweaks.json).

    stack     every byte read from a vendor stack, proven against the vendor's own md5 list
    luagroup  reader of the server's scene group scripts (tables + trigger functions, no Lua runtime)
    towersim  symbolic run of one Spiral Abyss scene: which slots spawn, in which order, how many kills
    abyss     floors / chambers / halves / waves, and the monsters a slot may hold, each with its pose
    drops     the drop tables: statue domains, their bounds and previews, the item caps, what may be added
              to a statue row
    quests    quest reward rows, the daily-commission drops, the items a multiplier leaves at their count

Standard library only.
"""
