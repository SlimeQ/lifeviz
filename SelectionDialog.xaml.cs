using System.Collections.Generic;
using System.Windows;

namespace lifeviz;

public partial class SelectionDialog : Window
{
    public SelectionDialog(string title, IReadOnlyList<SelectionItem> items)
    {
        InitializeComponent();
        Title = title;
        ItemsList.ItemsSource = items;
        if (items.Count > 0)
        {
            ItemsList.SelectedIndex = 0;
        }
    }

    public object? SelectedValue { get; private set; }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is SelectionItem item)
        {
            SelectedValue = item.Value;
        }

        DialogResult = true;
    }
}

public sealed class SelectionItem
{
    public SelectionItem(string label, object value)
    {
        Label = label;
        Value = value;
    }

    public string Label { get; }
    public object Value { get; }
}
