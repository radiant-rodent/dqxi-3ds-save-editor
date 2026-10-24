using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;

namespace SaveEditor
{
    public sealed class RootVM
    {
        public GameVM Game { get; set; }
        public BagVM ItemBag { get; set; }
        public BagVM EquipBag { get; set; }
        public OwnedListVM KeyItems { get; set; }
        public OwnedListVM Recipes { get; set; }
        public List<CharacterVM> Characters { get; set; }
        public DefeatListVM Monsters { get; set; }
        public FlagListVM AccoladeList { get; set; }
        public ObtainedListVM ItemList { get; set; }
        public ProgressVM Progress { get; set; }
    }

    public sealed class HexRow
    {
        public int Offset { get; set; }
        public string OffsetText => Offset.ToString("X5");
        public string Hex { get; set; }
        public string Label { get; set; }
    }

    public sealed class Hit
    {
        public int Offset { get; set; }
        public string OffsetText => Offset.ToString("X5");
        public string Kind { get; set; }
        public string Label { get; set; }
        public string Before { get; set; }
        public string After { get; set; }
    }

    public partial class MainWindow : Window
    {
        public static readonly RoutedUICommand OpenCmd = new RoutedUICommand();
        public static readonly RoutedUICommand SaveCmd = new RoutedUICommand();
        public static readonly RoutedUICommand SaveAsCmd = new RoutedUICommand();

        readonly Editor ed;
        RootVM vm;
        ICollectionView charView;
        string lastStatus = "";

        static readonly string[] KindNames = { "u8", "u16", "u32", "f32" };

