using Compression.Lib;
using Compression.Mounting;
using Compression.Mounting.Dokan;
using Compression.Mounting.Fuse;
using Compression.NativeUI;
using Compression.NativeUI.ViewModels;
using Compression.NativeUI.Views;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Backends;
using Hawkynt.NativeForms.Backends.Gtk;
using Hawkynt.NativeForms.Backends.Windows;
using F = Compression.Lib.FormatDetector.Format;

BackendRegistry.Register(new Win32Backend());
BackendRegistry.Register(new GtkBackend());

InstallCrashLogging();

// Documentation capture: build the fixture, show the one window, and let the CI job take the
// picture from outside. Handled before anything else so no other startup work interferes.
if (TryTakeScreenshotArgument(args, out var target, out var fixtureRoot)) {
  Application.Run(ScreenshotMode.CreateWindow(target, fixtureRoot));
  return 0;
}

// Warm the format registry off the UI thread, so the first command that asks what a file is does
// not pay the whole registration inline. Command predicates short-circuit on IsReady until it
// finishes, then a requery lights up whatever became available.
_ = Task.Run(() => {
  try {
    FormatRegistration.EnsureInitialized();
  } catch {
    // Swallowed here; the next real call raises it normally.
  }
}).ContinueWith(_ => CommandManager.InvalidateRequerySuggested());

// The verbs live in ShellVerbs so the Explorer registration and this dispatch cannot drift apart.
switch (args) {
  // Launch straight into analysis, optionally on a file.
  case [ShellVerbs.Analyze or ShellVerbs.AnalyzeSlash or ShellVerbs.AnalyzeShort, .. var rest]: {
    var window = new AnalysisWindow();
    if (rest is [var path, ..] && File.Exists(path)) window.RunAnalysis(path, File.ReadAllBytes(path));
    Application.Run(window);
    return 0;
  }

  case [ShellVerbs.CreateZip or ShellVerbs.Create7z, var inputPath, ..]:
    return CreateArchive(inputPath, args[0] == ShellVerbs.CreateZip ? ".zip" : ".7z");

  case [ShellVerbs.Extract, var archivePath, ..]:
    return ExtractArchive(archivePath);

  // The "Extract here (CWB)" shell verb. It takes no destination because "here" is the archive's
  // own folder, which is what every other archiver's identically named entry does.
  case [ShellVerbs.ExtractHere, var archivePath, ..]:
    return ExtractArchiveHere(archivePath);
}

var backends = new List<IFilesystemMountBackend>();
if (OperatingSystem.IsWindows()) {
  var dokan = new DokanFilesystemMountBackend();
  if (dokan.RuntimeStatus.IsAvailable) backends.Add(dokan);
} else if (OperatingSystem.IsLinux()) {
  var fuse = new FuseFilesystemMountBackend();
  if (fuse.RuntimeStatus.IsAvailable) backends.Add(fuse);
}

var launcher = new RegistryMountLauncher(new FilesystemMountLauncher(new MountBackendRegistry(backends)));
var shell = new MainForm(backends, launcher);

// A file argument is a file association opening that file; otherwise restore the browser where it
// was left, walking up to the deepest surviving ancestor if that folder is gone.
if (args is [var startupFile, ..] && File.Exists(startupFile)) shell.OpenArchive(startupFile);
else shell.StartInOsBrowserAtLastFolder();

Application.Run(shell);
return 0;

static bool TryTakeScreenshotArgument(string[] args, out string target, out string fixtureRoot) {
  const string Prefix = ShellVerbs.ScreenshotPrefix;
  target = "";
  fixtureRoot = Path.Combine(Path.GetTempPath(), "CompressionWorkbench-Screenshots");

  var argument = args.FirstOrDefault(a => a.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase));
  if (argument is null) return false;

  target = argument[Prefix.Length..];
  if (!ScreenshotMode.Targets.Contains(target))
    throw new ArgumentException($"--screenshot= expects one of: {string.Join(", ", ScreenshotMode.Targets)}");

  return true;
}

