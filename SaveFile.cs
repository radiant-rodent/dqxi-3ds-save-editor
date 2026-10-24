using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SaveEditor
{
    public enum Kind { U8, U16, U32, F32, Text }

    // A named value at a fixed offset (used for labels in the raw view and for --dump).
    public sealed class Field
    {
        public readonly string Label;
        public readonly int Offset;
        public readonly Kind Kind;
        public readonly string Note;
        public readonly int Units;    // text length in UTF-16 units

        public Field(string label, int offset, Kind kind, string note = "", int units = 7)
        {
            Label = label;
            Offset = offset;
            Kind = kind;
            Note = note;
            Units = units;
        }

        public int Size => Kind == Kind.U8 ? 1 : Kind == Kind.U16 ? 2 : Kind == Kind.Text ? 2 * Units : 4;
    }

    // A stretch of the slot file and what it holds.
    public sealed class Region
    {
        public int Start { get; }
        public int End { get; }
        public string Name { get; }
        public string Note { get; }
        public bool Known { get; }

        public Region(int start, int end, string name, string note, bool known = true)
        {
            Start = start;
            End = end;
            Name = name;
            Note = note;
            Known = known;
        }

        public string Range => $"0x{Start:X4}–0x{End - 1:X4}";
        public int Size => End - Start;
    }

    // A DQXI 3DS slot file (Data0..2), 51376 bytes. The last 8 bytes are u32 0x5E and the CRC32
    // of everything before them. Layout notes say how each part was checked (emulator = changed
    // in a copy of the save and seen on screen in Azahar).
    public sealed class SaveFile
    {
        public const int FileSize = 51376;
        public const int RecordSize = 0x200;
        public const int RecordCount = 31;
        public const int InvSlots = 24;              // per-character item list, +0x24
        public const int EquipSlots = 6;             // equipped list indices, +0x54
        public const int PanelSlots = 60;            // learned skill panel ids, +0x5A (the code clears 60)
        public const int ItemBag = 0x3E34, ItemBagSlots = 174;
        public const int EquipBag = 0x40EC, EquipBagSlots = 2341;
        public const int KeyItems = 0x65C4, KeyItemSlots = 90;
        public const int Recipes = 0x6678, RecipeSlots = 105;
        public const int Gold = 0x3E28, Gold2 = 0x3E2C, PlayTime = 0x3E24;
        public const int PartyOrder = 0x3E04, PartySlots = 31;
        public const int BattlesWon = 0x3E30;
        public const int Accolades = 0x6764, AccoladeCount = 300;     // bit per accolade
        public const int Settings = 0x6FBF;                            // battle speed, BGM, SFX, tilt camera
        public const int Defeats = 0x78E4, DefeatSlots = 1043;         // u32 per enemy_base entry
        public const int ItemsSeen = 0x8930, ItemsSeenBits = 2875;     // bit per item id
        public const int DisplayMode = 0x6FCC;
        public const int SaveLocationText = 0xC6FA, SaveLocationUnits = 64;
        public const int ProfileName = 0xC46C, ProfileMessage = 0xC47A;
        // Decoded from code.bin (accessor functions named in the disassembly project).
        public const int PartyCount = 0x6580;                          // members in the line-up
        public const int RecordCounters = 0x6798, RecordCounterCount = 51;   // u32, AddRecordCount
        public const int RecordFlags = 0x6864, RecordFlagCount = 96;   // bits, SetRecordFlag
        public const int GlobalFlags = 0x6870, GlobalFlagCount = 686;  // u8, Get/SetGlobalGameFlag
        public const int Scenarios = 0x6B2D, ScenarioSize = 20, ScenarioCount = 54;  // SetScenarioFlag
        public const int QuestStates = 0x6F65, QuestCount = 90;        // u8, GetQuestFlag
        public const int EvacPoint = 0x6FE2, MiniMedals = 0x6FE4, CasinoTokens = 0x6FF4;
        public const int ZoomMask = 0x78E0, ZoomCount = 25;            // bits, RuraPointAdd
        public const int Tutorials = 0x9562, TutorialCount = 101;      // u8, StartTutorialUI
        public const int Cutscenes = 0x97AC, CutsceneBytes = 84;       // bits, GetCSWatchFlag
        public const int QuestDisabled = 0xC7A8;                        // bit per quest, SetQuestEnableFlag
        public const int ScenarioFinished = 0x22, ScenarioNotStarted = 0xFF;

        // Global flag set when a character joins (PartyAdd's table in code.bin): record → flag.
        public static readonly (int record, int flag)[] JoinFlags =
            { (1, 325), (2, 326), (3, 327), (27, 328), (4, 329), (5, 330), (6, 331), (7, 332) };
        const uint Magic = 0x5E;

        public static readonly string[] EquipSlotNames = { "Right hand", "Left hand", "Head", "Torso", "Accessory 1", "Accessory 2" };
        public static readonly string[] TacticsNames =
            { "Show No Mercy", "Fight Wisely", "Mix It Up", "Focus On Healing", "Don't Use MP", "Follow Orders" };
        public static readonly string[] DisplayModeNames = { "2D", "3D", "2-way (3D top, 2D bottom)" };

        // Offsets inside a character record.
        public static readonly Field[] RecordFields =
        {
            new Field("ID (= record number)", 0x00, Kind.U16),
            new Field("Name", 0x02, Kind.Text),
            new Field("Level", 0x10, Kind.U16, "emulator: Camp menu"),
            new Field("u16 0x12 (always 0)", 0x12, Kind.U16),
            new Field("EXP", 0x14, Kind.U32, "emulator: Stats screen"),
            new Field("u32 0x18 (always 0)", 0x18, Kind.U32, "not skill points (emulator)"),
            new Field("Tactics", 0x1C, Kind.U8, "emulator: 1 = Fight Wisely, 5 = Follow Orders"),
            new Field("Battle byte", 0x1D, Kind.U8, "255 by default, 0-2 in a few records of the 41 test saves"),
            new Field("Skill points spent", 0x1E, Kind.U16, "emulator: available = level table total − this"),
            new Field("HP (current)", 0x20, Kind.U16, "emulator: Camp menu"),
            new Field("MP (current)", 0x22, Kind.U16, "emulator: Camp menu"),
            new Field("Seeds: Max HP", 0x100, Kind.U16, "emulator: Stats screen, Other column"),
            new Field("Seeds: Max MP", 0x102, Kind.U16),
            new Field("Seeds: Strength", 0x104, Kind.U16),
            new Field("Seeds: Deftness", 0x106, Kind.U16),
            new Field("Seeds: Agility", 0x108, Kind.U16),
            new Field("Seeds: Resilience", 0x10A, Kind.U16),
            new Field("Seeds: Magical might", 0x10C, Kind.U16),
            new Field("Seeds: Magical mending", 0x10E, Kind.U16),
            new Field("Seeds: Charm", 0x110, Kind.U16),
            new Field("State byte 0xF6", 0xF6, Kind.U8, "0xFF by default, 0 or 2 in the test saves"),
            new Field("State byte 0xF7", 0xF7, Kind.U8, "0xFF by default, 0 or 14 in the test saves"),
            new Field("Growing float", 0xFC, Kind.F32, "17.75 / 3.0 / 114.0 in the test saves; likely the hidden pep gauge"),
        };

        public static readonly Field[] SlotFields =
        {
            new Field("Save format magic (0x007842DC)", 0x3E00, Kind.U32, "same in every save"),
            new Field("Play time (seconds)", PlayTime, Kind.U32, "grows with play time in every save"),
            new Field("Gold", Gold, Kind.U32, "emulator: Camp menu"),
            new Field("Gold, second value", Gold2, Kind.U32, "not shown in the Camp menu (emulator); equal to Gold in most saves"),
            new Field("Battles won", BattlesWon, Kind.U32, "emulator: Info overview showed 26"),
            new Field("Battle speed", Settings, Kind.U8, "emulator: 0 Fast, 1 Plain, 2 Slow"),
            new Field("BGM volume", Settings + 1, Kind.U8, "emulator: 0-4 = 1-5"),
            new Field("SFX volume", Settings + 2, Kind.U8, "emulator: 0-4 = 1-5"),
            new Field("Tilt camera", Settings + 3, Kind.U8, "emulator: 0 Normal, 1 Inverted"),
            new Field("Current area id", 0x6FC4, Kind.U32, "differs with the location (35 in the Manglegrove)"),
            new Field("Sparkly spot timer 1 (seconds)", 0x8A9C, Kind.U32, "counts down every second of play"),
            new Field("Party size", PartyCount, Kind.U32, "PartyAdd; emulator: line-up + this + joined flag adds a member"),
            new Field("Position X", 0x658C, Kind.F32),
            new Field("Position Y", 0x6590, Kind.F32),
            new Field("Position Z", 0x6594, Kind.F32),
            new Field("Facing X", 0x6598, Kind.F32),
            new Field("Facing Y", 0x659C, Kind.F32),
            new Field("Facing Z", 0x65A0, Kind.F32),
            new Field("Area", 0x65A8, Kind.U32),
            new Field("Room", 0x65AC, Kind.U32),
            new Field("No Shopping flag", 0x6A80, Kind.U8, "emulator tests (scenario.py)"),
            new Field("Display mode", DisplayMode, Kind.U8, "0 = 2D, 1 = 3D, 2 = 2-way"),
            new Field("Profile name", ProfileName, Kind.Text),
            new Field("Profile message", ProfileMessage, Kind.Text, "", 24),
            new Field("Location shown on the save menu", SaveLocationText, Kind.Text, "", 32),
        };

        public static readonly Region[] Regions =
        {
            new Region(0x0000, 0x3E00, "Character records", "31 records of 0x200 bytes: the party, their second forms and story guests"),
            new Region(0x3E00, 0x3E04, "Format magic", "0x007842DC in every save"),
            new Region(PartyOrder, PartyOrder + PartySlots, "Party line-up", "record numbers in party order, 0xFF ends (emulator); count at 0x6580"),
            new Region(0x3E23, 0x3E24, "Padding", "0 in every save"),
            new Region(0x3E24, 0x3E30, "Play time and gold", "play time in seconds (capped at 999:59:59), gold, a second gold value"),
            new Region(BattlesWon, 0x3E34, "Battles won", "u32 (emulator: Info overview)"),
            new Region(ItemBag, EquipBag, "Item Bag", "174 slots: u16 item id, u16 count (emulator)"),
            new Region(EquipBag, 0x6580, "Equipment Bag", "u16 item id, u16 count (emulator)"),
            new Region(0x6580, 0x65C4, "Party size and position", "u32 party size (PartyAdd), position, facing, area and room"),
            new Region(KeyItems, Recipes, "Key Items", "90 u16 item ids in the order they were obtained (emulator)"),
            new Region(Recipes, 0x674A, "Recipe books", "105 u16 item ids"),
            new Region(0x674A, 0x6754, "Unused", "0 in all 41 test saves; no code reads or writes it"),
            new Region(0x6754, Accolades, "Costume accessories obtained", "bit per hat/costume item (item ids 2723-2840)"),
            new Region(Accolades, 0x678C, "Accolades", "one bit per accolade, 300 (emulator)"),
            new Region(0x678C, RecordCounters, "Unused", "0xFF fill in all 41 test saves (after the 40 accolade bytes the game clears); no code reads it"),
            new Region(RecordCounters, RecordFlags, "Record counters", "51 u32 tallies (AddRecordCount)"),
            new Region(RecordFlags, GlobalFlags, "Record flags", "96 bits (SetRecordFlag)"),
            new Region(GlobalFlags, GlobalFlags + GlobalFlagCount, "Global flags", "686 story/event bytes (Get/SetGlobalGameFlag); 325-332 = party members joined"),
            new Region(0x6B1E, Scenarios, "Event bits", "bit per id set by a field event (0x6B20)"),
            new Region(Scenarios, QuestStates, "Story chapters", "54 × 20 bytes: step (0xFF not started, 0x22 finished) + 19 sub-flags"),
            new Region(QuestStates, Settings, "Quest states", "byte per side quest: 0xFF unavailable, 0 not taken ... 9/10 completed"),
            new Region(Settings, 0x6FC4, "System settings", "battle speed, BGM, SFX, tilt camera (emulator), one more setting byte"),
            new Region(0x6FC4, 0x6FCE, "Area and display", "current area id (u16) at 0x6FC4, display mode at 0x6FCC"),
            new Region(0x6FCE, EvacPoint, "Respawn points", "where the party wakes up after a wipe and the special respawn (SetBasicRespawnID)"),
            new Region(EvacPoint, MiniMedals, "Evac point, Nelsen hope flag", "SetRiremitoPoint, SetNelsenHopeFlag"),
            new Region(MiniMedals, CasinoTokens, "Mini Medals", "held (0x6FE4), handed in to the Academy (0x6FEC), hand-in count (0x6FF0)"),
            new Region(CasinoTokens, 0x6FFC, "Casino tokens", "u32, capped at 9,999,999"),
            new Region(0x6FFC, 0x701C, "Casino jackpots", "4 × (u32 jackpot coins, stage byte)"),
            new Region(0x701C, 0x7020, "Field load byte", "byte copied into the field object when a map loads (0 in every save), 3 padding bytes"),
            new Region(0x7020, 0x78D0, "Field object states", "278 × (u16 object id, u8 sub at +3, u32 value), 0xFFFF = empty"),
            new Region(0x78D0, ZoomMask, "Areas visited", "bit per level_area entry (123 areas)"),
            new Region(ZoomMask, Defeats, "Zoom destinations", "bit per rura_point entry (25)"),
            new Region(Defeats, ItemsSeen, "Monsters defeated", "u32 per enemy table entry, 1043 (emulator: Monster List counts)"),
            new Region(ItemsSeen, ItemsSeen + 360, "Items obtained", "one bit per item id (emulator: Info, Item List)"),
            new Region(0x8A98, 0x8BD8, "Sparkly spot timers", "40 × (u16 glitter_drop id, u32 seconds until it refills), ticked every second"),
            new Region(0x8BD8, 0x8D18, "Spirit spawn timers", "40 × (u16 spirit_generator_table id, u32 seconds until it can spawn again), ticked every second"),
            new Region(0x8D18, 0x9218, "Unused", "0 in all 41 test saves; no code reads or writes it"),
            new Region(0x9218, 0x927C, "Treasure taken", "bit per fixed_drop_param entry (774 chests and fixed pickups)"),
            new Region(0x927C, 0x9562, "Vehicles and respawn jump", "embark records (SetEmbarkartionInfo: position floats), LoadRespawnJump"),
            new Region(Tutorials, 0x95C8, "Tutorials", "byte per tutorial page: 0 unseen, 1 shown, 3 read"),
            new Region(0x95C8, 0x9628, "Parked vehicles", "6 × 16 bytes: in-use byte, position floats (-1 = none), two state bytes at +0x0C"),
            new Region(0x9628, 0x9630, "Forge values", "two bytes and a u32 the forge caps at 9999"),
            new Region(0x9630, 0x9798, "Items forged", "bit per base item id crafted at the Fun-Size Forge (Bronze sword and Divine dagger in the test save)"),
            new Region(0x9798, Cutscenes, "Unused", "0xFF fill"),
            new Region(Cutscenes, 0x9800, "Cutscenes watched", "bit per cutscene (GetCSWatchFlag)"),
            new Region(0x9800, 0x984E, "Debug menu values", "scenario/jump values the debug menu writes (StartDebugMenu); 0xFF = unset"),
            new Region(0x984E, 0x9988, "Scenario battle results", "byte per story battle (GetScenarioBattleResult)"),
            new Region(0x9988, 0x99E0, "Battle return point", "floats: where to put the party after a battle (BattleEndToDefaultField)"),
            new Region(0x99E0, 0x99E4, "Field flag word", "bit 2 set when casino tokens are added, bit 3 set in later saves"),
            new Region(0x99E4, 0x9DE4, "Field id list", "256 × (s16 id, flag byte, byte), -1 = empty; bit 2 of the flag byte is checked by field code (empty in the known saves)"),
            new Region(0x9DE4, 0xB93C, "StreetPass spirits", "50 received profiles × 0x8C: 0x84-byte profile, two bytes, u32 serial (-1 = empty) (GetSpiritNum)"),
            new Region(0xB93C, 0xC42C, "Kept spirits", "20 profiles × 0x8C, same layout, copied from the spirit list"),
            new Region(0xC42C, 0xC430, "Padding", "0 in every save"),
            new Region(0xC430, 0xC4B4, "Own StreetPass profile", "0x84 bytes: four party names (7 chars each), u32, player name at 0xC46C, greeting at 0xC47A, answer bytes from 0xC4A0"),
            new Region(0xC4B4, 0xC538, "Profile being received", "0x84-byte profile buffer (names written at +0x0E/+0x1C/+0x2A)"),
            new Region(0xC538, 0xC548, "Profile ids", "8-byte id (copied from 0xC780) and the own profile's serial"),
            new Region(0xC548, 0xC594, "StreetPass counters", "u32 next spirit serial at 0xC548, field bytes 0xC54C-0xC560"),
            new Region(0xC594, 0xC5B4, "Dive dungeon chests", "bit per divecmn item_box_param entry (250)"),
            new Region(0xC5B4, 0xC654, "Dive dungeon records", "10 × 8 u16, 0x7FFF = none (reset together with the chests)"),
            new Region(0xC654, 0xC66C, "Dive dungeon state", "0xC654/0xC655 state bytes, 0xC66A run counter (max 255), 0xC66B"),
            new Region(0xC66C, 0xC6EC, "Hat tickets", "byte per costume item (AddBoushiTicketNum, max 99)"),
            new Region(0xC6EC, SaveLocationText, "Save menu values", "bytes the slot menu and title read"),
            new Region(SaveLocationText, 0xC780, "Save menu location", "location name the slot menu shows"),
            new Region(0xC780, QuestDisabled, "Save id", "8-byte id made for each new game (copied to profiles at 0xC538), then zero padding"),
            new Region(QuestDisabled, QuestDisabled + 12, "Quests disabled", "bit per side quest (SetQuestEnableFlag writes the inverse)"),
            new Region(QuestDisabled + 12, FileSize - 8, "Unused / runtime", "0 in all 41 test saves; 0xC8AC-0xC8AF are runtime fields the checksum overwrites"),
            new Region(FileSize - 8, FileSize, "Checksum", "u32 0x5E, CRC32 of the bytes before it (the editor rewrites it on save)"),
        };

        public string Path;
        public byte[] Bytes;

        public static SaveFile Load(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length != FileSize)
                throw new InvalidDataException($"{System.IO.Path.GetFileName(path)} is {bytes.Length} bytes; a DQXI slot file (Data0, Data1, Data2) is {FileSize}.");
            return new SaveFile { Path = path, Bytes = bytes };
        }

        public SaveFile Clone() => new SaveFile { Path = Path, Bytes = (byte[])Bytes.Clone() };

        public bool CrcOk()
        {
            int end = Bytes.Length - 8;
            return BitConverter.ToUInt32(Bytes, end) == Magic && BitConverter.ToUInt32(Bytes, end + 4) == Crc32.Compute(Bytes, 0, end);
        }

        public void FixCrc()
        {
            int end = Bytes.Length - 8;
            BitConverter.GetBytes(Magic).CopyTo(Bytes, end);
            BitConverter.GetBytes(Crc32.Compute(Bytes, 0, end)).CopyTo(Bytes, end + 4);
        }

        public void WriteTo(string path)
        {
            FixCrc();
            File.WriteAllBytes(path, Bytes);
            Path = path;
        }

        public byte U8(int o) => Bytes[o];
        public ushort U16(int o) => BitConverter.ToUInt16(Bytes, o);
        public uint U32(int o) => BitConverter.ToUInt32(Bytes, o);
        public float F32(int o) => BitConverter.ToSingle(Bytes, o);
        public void SetU8(int o, int v) => Bytes[o] = (byte)v;
        public void SetU16(int o, int v) => BitConverter.GetBytes((ushort)v).CopyTo(Bytes, o);
        public void SetU32(int o, uint v) => BitConverter.GetBytes(v).CopyTo(Bytes, o);
        public void SetF32(int o, float v) => BitConverter.GetBytes(v).CopyTo(Bytes, o);

        public string Text(int off, int units)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < units; i++)
            {
                char c = (char)U16(off + 2 * i);
                if (c == 0) break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        public void SetText(int off, int units, string s)
        {
            if (s.Length > units) s = s.Substring(0, units);
            for (int i = 0; i < units; i++)
                SetU16(off + 2 * i, i < s.Length ? s[i] : 0);
        }

        public static int Record(int index) => index * RecordSize;

        public string RecordName(int index) => Text(Record(index) + 2, 7);

        // 6 characters and the NUL (a 7th character ran into the next field: "Hendrik$").
        public void SetRecordName(int index, string s) => SetText(Record(index) + 2, 7, s.Length > 6 ? s.Substring(0, 6) : s);

        // u16 lists that end at the first 0xFFFF (character items, key items, panels).
        public List<int> IdList(int off, int slots)
        {
            var list = new List<int>();
            for (int i = 0; i < slots; i++)
            {
                int v = U16(off + 2 * i);
                if (v == 0xFFFF) break;
                list.Add(v);
            }
            return list;
        }

        public void SetIdList(int off, int slots, IList<int> ids)
        {
            for (int i = 0; i < slots; i++)
                SetU16(off + 2 * i, i < ids.Count ? ids[i] : 0xFFFF);
        }

        // Bag slots: u16 id, u16 count. Empty slots are 0xFFFF, 0.
        public List<(int id, int count)> Bag(int off, int slots)
        {
            var list = new List<(int, int)>();
            for (int i = 0; i < slots; i++)
            {
                int id = U16(off + 4 * i);
                if (id != 0xFFFF) list.Add((id, U16(off + 4 * i + 2)));
            }
            return list;
        }

        public void SetBag(int off, int slots, IList<(int id, int count)> entries)
        {
            for (int i = 0; i < slots; i++)
            {
                SetU16(off + 4 * i, i < entries.Count ? entries[i].id : 0xFFFF);
                SetU16(off + 4 * i + 2, i < entries.Count ? entries[i].count : 0);
            }
        }

        public string Format(Field f, int baseOff = 0)
        {
            int o = baseOff + f.Offset;
            var inv = CultureInfo.InvariantCulture;
            switch (f.Kind)
            {
                case Kind.U8: return U8(o).ToString(inv);
                case Kind.U16: return U16(o).ToString(inv);
                case Kind.U32: return U32(o).ToString(inv);
                case Kind.F32: return F32(o).ToString("G9", inv);
                default: return Text(o, f.Units);
            }
        }

        public static Region RegionAt(int off) => Regions.FirstOrDefault(r => off >= r.Start && off < r.End);

        // Name of what lives at this offset, as precise as the layout knows it.
        public string Describe(int off)
        {
            foreach (var f in SlotFields)
                if (off >= f.Offset && off < f.Offset + f.Size) return f.Label;
            if (off < RecordCount * RecordSize)
            {
                int rec = off / RecordSize, rel = off % RecordSize;
                string who = $"record {rec} ({RecordName(rec)})";
                foreach (var f in RecordFields)
                    if (rel >= f.Offset && rel < f.Offset + f.Size) return $"{who}: {f.Label}";
                if (rel >= 0x24 && rel < 0x54) return $"{who}: item {(rel - 0x24) / 2 + 1}";
                if (rel >= 0x54 && rel < 0x5A) return $"{who}: equipped {EquipSlotNames[rel - 0x54]}";
                if (rel >= 0x5A && rel < 0x5A + 2 * PanelSlots) return $"{who}: learned panel {(rel - 0x5A) / 2 + 1}";
                return $"{who}: unused (+0x{rel:X3}, constant in every test save)";
            }
            if (off >= ItemBag && off < EquipBag) return $"Item Bag slot {(off - ItemBag) / 4 + 1}";
            if (off >= EquipBag && off < 0x6580) return $"Equipment Bag slot {(off - EquipBag) / 4 + 1}";
            if (off >= KeyItems && off < Recipes) return $"Key item {(off - KeyItems) / 2 + 1}";
            if (off >= Recipes && off < 0x674A) return $"Recipe book {(off - Recipes) / 2 + 1}";
            if (off >= Defeats && off < ItemsSeen) return $"Monsters defeated, enemy {(off - Defeats) / 4}";
            if (off >= Accolades && off < Accolades + 38) return $"Accolades {(off - Accolades) * 8}–{(off - Accolades) * 8 + 7}";
            if (off >= ItemsSeen && off < ItemsSeen + 360) return $"Items obtained, ids {(off - ItemsSeen) * 8}–{(off - ItemsSeen) * 8 + 7}";
            var r = RegionAt(off);
            return r == null ? "" : r.Name;
        }
    }

    static class Crc32
    {
        static readonly uint[] Table = BuildTable();

        static uint[] BuildTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        public static uint Compute(byte[] d, int start, int length)
        {
            uint c = 0xFFFFFFFF;
            for (int i = start; i < start + length; i++) c = Table[(c ^ d[i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFF;
        }
    }
}