        public MainWindow(string path)
        {
            InitializeComponent();
            ed = new Editor(GameData.Load());
            ed.Changed += UpdateTitle;
            foreach (var t in SaveFile.TacticsNames) TacticsBox.Items.Add(t);
            foreach (var m in SaveFile.DisplayModeNames) DisplayModeBox.Items.Add(m);
            foreach (var k in KindNames) InspKind.Items.Add(k);
            InspKind.SelectedIndex = 2;
            RegionGrid.ItemsSource = SaveFile.Regions;
            SideData.Text = ed.Game.Loaded
                ? $"Game data: {ed.Game.Items.Count} items"
                : "No gamedata.json next to the exe: names show as numbers (see README).";

            AllowDrop = true;
            Drop += (s, e) =>
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0 && ConfirmDiscard())
                    Open(files[0]);
            };
            Closing += (s, e) => { if (!ConfirmDiscard()) e.Cancel = true; };
            UpdateTitle();
            if (path != null) Loaded += (s, e) => Open(path);
        }

        // ------------------------------------------------------------ file handling

        void Open(string path)
        {
            try
            {
                ed.Save = SaveFile.Load(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Open", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ed.Dirty = false;
            BuildViewModels();
            EmptyState.Visibility = Visibility.Collapsed;
            Pages.Visibility = Visibility.Visible;
            if (Nav.SelectedIndex < 0) Nav.SelectedIndex = 0;
            else ShowPage();
            Status(ed.Save.CrcOk() ? "Opened. Checksum OK." : "Opened. The checksum does not match; saving writes a new one.");
            UpdateTitle();
        }

        void BuildViewModels()
        {
            int selected = (CharList.SelectedItem as CharacterVM)?.Index ?? 0;
            var chars = Enumerable.Range(0, SaveFile.RecordCount).Select(i => new CharacterVM(ed, i)).ToList();
            vm = new RootVM
            {
                Characters = chars,
                Game = new GameVM(ed) { Lineup = new PartyOrderVM(ed, chars) },
                ItemBag = new BagVM(ed, "Item Bag",
                    "Shared bag for items and materials, as in Items → Item Bag. 174 slots.",
                    SaveFile.ItemBag, SaveFile.ItemBagSlots, it => it.Type <= 8 || it.Type >= 35),
                EquipBag = new BagVM(ed, "Equipment Bag",
                    "Shared bag for weapons, armour and accessories, as in Items → Equipment Bag.",
                    SaveFile.EquipBag, SaveFile.EquipBagSlots, it => it.Type >= 11 && it.Type <= 36),
                KeyItems = new OwnedListVM(ed, "Key Items",
                    "Tick an item to give it. The game lists key items in the order they were obtained.",
                    SaveFile.KeyItems, SaveFile.KeyItemSlots, 9),
                Recipes = new OwnedListVM(ed, "Recipe Books",
                    "Recipe books unlock Fun-Size Forge recipes.",
                    SaveFile.Recipes, SaveFile.RecipeSlots, 10),
            };
            vm.Monsters = new DefeatListVM(ed);
            vm.AccoladeList = new FlagListVM(ed, "Accolades",
                "Accolades earned (Info, Accolades Earned). Setting one here does not give the in-game reward.",
                "Earned", SaveFile.Accolades,
                Enumerable.Range(0, SaveFile.AccoladeCount).Select(i => (i,
                    i < ed.Game.Accolades.Count ? ed.Game.Accolades[i].name : $"Accolade {i}",
                    i < ed.Game.Accolades.Count ? ed.Game.Accolades[i].text : "")));
            vm.ItemList = new ObtainedListVM(ed);
            vm.Progress = ProgressVM.Build(ed);
            vm.ItemBag.Reload();
            vm.EquipBag.Reload();
            vm.KeyItems.Reload();
            vm.Recipes.Reload();
            DataContext = vm;
            PageGame.DataContext = vm;
            PartySummary.ItemsSource = chars.Where(c => c.IsParty).ToList();

            charView = new ListCollectionView(chars);
            charView.Filter = o => ShowAllRecords.IsChecked == true || ((CharacterVM)o).IsParty;
            CharList.ItemsSource = charView;
            CharList.SelectedItem = chars[Math.Min(selected, chars.Count - 1)];
            if (CharList.SelectedItem == null) CharList.SelectedIndex = 0;
            hexRowsStale = true;
        }

        bool Save(string path)
        {
            try
            {
                if (File.Exists(path)) Backup(path);
                ed.Save.WriteTo(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Save", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            ed.Dirty = false;
            UpdateTitle();
            Status($"Saved {Path.GetFileName(path)} (checksum updated). The old file is in {BackupDir}.");
            return true;
        }

        static string BackupDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DQXISaveEditor", "backups");

        // Every save keeps the file it replaces, outside the game's save folder.
        static void Backup(string path)
        {
            Directory.CreateDirectory(BackupDir);
            string name = $"{DateTime.Now:yyyyMMdd-HHmmss}_{Path.GetFileName(Path.GetDirectoryName(path))}_{Path.GetFileName(path)}";
            File.Copy(path, Path.Combine(BackupDir, name), true);
        }

        bool ConfirmDiscard()
        {
            if (ed.Save == null || !ed.Dirty) return true;
            var r = MessageBox.Show(this, "Save the changes first?", "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return false;
            if (r == MessageBoxResult.Yes) return Save(ed.Save.Path);
            return true;
        }

        static string DefaultFolder()
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
            foreach (var emu in new[] { "AzaharPlus", "Azahar", "Citra" })
            {
                string sd = Path.Combine(root, emu, "sdmc", "Nintendo 3DS");
                if (!Directory.Exists(sd)) continue;
                try
                {
                    var hit = Directory.GetDirectories(sd, "00000001", SearchOption.AllDirectories)
                        .FirstOrDefault(d => d.Replace('\\', '/').Contains("00199200/data"));
                    if (hit != null) return hit;
                }
                catch (Exception) { }
            }
            return null;
        }

        void OnOpen(object sender, RoutedEventArgs e)
        {
            if (!ConfirmDiscard()) return;
            var dlg = new OpenFileDialog
            {
                Title = "Open a DQXI save slot",
                Filter = "DQXI slot (Data0, Data1, Data2)|Data*|All files|*.*",
            };
            string dir = ed.Save != null ? Path.GetDirectoryName(ed.Save.Path) : DefaultFolder();
            if (dir != null) dlg.InitialDirectory = dir;
            if (dlg.ShowDialog(this) == true) Open(dlg.FileName);
        }

        void OnSave(object sender, RoutedEventArgs e)
        {
            if (ed.Save == null) return;
            CommitEdits();
            Save(ed.Save.Path);
        }

        void OnSaveAs(object sender, RoutedEventArgs e)
        {
            if (ed.Save == null) return;
            CommitEdits();
            var dlg = new SaveFileDialog
            {
                Title = "Save the slot as",
                FileName = Path.GetFileName(ed.Save.Path),
                InitialDirectory = Path.GetDirectoryName(ed.Save.Path),
                Filter = "All files|*.*",
            };
            if (dlg.ShowDialog(this) == true) Save(dlg.FileName);
        }

        void OnRevert(object sender, RoutedEventArgs e)
        {
            if (ed.Save == null) return;
            if (ed.Dirty && MessageBox.Show(this, "Drop all unsaved changes?", "Revert", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;
            ed.Dirty = false;
            Open(ed.Save.Path);
        }

        // A text box that still has focus has not written its value yet.
        void CommitEdits()
        {
            if (Keyboard.FocusedElement is TextBox tb)
                tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }

        void UpdateTitle()
        {
            bool open = ed.Save != null;
            SaveButton.IsEnabled = SaveAsButton.IsEnabled = RevertButton.IsEnabled = open;
            if (!open)
            {
                Title = "DQXI Save Editor";
                return;
            }
            string file = Path.GetFileName(ed.Save.Path);
            string hero = ed.Game.ShowName(ed.Save.RecordName(0));
            Title = $"{(ed.Dirty ? "• " : "")}{hero} – {file} – DQXI Save Editor";
            FileTitle.Text = $"{hero}  ·  {file}{(ed.Dirty ? "  ·  unsaved changes" : "")}";
            FilePath.Text = ed.Save.Path;
            SideFile.Text = $"{hero}\nPlay time {GameVM.FormatTime(ed.Save.U32(SaveFile.PlayTime))}\n{ed.Save.U32(SaveFile.Gold):N0} G";
            StatusRight.Text = ed.Dirty ? "Unsaved changes" : "Saved";
            StatusRight.Foreground = (System.Windows.Media.Brush)FindResource(ed.Dirty ? "Warn" : "Good");
            hexRowsStale = true;
        }

        void Status(string s)
        {
            lastStatus = s;
            StatusText.Text = s;
        }

        // Renders each page (nav tag, optionally "PageParty:3" for a record) to outDir/<tag>.png.
        public void Snapshot(string outDir, string[] pages)
        {
            Directory.CreateDirectory(outDir);
            if (pages.Length == 0) pages = Nav.Items.Cast<ListBoxItem>().Select(i => (string)i.Tag).ToArray();
            foreach (string spec in pages)
            {
                var parts = spec.Split(':');
                Go(parts[0]);
                if (parts.Length > 1 && int.TryParse(parts[1], out int rec))
                {
                    ShowAllRecords.IsChecked = rec > 7;
                    CharList.SelectedItem = vm.Characters[rec];
                }
                UpdateLayout();
                Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var root = (FrameworkElement)Content;
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96,
                    System.Windows.Media.PixelFormats.Pbgra32);
                var bg = new System.Windows.Media.DrawingVisual();
                using (var dc = bg.RenderOpen())
                    dc.DrawRectangle((System.Windows.Media.Brush)FindResource("Page"), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                bmp.Render(bg);
                bmp.Render(root);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using var f = File.Create(Path.Combine(outDir, spec.Replace(':', '_') + ".png"));
                enc.Save(f);
            }
        }

        // ------------------------------------------------------------ navigation

        void OnNav(object sender, SelectionChangedEventArgs e) => ShowPage();

        void ShowPage()
        {
            if (Nav.SelectedItem is not ListBoxItem item) return;
            string tag = (string)item.Tag;
            foreach (UIElement page in Pages.Children)
                page.Visibility = page is FrameworkElement fe && fe.Name == tag ? Visibility.Visible : Visibility.Collapsed;
            if (tag == "PageRaw") RefreshHex();
        }

        void Go(string tag)
        {
            foreach (ListBoxItem it in Nav.Items)
                if ((string)it.Tag == tag) Nav.SelectedItem = it;
        }

        void OnPartyCard(object sender, RoutedEventArgs e)
        {
            Go("PageParty");
            CharList.SelectedItem = ((FrameworkElement)sender).Tag;
        }

        // ------------------------------------------------------------ party

        CharacterVM Current => CharList.SelectedItem as CharacterVM;

        void OnCharSelected(object sender, SelectionChangedEventArgs e) => CharDetail.DataContext = Current;

        void OnShowAll(object sender, RoutedEventArgs e) => charView?.Refresh();

        void OnHeal(object sender, RoutedEventArgs e) => Current?.Heal();

        int[] SelectedInv() => InvGrid.SelectedItems.Cast<InvItemVM>().Select(i => i.Index).OrderBy(i => i).ToArray();

        void OnInvAdd(object sender, RoutedEventArgs e)
        {
            var c = Current;
            if (c == null || !c.CanAddItem) return;
            var pick = new ItemPicker(this, $"Give {c.Title} an item", ItemPicker.Items(ed.Game, it => it.Type != 9 && it.Type != 10), false);
            if (pick.ShowDialog() != true) return;
            c.AddItem(pick.Chosen.Id);
            InvGrid.SelectedIndex = c.Items.Count - 1;
            Status($"{c.Title} got {pick.Chosen.Name}.");
        }

        void OnInvChange(object sender, RoutedEventArgs e)
        {
            var c = Current;
            var sel = SelectedInv();
            if (c == null || sel.Length != 1) return;
            int index = sel[0];
            var pick = new ItemPicker(this, $"Replace item {index + 1} of {c.Title}", ItemPicker.Items(ed.Game, it => it.Type != 9 && it.Type != 10), false, c.Items[index].ItemId);
            if (pick.ShowDialog() != true) return;
            c.ReplaceItem(index, pick.Chosen.Id);
            InvGrid.SelectedIndex = index;
        }

        void OnInvRemove(object sender, RoutedEventArgs e)
        {
            var c = Current;
            var sel = SelectedInv();
            if (c == null || sel.Length == 0) return;
            c.RemoveItems(sel);
        }

        void OnInvUp(object sender, RoutedEventArgs e) => MoveInv(-1);
        void OnInvDown(object sender, RoutedEventArgs e) => MoveInv(1);

        void MoveInv(int delta)
        {
            var c = Current;
            var sel = SelectedInv();
            if (c == null || sel.Length != 1) return;
            c.MoveItem(sel[0], delta);
            InvGrid.SelectedIndex = Math.Max(0, Math.Min(c.Items.Count - 1, sel[0] + delta));
        }

        void OnPanelAdd(object sender, RoutedEventArgs e)
        {
            var c = Current;
            if (c == null || !c.CanAddPanel) return;
            if (ed.Game.Panels.Count == 0)
            {
                Status("No panel table in gamedata.json; regenerate it with dq11tool.savedata.");
                return;
            }
            var pick = new ItemPicker(this, $"Learn a skill panel for {c.Title}", ed.Game.Panels, false);
            if (pick.ShowDialog() != true) return;
            c.AddPanel(pick.Chosen.Id);
            Status($"{c.Title} learned {pick.Chosen.Name}. Spent points are unchanged; use 'Spent = panel costs' to charge for it.");
        }

        void OnPanelRemove(object sender, RoutedEventArgs e)
        {
            var c = Current;
            if (c == null) return;
            var ids = PanelGrid.SelectedItems.Cast<PanelRowVM>().Select(p => p.Id).ToList();
            if (ids.Count > 0) c.RemovePanels(ids);
        }

        void OnPanelSpend(object sender, RoutedEventArgs e) => Current?.SpendForPanels();

        // ------------------------------------------------------------ line-up

        CharacterVM LineupSelected => LineupList.SelectedItem as CharacterVM;

        void OnLineupUp(object sender, RoutedEventArgs e) => MoveLineup(-1);
        void OnLineupDown(object sender, RoutedEventArgs e) => MoveLineup(1);

        void MoveLineup(int delta)
        {
            var c = LineupSelected;
            if (c == null) return;
            vm.Game.Lineup.Move(c, delta);
            LineupList.SelectedItem = c;
        }

        void OnLineupRemove(object sender, RoutedEventArgs e)
        {
            if (LineupSelected != null) vm.Game.Lineup.Remove(LineupSelected);
        }

        void OnLineupAdd(object sender, RoutedEventArgs e)
        {
            if (LineupAdd.SelectedItem is CharacterVM c) vm.Game.Lineup.Add(c);
        }

        // ------------------------------------------------------------ bags and lists

        static T Ctx<T>(object sender) where T : class => (sender as FrameworkElement)?.DataContext as T;

        void OnBagSelection(object sender, SelectionChangedEventArgs e)
        {
            var grid = (DataGrid)sender;
            if (grid.DataContext is BagVM bag) bag.Selected = grid.SelectedItems.Cast<BagEntryVM>().ToList();
        }

        void OnBagAdd(object sender, RoutedEventArgs e)
        {
            var bag = Ctx<BagVM>(sender);
            if (bag == null) return;
            var pick = new ItemPicker(this, $"Add to the {bag.Title}", ItemPicker.Items(ed.Game, bag.Accepts), true);
            if (pick.ShowDialog() != true) return;
            if (bag.Add(pick.Chosen.Id, pick.Count) == null) Status($"The {bag.Title} is full.");
            else Status($"Added {pick.Count} × {pick.Chosen.Name}.");
        }

        void OnBagRemove(object sender, RoutedEventArgs e)
        {
            var bag = Ctx<BagVM>(sender);
            if (bag == null || bag.Selected.Count == 0) return;
            bag.Remove(bag.Selected);
        }

        void OnBagSetCount(object sender, RoutedEventArgs e)
        {
            var bag = Ctx<BagVM>(sender);
            if (bag == null) return;
            var targets = bag.Selected.Count > 0 ? bag.Selected : bag.Entries.ToList();
            string answer = Prompt.Ask(this, "Set count", $"New count for {(bag.Selected.Count > 0 ? $"{targets.Count} selected" : "every")} item(s), 1 to 999:", "99");
            if (answer == null) return;
            if (!int.TryParse(answer, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1 || n > 999)
            {
                Status("Count is 1 to 999.");
                return;
            }
            foreach (var t in targets) t.Count = n;
        }

        void OnBagSort(object sender, RoutedEventArgs e) => Ctx<BagVM>(sender)?.Sort();

        void OnFlagsAll(object sender, RoutedEventArgs e) => Ctx<FlagListVM>(sender)?.SetAll(true);
        void OnFlagsNone(object sender, RoutedEventArgs e) => Ctx<FlagListVM>(sender)?.SetAll(false);

        void OnObtainedAll(object sender, RoutedEventArgs e) => Ctx<ObtainedListVM>(sender)?.SetAll(true, false);
        void OnObtainedAllForged(object sender, RoutedEventArgs e) => Ctx<ObtainedListVM>(sender)?.SetAll(true, true);
        void OnObtainedNone(object sender, RoutedEventArgs e) => Ctx<ObtainedListVM>(sender)?.SetAll(false, true);

        void OnRestoreNames(object sender, RoutedEventArgs e)
        {
            int n = vm.Game.RestoreNames();
            foreach (var c in vm.Characters) c.Reload();
            Status(n == 1 ? "Restored 1 name." : $"Restored {n} names.");
        }

        void OnOwnAll(object sender, RoutedEventArgs e) => Ctx<OwnedListVM>(sender)?.SetAll(true);
        void OnOwnNone(object sender, RoutedEventArgs e) => Ctx<OwnedListVM>(sender)?.SetAll(false);

        // ------------------------------------------------------------ raw data

        bool hexRowsStale = true;
        List<HexRow> hexRows;

        void RefreshHex()
        {
            if (ed.Save == null || !hexRowsStale) return;
            var b = ed.Save.Bytes;
            hexRows = new List<HexRow>(b.Length / 16);
            string prevLabel = null;
            for (int o = 0; o < b.Length; o += 16)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < 16 && o + i < b.Length; i++)
                {
                    sb.Append(b[o + i].ToString("X2"));
                    sb.Append(i == 7 ? "  " : " ");
                }
                string label = RowLabel(o);
                hexRows.Add(new HexRow { Offset = o, Hex = sb.ToString(), Label = label == prevLabel ? "" : label });
                prevLabel = label;
            }
            var keep = HexList.SelectedIndex;
            HexList.ItemsSource = hexRows;
            if (keep >= 0) HexList.SelectedIndex = keep;
            hexRowsStale = false;
        }

        // Row label: the field at the row start, or the region name.
        string RowLabel(int o)
        {
            if (o < SaveFile.RecordCount * SaveFile.RecordSize)
            {
                int rec = o / SaveFile.RecordSize, rel = o % SaveFile.RecordSize;
                string name = ed.Game.ShowName(ed.Save.RecordName(rec));
                if (rel == 0) return $"record {rec}: {name}";
                if (rel < 0x60) return $"record {rec}: {ed.Save.Describe(o).Split(": ").Last()}";
                return rel < 0x5A + 2 * SaveFile.PanelSlots ? $"record {rec}: learned panels" : $"record {rec}: +0x{rel:X3}";
            }
            return ed.Save.Describe(o);
        }

        void OnRegionSelected(object sender, SelectionChangedEventArgs e)
        {
            if (RegionGrid.SelectedItem is Region r) Inspect(r.Start, true);
        }

        void OnHitSelected(object sender, SelectionChangedEventArgs e)
        {
            if ((sender as DataGrid)?.SelectedItem is Hit h) Inspect(h.Offset, true);
        }

        void OnHexSelected(object sender, SelectionChangedEventArgs e)
        {
            if (HexList.SelectedItem is HexRow row && !inspecting) Inspect(row.Offset, false);
        }

        bool inspecting;

        void Inspect(int off, bool scroll)
        {
            if (ed.Save == null) return;
            RefreshHex();
            inspecting = true;
            InspOffset.Text = off.ToString("X");
            if (scroll)
            {
                var row = hexRows[off / 16];
                HexList.SelectedItem = row;
                HexList.ScrollIntoView(hexRows[Math.Min(hexRows.Count - 1, off / 16 + 12)]);
                HexList.ScrollIntoView(row);
            }
            inspecting = false;
            ShowInspected();
        }

        int InspectedOffset()
        {
            return int.TryParse(InspOffset.Text.Trim().Replace("0x", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int o)
                && o >= 0 && o < SaveFile.FileSize ? o : -1;
        }

        void ShowInspected()
        {
            int o = InspectedOffset();
            if (o < 0 || ed.Save == null)
            {
                InspLabel.Text = "";
                return;
            }
            var s = ed.Save;
            int k = InspKind.SelectedIndex;
            int size = k == 0 ? 1 : k == 1 ? 2 : 4;
            if (o + size > s.Bytes.Length) { InspValue.Text = ""; return; }
            InspValue.Text = k == 0 ? s.U8(o).ToString(CultureInfo.InvariantCulture)
                : k == 1 ? s.U16(o).ToString(CultureInfo.InvariantCulture)
                : k == 2 ? s.U32(o).ToString(CultureInfo.InvariantCulture)
                : s.F32(o).ToString("G9", CultureInfo.InvariantCulture);
            var region = SaveFile.RegionAt(o);
            InspLabel.Text = $"{s.Describe(o)}{(region != null && !string.IsNullOrEmpty(region.Note) ? "  —  " + region.Note : "")}";
        }

        void OnInspKey(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            int o = InspectedOffset();
            if (o >= 0) Inspect(o, true);
        }

        void OnInspOffsetChanged(object sender, RoutedEventArgs e) => ShowInspected();
        void OnInspKindChanged(object sender, SelectionChangedEventArgs e) => ShowInspected();

        void OnInspValueKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) OnInspWrite(sender, e);
        }

        void OnInspWrite(object sender, RoutedEventArgs e)
        {
            int o = InspectedOffset();
            if (o < 0 || ed.Save == null) return;
            var s = ed.Save;
            var inv = CultureInfo.InvariantCulture;
            string v = InspValue.Text.Trim();
            try
            {
                switch (InspKind.SelectedIndex)
                {
                    case 0: s.SetU8(o, byte.Parse(v, inv)); break;
                    case 1: s.SetU16(o, ushort.Parse(v, inv)); break;
                    case 2: s.SetU32(o, uint.Parse(v, inv)); break;
                    default: s.SetF32(o, float.Parse(v, inv)); break;
                }
            }
            catch (Exception)
            {
                Status($"'{v}' is not a valid {KindNames[InspKind.SelectedIndex]} value.");
                return;
            }
            ed.Touch();
            ReloadPages();
            Inspect(o, false);
            Status($"Wrote {v} at 0x{o:X} ({s.Describe(o)}).");
        }

        // After a raw edit every page reads the bytes again.
        void ReloadPages()
        {
            int sel = HexList.SelectedIndex;
            BuildViewModels();
            hexRowsStale = true;
            RefreshHex();
            HexList.SelectedIndex = sel;
        }

        void OnFindKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) OnFind(sender, e);
        }

        void OnFind(object sender, RoutedEventArgs e)
        {
            if (ed.Save == null) return;
            if (!long.TryParse(FindBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) || v < 0)
            {
                Status("Type a whole number to find.");
                return;
            }
            var s = ed.Save;
            var hits = new List<Hit>();
            int end = s.Bytes.Length - 8;
            for (int o = 0; o < end && hits.Count < 2000; o++)
            {
                if (v <= uint.MaxValue && o % 2 == 0 && o + 4 <= end && s.U32(o) == v) hits.Add(new Hit { Offset = o, Kind = "u32" });
                else if (v <= ushort.MaxValue && o % 2 == 0 && o + 2 <= end && s.U16(o) == v) hits.Add(new Hit { Offset = o, Kind = "u16" });
                else if (v <= byte.MaxValue && v > 0 && s.U8(o) == v) hits.Add(new Hit { Offset = o, Kind = "u8" });
            }
            foreach (var h in hits) h.Label = s.Describe(h.Offset);
            FindGrid.ItemsSource = hits;
            Status($"{hits.Count} place(s) hold {v}{(hits.Count >= 2000 ? " (first 2000)" : "")}.");
        }

        void OnCompare(object sender, RoutedEventArgs e)
        {
            if (ed.Save == null) return;
            var dlg = new OpenFileDialog { Title = "Compare with another slot file", InitialDirectory = Path.GetDirectoryName(ed.Save.Path) };
            if (dlg.ShowDialog(this) != true) return;
            Compare(dlg.FileName);
        }

        void OnCompareDisk(object sender, RoutedEventArgs e)
        {
            if (ed.Save != null) Compare(ed.Save.Path);
        }

        void Compare(string path)
        {
            SaveFile other;
            try { other = SaveFile.Load(path); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Compare", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var a = other.Bytes;
            var b = ed.Save.Bytes;
            var hits = new List<Hit>();
            int end = b.Length - 8;
            for (int o = 0; o < end; o++)
            {
                if (a[o] == b[o]) continue;
                int start = o;
                while (o + 1 < end && (a[o + 1] != b[o + 1] || (o + 2 < end && a[o + 2] != b[o + 2])) && o - start < 15) o++;
                int len = o - start + 1;
                hits.Add(new Hit
                {
                    Offset = start,
                    Before = BitConverter.ToString(a, start, len).Replace("-", " "),
                    After = BitConverter.ToString(b, start, len).Replace("-", " "),
                    Label = ed.Save.Describe(start),
                });
            }
            DiffGrid.ItemsSource = hits;
            CompareInfo.Text = $"{hits.Count} changed place(s) between {Path.GetFileName(path)} (Other) and the open file (This).";
        }
    }

    // Small text prompt.
    static class Prompt
    {
        public static string Ask(Window owner, string title, string text, string value)
        {
            var box = new TextBox { Text = value, Margin = new Thickness(0, 8, 0, 14) };
            var ok = new Button { Content = "OK", IsDefault = true, Style = (Style)owner.FindResource("Primary"), Margin = new Thickness(0) };
            var cancel = new Button { Content = "Cancel", IsCancel = true };
            var w = new Window
            {
                Title = title,
                Owner = owner,
                Width = 380,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
            };
            ok.Click += (s, e) => w.DialogResult = true;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(box);
            panel.Children.Add(buttons);
            w.Content = panel;
            w.Loaded += (s, e) => { box.Focus(); box.SelectAll(); };
            return w.ShowDialog() == true ? box.Text : null;
        }
    }
}
