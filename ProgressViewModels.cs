using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Data;

namespace SaveEditor
{
    // Story chapters, side quests and the game's progress flags. Offsets and meanings come from
    // the accessor functions in code.bin (see SaveFile constants); names from gamedata.json.

    // ---------------------------------------------------------------- story chapters

    // 0x6B2D: 54 chapters × 20 bytes. Byte 0 is the chapter's step: 0xFF not started, counts up
    // while it plays (0x0C, 0x0E ... in the first chapter), 0x22 finished. Bytes 1-19 are the
    // chapter's own sub-flags.
    public sealed class ScenarioRowVM : Bindable
    {
        readonly Editor ed;
        public int Index { get; }
        public string Name { get; }

        public ScenarioRowVM(Editor ed, int index, string name)
        {
            this.ed = ed;
            Index = index;
            Name = name;
        }

        int Off => SaveFile.Scenarios + SaveFile.ScenarioSize * Index;
        public string Number => (Index + 1).ToString(CultureInfo.InvariantCulture);

        public int Step
        {
            get => ed.Save.U8(Off);
            set
            {
                if (value < 0 || value > 255) throw new ArgumentException("Step is 0 to 255 (255 = not started, 34 = finished).");
                ed.Save.SetU8(Off, value);
                ed.Touch();
                Changed();
            }
        }

        public string Status => Step == SaveFile.ScenarioNotStarted ? "Not started"
            : Step == SaveFile.ScenarioFinished ? "Finished"
            : $"Playing (step {Step})";

        public bool Finished
        {
            get => Step == SaveFile.ScenarioFinished;
            set => Step = value ? SaveFile.ScenarioFinished : SaveFile.ScenarioNotStarted;
        }

        public bool Started => Step != SaveFile.ScenarioNotStarted;

