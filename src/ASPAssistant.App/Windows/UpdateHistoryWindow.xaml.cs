using System.Windows;
using System.Windows.Input;
using ASPAssistant.Core.Data;

namespace ASPAssistant.App.Windows;

public partial class UpdateHistoryWindow : Window
{
    public UpdateHistoryWindow(IReadOnlyList<UpdateHistoryEntry> entries)
    {
        InitializeComponent();
        WindowUiScale.PrepareCentered(this);
        HistoryItems.ItemsSource = entries.Select((entry, index) => new HistoryRow(entry, index == 0)).ToArray();
    }

    private sealed class HistoryRow(UpdateHistoryEntry entry, bool expanded)
    {
        public UpdateHistoryEntry Entry { get; } = entry;
        public string Header => $"{Entry.Version}  ·  {Entry.PublishedDate:yyyy-MM-dd}";
        public bool Expanded { get; set; } = expanded;
    }

    private void OnEntryLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBlock { DataContext: HistoryRow row } text)
            ReleaseNoteLinks.Render(text, row.Entry.Content, ReleaseNoteLinks.Open, message => HintText.Text = message);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
