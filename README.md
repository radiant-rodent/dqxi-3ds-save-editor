# DQXI 3DS Save Editor

A Windows save editor for **Dragon Quest XI (Nintendo 3DS)** save slots (`Data0`, `Data1`, `Data2`),
for example the ones Azahar/Citra keep under
`sdmc/Nintendo 3DS/…/title/00040000/00199200/data/00000001`.

Every byte of the 51,376-byte slot file has a known region, and most values can be edited:

| Page | What you can edit |
|---|---|
| Overview | hero name, gold, play time, Mini Medals, casino tokens, Evac point, line-up, system settings, StreetPass profile text, position |
| Party | level/EXP, HP/MP, tactics, skill points, seeds, equipment, each character's items, learned skill panels |
| Item Bag / Equipment Bag | add, remove, count, forge level (+1 to +3) |
| Key Items / Recipe Books | owned or not |
| Story | the 54 chapters: finished flag, step, sub-flags |
| Quests | all 90 side quests with their Quest Catalogue state |
| Zoom destinations | the 25 places |
| Monsters | defeat counts per monster |
| Accolades / Item List | earned accolades, items ever obtained (with forged versions) |
| Flags and counters | 686 global flags (named after the event scripts that use them), record counters, tutorials, record flags, cutscenes |
| Raw data | hex view with the region map, value search, compare two saves, write any value |

Saving rewrites the slot's checksum and keeps a copy of the file it replaces in
`%LOCALAPPDATA%\DQXISaveEditor\backups`.

## Download

Grab `SaveEditor.exe` from the [releases](https://github.com/radiant-rodent/dqxi-3ds-save-editor/releases):
one portable file, no install, no .NET needed (Windows 10/11, 64-bit). Game data (names, tables)
and item icons are embedded directly in the editor, so no extra files are needed.

## Building

Needs the .NET 9 SDK (Windows, WPF):

```
dotnet build -c Release
bin\Release\net9.0-windows\SaveEditor.exe [path to Data1]
```

Portable single-file exe (what the releases contain):

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
```

## Game data

All game tables (`Assets/gamedata.json`) and item icons (`Assets/icons.png`) are included and
embedded directly into the executable. If desired, you can place a custom `gamedata.json` next to the
executable to override the built-in tables.

## Command line

- `SaveEditor.exe --dump Data1 out.txt`: text dump of the known fields
- `SaveEditor.exe --test-edit in out`: a fixed set of edits through the editor's own code (used for emulator tests)
- `SaveEditor.exe --snapshot Data1 outdir [PageTag[:record] ...]`: render pages to PNG

## Notes

Values were decoded from the game's code and tables and checked in an emulator where possible.
Editing story chapters, quests or flags can put a save into states the game never expects;
keep the backups.

Not affiliated with Square Enix. Dragon Quest is a trademark of Square Enix.
