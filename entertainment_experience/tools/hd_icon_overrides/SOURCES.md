# hd_icon_overrides — HD icon art that the upstream Akebi HD set gets wrong or never had

`tools/make_hd_icons.py` takes a label's HD art from here first, then from the gitignored upstream set in
`heavy_ref/res/iconsHD/`. Originals, untrimmed; the tool fits them into the shipped 128 px icons.

| File | Why it is here | Source | Verified by |
|---|---|---|---|
| `ImagingConch.png` | Upstream's HD ImagingConch is a byte copy of EchoingConch (HoYoLAB reused one image for both labels) | `https://enka.network/ui/UI_ItemIcon_101935.png` — 2.8 quest items 101935-101937 "Misplaced Conch 1/2/3", whose description calls the item an Imaging Conch | 2.8 MaterialExcel: all three items use icon `UI_ItemIcon_101935`; same art as the wiki's "Item Misplaced Conch 1" (a smaller re-encode there, not the same bytes) |
| `SeaRewardCrate.png` | New label (1.6 gadget 70950093 "海上奖励木箱") | HoYoLAB map label 129 "Wooden Mora Chest", `https://act-webstatic.hoyoverse.com/map_manage/20250424/85e526881a5e9729910ae4fdcfa490fe_3085184466534430280.png` — a crate floating in water; same object class, other region | md5 of the file = the URL's filename prefix |
| `PhantasmalConchSlot.png` | New label (2.8 gadgets 70310344-46 / 70500054 `Dreamconch_Slot`) | HoYoLAB map label 187 "Drained Conch Cup", `https://act.hoyoverse.com/map_manage/20231031/6fea28ba75798c9842d8c628552138f1_6939284778296288798.png` — a conch receptacle; same object class, later region | md5 = filename prefix |
| `DreamPortal.png` | New label (2.8 gadget 70290286 `DreamPortal_01`) | HoYoLAB map label 322 "Enkanomiya Phase Gate", `https://act.hoyoverse.com/map_manage/20221125/d16a5adbb5d18a927553a762780356af_227960625027567836.png` | md5 = filename prefix |
| `AbyssHerald.png` | Label with no icon at all (Enkanomiya + Chasm underground, "Abyss Herald") | `https://gi.yatta.moe/assets/UI/monster/UI_MonsterIcon_Invoker_Herald_Water_01.png` (enka.network does not serve monster icons: 404) | 2.8 MonsterDescribe 20201 "Abyss Herald: Wicked Torrents" (monsters 22020101/22020102); same picture as the label's own HoYoLAB art (md5 e25ba3df7ca0..., alpha differs on <0.1% of pixels). Its 30x30 icon went to `res/icons/` |
| `BathysmalVishap.png` | Label with no icon at all (Enkanomiya, "Bathysmal Vishap") | `https://gi.yatta.moe/assets/UI/monster/UI_MonsterIcon_Drake_Deepsea_Electric.png` (enka.network does not serve monster icons: 404) | 2.8 MonsterDescribe 60508 "Bolteater Bathysmal Vishap" (monsters 26050801/26051101/26050802) - the variant HoYoLAB's own label shows; same picture as the label's own HoYoLAB art (md5 188d967c0d7e..., alpha differs on <0.1% of pixels). Its 30x30 icon went to `res/icons/` |
| `TheBlackSerpents.png` | Label with no icon at all (Chasm underground, "The Black Serpents") | `https://gi.yatta.moe/assets/UI/monster/UI_MonsterIcon_ForlornVessel_Strong_Lance_Water.png` (enka.network does not serve monster icons: 404) | 2.8 MonsterDescribe 20702 "Shadowy Husk: Line Breaker" (monsters 22070201/22070202); the Husks' codex family is "The Black Serpent Knights' End"; same picture as the label's own HoYoLAB art (md5 b40cbfe1ab36..., alpha differs on <0.1% of pixels). Its 30x30 icon went to `res/icons/` |

`ConstellationMechanism` has no HD art: its 30x30 icon is composed (no authentic art exists), so the HD
lookup falls back to it.
