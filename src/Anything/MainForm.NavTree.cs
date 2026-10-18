using Anything.Explorer;

namespace Anything;

/// <summary>The navigation pane: Windows' own folder tree (Home, This PC, Network...), kept in sync with the view.</summary>
internal sealed partial class MainForm
{
    readonly NavigationTree _tree = new() { Dock = DockStyle.Fill };

    void BuildNavTree()
    {
        _navPanel.Controls.Add(_tree);
        _tree.FolderSelected += location => OpenFolder(location);
    }

    partial void SyncNavTreeCore(string location)
    {
        if (!string.Equals(_tree.SelectedParsingName, location, StringComparison.OrdinalIgnoreCase))
            _tree.SelectFolder(location);
    }
}
