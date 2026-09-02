using F = Compression.Lib.FormatDetector.Format;

namespace Compression.UI;

public partial class App : System.Windows.Application {
  private const string ScreenshotDemoArgument = "--screenshot-demo";

  protected override void OnStartup(System.Windows.StartupEventArgs e) {
    base.OnStartup(e);

    // Under Wine, WPF popup/menu windows use layered (transparent) HWNDs that
    // Wine cannot composite, producing solid-black rectangles. Forcing software
    // rendering eliminates that code path. Activated only when the Wine launch
    // script sets this variable so normal Windows builds are unaffected.
    if (System.Environment.GetEnvironmentVariable("COMPRESSIONWORKBENCH_WINE") == "1")
      System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

    // Two capture entry points, deliberately. `--screenshot-demo=<file>` renders the main window
    // alone and is what generate.yml calls on every working-branch push; `--screenshots [dir]`
    // renders the whole documented surface set - archive browser, analysis, maintenance. They take
    // different flags and neither replaces the other, so both are checked here. Software rendering
    // keeps the checked-in images independent from the runner's GPU.
    if (TryGetDocumentationScreenshotRequest(e.Args, out var screenshotPath, out var demoArchivePath)) {
      System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
      CaptureDocumentationScreenshot(screenshotPath, demoArchivePath);
      return;
    }

    if (ScreenshotMode.TryRun(this, e.Args))
      return;

    // Warm the format registry on a background thread so the first user-driven
    // CanExecute / right-click that calls FormatDetector.DetectByExtension or
    // FormatRegistry.GetArchiveOps doesn't pay the ~180-descriptor registration
    // tax inline. Fire-and-forget; CanExecute predicates short-circuit on
    // FormatRegistration.IsReady so the UI doesn't block while warming.
    // When warm-up finishes we marshal back to the dispatcher and force
    // CommandManager to re-query so the now-enabled commands light up.
    System.Threading.Tasks.Task.Run(() => {
      try { Compression.Lib.FormatRegistration.EnsureInitialized(); }
      catch { /* swallow — surfaced again on the next real call which throws normally */ }
    }).ContinueWith(_ => {
      // Dispatch back to the UI thread so command-binding re-evaluates.
      // Without this the menu items stay grayed until something else
      // triggers a requery (mouse move, focus change, etc.).
      Dispatcher.BeginInvoke(System.Windows.Input.CommandManager.InvalidateRequerySuggested);
    });

    // Surface unhandled exceptions to a crash log so future "just crashed" reports
    // come with a stack trace. WPF dispatcher + thread-pool + AppDomain all need
    // their own hook; we route all three to the same writer.
    this.DispatcherUnhandledException += (_, ex) => { LogCrash("Dispatcher", ex.Exception); ex.Handled = false; };
    System.AppDomain.CurrentDomain.UnhandledException += (_, ex) => LogCrash("AppDomain", ex.ExceptionObject as System.Exception);
    System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ex) => { LogCrash("Task", ex.Exception); ex.SetObserved(); };

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

    // Handle file association: if launched with a file argument, open it.
    // Otherwise restore the OS-browser at the last-used folder (or deepest
    // surviving ancestor if it has been removed since last session).
    if (e.Args.Length > 0 && System.IO.File.Exists(e.Args[0])) {
      mainWindow.OpenArchive(e.Args[0]);
    } else {
      mainWindow.StartInOsBrowserAtLastFolder();
    }
  }

  private static bool TryGetDocumentationScreenshotRequest(
    string[] args,
    out string outputPath,
    out string? demoArchivePath
  ) {
    var prefix = ScreenshotDemoArgument + "=";
    var screenshotArgument = args.FirstOrDefault(arg =>
      arg.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase)
    );

    if (screenshotArgument is null) {
      outputPath = string.Empty;
      demoArchivePath = null;
      return false;
    }

    outputPath = screenshotArgument[prefix.Length..];
    if (string.IsNullOrWhiteSpace(outputPath))
      throw new System.ArgumentException($"{ScreenshotDemoArgument}= requires an output path.");

    demoArchivePath = args.FirstOrDefault(arg =>
      !arg.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase)
      && System.IO.File.Exists(arg)
    );
    return true;
  }

  private void CaptureDocumentationScreenshot(string outputPath, string? demoArchivePath) {
    try {
      Compression.Lib.FormatRegistration.EnsureInitialized();

      var window = new MainWindow();
      MainWindow = window;
      window.Show();

      if (demoArchivePath is not null)
        window.OpenArchive(demoArchivePath);

      window.Dispatcher.BeginInvoke(new System.Action(() => {
        try {
          SaveWindowAsPng(window, outputPath);
          window.Close();
          Shutdown();
        } catch (System.Exception ex) {
          System.Diagnostics.Trace.TraceError($"Screenshot generation failed: {ex}");
          window.Close();
          Shutdown(1);
        }
      }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    } catch (System.Exception ex) {
      System.Diagnostics.Trace.TraceError($"Screenshot setup failed: {ex}");
      Shutdown(1);
    }
  }

  private static void SaveWindowAsPng(System.Windows.Window window, string outputPath) {
    window.UpdateLayout();

    var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
    var pixelWidth = System.Math.Max(1, (int)System.Math.Ceiling(window.ActualWidth * dpi.DpiScaleX));
    var pixelHeight = System.Math.Max(1, (int)System.Math.Ceiling(window.ActualHeight * dpi.DpiScaleY));
    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
      pixelWidth,
      pixelHeight,
      dpi.PixelsPerInchX,
      dpi.PixelsPerInchY,
      System.Windows.Media.PixelFormats.Pbgra32
    );
    bitmap.Render(window);

    var fullPath = System.IO.Path.GetFullPath(outputPath);
    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
    using var stream = System.IO.File.Create(fullPath);
    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
    encoder.Save(stream);
  }

  private static void LogCrash(string source, System.Exception? ex) {
    try {
      var path = System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "CompressionWorkbench", "crash.log");
      System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
      var msg = $"[{System.DateTime.Now:O}] {source} unhandled exception:\n{ex}\n\n";
      System.IO.File.AppendAllText(path, msg);
      System.Windows.MessageBox.Show(
        $"Unhandled {source} exception:\n\n{ex?.Message}\n\nFull trace appended to:\n{path}",
        "CompressionWorkbench — crash",
        System.Windows.MessageBoxButton.OK,
        System.Windows.MessageBoxImage.Error);
    } catch {
      /* last-ditch: don't compound the original failure */
    }
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