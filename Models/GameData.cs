using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SaveEditor
{
    // Something a picker dialog can list.
    public interface IPickable
    {
        int Id { get; }
        string Name { get; }
        string Category { get; }
        ImageSource Icon { get; }
    }

    // A skill panel (panel.bxon): an ability, a spell or a stat bonus, with its skill point cost.
    public sealed class PanelInfo : IPickable
    {
        public static readonly string[] KindNames = { "Ability", "Spell", "Bonus" };
        public int Id { get; set; }
        public int Kind { get; set; }
        public string Name { get; set; }
        public int Cost { get; set; }
        public string Category => $"{(Kind < KindNames.Length ? KindNames[Kind] : "?")}, {Cost} SP";
        public ImageSource Icon => null;
        public override string ToString() => Name;
    }

    public sealed class ItemInfo : IPickable
    {
        public int Id { get; set; }
        public string BaseName { get; set; }  // without the forge level
        public int Plus { get; set; }         // forge level: +1..+3 are their own ids right after the base item
        public int MaxPlus { get; set; }      // on a base item: the highest forge level that exists
        public int IconIndex { get; set; } = -1;
        public GameData Game;
        public int Type { get; set; }       // item.bxon +0x0C, see GameData.TypeNames
        public int Slot { get; set; }       // item.bxon +0x29, see GameData.SlotNames; 255 = not equipment
        public int Attack { get; set; }
        public int Defence { get; set; }
        public int Buy { get; set; }
        public int Sell { get; set; }

        public string Name => Plus > 0 && BaseName != "" ? $"{BaseName}+{Plus}" : BaseName;
        public int BaseId => Id - Plus;
        public string Category => GameData.TypeName(Type);
        public string Label => $"{Name}  ({Id})";
        public ImageSource Icon => Game?.Icon(IconIndex);
        public bool IsWeapon => Slot <= 9;
        // Two-handed weapons also fill the shield slot (greatswords, claws, spears, staves).
        public bool TwoHanded => Slot == 5 || Slot == 6 || Slot == 7 || Slot == 9;
        public override string ToString() => Name;
    }

    // Item names and the level tables, from gamedata.json (see dq11tool/savedata.py).
    public sealed class GameData
    {
        public static readonly string[] TypeNames =
        {
            "Items", "Ore", "Wood", "Thread & plants", "Gems", "Hides", "Bones & scales", "Stones",
            "Other materials", "Key items", "Recipe books", "Swords", "Knives", "Axes", "Wands",
            "Boomerangs", "Greatswords", "Claws", "Spears", "Whips", "Staves", "Small shields",
            "Large shields", "Hats", "Helmets", "Clothes", "Armour", "Robes", "Earrings", "Necklaces",
            "Rings", "Gloves", "Bracers", "Footwear", "Other accessories", "Passes", "Costume accessories",
            "Mini Medals", "Pearls", "Casino Tokens",
        };

        public static readonly string[] SlotNames =
        {
            "Sword", "Knife", "Axe", "Wand", "Boomerang", "Greatsword", "Claws", "Spear", "Whip", "Staff",
            "Small shield", "Large shield", "Head", "Body", "Accessory",
        };

        // Level table columns after the EXP threshold.
        public static readonly string[] StatNames =
        {
            "Max HP", "Max MP", "Strength", "Resilience", "Agility", "Deftness", "Charm",
            "Magical might", "Magical mending",
        };

        public readonly List<ItemInfo> Items = new List<ItemInfo>();
        public readonly List<PanelInfo> Panels = new List<PanelInfo>();
        readonly Dictionary<string, int[][]> levels = new Dictionary<string, int[][]>();
        string[] recordParams = new string[0];
        public string SourcePath;

        public bool Loaded => Items.Count > 0;

        public static string TypeName(int t) => t >= 0 && t < TypeNames.Length ? TypeNames[t] : "Unused";

        public static GameData Load()
        {
            var g = new GameData();
            string path = Find();
            if (path == null)
            {
                g.LoadIcons(null, 64, 32);
                return g;
            }
            g.SourcePath = path;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            foreach (var e in root.GetProperty("items").EnumerateArray())
            {
                g.Items.Add(new ItemInfo
                {
                    Game = g,
                    Id = e.GetProperty("id").GetInt32(),
                    BaseName = e.GetProperty("name").GetString(),
                    Plus = e.TryGetProperty("plus", out var pl) ? pl.GetInt32() : 0,
                    IconIndex = e.TryGetProperty("icon", out var ic) && ic.ValueKind == JsonValueKind.Number ? ic.GetInt32() : -1,
                    Type = e.GetProperty("type").GetInt32(),
                    Slot = e.GetProperty("slot").GetInt32(),
                    Attack = e.GetProperty("attack").GetInt32(),
                    Defence = e.GetProperty("defence").GetInt32(),
                    Buy = e.GetProperty("buy").GetInt32(),
                    Sell = e.GetProperty("sell").GetInt32(),
                });
            }
            foreach (var p in root.GetProperty("levels").EnumerateObject())
                g.levels[p.Name] = p.Value.EnumerateArray()
                    .Select(r => r.EnumerateArray().Select(v => v.GetInt32()).ToArray()).ToArray();
            g.recordParams = root.GetProperty("recordParams").EnumerateArray().Select(v => v.GetString()).ToArray();
            if (root.TryGetProperty("panels", out var panels))
                foreach (var e in panels.EnumerateArray())
                    g.Panels.Add(new PanelInfo
                    {
                        Id = e.GetProperty("id").GetInt32(),
                        Kind = e.GetProperty("kind").GetInt32(),
                        Name = e.GetProperty("name").GetString(),
                        Cost = e.GetProperty("cost").GetInt32(),
                    });
            g.LoadExtra(root);
            // forge levels: +N follows its base item directly
            foreach (var it in g.Items)
                if (it.Plus > 0 && it.Id - it.Plus >= 0) g.Items[it.Id - it.Plus].MaxPlus = Math.Max(g.Items[it.Id - it.Plus].MaxPlus, it.Plus);
            g.LoadIcons(Path.Combine(Path.GetDirectoryName(path), "icons.png"),
                root.TryGetProperty("iconSize", out var sz) ? sz.GetInt32() : 64,
                root.TryGetProperty("iconsPerRow", out var pr) ? pr.GetInt32() : 32);
            return g;
        }

        // ---- item icons: one sheet (icons.png next to gamedata.json), cut and cached per tile

        BitmapSource iconSheet;
        int iconSize, iconsPerRow;
        readonly Dictionary<int, ImageSource> iconCache = new Dictionary<int, ImageSource>();

        void LoadIcons(string path, int size, int perRow)
        {
            iconSize = size;
            iconsPerRow = perRow;

            // 1. If an external icons.png is found, use it
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(Path.GetFullPath(path));
                    bmp.EndInit();
                    bmp.Freeze();
                    iconSheet = bmp;
                    return;
                }
                catch (Exception) { }
            }

            // 2. Embedded resource stream
            try
            {
                var asm = typeof(GameData).Assembly;
                var resName = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("icons.png", StringComparison.OrdinalIgnoreCase));
                if (resName != null)
                {
                    using var s = asm.GetManifestResourceStream(resName);
                    if (s != null)
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = s;
                        bmp.EndInit();
                        bmp.Freeze();
                        iconSheet = bmp;
                        return;
                    }
                }
            }
            catch (Exception) { }

            // 3. WPF pack URI fallback
            try
            {
                var uri = new Uri("pack://application:,,,/Assets/icons.png", UriKind.Absolute);
                var info = Application.GetResourceStream(uri);
                if (info != null)
                {
                    using var s = info.Stream;
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = s;
                    bmp.EndInit();
                    bmp.Freeze();
                    iconSheet = bmp;
                    return;
                }
            }
            catch (Exception) { iconSheet = null; }
        }

        public ImageSource Icon(int index)
        {
            if (iconSheet == null || index < 0) return null;
            if (iconCache.TryGetValue(index, out var img)) return img;
            int x = index % iconsPerRow * iconSize, y = index / iconsPerRow * iconSize;
            if (x + iconSize > iconSheet.PixelWidth || y + iconSize > iconSheet.PixelHeight) return null;
            var crop = new CroppedBitmap(iconSheet, new Int32Rect(x, y, iconSize, iconSize));
            crop.Freeze();
            iconCache[index] = crop;
            return crop;
        }

        public ImageSource ItemIcon(int id) => Item(id)?.Icon;

        // The item with the same base at another forge level (or the base itself for 0).
        public ItemInfo Variant(ItemInfo it, int plus)
        {
            if (it == null) return null;
            var b = Item(it.BaseId);
            if (b == null || plus < 0 || plus > b.MaxPlus) return null;
            return Item(b.Id + plus);
        }

        public readonly List<string> Enemies = new List<string>();
        public readonly List<(string name, string text)> Accolades = new List<(string, string)>();
        public readonly List<string> Scenarios = new List<string>();
        public readonly List<string> Quests = new List<string>();
        public readonly List<int> QuestParts = new List<int>();
        public readonly List<string> Zoom = new List<string>();
        public readonly List<string> Tutorials = new List<string>();
        // The name each character record starts with, and the long names a new game stores as one
        // character U+E010 + k (the save keeps 6 characters per name; dq11tool/namefix.py).
        public readonly List<string> PartyNames = new List<string>();
        public readonly List<string> NameCodes = new List<string>();
        public const int NameCodeBase = 0xE010, NameUnits = 6;
        // (name, note) per flag/counter, from the event scripts that use them (dq11tool/scriptflags.py)
        public readonly Dictionary<string, List<(string name, string note)>> Notes =
            new Dictionary<string, List<(string, string)>>();

        void LoadExtra(JsonElement root)
        {
            if (root.TryGetProperty("enemies", out var en))
                foreach (var e in en.EnumerateArray()) Enemies.Add(e.GetString());
            if (root.TryGetProperty("accolades", out var ac))
                foreach (var e in ac.EnumerateArray())
                    Accolades.Add((e.GetProperty("name").GetString(), e.GetProperty("text").GetString()));
            Strings(root, "scenarios", Scenarios);
            Strings(root, "quests", Quests);
            Strings(root, "zoom", Zoom);
            Strings(root, "tutorials", Tutorials);
            Strings(root, "partyNames", PartyNames);
            Strings(root, "nameCodes", NameCodes);
            foreach (var key in new[] { "global", "record", "cutscene", "counter" })
                if (root.TryGetProperty(key + "Notes", out var arr))
                    Notes[key] = arr.EnumerateArray().Select(e => (e[0].GetString(), e[1].GetString())).ToList();
            if (root.TryGetProperty("questParts", out var qp))
                foreach (var e in qp.EnumerateArray()) QuestParts.Add(e.GetInt32());
        }

        static void Strings(JsonElement root, string key, List<string> into)
        {
            if (root.TryGetProperty(key, out var arr))
                foreach (var e in arr.EnumerateArray()) into.Add(e.GetString());
        }

        public (string name, string note) Note(string table, int i) =>
            Notes.TryGetValue(table, out var l) && i >= 0 && i < l.Count ? l[i] : ("", "");

        // Name from a list, or a numbered fallback when the table is missing or the entry is empty.
        public static string Named(List<string> list, int i, string fallback) =>
            i >= 0 && i < list.Count && !string.IsNullOrWhiteSpace(list[i]) ? list[i] : $"{fallback} {i}";

        // A stored name as the game shows it: name codes replaced by the full names.
        public string ShowName(string stored)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in stored)
            {
                int k = c - NameCodeBase;
                sb.Append(k >= 0 && k < NameCodes.Count && NameCodes[k] != "" ? NameCodes[k] : c.ToString());
            }
            return sb.ToString();
        }

        // What to store for a name: a full name longer than the save's 6 characters becomes its code.
        public string StoreName(string name)
        {
            name = name ?? "";
            int k = name.Length > NameUnits ? NameCodes.IndexOf(name) : -1;
            if (k >= 0) return ((char)(NameCodeBase + k)).ToString();
            if (name.Length > NameUnits)
                throw new ArgumentException($"Up to {NameUnits} characters, or one of the full names {string.Join(", ", NameCodes.Where(n => n != ""))}.");
            return name;
        }

        public string DefaultName(int record) => record >= 0 && record < PartyNames.Count ? PartyNames[record] : null;

        public PanelInfo Panel(int id) => id >= 0 && id < Panels.Count ? Panels[id] : null;

        // gamedata.json next to the exe, or in a parent folder (the saveedit/ source folder
        // when the exe runs from bin/).
        static string Find()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
            {
                string p = Path.Combine(dir.FullName, "gamedata.json");
                if (File.Exists(p)) return p;
                p = Path.Combine(dir.FullName, "saveedit", "gamedata.json");
                if (File.Exists(p)) return p;
            }
            return null;
        }

        public ItemInfo Item(int id) => id >= 0 && id < Items.Count ? Items[id] : null;

        public string ItemName(int id)
        {
            if (id == 0xFFFF) return "";
            var it = Item(id);
            return it == null || it.Name == "" ? $"Item {id}" : it.Name;
        }

        // Level table row for a character record: [exp, maxHP, maxMP, str, res, agi, dex, charm,
        // magic might, magic mending, total skill points], or null.
        public int[] LevelRow(int record, int level)
        {
            if (record < 0 || record >= recordParams.Length) return null;
            if (!levels.TryGetValue(recordParams[record], out var rows)) return null;
            if (level < 1 || level > rows.Length) return null;
            var r = rows[level - 1];
            return r.Skip(1).Any(v => v != 0) || r[0] != 0 ? r : null;
        }

        // Highest level whose EXP threshold is <= exp.
        public int LevelForExp(int record, long exp)
        {
            int lv = 1;
            for (int l = 1; l <= 99; l++)
            {
                var r = LevelRow(record, l);
                if (r == null) break;
                if (r[0] <= exp) lv = l; else break;
            }
            return lv;
        }

        public bool HasLevels(int record) => LevelRow(record, 2) != null;
    }
}
