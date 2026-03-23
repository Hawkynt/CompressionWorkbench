namespace Compression.UI;

public partial class App : System.Windows.Application {
  protected override void OnStartup(System.Windows.StartupEventArgs e) {
    base.OnStartup(e);

    // --analyze [file] : launch directly into analysis window
    if (e.Args.Length > 0 && e.Args[0] is "--analyze" or "/analyze" or "-a") {
      var win = new Views.AnalysisWindow { ShowInTaskbar = true };
      MainWindow = win;
      win.Show();

      if (e.Args.Length > 1 && System.IO.File.Exists(e.Args[1])) {
        var data = System.IO.File.ReadAllBytes(e.Args[1]);
        win.RunAnalysis(e.Args[1], data);
      }
      return;
    }

    // Normal launch: show main archive browser
    var mainWindow = new MainWindow();
    MainWindow = mainWindow;
    mainWindow.Show();

    // Handle file association: if launched with a file argument, open it
    if (e.Args.Length > 0 && System.IO.File.Exists(e.Args[0]))
      mainWindow.OpenArchive(e.Args[0]);
  }
}
