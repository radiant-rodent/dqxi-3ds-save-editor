using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;

namespace SaveEditor
{
    static class Program
    {
        // "SaveEditor.exe [slot file]" opens the editor; "SaveEditor.exe --dump slot out.txt" writes the
        // decoded values as text (used to check the parser against the emulator saves).
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length == 3 && args[0] == "--dump")
            {
                Dump(args[1], args[2]);
                return;
            }
            if (args.Length == 3 && args[0] == "--test-edit")
            {
                TestEdit(args[1], args[2]);
                return;
            }
            if (args.Length == 3 && args[0] == "--restore-names")
            {
                // the Overview's "Restore full names" without the window
                var ed = new Editor(GameData.Load()) { Save = SaveFile.Load(args[1]) };
                Console.WriteLine($"restored {new GameVM(ed).RestoreNames()} names");
                ed.Save.WriteTo(args[2]);
                return;
            }
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/SaveEditor;component/Themes/Theme.xaml"),
            });
            if (args.Length >= 3 && args[0] == "--snapshot")
            {
                // --snapshot slot outdir [page ...]: renders pages to PNG off screen (for checking the layout)
                var w = new MainWindow(args[1]) { Left = -20000, Top = 0, ShowInTaskbar = false, ShowActivated = false };
                w.Loaded += (s, e) => w.Dispatcher.BeginInvoke(new Action(() =>
                {
                    w.Snapshot(args[2], args.Skip(3).ToArray());
                    w.Close();
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                app.Run(w);
                return;
            }
            app.Run(new MainWindow(args.Length > 0 ? args[0] : null));
        }

        // Makes a fixed set of edits through the same view models the window uses, for checking
        // the result in the emulator (items, equipment, bags, key items, level, skill points, gold).
        static void TestEdit(string path, string outPath)
        {
            var ed = new Editor(GameData.Load()) { Save = SaveFile.Load(path) };
            int Id(string name) => ed.Game.Items.First(i => i.Name == name).Id;

            var hero = new CharacterVM(ed, 0);
            hero.AddItem(Id("Metal slime sword"));
            hero.SetEquip(0, hero.Items.Count - 1);
            hero.SpAvailable = hero.SpTotal;

            var erik = new CharacterVM(ed, 1);
            erik.RemoveItems(new[] { 2 });                 // Birchwood boomerang
            erik.AddItem(Id("Pot lid"));
            erik.SetEquip(1, erik.Items.Count - 1);
            erik.Level = 10;

            var bag = new BagVM(ed, "", "", SaveFile.ItemBag, SaveFile.ItemBagSlots, null);
            bag.Reload();
            bag.Add(Id("Medicinal herb"), 12);
            bag.Entries.First(e => e.Name == "Copper ore").Count = 50;
            var eqBag = new BagVM(ed, "", "", SaveFile.EquipBag, SaveFile.EquipBagSlots, null);
            eqBag.Reload();
            eqBag.Add(Id("Hairband"), 1);
            eqBag.Entries.First(e => e.ItemId == Id("Hairband")).ForgeLevel = 2;          // -> Hairband+2
            hero.Items.First(i => i.ItemId == Id("Metal slime sword")).ForgeLevel = 1;    // -> Metal slime sword+1
            new PartyOrderVM(ed, Enumerable.Range(0, SaveFile.RecordCount).Select(i => new CharacterVM(ed, i)).ToList())
                .Add(new CharacterVM(ed, 2));                                            // Veronica joins (count + flag 326)
            var keys = new OwnedListVM(ed, "", "", SaveFile.KeyItems, SaveFile.KeyItemSlots, 9);
            keys.Reload();
            keys.All.First(k => k.Name == "Magic key").Owned = true;
            new GameVM(ed).Gold = 99999;

            ed.Save.WriteTo(outPath);
        }

        static void Dump(string path, string outPath)
        {
            var s = SaveFile.Load(path);
            var g = GameData.Load();
            var sb = new StringBuilder();
            sb.AppendLine($"file {Path.GetFileName(path)} {s.Bytes.Length} bytes, checksum {(s.CrcOk() ? "ok" : "BAD")}");
            foreach (var f in SaveFile.SlotFields)
                sb.AppendLine($"{f.Label}\t0x{f.Offset:X4}\t{s.Format(f)}");
            for (int i = 0; i < SaveFile.RecordCount; i++)
            {
                int b = SaveFile.Record(i);
                sb.AppendLine($"record {i}\t{g.ShowName(s.RecordName(i))}");
                foreach (var f in SaveFile.RecordFields)
                    sb.AppendLine($"  {f.Label}\t+0x{f.Offset:X2}\t{s.Format(f, b)}");
                var items = s.IdList(b + 0x24, SaveFile.InvSlots);
                sb.AppendLine($"  items\t{string.Join(", ", items.Select(id => g.ItemName(id)))}");
                sb.AppendLine($"  equipped\t{string.Join(", ", Enumerable.Range(0, 6).Select(k => s.U8(b + 0x54 + k)))}");
                sb.AppendLine($"  panels\t{string.Join(", ", s.IdList(b + 0x5A, SaveFile.PanelSlots))}");
            }
            foreach (var (name, off, slots) in new[] { ("item bag", SaveFile.ItemBag, SaveFile.ItemBagSlots), ("equipment bag", SaveFile.EquipBag, SaveFile.EquipBagSlots) })
                sb.AppendLine($"{name}\t{string.Join(", ", s.Bag(off, slots).Select(e => $"{g.ItemName(e.id)} x{e.count}"))}");
            sb.AppendLine($"key items\t{string.Join(", ", s.IdList(SaveFile.KeyItems, SaveFile.KeyItemSlots).Select(g.ItemName))}");
            sb.AppendLine($"recipe books\t{string.Join(", ", s.IdList(SaveFile.Recipes, SaveFile.RecipeSlots).Select(g.ItemName))}");
            File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8);
        }
    }
}
