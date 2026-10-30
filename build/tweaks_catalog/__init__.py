"""Helper modules of build/make_tweaks_catalog.py (the generator of agent/payloads/<version>/tweaks.json).

    stack     every byte read from a vendor stack, proven against the vendor's md5 list: the stack's own, or
              the one of the vendor's data archive (--vendor-data)
    luagroup  reader of the server's scene group scripts (tables + trigger functions, no Lua runtime)
    towersim  symbolic run of one scene's group scripts -- a Spiral Abyss chamber, a statue domain: which
              slots spawn, in which order, how many kills; and whether a scene stays inside what the run models
    abyss     floors / chambers / halves / waves, and the monsters a slot may hold, each with its pose
    lineups   the monster group of every statue domain: its spawn slots, where the run is a proof of them
              its waves, and the largest level anything of its scene carries -- the bound of the domain's
              enemy level
    drops     the drop tables: statue domains, their bounds and previews, the item caps, what may be added
              to a statue row
    quests    the reward rows of the quests and of the hangout events (each tier of a hangout chapter), the
              daily-commission drops, the items a multiplier leaves at their count
    mutations the proofs of the modules above, held against copies of a stack's files that are edited in
              memory (make_tweaks_catalog.py --mutations)

Standard library only.
"""
