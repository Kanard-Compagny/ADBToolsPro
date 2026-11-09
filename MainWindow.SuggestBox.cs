using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace ADBToolsNew
{
    /// <summary>
    /// Champ de saisie avec suggestions filtrées au fil de la frappe (choix d'une application parmi des centaines) :
    /// remplace la liste déroulante modifiable, dont le style du programme n'affichait ni la saisie ni le choix.
    /// </summary>
    public partial class MainWindow
    {
        private sealed class SuggestBox
        {
            public TextBox Box; public Popup Popup; public ListBox List;
            public Func<IEnumerable<string>> Source = () => Enumerable.Empty<string>();
            public Action<string> Picked;
            public bool Updating;
        }

        private static SuggestBox AttachSuggest(TextBox box, Popup popup, ListBox list, Action<string> picked)
        {
            var s = new SuggestBox { Box = box, Popup = popup, List = list, Picked = picked };

            void Refresh(bool open)
            {
                string q = (box.Text ?? "").Trim();
                var items = s.Source().Where(x => q.Length == 0 || x.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).Take(300).ToList();
                // Le nom exact déjà saisi : inutile de le proposer seul.
                if (items.Count == 1 && string.Equals(items[0], q, StringComparison.OrdinalIgnoreCase)) items.Clear();
                list.ItemsSource = items;
                popup.IsOpen = open && items.Count > 0;
            }
            void Pick(string value)
            {
                if (string.IsNullOrEmpty(value)) return;
                s.Updating = true;
                box.Text = value;
                box.CaretIndex = value.Length;
                s.Updating = false;
                popup.IsOpen = false;
                s.Picked?.Invoke(value);
            }

            box.TextChanged += (o, e) => { if (!s.Updating) Refresh(box.IsKeyboardFocusWithin); };
            box.GotKeyboardFocus += (o, e) => Refresh(true);
            box.PreviewMouseLeftButtonUp += (o, e) => { if (!popup.IsOpen) Refresh(true); };
            box.LostKeyboardFocus += (o, e) =>
                box.Dispatcher.BeginInvoke(new Action(() => { if (!box.IsKeyboardFocusWithin && !list.IsKeyboardFocusWithin) popup.IsOpen = false; }), DispatcherPriority.Background);
            box.PreviewKeyDown += (o, e) =>
            {
                if (e.Key == Key.Down && popup.IsOpen && list.Items.Count > 0)
                {
                    list.SelectedIndex = 0;
                    (list.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
                    e.Handled = true;
                }
                else if (e.Key == Key.Enter)
                {
                    Pick(popup.IsOpen && list.Items.Count == 1 ? (string)list.Items[0] : (box.Text ?? "").Trim());
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape) popup.IsOpen = false;
            };
            list.PreviewMouseLeftButtonUp += (o, e) =>
            {
                var item = ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) as ListBoxItem;
                if (item?.Content is string v) Pick(v);
            };
            list.PreviewKeyDown += (o, e) =>
            {
                if (e.Key == Key.Enter && list.SelectedItem is string v) { Pick(v); box.Focus(); e.Handled = true; }
                else if (e.Key == Key.Escape) { popup.IsOpen = false; box.Focus(); e.Handled = true; }
            };
            list.LostKeyboardFocus += (o, e) =>
                list.Dispatcher.BeginInvoke(new Action(() => { if (!box.IsKeyboardFocusWithin && !list.IsKeyboardFocusWithin) popup.IsOpen = false; }), DispatcherPriority.Background);
            return s;
        }
    }
}
