// Modified for ASP Assistant Regulation on 2026-11-09; see NOTICE.
using System.Windows;
using System.Windows.Controls;
using ASPAssistant.Core.Models;

namespace ASPAssistant.App.Views;

public partial class EnemyView : UserControl
{
    private readonly HashSet<string> _collapsedTypes = [];

    private void OnTypeLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Expander { DataContext: EnemyTypeEntry type } section)
            section.IsExpanded = !_collapsedTypes.Contains(type.TypeName);
    }

    private void OnTypeExpansionChanged(object sender, RoutedEventArgs e)
    {
        if (sender != e.OriginalSource || sender is not Expander { IsLoaded: true, DataContext: EnemyTypeEntry type } section) return;
        if (section.IsExpanded) _collapsedTypes.Remove(type.TypeName);
        else _collapsedTypes.Add(type.TypeName);
    }

    public EnemyView() => InitializeComponent();
}