static int CreateArchive(string inputPath, string extension) {
  FormatRegistration.EnsureInitialized();

  var baseName = Path.GetFileName(inputPath);
  var directory = Path.GetDirectoryName(inputPath) ?? ".";
  var archivePath = Path.Combine(directory, baseName + extension);

  var format = FormatDetector.DetectByExtension(archivePath);
  if (format == F.Unknown) {
    MessageBox.Show($"Unknown archive format for {extension}", "Create Archive", MessageBoxButtons.OK, MessageBoxIcon.Error);
    return 1;
  }

  var options = new CreateOptionsWindow(format) {
    Text = $"Create {extension.TrimStart('.')} archive — {baseName}{extension}",
  };
  if (options.ShowDialog(null!) != DialogResult.OK) return 0;

  try {
    ArchiveOperations.Create(archivePath, ArchiveInput.Resolve([inputPath]), options.Options.ToOptions());
    MessageBox.Show($"Created {Path.GetFileName(archivePath)} successfully.",
      "Create Archive", MessageBoxButtons.OK, MessageBoxIcon.Information);
    return 0;
  } catch (Exception ex) {
    MessageBox.Show($"Error creating archive: {ex.Message}", "Create Archive", MessageBoxButtons.OK, MessageBoxIcon.Error);
    return 1;
  }
}

static int ExtractArchive(string archivePath) {
  FormatRegistration.EnsureInitialized();
  if (!RequireArchive(archivePath)) return 1;

  var dialog = new FolderBrowserDialog { Title = $"Extract {Path.GetFileName(archivePath)} to:" };
  if (dialog.ShowDialog() != DialogResult.OK) return 0;

  return ExtractTo(archivePath, dialog.SelectedPath);
}

static int ExtractArchiveHere(string archivePath) {
  FormatRegistration.EnsureInitialized();
  if (!RequireArchive(archivePath)) return 1;

  // Explorer passes an absolute path, but a relative one from a console still has to land beside
  // the archive rather than in whatever the working directory happens to be.
  var destination = Path.GetDirectoryName(Path.GetFullPath(archivePath));
  if (string.IsNullOrEmpty(destination)) {
    MessageBox.Show($"Cannot determine a folder to extract into for {archivePath}.",
      "Extract Here", MessageBoxButtons.OK, MessageBoxIcon.Error);
    return 1;
  }

  return ExtractTo(archivePath, destination, "Extract Here");
}

static bool RequireArchive(string archivePath) {
  if (File.Exists(archivePath)) return true;

  MessageBox.Show($"File not found: {archivePath}", "Extract", MessageBoxButtons.OK, MessageBoxIcon.Error);
  return false;
}

static int ExtractTo(string archivePath, string destination, string caption = "Extract") {
  try {
    ArchiveOperations.Extract(archivePath, destination, password: null, files: null);
    MessageBox.Show($"Extracted to {destination} successfully.", caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
    return 0;
  } catch (Exception ex) {
    MessageBox.Show($"Error extracting: {ex.Message}", caption, MessageBoxButtons.OK, MessageBoxIcon.Error);
    return 1;
  }
}

/// <summary>
/// Routes every unhandled exception to one crash log, so a "it just crashed" report arrives with a
/// stack trace. The thread pool and the app domain each need their own hook.
/// </summary>
static void InstallCrashLogging() {
  AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("AppDomain", e.ExceptionObject as Exception);
  TaskScheduler.UnobservedTaskException += (_, e) => {
    LogCrash("Task", e.Exception);
    e.SetObserved();
  };

  static void LogCrash(string source, Exception? ex) {
    try {
      var path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CompressionWorkbench", "crash.log");
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      File.AppendAllText(path, $"[{DateTime.Now:O}] {source} unhandled exception:\n{ex}\n\n");

      MessageBox.Show(
        $"Unhandled {source} exception:\n\n{ex?.Message}\n\nFull trace appended to:\n{path}",
        "CompressionWorkbench — crash", MessageBoxButtons.OK, MessageBoxIcon.Error);
    } catch {
      // Last-ditch: never compound the original failure.
    }
  }
}
