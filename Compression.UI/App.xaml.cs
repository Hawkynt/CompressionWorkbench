namespace Compression.UI;

public partial class App : System.Windows.Application {
  protected override void OnStartup(System.Windows.StartupEventArgs e) {
    base.OnStartup(e);

    // Handle file association: if launched with a file argument, open it
    if (e.Args.Length > 0 && System.IO.File.Exists(e.Args[0])) {
      var mainWindow = new MainWindow();
      mainWindow.Show();
      mainWindow.OpenArchive(e.Args[0]);
    }
  }
}
