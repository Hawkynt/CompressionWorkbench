using System.Windows;

namespace Compression.UI.Views;

public partial class HeatmapExplorerWindow : Window {
  public HeatmapExplorerWindow() {
    InitializeComponent();
  }

  /// <summary>Opens a specific file directly.</summary>
  public void OpenFile(string path) {
    Title = $"Heatmap Explorer - {System.IO.Path.GetFileName(path)}";
    HeatmapGrid.OpenFile(path);
  }

  private void OnOpenFile(object sender, RoutedEventArgs e) {
    var dlg = new Microsoft.Win32.OpenFileDialog {
      Filter = "All files|*.*|Disk images|*.vmdk;*.vhd;*.qcow2;*.vdi;*.img;*.iso|Archives|*.zip;*.7z;*.rar;*.tar"
    };
    if (dlg.ShowDialog() != true) return;
    OpenFile(dlg.FileName);
  }
}
