using F = Compression.Lib.FormatDetector.Format;

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

    // --create-zip / --create-7z <path> : create archive from file/folder
    if (e.Args.Length >= 2 && e.Args[0] is "--create-zip" or "--create-7z") {
      var inputPath = e.Args[1];
      var ext = e.Args[0] == "--create-zip" ? ".zip" : ".7z";
      HandleCreateArchive(inputPath, ext);
      return;
    }

    // --extract <file> : extract archive with folder picker
    if (e.Args.Length >= 2 && e.Args[0] is "--extract") {
      var archivePath = e.Args[1];
      HandleExtractArchive(archivePath);
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

  private void HandleCreateArchive(string inputPath, string ext) {
    Compression.Lib.FormatRegistration.EnsureInitialized();

    // Determine archive output path: same location, same name + extension
    var baseName = System.IO.Path.GetFileName(inputPath);
    var dir = System.IO.Path.GetDirectoryName(inputPath) ?? ".";
    var archivePath = System.IO.Path.Combine(dir, baseName + ext);

    var format = Compression.Lib.FormatDetector.DetectByExtension(archivePath);
    if (format == F.Unknown) {
      System.Windows.MessageBox.Show($"Unknown archive format for {ext}", "Create Archive",
        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
      Shutdown();
      return;
    }

    // Show options dialog (no owner window since main window isn't shown)
    var optsDlg = new CreateOptionsWindow(format);
    optsDlg.Title = $"Create {ext.TrimStart('.')} archive — {baseName}{ext}";
    if (optsDlg.ShowDialog() != true) {
      Shutdown();
      return;
    }

    var opts = optsDlg.Options.ToOptions();

    // Resolve input files
    var inputs = Compression.Lib.ArchiveInput.Resolve([inputPath]);

    try {
      Compression.Lib.ArchiveOperations.Create(archivePath, inputs, opts);
      System.Windows.MessageBox.Show($"Created {System.IO.Path.GetFileName(archivePath)} successfully.",
        "Create Archive", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }
    catch (System.Exception ex) {
      System.Windows.MessageBox.Show($"Error creating archive: {ex.Message}",
        "Create Archive", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
    }

    Shutdown();
  }

  private void HandleExtractArchive(string archivePath) {
    Compression.Lib.FormatRegistration.EnsureInitialized();

    if (!System.IO.File.Exists(archivePath)) {
      System.Windows.MessageBox.Show($"File not found: {archivePath}", "Extract",
        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
      Shutdown();
      return;
    }

    // Show folder picker
    var dlg = new System.Windows.Forms.FolderBrowserDialog {
      Description = $"Extract {System.IO.Path.GetFileName(archivePath)} to:",
      UseDescriptionForTitle = true,
    };

    if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) {
      Shutdown();
      return;
    }

    try {
      Compression.Lib.ArchiveOperations.Extract(archivePath, dlg.SelectedPath, password: null, files: null);
      System.Windows.MessageBox.Show($"Extracted to {dlg.SelectedPath} successfully.",
        "Extract", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }
    catch (System.Exception ex) {
      System.Windows.MessageBox.Show($"Error extracting: {ex.Message}",
        "Extract", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
    }

    Shutdown();
  }
}
