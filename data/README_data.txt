Romestead Save Editor data folder

This folder stores external plain-text databases used by the application.

Files:
- romestead_items.json: item IDs, names, categories, stack limits, flags and editor safety data.
- game_text.json: game localization keys used by item/NPC/citizen display.
- citizen_traits.json: citizen aura/trait IDs and their stat effects.

These files are intentionally not encrypted. They are loaded at startup so future game updates can be handled by refreshing/replacing the database files instead of editing C# source code.

item_auras.json
  Equipment item aura database used by the item aura page and player inventory aura editor.
  It is plain JSON and can be regenerated from Shared.Data.Auras.SharedDataSetup.SetupItemsAuras.
