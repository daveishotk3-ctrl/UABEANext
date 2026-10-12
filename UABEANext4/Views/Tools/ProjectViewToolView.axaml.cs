using Avalonia.Controls;
using Avalonia.Interactivity;

namespace UABEANext4.Views.Tools;
public partial class ProjectViewToolView : UserControl
{
    public ProjectViewToolView()
    {
        InitializeComponent();
    }

    private void FrameAllButton_Click(object? sender, RoutedEventArgs e)
    {
        Viewport.FrameAll();
    }
}
