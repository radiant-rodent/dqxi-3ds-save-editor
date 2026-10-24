using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Media;

namespace SaveEditor
{
    public abstract class Bindable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Raise([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        protected void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
    }

    // The open save and the game tables; every view model writes through it.
    public sealed class Editor
    {
        public SaveFile Save;
        public readonly GameData Game;
        public bool Dirty;
        public event Action Changed;

        public Editor(GameData game) => Game = game;

        public void Touch()
        {
            Dirty = true;
            Changed?.Invoke();
        }
    }

    // One stat: base from the level table, seed bonus from the record (editable).
    public sealed class StatVM : Bindable
    {
        readonly Editor ed;
        readonly int? baseValue;
        readonly int seedOffset;
        public string Name { get; }

        public StatVM(Editor ed, string name, int? baseValue, int seedOffset)
        {
            this.ed = ed;
            Name = name;
            this.baseValue = baseValue;
            this.seedOffset = seedOffset;
        }

        public string Base => baseValue?.ToString(CultureInfo.InvariantCulture) ?? "–";

        public int Seeds
        {
            get => ed.Save.U16(seedOffset);
            set
            {
                if (value < 0 || value > 999) throw new ArgumentException("Seed bonus is 0 to 999.");
                ed.Save.SetU16(seedOffset, value);
                ed.Touch();
                Raise();
                Raise(nameof(Total));
            }
        }

        public string Total => baseValue == null ? "" : (baseValue.Value + Seeds).ToString(CultureInfo.InvariantCulture);
    }

    public sealed class PanelRowVM
    {
        public int Id { get; }
        public string Name { get; }
        public string Kind { get; }
        public int Cost { get; }

        public PanelRowVM(PanelInfo p, int id)
        {
            Id = id;
            Name = p?.Name ?? $"Panel {id}";
            Kind = p == null ? "" : PanelInfo.KindNames[Math.Min(p.Kind, 2)];
            Cost = p?.Cost ?? 0;
        }
    }

    // Forge level choices for an item: "—" (base) and +1..+N.
    public static class Forge
    {
        static readonly string[] Labels = { "—", "+1", "+2", "+3", "+4", "+5" };

        public static List<string> Choices(GameData g, int id)
        {
            var it = g.Item(id);
            var b = it == null ? null : g.Item(it.BaseId);
            int max = b?.MaxPlus ?? 0;
            return Labels.Take(Math.Min(max, Labels.Length - 1) + 1).ToList();
        }

        public static int Level(GameData g, int id) => g.Item(id)?.Plus ?? 0;

        public static string BaseName(GameData g, int id)
        {
            var it = g.Item(id);
            if (id == 0xFFFF) return "";
            return it == null || it.BaseName == "" ? $"Item {id}" : it.BaseName;
        }
    }

    // ---------------------------------------------------------------- characters

    public sealed class InvItemVM : Bindable
    {
        readonly Editor ed;
        int index;
        string equipped = "";
        public int ItemId { get; }

        public int Index
        {
            get => index;
            set { index = value; Raise(); Raise(nameof(Number)); }
        }

        public string Equipped
        {
            get => equipped;
            set { equipped = value; Raise(); }
        }

        readonly Action<int, int> replace;     // (list index, new item id)

        public InvItemVM(Editor ed, int index, int id, Action<int, int> replace = null)
        {
            this.ed = ed;
            this.index = index;
            ItemId = id;
            this.replace = replace;
        }

        public string Name => Forge.BaseName(ed.Game, ItemId);
        public ImageSource Icon => ed.Game.ItemIcon(ItemId);
        public List<string> ForgeChoices => Forge.Choices(ed.Game, ItemId);
        public bool CanForge => ForgeChoices.Count > 1;

        public int ForgeLevel
        {
            get => Forge.Level(ed.Game, ItemId);
            set
            {
                var to = ed.Game.Variant(ed.Game.Item(ItemId), value);
                if (to != null && to.Id != ItemId) replace?.Invoke(Index, to.Id);
            }
        }
        public string Category => ed.Game.Item(ItemId)?.Category ?? "";
        public string Number => (Index + 1).ToString(CultureInfo.InvariantCulture);
        public string Power
        {
            get
            {
                var it = ed.Game.Item(ItemId);
                if (it == null) return "";
                if (it.Attack > 0) return $"Atk +{it.Attack}";
                if (it.Defence > 0) return $"Def +{it.Defence}";
                return "";
            }
        }
    }

    public sealed class EquipChoice
    {
        public int Index { get; set; }          // index into the character's item list, 255 = nothing
        public string Label { get; set; }
        public System.Windows.Media.ImageSource Icon { get; set; }
        public override string ToString() => Label;
    }

    public sealed class EquipSlotVM : Bindable
    {
        readonly CharacterVM owner;
        public int Slot { get; }
        public string SlotName => SaveFile.EquipSlotNames[Slot];
        public ObservableCollection<EquipChoice> Choices { get; } = new ObservableCollection<EquipChoice>();

        public EquipSlotVM(CharacterVM owner, int slot)
        {
            this.owner = owner;
            Slot = slot;
        }

        public bool Enabled => !(Slot == 1 && owner.WeaponTwoHanded);

        public EquipChoice Selected
        {
            get
            {
                int idx = owner.EquipIndex(Slot);
                return Choices.FirstOrDefault(c => c.Index == idx) ?? Choices.FirstOrDefault();
            }
            set
            {
                if (value == null || value.Index == owner.EquipIndex(Slot)) return;
                owner.SetEquip(Slot, value.Index);
            }
        }

        public void Refresh()
        {
            Raise(nameof(Selected));
            Raise(nameof(Enabled));
        }
    }

    public sealed class CharacterVM : Bindable
    {
        readonly Editor ed;
        public int Index { get; }
        public ObservableCollection<InvItemVM> Items { get; } = new ObservableCollection<InvItemVM>();
        public ObservableCollection<StatVM> Stats { get; } = new ObservableCollection<StatVM>();
        public List<EquipSlotVM> Equip { get; }

        SaveFile S => ed.Save;
        int B => SaveFile.Record(Index);

        public CharacterVM(Editor ed, int index)
        {
            this.ed = ed;
            Index = index;
            Equip = Enumerable.Range(0, SaveFile.EquipSlots).Select(i => new EquipSlotVM(this, i)).ToList();
            Reload();
        }

        public void Reload()
        {
            Items.Clear();
            var ids = S.IdList(B + 0x24, SaveFile.InvSlots);
            for (int i = 0; i < ids.Count; i++) Items.Add(NewItem(i, ids[i]));
            RebuildEquip();
            RebuildStats();
            ReloadPanels();
            RaiseAll();
        }

        // Records 0-7 are the party; 8-30 are second forms and story guests.
        public bool IsParty => Index <= 7;
        public string Group => IsParty ? "Party" : "Other records";

        public string Name
        {
            get => ed.Game.ShowName(S.RecordName(Index));
            set
            {
                S.SetRecordName(Index, ed.Game.StoreName(value));
                Touched();
                Raise(nameof(Title));
            }
        }

        public string Title => Name == "" ? $"Record {Index}" : Name;
        public string Subtitle => $"Lv {Level}  ·  record {Index}";

        public int Level
        {
            get => S.U16(B + 0x10);
            set
            {
                if (value < 1 || value > 99) throw new ArgumentException("Level is 1 to 99.");
                if (value == Level) return;
                S.SetU16(B + 0x10, value);
                // Keep EXP inside the new level, so the game does not level the character up or down.
                var row = ed.Game.LevelRow(Index, value);
                var next = ed.Game.LevelRow(Index, value + 1);
                if (row != null && (Exp < row[0] || (next != null && Exp >= next[0])))
                    S.SetU32(B + 0x14, (uint)row[0]);
                Touched();
                RebuildStats();
                RaiseAll();
            }
        }

        public long Exp
        {
            get => S.U32(B + 0x14);
            set
            {
                if (value < 0 || value > uint.MaxValue) throw new ArgumentException("EXP must be 0 or more.");
                S.SetU32(B + 0x14, (uint)value);
                if (ed.Game.HasLevels(Index))
                    S.SetU16(B + 0x10, ed.Game.LevelForExp(Index, value));
                Touched();
                RebuildStats();
                RaiseAll();
            }
        }

        public string ExpHint
        {
            get
            {
                var next = ed.Game.LevelRow(Index, Level + 1);
                if (next == null) return "";
                return string.Format(CultureInfo.InvariantCulture, "{0:N0} to next level", Math.Max(0, next[0] - Exp));
            }
        }

        int[] Row => ed.Game.LevelRow(Index, Level);

        public int Hp
        {
            get => S.U16(B + 0x20);
            set { CheckU16(value, "HP"); S.SetU16(B + 0x20, value); Touched(); Raise(); }
        }

        public int Mp
        {
            get => S.U16(B + 0x22);
            set { CheckU16(value, "MP"); S.SetU16(B + 0x22, value); Touched(); Raise(); }
        }

        public string MaxHp => Row == null ? "" : $"/ {Row[1]} base";
        public string MaxMp => Row == null ? "" : $"/ {Row[2]} base";

        public void Heal()
        {
            var r = Row;
            if (r == null) return;
            S.SetU16(B + 0x20, Math.Max(Hp, r[1]));
            S.SetU16(B + 0x22, Math.Max(Mp, r[2]));
            Touched();
            Raise(nameof(Hp));
            Raise(nameof(Mp));
        }

        public int Tactics
        {
            get => S.U8(B + 0x1C);
            set { S.SetU8(B + 0x1C, value); Touched(); Raise(); }
        }

        public int SpSpent
        {
            get => S.U16(B + 0x1E);
            set
            {
                CheckU16(value, "Skill points spent");
                S.SetU16(B + 0x1E, value);
                Touched();
                Raise();
                Raise(nameof(SpAvailable));
            }
        }

        public int SpTotal => Row?[10] ?? 0;

        // The Skill Panel shows (level table total − spent).
        public int SpAvailable
        {
            get => Math.Max(0, SpTotal - SpSpent);
            set
            {
                if (value < 0 || value > SpTotal)
                    throw new ArgumentException($"At level {Level} this character has earned {SpTotal} skill points.");
                SpSpent = SpTotal - value;
            }
        }

        public string SpHint => Row == null ? "" : $"of {SpTotal} earned by level {Level}";

        // ---- learned skill panels (+0x5A, u16 panel ids in the order they were learned)

        public ObservableCollection<PanelRowVM> Panels { get; } = new ObservableCollection<PanelRowVM>();

        List<int> PanelIds => S.IdList(B + 0x5A, SaveFile.PanelSlots);

        void ReloadPanels()
        {
            Panels.Clear();
            foreach (int id in PanelIds) Panels.Add(new PanelRowVM(ed.Game.Panel(id), id));
            Raise(nameof(PanelSummary));
        }

        public int PanelCost => Panels.Sum(p => p.Cost);

        public string PanelSummary => $"{Panels.Count} learned, {PanelCost} SP worth";

        public bool CanAddPanel => Panels.Count < SaveFile.PanelSlots;

        public void AddPanel(int id)
        {
            var ids = PanelIds;
            if (ids.Contains(id) || ids.Count >= SaveFile.PanelSlots) return;
            ids.Add(id);
            S.SetIdList(B + 0x5A, SaveFile.PanelSlots, ids);
            Touched();
            ReloadPanels();
        }

        public void RemovePanels(IEnumerable<int> remove)
        {
            var set = new HashSet<int>(remove);
            S.SetIdList(B + 0x5A, SaveFile.PanelSlots, PanelIds.Where(i => !set.Contains(i)).ToList());
            Touched();
            ReloadPanels();
        }

        // Spent points = what the learned panels cost, so the Skill Panel adds up.
        public void SpendForPanels() => SpSpent = PanelCost;

        // ---- stats: level table base + seed bonuses (+0x100.., emulator: the Stats screen's "Other" column)

        static readonly int[] SeedOffsets = { 0x100, 0x102, 0x104, 0x10A, 0x108, 0x106, 0x110, 0x10C, 0x10E };

        void RebuildStats()
        {
            Stats.Clear();
            var r = Row;
            for (int i = 0; i < GameData.StatNames.Length; i++)
                Stats.Add(new StatVM(ed, GameData.StatNames[i], r?[i + 1], B + SeedOffsets[i]));
        }

        // ---- items and equipment

        public bool CanAddItem => Items.Count < SaveFile.InvSlots;
        public string ItemCount => $"{Items.Count} / {SaveFile.InvSlots}";

        public int EquipIndex(int slot) => S.U8(B + 0x54 + slot);

        public bool WeaponTwoHanded
        {
            get
            {
                int w = EquipIndex(0);
                return w < Items.Count && (ed.Game.Item(Items[w].ItemId)?.TwoHanded ?? false);
            }
        }

        public void SetEquip(int slot, int index)
        {
            bool wasTwoHanded = WeaponTwoHanded;
            S.SetU8(B + 0x54 + slot, index);
            if (slot == 0)
            {
                if (WeaponTwoHanded) S.SetU8(B + 0x55, index);          // the game stores it in both slots
                else if (wasTwoHanded) S.SetU8(B + 0x55, 0xFF);
            }
            Touched();
            RefreshEquipMarks();
        }

        static bool Fits(ItemInfo it, int slot)
        {
            if (it == null) return false;
            switch (slot)
            {
                case 0: return it.Slot <= 9;
                case 1: return it.Slot == 10 || it.Slot == 11;
                case 2: return it.Slot == 12;
                case 3: return it.Slot == 13;
                default: return it.Slot == 14;
            }
        }

        void RebuildEquip()
        {
            foreach (var e in Equip)
            {
                e.Choices.Clear();
                e.Choices.Add(new EquipChoice { Index = 0xFF, Label = "(nothing)" });
                for (int i = 0; i < Items.Count; i++)
                {
                    var it = ed.Game.Item(Items[i].ItemId);
                    if (Fits(it, e.Slot) || EquipIndex(e.Slot) == i)
                        e.Choices.Add(new EquipChoice { Index = i, Label = $"{ed.Game.ItemName(Items[i].ItemId)}   (item {i + 1})", Icon = it?.Icon });
                }
                // keep an out-of-range value visible instead of silently showing "nothing"
                int cur = EquipIndex(e.Slot);
                if (cur != 0xFF && e.Choices.All(c => c.Index != cur))
                    e.Choices.Add(new EquipChoice { Index = cur, Label = $"(list index {cur}, no item)" });
            }
            RefreshEquipMarks();
        }

        void RefreshEquipMarks()
        {
            foreach (var it in Items)
            {
                var slots = Enumerable.Range(0, SaveFile.EquipSlots).Where(s => EquipIndex(s) == it.Index).ToList();
                if (slots.Count == 2 && slots[0] == 0 && slots[1] == 1) slots.RemoveAt(1);   // two-handed weapon
                it.Equipped = slots.Count == 0 ? "" : string.Join(", ", slots.Select(s => SaveFile.EquipSlotNames[s]));
            }
            foreach (var e in Equip) e.Refresh();
        }

        InvItemVM NewItem(int index, int id) => new InvItemVM(ed, index, id, (i, to) => ReplaceItem(i, to));

        void WriteItems() => S.SetIdList(B + 0x24, SaveFile.InvSlots, Items.Select(i => i.ItemId).ToList());

        public void AddItem(int id)
        {
            if (!CanAddItem) return;
            Items.Add(NewItem(Items.Count, id));
            WriteItems();
            Touched();
            RebuildEquip();
            Raise(nameof(CanAddItem));
            Raise(nameof(ItemCount));
        }

        public void ReplaceItem(int index, int id)
        {
            var old = ed.Game.Item(Items[index].ItemId);
            var now = ed.Game.Item(id);
            Items[index] = NewItem(index, id);
            WriteItems();
            // an equipped item that no longer fits its slot is taken off
            for (int s = 0; s < SaveFile.EquipSlots; s++)
                if (EquipIndex(s) == index && !Fits(now, s)) S.SetU8(B + 0x54 + s, 0xFF);
            if (EquipIndex(0) == index && !(now?.TwoHanded ?? false) && (old?.TwoHanded ?? false) && EquipIndex(1) == index)
                S.SetU8(B + 0x55, 0xFF);
            Touched();
            RebuildEquip();
        }

        public void RemoveItems(IEnumerable<int> indices)
        {
            foreach (int index in indices.OrderByDescending(i => i))
            {
                Items.RemoveAt(index);
                // equipped indices point into the list: drop the removed one, shift the ones after it
                for (int s = 0; s < SaveFile.EquipSlots; s++)
                {
                    int e = EquipIndex(s);
                    if (e == index) S.SetU8(B + 0x54 + s, 0xFF);
                    else if (e != 0xFF && e > index) S.SetU8(B + 0x54 + s, e - 1);
                }
            }
            for (int i = 0; i < Items.Count; i++) Items[i].Index = i;
            WriteItems();
            Touched();
            RebuildEquip();
            Raise(nameof(CanAddItem));
            Raise(nameof(ItemCount));
        }

        public void MoveItem(int index, int delta)
        {
            int to = index + delta;
            if (to < 0 || to >= Items.Count) return;
            var a = Items[index];
            Items.RemoveAt(index);
            Items.Insert(to, a);
            for (int s = 0; s < SaveFile.EquipSlots; s++)
            {
                int e = EquipIndex(s);
                if (e == index) S.SetU8(B + 0x54 + s, to);
                else if (e == to) S.SetU8(B + 0x54 + s, index);
            }
            for (int i = 0; i < Items.Count; i++) Items[i].Index = i;
            WriteItems();
            Touched();
            RebuildEquip();
        }

        void Touched()
        {
            ed.Touch();
            Raise(nameof(Subtitle));
            Raise(nameof(ExpHint));
            Raise(nameof(MaxHp));
            Raise(nameof(MaxMp));
            Raise(nameof(SpHint));
            Raise(nameof(SpTotal));
            Raise(nameof(SpAvailable));
        }

        static void CheckU16(int v, string what)
        {
            if (v < 0 || v > 0xFFFF) throw new ArgumentException($"{what} is 0 to 65535.");
        }
    }

    // ---------------------------------------------------------------- bags

    public sealed class BagEntryVM : Bindable
    {
        readonly BagVM bag;
        public int ItemId { get; private set; }
        int count;

        public BagEntryVM(BagVM bag, int id, int count)
        {
            this.bag = bag;
            ItemId = id;
            this.count = count;
        }

        public string Name => Forge.BaseName(bag.Game, ItemId);
        public string FullName => bag.Game.ItemName(ItemId);
        public string Category => bag.Game.Item(ItemId)?.Category ?? "";
        public int Id => ItemId;
        public ImageSource Icon => bag.Game.ItemIcon(ItemId);
        public List<string> ForgeChoices => Forge.Choices(bag.Game, ItemId);
        public bool CanForge => ForgeChoices.Count > 1;

        // Another forge level is another item id; a stack of that id already in the bag absorbs this one.
        public int ForgeLevel
        {
            get => Forge.Level(bag.Game, ItemId);
            set
            {
                var to = bag.Game.Variant(bag.Game.Item(ItemId), value);
                if (to == null || to.Id == ItemId) return;
                ItemId = to.Id;
                bag.Merge(this);
                RaiseAll();
            }
        }

        public int Count
        {
            get => count;
            set
            {
                if (value < 1 || value > 999) throw new ArgumentException("Count is 1 to 999 (the game shows up to 99).");
                count = value;
                bag.Write();
                Raise();
            }
        }
    }

    public sealed class BagVM : Bindable
    {
        readonly Editor ed;
        public string Title { get; }
        public string Note { get; }
        public readonly int Offset, Slots;
        public readonly Func<ItemInfo, bool> Accepts;
        public ObservableCollection<BagEntryVM> Entries { get; } = new ObservableCollection<BagEntryVM>();
        public ICollectionView View { get; }
        public List<BagEntryVM> Selected = new List<BagEntryVM>();
        public GameData Game => ed.Game;
        string search = "";

        public BagVM(Editor ed, string title, string note, int offset, int slots, Func<ItemInfo, bool> accepts)
        {
            this.ed = ed;
            Title = title;
            Note = note;
            Offset = offset;
            Slots = slots;
            Accepts = accepts;
            View = CollectionViewSource.GetDefaultView(Entries);
            View.Filter = o => search == "" || o is BagEntryVM e &&
                (e.FullName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 e.Category.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public string Search
        {
            get => search;
            set
            {
                search = (value ?? "").Trim();
                View.Refresh();
                Raise();
            }
        }

        public void Reload()
        {
            Entries.Clear();
            foreach (var (id, count) in ed.Save.Bag(Offset, Slots))
                Entries.Add(new BagEntryVM(this, id, count));
            Raise(nameof(Summary));
        }

        public string Summary => $"{Entries.Count} of {Slots} slots used";

        public void Write()
        {
            ed.Save.SetBag(Offset, Slots, Entries.Select(e => (e.ItemId, e.Count)).ToList());
            ed.Touch();
            Raise(nameof(Summary));
        }

        // Adds to an existing stack of the same item, else appends a slot.
        public BagEntryVM Add(int id, int count)
        {
            var have = Entries.FirstOrDefault(e => e.ItemId == id);
            if (have != null)
            {
                have.Count = Math.Min(999, have.Count + count);
                return have;
            }
            if (Entries.Count >= Slots) return null;
            var e = new BagEntryVM(this, id, count);
            Entries.Add(e);
            Write();
            return e;
        }

        public void Merge(BagEntryVM e)
        {
            var other = Entries.FirstOrDefault(o => o != e && o.ItemId == e.ItemId);
            if (other != null)
            {
                Entries.Remove(e);
                other.Count = Math.Min(999, other.Count + e.Count);
            }
            Write();
        }

        public void Remove(IEnumerable<BagEntryVM> list)
        {
            foreach (var e in list.ToList()) Entries.Remove(e);
            Write();
        }

        // Sort by item id, which is the game's own category order.
        public void Sort()
        {
            var sorted = Entries.OrderBy(e => e.ItemId).ToList();
            Entries.Clear();
            foreach (var e in sorted) Entries.Add(e);
            Write();
        }
    }

    // ---------------------------------------------------------------- key items, recipe books

    public sealed class OwnedItemVM : Bindable
    {
        readonly OwnedListVM list;
        public ItemInfo Item { get; }
        public string Name => Item.Name == "" ? $"Item {Item.Id}" : Item.Name;
        public int Id => Item.Id;
        public ImageSource Icon => Item.Icon;

        public OwnedItemVM(OwnedListVM list, ItemInfo item)
        {
            this.list = list;
            Item = item;
        }

        public bool Owned
        {
            get => list.Ids.Contains(Item.Id);
            set
            {
                if (value == Owned) return;
                list.Toggle(Item.Id, value);
                Raise();
            }
        }

        public string Order
        {
            get
            {
                int i = list.Ids.IndexOf(Item.Id);
                return i < 0 ? "" : (i + 1).ToString(CultureInfo.InvariantCulture);
            }
        }

        public void RefreshOrder() => Raise(nameof(Order));
    }

    // A u16 list of item ids in the order they were obtained (key items, recipe books).
    public sealed class OwnedListVM : Bindable
    {
        readonly Editor ed;
        public string Title { get; }
        public string Note { get; }
        readonly int offset, slots, type;
        public List<int> Ids = new List<int>();
        public ObservableCollection<OwnedItemVM> All { get; } = new ObservableCollection<OwnedItemVM>();
        public ICollectionView View { get; }
        string search = "";

        public OwnedListVM(Editor ed, string title, string note, int offset, int slots, int type)
        {
            this.ed = ed;
            Title = title;
            Note = note;
            this.offset = offset;
            this.slots = slots;
            this.type = type;
            View = CollectionViewSource.GetDefaultView(All);
            View.Filter = o => search == "" || o is OwnedItemVM e && e.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public string Search
        {
            get => search;
            set
            {
                search = (value ?? "").Trim();
                View.Refresh();
                Raise();
            }
        }

        public void Reload()
        {
            Ids = ed.Save.IdList(offset, slots);
            All.Clear();
            var known = new HashSet<int>();
            foreach (var it in ed.Game.Items.Where(i => i.Type == type))
            {
                All.Add(new OwnedItemVM(this, it));
                known.Add(it.Id);
            }
            // ids the table does not list (or no game data at all)
            foreach (int id in Ids.Where(i => !known.Contains(i)))
                All.Add(new OwnedItemVM(this, ed.Game.Item(id) ?? new ItemInfo { Id = id, BaseName = $"Item {id}" }));
            Raise(nameof(Summary));
        }

        public string Summary => $"{Ids.Count} of {All.Count} owned";

        public void Toggle(int id, bool on)
        {
            if (on && !Ids.Contains(id) && Ids.Count < slots) Ids.Add(id);
            if (!on) Ids.Remove(id);
            ed.Save.SetIdList(offset, slots, Ids);
            ed.Touch();
            foreach (var o in All) o.RefreshOrder();
            Raise(nameof(Summary));
        }

        public void SetAll(bool on)
        {
            if (on)
            {
                foreach (var o in All)
                    if (!Ids.Contains(o.Id) && Ids.Count < slots) Ids.Add(o.Id);
            }
            else Ids.Clear();
            ed.Save.SetIdList(offset, slots, Ids);
            ed.Touch();
            Reload();
        }
    }

    // ---------------------------------------------------------------- bit lists (accolades, items obtained)

    public sealed class FlagRowVM : Bindable
    {
        readonly FlagListVM list;
        public int Id { get; }
        public string Name { get; }
        public string Detail { get; }

        public FlagRowVM(FlagListVM list, int id, string name, string detail)
        {
            this.list = list;
            Id = id;
            Name = name;
            Detail = detail;
        }

        public bool Set
        {
            get => list.Get(Id);
            set
            {
                list.Put(Id, value);
                Raise();
            }
        }

        public void Refresh() => Raise(nameof(Set));
    }

    public sealed class FlagListVM : Bindable
    {
        readonly Editor ed;
        readonly int offset;
        public string Title { get; }
        public string Note { get; }
        public string Column { get; }
        public ObservableCollection<FlagRowVM> Rows { get; } = new ObservableCollection<FlagRowVM>();
        public ICollectionView View { get; }
        string search = "";
        bool onlySet;

        public FlagListVM(Editor ed, string title, string note, string column, int offset, IEnumerable<(int id, string name, string detail)> rows)
        {
            this.ed = ed;
            this.offset = offset;
            Title = title;
            Note = note;
            Column = column;
            foreach (var (id, name, detail) in rows) Rows.Add(new FlagRowVM(this, id, name, detail));
            View = CollectionViewSource.GetDefaultView(Rows);
            View.Filter = o => o is FlagRowVM r && (!onlySet || r.Set) &&
                (search == "" || r.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 (r.Detail ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
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

        public bool Get(int id) => (ed.Save.U8(offset + id / 8) >> (id % 8) & 1) != 0;

        public void Put(int id, bool on)
        {
            int o = offset + id / 8, b = 1 << (id % 8);
            ed.Save.SetU8(o, on ? ed.Save.U8(o) | b : ed.Save.U8(o) & ~b);
            ed.Touch();
            Raise(nameof(Summary));
        }

        public string Summary => $"{Rows.Count(r => r.Set)} of {Rows.Count} set";

        public void SetAll(bool on)
        {
            foreach (var r in Rows) Put(r.Id, on);
            foreach (var r in Rows) r.Refresh();
            View.Refresh();
            Raise(nameof(Summary));
        }
    }

    // ---------------------------------------------------------------- Item List (items ever obtained)

    // One row per item; forged +1..+3 versions have their own bits, shown as extra columns.
    public sealed class ObtainedRowVM : Bindable
    {
        readonly ObtainedListVM list;
        public ItemInfo Item { get; }

        public ObtainedRowVM(ObtainedListVM list, ItemInfo item)
        {
            this.list = list;
            Item = item;
        }

        public string Name => Item.BaseName;
        public string Category => Item.Category;
        public int Id => Item.Id;
        public ImageSource Icon => Item.Icon;
        public bool Has1 => Item.MaxPlus >= 1;
        public bool Has2 => Item.MaxPlus >= 2;
        public bool Has3 => Item.MaxPlus >= 3;

        bool Get(int plus) => list.Get(Item.Id + plus);

        void Put(int plus, bool on, [System.Runtime.CompilerServices.CallerMemberName] string name = null)
        {
            list.Put(Item.Id + plus, on);
            Raise(name);
        }

        public bool Set { get => Get(0); set => Put(0, value); }
        public bool Set1 { get => Has1 && Get(1); set { if (Has1) Put(1, value); } }
        public bool Set2 { get => Has2 && Get(2); set { if (Has2) Put(2, value); } }
        public bool Set3 { get => Has3 && Get(3); set { if (Has3) Put(3, value); } }
        public bool Any => Set || Set1 || Set2 || Set3;

        public void Refresh() => RaiseAll();
    }

    public sealed class ObtainedListVM : Bindable
    {
        readonly Editor ed;
        public ObservableCollection<ObtainedRowVM> Rows { get; } = new ObservableCollection<ObtainedRowVM>();
        public ICollectionView View { get; }
        string search = "";
        bool onlySet;

        public ObtainedListVM(Editor ed)
        {
            this.ed = ed;
            foreach (var it in ed.Game.Items.Where(i => i.Plus == 0 && i.BaseName != "" && i.Id < SaveFile.ItemsSeenBits))
                Rows.Add(new ObtainedRowVM(this, it));
            View = CollectionViewSource.GetDefaultView(Rows);
            View.Filter = o => o is ObtainedRowVM r && (!onlySet || r.Any) &&
                (search == "" || r.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 r.Category.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
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

        public bool Get(int id) => (ed.Save.U8(SaveFile.ItemsSeen + id / 8) >> (id % 8) & 1) != 0;

        public void Put(int id, bool on)
        {
            int o = SaveFile.ItemsSeen + id / 8, b = 1 << (id % 8);
            ed.Save.SetU8(o, on ? ed.Save.U8(o) | b : ed.Save.U8(o) & ~b);
            ed.Touch();
            Raise(nameof(Summary));
        }

        public string Summary => $"{Rows.Count(r => r.Set)} of {Rows.Count} items obtained";

        // Base items only, or every forge level too.
        public void SetAll(bool on, bool forged)
        {
            foreach (var r in Rows)
            {
                Put(r.Id, on);
                if (forged)
                    for (int k = 1; k <= r.Item.MaxPlus; k++) Put(r.Id + k, on);
                r.Refresh();
            }
            View.Refresh();
        }
    }

    // ---------------------------------------------------------------- monsters defeated

    public sealed class DefeatRowVM : Bindable
    {
        readonly DefeatListVM list;
        public int Id { get; }
        public string Name { get; }

        public DefeatRowVM(DefeatListVM list, int id, string name)
        {
            this.list = list;
            Id = id;
            Name = name;
        }

        public long Count
        {
            get => list.Get(Id);
            set
            {
                if (value < 0 || value > 9999999) throw new ArgumentException("0 to 9999999.");
                list.Put(Id, (uint)value);
                Raise();
            }
        }
    }

    // One u32 per enemy table entry; the Monster List adds up entries with the same name.
    public sealed class DefeatListVM : Bindable
    {
        readonly Editor ed;
        public ObservableCollection<DefeatRowVM> Rows { get; } = new ObservableCollection<DefeatRowVM>();
        public ICollectionView View { get; }
        string search = "";
        bool onlySet = true;

        public DefeatListVM(Editor ed)
        {
            this.ed = ed;
            for (int i = 0; i < SaveFile.DefeatSlots; i++)
            {
                string name = i < ed.Game.Enemies.Count ? ed.Game.Enemies[i] : $"Enemy {i}";
                if (name == "") name = $"Enemy {i}";
                Rows.Add(new DefeatRowVM(this, i, name));
            }
            View = CollectionViewSource.GetDefaultView(Rows);
            View.Filter = o => o is DefeatRowVM r && (!onlySet || r.Count > 0) &&
                (search == "" || r.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
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

        public long BattlesWon
        {
            get => ed.Save.U32(SaveFile.BattlesWon);
            set
            {
                if (value < 0 || value > uint.MaxValue) throw new ArgumentException("Must be 0 or more.");
                ed.Save.SetU32(SaveFile.BattlesWon, (uint)value);
                ed.Touch();
                Raise();
            }
        }

        public uint Get(int id) => ed.Save.U32(SaveFile.Defeats + 4 * id);

        public void Put(int id, uint v)
        {
            ed.Save.SetU32(SaveFile.Defeats + 4 * id, v);
            ed.Touch();
            Raise(nameof(Summary));
        }

        public string Summary
        {
            get
            {
                var hit = Rows.Where(r => r.Count > 0).ToList();
                return $"{hit.Sum(r => r.Count)} defeated, {hit.Select(r => r.Name).Distinct().Count()} kinds";
            }
        }
    }

    // ---------------------------------------------------------------- party line-up

    // 0x3E04: record numbers in party order, 0xFF ends; 0x6580 holds how many there are, and each
    // companion has a "joined" global flag (PartyAdd sets both). Emulator: swapping 00 01 made Erik lead.
    public sealed class PartyOrderVM : Bindable
    {
        readonly Editor ed;
        readonly List<CharacterVM> chars;
        public ObservableCollection<CharacterVM> Members { get; } = new ObservableCollection<CharacterVM>();

        public PartyOrderVM(Editor ed, List<CharacterVM> chars)
        {
            this.ed = ed;
            this.chars = chars;
            Reload();
        }

        public void Reload()
        {
            Members.Clear();
            for (int i = 0; i < SaveFile.PartySlots; i++)
            {
                int r = ed.Save.U8(SaveFile.PartyOrder + i);
                if (r == 0xFF) break;
                if (r < chars.Count) Members.Add(chars[r]);
            }
            Raise(nameof(Available));
        }

        public List<CharacterVM> Available => chars.Where(c => !Members.Contains(c)).ToList();

        void Write()
        {
            for (int i = 0; i < SaveFile.PartySlots; i++)
                ed.Save.SetU8(SaveFile.PartyOrder + i, i < Members.Count ? Members[i].Index : 0xFF);
            ed.Save.SetU32(SaveFile.PartyCount, (uint)Members.Count);
            ed.Touch();
            Raise(nameof(Available));
        }

        void SetJoined(CharacterVM c, bool on)
        {
            foreach (var (record, flag) in SaveFile.JoinFlags)
                if (record == c.Index) ed.Save.SetU8(SaveFile.GlobalFlags + flag, on ? 1 : 0);
        }

        public void Add(CharacterVM c)
        {
            if (c == null || Members.Contains(c) || Members.Count >= SaveFile.PartySlots) return;
            Members.Add(c);
            SetJoined(c, true);
            Write();
        }

        public void Remove(CharacterVM c)
        {
            if (Members.Count <= 1) return;        // keep at least one member
            Members.Remove(c);
            SetJoined(c, false);
            Write();
        }

        public void Move(CharacterVM c, int delta)
        {
            int i = Members.IndexOf(c), to = i + delta;
            if (i < 0 || to < 0 || to >= Members.Count) return;
            Members.Move(i, to);
            Write();
        }
    }

    // ---------------------------------------------------------------- game-wide values

    public sealed class GameVM : Bindable
    {
        readonly Editor ed;
        SaveFile S => ed.Save;

        public GameVM(Editor ed) => this.ed = ed;

        public PartyOrderVM Lineup { get; set; }

        public void Reload() => RaiseAll();

        void Done([CallerMemberName] string name = null)
        {
            ed.Touch();
            Raise(name);
        }

        public long Gold
        {
            get => S.U32(SaveFile.Gold);
            set { CheckU32(value); S.SetU32(SaveFile.Gold, (uint)value); Done(); }
        }

        public long Gold2
        {
            get => S.U32(SaveFile.Gold2);
            set { CheckU32(value); S.SetU32(SaveFile.Gold2, (uint)value); Done(); }
        }

        public int Hours
        {
            get => (int)(S.U32(SaveFile.PlayTime) / 3600);
            set { SetTime(value, Minutes, Seconds); Done(); }
        }

        public int Minutes
        {
            get => (int)(S.U32(SaveFile.PlayTime) / 60 % 60);
            set { SetTime(Hours, value, Seconds); Done(); }
        }

        public int Seconds
        {
            get => (int)(S.U32(SaveFile.PlayTime) % 60);
            set { SetTime(Hours, Minutes, value); Done(); }
        }

        public string PlayTimeText => FormatTime(S.U32(SaveFile.PlayTime));

        public static string FormatTime(uint secs) => $"{secs / 3600}:{secs / 60 % 60:00}:{secs % 60:00}";

        void SetTime(int h, int m, int s)
        {
            if (h < 0 || m < 0 || m > 59 || s < 0 || s > 59) throw new ArgumentException("Hours 0+, minutes and seconds 0 to 59.");
            S.SetU32(SaveFile.PlayTime, (uint)(h * 3600 + m * 60 + s));
            Raise(nameof(PlayTimeText));
        }

        public int DisplayMode
        {
            get => Math.Min((int)S.U8(SaveFile.DisplayMode), 2);
            set { S.SetU8(SaveFile.DisplayMode, value); Done(); }
        }

        // System settings (Misc → System Settings), checked in the emulator.
        public int BattleSpeed { get => Clamp(0, 2); set { S.SetU8(SaveFile.Settings, value); Done(); } }
        public int Bgm { get => Clamp(1, 4); set { S.SetU8(SaveFile.Settings + 1, value); Done(); } }
        public int Sfx { get => Clamp(2, 4); set { S.SetU8(SaveFile.Settings + 2, value); Done(); } }
        public int TiltCamera { get => Clamp(3, 1); set { S.SetU8(SaveFile.Settings + 3, value); Done(); } }

        int Clamp(int i, int max) => Math.Min((int)S.U8(SaveFile.Settings + i), max);

        public string HeroName
        {
            get => ed.Game.ShowName(S.RecordName(0));
            set { S.SetRecordName(0, ed.Game.StoreName(value)); Done(); }
        }

        // Records 1-30 cannot be renamed in the game, so a stored name other than the record's
        // name in this translation is one an older patch cut ("Veroni", "Sylv.", "King Ir"; "Hendrik"
        // without its NUL) or the Japanese game stored.
        public List<int> OutdatedNames() => Enumerable.Range(1, SaveFile.RecordCount - 1)
            .Where(r => S.RecordName(r) != "" && ed.Game.DefaultName(r) is string d && d != ""
                        && (ed.Game.ShowName(S.RecordName(r)) != d || S.RecordName(r).Length > GameData.NameUnits))
            .ToList();

        public string NamesNote
        {
            get
            {
                if (ed.Game.PartyNames.Count == 0) return "Needs gamedata.json.";
                var old = OutdatedNames();
                if (old.Count == 0) return "Every character has its full name.";
                return $"{old.Count} names were cut or come from an older version: " +
                       string.Join(", ", old.Select(r => $"{S.RecordName(r)} → {ed.Game.DefaultName(r)}").Distinct());
            }
        }

        public bool CanRestoreNames => ed.Game.PartyNames.Count > 0 && OutdatedNames().Count > 0;

        public int RestoreNames()
        {
            var old = OutdatedNames();
            foreach (int r in old) S.SetRecordName(r, ed.Game.StoreName(ed.Game.DefaultName(r)));
            if (old.Count > 0) ed.Touch();
            Raise(nameof(NamesNote));
            Raise(nameof(CanRestoreNames));
            return old.Count;
        }

        public string ProfileName
        {
            get => S.Text(SaveFile.ProfileName, 7);
            set { S.SetText(SaveFile.ProfileName, 7, value ?? ""); Done(); }
        }

        public string ProfileMessage
        {
            get => S.Text(SaveFile.ProfileMessage, 24);
            set { S.SetText(SaveFile.ProfileMessage, 24, value ?? ""); Done(); }
        }

        public string SaveLocation
        {
            get => S.Text(SaveFile.SaveLocationText, 32);
            set { S.SetText(SaveFile.SaveLocationText, 32, value ?? ""); Done(); }
        }

        public long MiniMedals
        {
            get => S.U32(SaveFile.MiniMedals);
            set { CheckU32(value); S.SetU32(SaveFile.MiniMedals, (uint)value); Done(); }
        }

        // AddCasinoCoinNum caps the tokens at 9,999,999.
        public long CasinoTokens
        {
            get => S.U32(SaveFile.CasinoTokens);
            set
            {
                if (value < 0 || value > 9999999) throw new ArgumentException("0 to 9999999.");
                S.SetU32(SaveFile.CasinoTokens, (uint)value);
                Done();
            }
        }

        public int EvacPoint
        {
            get => S.U8(SaveFile.EvacPoint);
            set
            {
                if (value < 0 || value > 255) throw new ArgumentException("0 to 255.");
                S.SetU8(SaveFile.EvacPoint, value);
                Done();
            }
        }

        public string PosX { get => F(0x658C); set { SetF(0x658C, value); Done(); } }
        public string PosY { get => F(0x6590); set { SetF(0x6590, value); Done(); } }
        public string PosZ { get => F(0x6594); set { SetF(0x6594, value); Done(); } }
        public long Area { get => S.U32(0x65A8); set { CheckU32(value); S.SetU32(0x65A8, (uint)value); Done(); } }
        public long Room { get => S.U32(0x65AC); set { CheckU32(value); S.SetU32(0x65AC, (uint)value); Done(); } }

        string F(int o) => S.F32(o).ToString("0.###", CultureInfo.InvariantCulture);

        void SetF(int o, string v)
        {
            if (!float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                throw new ArgumentException("Not a number (use . for decimals).");
            S.SetF32(o, f);
        }

        static void CheckU32(long v)
        {
            if (v < 0 || v > uint.MaxValue) throw new ArgumentException("Must be 0 or more.");
        }
    }
}