        // The 19 sub-flag bytes, editable as hex.
        public string SubFlags
        {
            get => BitConverter.ToString(ed.Save.Bytes, Off + 1, SaveFile.ScenarioSize - 1).Replace("-", " ");
            set
            {
                var parts = (value ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != SaveFile.ScenarioSize - 1)
                    throw new ArgumentException($"{SaveFile.ScenarioSize - 1} hex bytes.");
                var bytes = parts.Select(p => byte.Parse(p, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
                bytes.CopyTo(ed.Save.Bytes, Off + 1);
                ed.Touch();
                Raise();
            }
        }

        public void Changed()
        {
            Raise(nameof(Step));
            Raise(nameof(Status));
            Raise(nameof(Finished));
            Raise(nameof(Started));
        }
    }

    public sealed class StoryVM : Bindable
    {
        readonly Editor ed;
        public ObservableCollection<ScenarioRowVM> Rows { get; } = new ObservableCollection<ScenarioRowVM>();

        public StoryVM(Editor ed)
        {
            this.ed = ed;
            for (int i = 0; i < SaveFile.ScenarioCount; i++)
                Rows.Add(new ScenarioRowVM(ed, i, GameData.Named(ed.Game.Scenarios, i, "Chapter")));
            foreach (var r in Rows) r.PropertyChanged += (s, e) => Raise(nameof(Summary));
        }

        public string Summary
        {
            get
            {
                int done = Rows.Count(r => r.Finished);
                var now = Rows.FirstOrDefault(r => r.Started && !r.Finished);
                return $"{done} of {Rows.Count} chapters finished" + (now != null ? $"  ·  now playing: {now.Name}" : "");
            }
        }
    }

    // ---------------------------------------------------------------- side quests

    // 0x6F65: one state byte per quest (quest_param order = Quest Catalogue order). Checked in the
    // emulator's Quest Catalogue: 0xFF = not listed (the story unlocks quests by setting 0),
    // 0 = "This quest cannot be taken now", 1 = available (hint and reward shown), 2 = Underway,
    // 7 and 8 = Achieved (report back), 9 = Cleared; 10 hides the quest from the catalogue.
    // Plus a "disabled" bit per quest at 0xC7A8.
    public sealed class QuestState
    {
        public int Value { get; set; }
        public string Label { get; set; }
        public override string ToString() => Label;
    }

    public sealed class QuestRowVM : Bindable
    {
        readonly QuestListVM list;
        readonly Editor ed;
        public int Index { get; }
        public string Name { get; }
        public string Part { get; }

        public QuestRowVM(QuestListVM list, Editor ed, int index, string name, int part)
        {
            this.list = list;
            this.ed = ed;
            Index = index;
            Name = name;
            Part = part == 0 ? "Tickington" : part >= 1 && part <= 3 ? $"Part {part}" : "";
        }

        public string Number => (Index + 1).ToString(CultureInfo.InvariantCulture);
        public IReadOnlyList<QuestState> States => QuestListVM.StateChoices;

        public int Value
        {
            get => ed.Save.U8(SaveFile.QuestStates + Index);
            set
            {
                ed.Save.SetU8(SaveFile.QuestStates + Index, value);
                ed.Touch();
                Raise();
                Raise(nameof(State));
                list.Changed();
            }
        }

        public QuestState State
        {
            get => States.FirstOrDefault(s => s.Value == Value) ?? new QuestState { Value = Value, Label = $"Value {Value}" };
            set { if (value != null) Value = value.Value; }
        }

        // SetQuestEnableFlag(quest, on) stores !on: a set bit blocks the quest.
        public bool Enabled
        {
            get => (ed.Save.U8(SaveFile.QuestDisabled + Index / 8) >> (Index % 8) & 1) == 0;
            set
            {
                int o = SaveFile.QuestDisabled + Index / 8, b = 1 << (Index % 8);
                ed.Save.SetU8(o, value ? ed.Save.U8(o) & ~b : ed.Save.U8(o) | b);
                ed.Touch();
                Raise();
            }
        }

        public void Refresh() => RaiseAll();
    }

    public sealed class QuestListVM : Bindable
    {
        public static readonly List<QuestState> StateChoices = new List<QuestState>
        {
            new QuestState { Value = 0xFF, Label = "Not unlocked yet" },
            new QuestState { Value = 0, Label = "Cannot be taken now" },
            new QuestState { Value = 1, Label = "Available" },
            new QuestState { Value = 2, Label = "Underway" },
            new QuestState { Value = 3, Label = "Underway (3)" },
            new QuestState { Value = 4, Label = "Underway (4)" },
            new QuestState { Value = 5, Label = "Underway (5)" },
            new QuestState { Value = 6, Label = "Underway (6)" },
            new QuestState { Value = 7, Label = "Achieved, report back (7)" },
            new QuestState { Value = 8, Label = "Achieved, report back" },
            new QuestState { Value = 9, Label = "Cleared" },
            new QuestState { Value = 10, Label = "Hidden from the catalogue" },
        };

        readonly Editor ed;
        public ObservableCollection<QuestRowVM> Rows { get; } = new ObservableCollection<QuestRowVM>();
        public ICollectionView View { get; }
        string search = "";
        bool onlyAvailable;

        public QuestListVM(Editor ed)
        {
            this.ed = ed;
            for (int i = 0; i < SaveFile.QuestCount; i++)
            {
                string name = GameData.Named(ed.Game.Quests, i, "Quest");
                int part = i < ed.Game.QuestParts.Count ? ed.Game.QuestParts[i] : -1;
                Rows.Add(new QuestRowVM(this, ed, i, name, part));
            }
            View = CollectionViewSource.GetDefaultView(Rows);
            View.Filter = o => o is QuestRowVM r && (!onlyAvailable || r.Value != 0xFF) &&
                (search == "" || r.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public string Search
        {
            get => search;
            set { search = (value ?? "").Trim(); View.Refresh(); Raise(); }
        }

        public bool OnlyAvailable
        {
            get => onlyAvailable;
            set { onlyAvailable = value; View.Refresh(); Raise(); }
        }

        public string Summary
        {
            get
            {
                int done = Rows.Count(r => r.Value == 9);
                int open = Rows.Count(r => r.Value >= 2 && r.Value <= 8);
                int listed = Rows.Count(r => r.Value != 0xFF && r.Value != 10);
                return $"{done} of {listed} cleared, {open} underway (as the Quest Catalogue counts)";
            }
        }

        public void Changed() => Raise(nameof(Summary));

        public void SetAll(IEnumerable<QuestRowVM> rows, int value)
        {
            foreach (var r in rows.ToList()) r.Value = value;
        }
    }

    // ---------------------------------------------------------------- numbered values

    // A row of a byte or u32 table (global flags, record counters, tutorials).
    public sealed class ValueRowVM : Bindable
    {
        readonly ValueListVM list;
        public int Id { get; }
        public string Name { get; }
        public string Detail { get; }

        public ValueRowVM(ValueListVM list, int id, string name, string detail)
        {
            this.list = list;
            Id = id;
            Name = name;
            Detail = detail;
        }

        public string Offset => $"0x{list.OffsetOf(Id):X4}";

        public long Value
        {
            get => list.Get(Id);
            set
            {
                list.Put(Id, value);
                Raise();
            }
        }

        public void Refresh() => Raise(nameof(Value));
    }

    public sealed class ValueListVM : Bindable
    {
        readonly Editor ed;
        readonly int offset, size;
        public string Title { get; }
        public string Note { get; }
        public string ValueHeader { get; }
        public ObservableCollection<ValueRowVM> Rows { get; } = new ObservableCollection<ValueRowVM>();
        public ICollectionView View { get; }
        string search = "";
        bool onlySet;

        public ValueListVM(Editor ed, string title, string note, string valueHeader, int offset, int size,
            IEnumerable<(int id, string name, string detail)> rows)
        {
            this.ed = ed;
            this.offset = offset;
            this.size = size;
            Title = title;
            Note = note;
            ValueHeader = valueHeader;
            foreach (var (id, name, detail) in rows) Rows.Add(new ValueRowVM(this, id, name, detail));
            View = CollectionViewSource.GetDefaultView(Rows);
            View.Filter = o => o is ValueRowVM r && (!onlySet || r.Value != 0) &&
                (search == "" || r.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 (r.Detail ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 r.Id.ToString(CultureInfo.InvariantCulture) == search);
        }

        public string Search
        {
            get => search;
            set { search = (value ?? "").Trim(); View.Refresh(); Raise(); }
        }

        public bool OnlySet
        {
            get => onlySet;
            set { onlySet = value; View.Refresh(); Raise(); }
        }

        public int OffsetOf(int id) => offset + size * id;

        public long Get(int id) => size == 1 ? ed.Save.U8(OffsetOf(id)) : size == 2 ? ed.Save.U16(OffsetOf(id)) : ed.Save.U32(OffsetOf(id));

        public long Max => size == 1 ? 255 : size == 2 ? 65535 : uint.MaxValue;

        public void Put(int id, long v)
        {
            if (v < 0 || v > Max) throw new ArgumentException($"0 to {Max}.");
            if (size == 1) ed.Save.SetU8(OffsetOf(id), (int)v);
            else if (size == 2) ed.Save.SetU16(OffsetOf(id), (int)v);
            else ed.Save.SetU32(OffsetOf(id), (uint)v);
            ed.Touch();
            Raise(nameof(Summary));
        }

        public string Summary => $"{Rows.Count(r => r.Value != 0)} of {Rows.Count} non-zero";

        public void SetAll(long v)
        {
            foreach (var r in Rows) Put(r.Id, v);
            foreach (var r in Rows) r.Refresh();
            View.Refresh();
        }
    }

    // ---------------------------------------------------------------- the Progress page

    public sealed class ProgressVM
    {
        public StoryVM Story { get; set; }
        public QuestListVM Quests { get; set; }
        public ValueListVM GlobalFlags { get; set; }
        public ValueListVM Counters { get; set; }
        public FlagListVM RecordFlags { get; set; }
        public ValueListVM Tutorials { get; set; }
        public FlagListVM Cutscenes { get; set; }
        public FlagListVM Zoom { get; set; }

        // Global flags whose meaning is known from the code that reads or sets them.
        static Dictionary<int, string> GlobalFlagNames(SaveFile save, GameData game)
        {
            var d = new Dictionary<int, string>
            {
                [1] = "Read by PresentSpirits",
                // the Draconian Quest options (bits 0-3 of the mask the game builds from these four)
                [527] = "Draconian Quest: no escape",
                [528] = "Draconian Quest: no shopping",
                [529] = "Draconian Quest: no armour",
                [531] = "Draconian Quest: shy heroes (too shy to talk)",
                [534] = "Nelsen hope flag (GetNelsenHopeFlag)",
            };
            foreach (var (record, flag) in SaveFile.JoinFlags)
            {
                string who = game.ShowName(save.RecordName(record));
                d[flag] = $"Joined the party: {(who == "" ? "record " + record : who)} (record {record})";
            }
            return d;
        }

        public static ProgressVM Build(Editor ed)
        {
            var names = GlobalFlagNames(ed.Save, ed.Game);
            var p = new ProgressVM
            {
                Story = new StoryVM(ed),
                Quests = new QuestListVM(ed),
                GlobalFlags = new ValueListVM(ed, "Global flags",
                    "686 story and event switches (one byte each, mostly 0 or 1), named after the scripts that set them. Change them only if you know what they do.",
                    "Value", SaveFile.GlobalFlags, 1,
                    Enumerable.Range(0, SaveFile.GlobalFlagCount).Select(i =>
                    {
                        var (sn, note) = ed.Game.Note("global", i);
                        string name = names.TryGetValue(i, out var n) ? n : sn != "" ? sn : "not used by any script";
                        return (i, name, note);
                    })),
                Counters = new ValueListVM(ed, "Record counters",
                    "51 counters the game adds to (AddRecordCount): forge uses, items obtained by kind and more. Accolades check them.",
                    "Count", SaveFile.RecordCounters, 4,
                    Enumerable.Range(0, SaveFile.RecordCounterCount).Select(i =>
                        (i, CounterName(i), ed.Game.Note("counter", i).note))),
                RecordFlags = new FlagListVM(ed, "Record flags", "96 one-time record bits (SetRecordFlag).", "Set",
                    SaveFile.RecordFlags,
                    Enumerable.Range(0, SaveFile.RecordFlagCount).Select(i =>
                    {
                        var (n, note) = ed.Game.Note("record", i);
                        return (i, n != "" ? n : "not used by any script", note);
                    })),
                Tutorials = new ValueListVM(ed, "Tutorials",
                    "Tutorial pages (Misc → Tutorials). 0 = not shown yet, 1 = shown, 3 = shown and read in the list.",
                    "State", SaveFile.Tutorials, 1,
                    Enumerable.Range(0, SaveFile.TutorialCount).Select(i =>
                        (i, GameData.Named(ed.Game.Tutorials, i, "Tutorial"), ""))),
                Cutscenes = new FlagListVM(ed, "Cutscenes watched", "One bit per story cutscene (GetCSWatchFlag).", "Watched",
                    SaveFile.Cutscenes,
                    Enumerable.Range(0, SaveFile.CutsceneBytes * 8).Select(i =>
                    {
                        var (n, note) = ed.Game.Note("cutscene", i);
                        return (i, n != "" ? n : "no script checks it", note);
                    })),
                Zoom = new FlagListVM(ed, "Zoom destinations",
                    "Places Zoom (and the Chimaera Wing) can take you. Ticking a place the story has not reached can warp you past events.",
                    "Known", SaveFile.ZoomMask,
                    Enumerable.Range(0, SaveFile.ZoomCount).Select(i =>
                        (i, GameData.Named(ed.Game.Zoom, i, "Destination") + (i == 23 ? " (2)" : ""), ""))),
            };
            return p;
        }

        // Forge recipe category (switch in the forge code) -> counter; the categories share counters.
        static readonly int[] ForgeCaseCounter =
            { 24, 24, 31, 44, 29, 38, 25, 27, 43, 30, 26, 32, 34, 36, 40, 39, 39, 48, 48, 33, 28, 35, 37, 37, 41, 41, 45, 46, 42, 42 };

        // What is known about each counter: emulator checks, the scripts and the code that adds to it.
        static string CounterName(int i)
        {
            switch (i)
            {
                case 4: return "Times camped (camp script)";
                case 5: return "Nights at an inn (inn script)";
                case 9: return "Herbal items used (emulator: +1 per Medicinal herb)";
                case 10: return "Gold in the bank (bank code)";
                case 20: return "Items crafted at the Fun-Size Forge (emulator: Info overview)";
                case 21: return "Forge: crafts with the bonus flag set";
                case 22: return "Items reworked at the Fun-Size Forge";
                case 23: return "+3 items crafted (Big Hitter accolade)";
            }
            var cases = Enumerable.Range(0, ForgeCaseCounter.Length).Where(k => ForgeCaseCounter[k] == i).ToList();
            if (cases.Count > 0)
                return $"Forge: crafts of one equipment category (recipe categories {string.Join(", ", cases)})";
            return $"Counter {i} (added to by game code, purpose not identified)";
        }
    }
}
