using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SaveEditor
{
    // Search box + category filter over items or skill panels.
    public partial class ItemPicker : Window
    {
        readonly IPickable[] entries;
        public IPickable Chosen { get; private set; }
        public int Count { get; private set; } = 1;

        public ItemPicker(Window owner, string heading, IEnumerable<IPickable> list, bool askCount, int current = -1)
        {
            InitializeComponent();
            Owner = owner;
            Heading.Text = heading;
            Title = heading;
            entries = list.Where(i => !string.IsNullOrEmpty(i.Name)).ToArray();
            Category.Items.Add("All categories");
            foreach (string c in entries.Select(i => i.Category).Distinct()) Category.Items.Add(c);
            Category.SelectedIndex = 0;
            CountPanel.Visibility = askCount ? Visibility.Visible : Visibility.Collapsed;
            Filter();
            if (current >= 0)
            {
                // a forged item is listed under its base item
                var curItem = entries.OfType<ItemInfo>().FirstOrDefault()?.Game?.Item(current);
                int listed = curItem?.BaseId ?? current;
                var cur = entries.FirstOrDefault(i => i.Id == listed);
                if (cur != null)
                {
                    List.SelectedItem = cur;
                    List.ScrollIntoView(cur);
                    if (curItem != null && curItem.Plus < ForgeBox.Items.Count) ForgeBox.SelectedIndex = curItem.Plus;
                }
            }
            UpdateForge();
            Loaded += (s, e) => Search.Focus();
        }

        // Base items only: the forge level is picked separately.
        public static IEnumerable<IPickable> Items(GameData g, Func<ItemInfo, bool> accepts) =>
            g.Items.Where(i => i.Plus == 0 && (accepts == null || accepts(i)));

        void OnSelection(object sender, SelectionChangedEventArgs e) => UpdateForge();

        void UpdateForge()
        {
            if (ForgeBox == null) return;
            int keep = Math.Max(0, ForgeBox.SelectedIndex);
            ForgeBox.Items.Clear();
            int max = (List.SelectedItem as ItemInfo)?.MaxPlus ?? 0;
            ForgeBox.Items.Add("—");
            for (int k = 1; k <= max; k++) ForgeBox.Items.Add($"+{k}");
            ForgeBox.SelectedIndex = Math.Min(keep, max);
            ForgePanel.Visibility = max > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void OnFilter(object sender, EventArgs e)
        {
            if (List != null) Filter();
        }

        void Filter()
        {
            string q = Search.Text.Trim();
            string cat = Category.SelectedIndex > 0 ? (string)Category.SelectedItem : null;
            bool isNumber = int.TryParse(q, NumberStyles.Integer, CultureInfo.InvariantCulture, out int num);
            var rows = entries.Where(i =>
                (cat == null || i.Category == cat) &&
                (q == "" || i.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || (isNumber && i.Id == num)))
                .ToList();
            List.ItemsSource = rows;
            if (rows.Count > 0 && List.SelectedItem == null) List.SelectedIndex = 0;
        }

        void OnDoubleClick(object sender, MouseButtonEventArgs e) => OnOk(sender, e);

        void OnListKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                OnOk(sender, e);
                e.Handled = true;
            }
        }

        void OnOk(object sender, RoutedEventArgs e)
        {
            if (List.SelectedItem is not IPickable it) return;
            if (CountPanel.Visibility == Visibility.Visible)
            {
                if (!int.TryParse(CountBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1 || n > 999)
                {
                    MessageBox.Show(this, "Count is 1 to 999.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                Count = n;
            }
            if (it is ItemInfo item && ForgeBox.SelectedIndex > 0)
                it = item.Game?.Variant(item, ForgeBox.SelectedIndex) ?? it;
            Chosen = it;
            DialogResult = true;
        }
    }
}
