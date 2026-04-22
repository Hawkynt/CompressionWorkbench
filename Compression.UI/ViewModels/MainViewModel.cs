using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Compression.Lib;
using Compression.Registry;

namespace Compression.UI.ViewModels;

internal sealed class MainViewModel : ViewModelBase {
  private string _archivePath = "";
  private string _format = "";
  private string _statusText = "Ready";
  private bool _isBusy;
  private double _progress;
  private string _currentFolder = "";

  public ObservableCollection<ArchiveEntryViewModel> Entries { get; } = [];
  public ObservableCollection<ArchiveEntryViewModel> SelectedEntries { get; } = [];
  public ObservableCollection<BreadcrumbSegment> Breadcrumbs { get; } = [];

  public string ArchivePath { get => _archivePath; set => SetField(ref _archivePath, value); }
  public string Format { get => _format; set => SetField(ref _format, value); }
  public string StatusText { get => _statusText; set => SetField(ref _statusText, value); }
  public bool IsBusy { get => _isBusy; set { SetField(ref _isBusy, value); OnPropertyChanged(nameof(IsNotBusy)); } }
  public bool IsNotBusy => !_isBusy;
  public double Progress { get => _progress; set => SetField(ref _progress, value); }
  public string CurrentFolder { get => _currentFolder; set { SetField(ref _currentFolder, value); RefreshBreadcrumbs(); } }

  public bool HasArchive => !string.IsNullOrEmpty(ArchivePath);
  public string Title => HasArchive
    ? $"CompressionWorkbench \u2014 {Path.GetFileName(ArchivePath)}"
    : "CompressionWorkbench";

  // Commands
  public ICommand OpenCommand { get; }
  public ICommand ExtractAllCommand { get; }
  public ICommand ExtractSelectedCommand { get; }
  public ICommand TestCommand { get; }
  public ICommand CreateCommand { get; }
  public ICommand NavigateUpCommand { get; }
  public ICommand NavigateIntoCommand { get; }
  public ICommand NavigateToBreadcrumbCommand { get; }
  public ICommand ViewAsTextCommand { get; }
  public ICommand ViewAsHexCommand { get; }
  public ICommand AddFilesCommand { get; }
  public ICommand PropertiesCommand { get; }
  public ICommand AnalyzeCommand { get; }
  public ICommand AnalyzeFileCommand { get; }
  public ICommand BenchmarkCommand { get; }
  public ICommand FileAssociationsCommand { get; }

  public MainViewModel() {
    OpenCommand = new RelayCommand(_ => OpenDialog());
    ExtractAllCommand = new AsyncRelayCommand(_ => ExtractAll(), _ => HasArchive);
    ExtractSelectedCommand = new AsyncRelayCommand(_ => ExtractSelected(), _ => HasArchive && SelectedEntries.Count > 0);
    TestCommand = new AsyncRelayCommand(_ => TestArchive(), _ => HasArchive);
    CreateCommand = new RelayCommand(_ => CreateDialog());
    NavigateUpCommand = new RelayCommand(_ => NavigateUp(), _ => !string.IsNullOrEmpty(CurrentFolder));
    NavigateIntoCommand = new RelayCommand(p => NavigateInto(p as ArchiveEntryViewModel));
    NavigateToBreadcrumbCommand = new RelayCommand(p => NavigateToBreadcrumb(p as string));
    ViewAsTextCommand = new RelayCommand(_ => ViewSelectedAs(hex: false), _ => HasArchive && HasSelectedFile);
    ViewAsHexCommand = new RelayCommand(_ => ViewSelectedAs(hex: true), _ => HasArchive && HasSelectedFile);
    AddFilesCommand = new RelayCommand(_ => AddFilesToArchive(), _ => HasArchive && CanAddFiles);
    PropertiesCommand = new RelayCommand(_ => ShowProperties(), _ => HasArchive && SelectedEntries.Count == 1 && !SelectedEntries[0].IsParentEntry);
    AnalyzeCommand = new RelayCommand(_ => ShowAnalysis(), _ => HasArchive && HasSelectedFile);
    AnalyzeFileCommand = new RelayCommand(_ => ShowAnalyzeFile());
    BenchmarkCommand = new RelayCommand(_ => ShowBenchmark());
    FileAssociationsCommand = new RelayCommand(_ => ShowFileAssociations());
  }

  private bool HasSelectedFile => SelectedEntries.Any(e => !e.IsDirectory && !e.IsParentEntry);
  private bool CanAddFiles => !string.IsNullOrEmpty(ArchivePath) && FormatDetector.DetectByExtension(ArchivePath) is var f
    && f != FormatDetector.Format.Unknown && !FormatDetector.IsStreamFormat(f);

  public void Open(string path) {
    try {
      IsBusy = true;
      StatusText = $"Opening {Path.GetFileName(path)}...";

      var format = FormatDetector.Detect(path);
      var entries = ArchiveOperations.List(path, password: null);

      ArchivePath = path;
      Format = format.ToString();
      CurrentFolder = "";

      _allEntries.Clear();
      foreach (var e in entries) {
        _allEntries.Add(new ArchiveEntryViewModel {
          Index = e.Index,
          Name = Path.GetFileName(e.Name.TrimEnd('/')),
          Path = e.Name,
          OriginalSize = e.OriginalSize,
          CompressedSize = e.CompressedSize,
          Method = e.Method,
          IsDirectory = e.IsDirectory,
          IsEncrypted = e.IsEncrypted,
          LastModified = e.LastModified,
        });
      }

      RefreshVisibleEntries();

      var totalOrig = entries.Sum(e => e.OriginalSize);
      var totalComp = entries.Where(e => e.CompressedSize >= 0).Sum(e => e.CompressedSize);
      var ratio = totalOrig > 0 ? $" ({100.0 * totalComp / totalOrig:F1}%)" : "";
      StatusText = $"{entries.Count} entries, {FormatSize(totalOrig)}{ratio} \u2014 {format}";

      OnPropertyChanged(nameof(HasArchive));
      OnPropertyChanged(nameof(Title));
    }
    catch (Exception ex) {
      StatusText = $"Error: {ex.Message}";
    }
    finally {
      IsBusy = false;
    }
  }

  private readonly List<ArchiveEntryViewModel> _allEntries = [];

  private void RefreshVisibleEntries() {
    Entries.Clear();
    var prefix = string.IsNullOrEmpty(CurrentFolder) ? "" : CurrentFolder;

    // Add ".." entry when inside a subfolder
    if (!string.IsNullOrEmpty(prefix)) {
      Entries.Add(new ArchiveEntryViewModel {
        Name = "..",
        Path = "",
        IsDirectory = true,
        IsParentEntry = true,
      });
    }

    // Collect immediate children, deduplicating folders
    // Key: normalized folder name with trailing slash, or file name
    var seen = new HashSet<string>(StringComparer.Ordinal);

    foreach (var e in _allEntries) {
      if (!e.Path.StartsWith(prefix, StringComparison.Ordinal)) continue;
      var remainder = e.Path[prefix.Length..];
      if (string.IsNullOrEmpty(remainder)) continue;

      var slashIdx = remainder.IndexOf('/');

      if (slashIdx < 0 && !e.IsDirectory) {
        // Direct file child
        if (seen.Add(remainder))
          Entries.Add(e);
      }
      else if (slashIdx < 0 && e.IsDirectory) {
        // Directory entry without trailing slash — treat as folder
        var key = remainder + "/";
        if (seen.Add(key))
          Entries.Add(e);
      }
      else if (slashIdx == remainder.Length - 1) {
        // Direct directory child (path ends with /)
        var key = remainder; // already has trailing /
        if (seen.Add(key))
          Entries.Add(e);
      }
      else {
        // Deeper entry — show the immediate subfolder as a virtual directory
        var subDir = remainder[..(slashIdx + 1)]; // e.g. "folder/"
        if (seen.Add(subDir)) {
          // Aggregate sizes for this virtual directory
          var dirPrefix = prefix + subDir;
          long origSum = 0, compSum = 0;
          var hasComp = false;
          foreach (var x in _allEntries) {
            if (!x.Path.StartsWith(dirPrefix, StringComparison.Ordinal)) continue;
            origSum += x.OriginalSize;
            if (x.CompressedSize >= 0) { compSum += x.CompressedSize; hasComp = true; }
          }
          Entries.Add(new ArchiveEntryViewModel {
            Name = subDir.TrimEnd('/'),
            Path = dirPrefix,
            OriginalSize = origSum,
            CompressedSize = hasComp ? compSum : -1,
            IsDirectory = true,
          });
        }
      }
    }
  }

  private void NavigateInto(ArchiveEntryViewModel? entry) {
    if (entry == null) return;
    if (entry.IsParentEntry) { NavigateUp(); return; }
    if (!entry.IsDirectory) return;
    CurrentFolder = entry.Path.EndsWith('/') ? entry.Path : entry.Path + "/";
    RefreshVisibleEntries();
  }

  private void NavigateUp() {
    if (string.IsNullOrEmpty(CurrentFolder)) return;
    var trimmed = CurrentFolder.TrimEnd('/');
    var lastSlash = trimmed.LastIndexOf('/');
    CurrentFolder = lastSlash >= 0 ? trimmed[..(lastSlash + 1)] : "";
    RefreshVisibleEntries();
  }

  private void NavigateToBreadcrumb(string? path) {
    CurrentFolder = path ?? "";
    RefreshVisibleEntries();
  }

  private void RefreshBreadcrumbs() {
    Breadcrumbs.Clear();
    // Root segment
    Breadcrumbs.Add(new BreadcrumbSegment { Label = Path.GetFileName(ArchivePath), FolderPath = "" });

    if (!string.IsNullOrEmpty(CurrentFolder)) {
      var parts = CurrentFolder.TrimEnd('/').Split('/');
      var accumulated = "";
      foreach (var part in parts) {
        accumulated += part + "/";
        Breadcrumbs.Add(new BreadcrumbSegment { Label = part, FolderPath = accumulated });
      }
    }
  }

  private void OpenDialog() {
    var dlg = new Microsoft.Win32.OpenFileDialog {
      Title = "Open Archive",
      Filter = BuildOpenFilter(),
    };
    if (dlg.ShowDialog() == true)
      Open(dlg.FileName);
  }

  private async Task ExtractAll() {
    var dlg = new System.Windows.Forms.FolderBrowserDialog {
      Description = "Select output folder",
      UseDescriptionForTitle = true,
    };
    if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

    await RunAsync($"Extracting to {dlg.SelectedPath}...", () => {
      ArchiveOperations.Extract(ArchivePath, dlg.SelectedPath, password: null, files: null);
    });
    StatusText = $"Extracted to {dlg.SelectedPath}";
  }

  /// <summary>
  /// Extracts the given selection to a per-session staging folder and returns the
  /// top-level paths for <see cref="DataFormats.FileDrop"/>. Called by the entry-list
  /// drag-out handler when the user drags archive entries to Explorer.
  /// <para>
  /// Files stay in <see cref="_dragOutStagingRoot"/> (under %TEMP%) until the archive
  /// is closed or the process exits — copies Windows may make for the drop target are
  /// independent. Nested directory selections preserve their in-archive relative paths
  /// so dropped folders look right in the target.
  /// </para>
  /// </summary>
  internal string[] MaterializeForDragOut(IReadOnlyList<ArchiveEntryViewModel> selection) {
    if (selection.Count == 0 || !HasArchive) return [];

    // Expand directory selections to all contained files, same as ExtractSelected does.
    var filePaths = new List<string>();
    foreach (var entry in selection) {
      if (entry.IsParentEntry) continue;
      if (entry.IsDirectory) {
        var dirPrefix = entry.Path.EndsWith('/') ? entry.Path : entry.Path + "/";
        foreach (var e in _allEntries)
          if (!e.IsDirectory && e.Path.StartsWith(dirPrefix, StringComparison.Ordinal))
            filePaths.Add(e.Path);
      } else {
        filePaths.Add(entry.Path);
      }
    }
    var files = filePaths.Distinct().ToArray();
    if (files.Length == 0) return [];

    // Ensure a clean staging dir per drag gesture; previous drag's contents are left in
    // place (the user may still be completing that drop), but the session-level directory
    // is reused so repeated drags don't leak unbounded temp folders.
    var stagingDir = Path.Combine(this.DragOutStagingRoot, Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(stagingDir);
    ArchiveOperations.Extract(ArchivePath, stagingDir, password: null, files: files);

    // For each dragged top-level entry (not the expanded directory contents), surface
    // its path under the staging dir. Explorer copies directories recursively.
    var topLevel = new List<string>();
    foreach (var entry in selection.Where(e => !e.IsParentEntry)) {
      var candidate = Path.Combine(stagingDir, entry.Path.Replace('/', Path.DirectorySeparatorChar));
      if (entry.IsDirectory) {
        if (Directory.Exists(candidate)) topLevel.Add(candidate);
      } else if (File.Exists(candidate)) {
        topLevel.Add(candidate);
      }
    }
    return topLevel.ToArray();
  }

  private string? _dragOutStagingRoot;
  private string DragOutStagingRoot {
    get {
      if (this._dragOutStagingRoot == null || !Directory.Exists(this._dragOutStagingRoot)) {
        this._dragOutStagingRoot = Path.Combine(Path.GetTempPath(),
          "cwb-drag-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(this._dragOutStagingRoot);
      }
      return this._dragOutStagingRoot;
    }
  }

  private async Task ExtractSelected() {
    var dlg = new System.Windows.Forms.FolderBrowserDialog {
      Description = "Select output folder",
      UseDescriptionForTitle = true,
    };
    if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

    // Collect all file paths: for selected directories, include all children
    var filePaths = new List<string>();
    foreach (var entry in SelectedEntries) {
      if (entry.IsParentEntry) continue;
      if (entry.IsDirectory) {
        // Include all files under this directory
        var dirPrefix = entry.Path.EndsWith('/') ? entry.Path : entry.Path + "/";
        foreach (var e in _allEntries) {
          if (!e.IsDirectory && e.Path.StartsWith(dirPrefix, StringComparison.Ordinal))
            filePaths.Add(e.Path);
        }
      }
      else {
        filePaths.Add(entry.Path);
      }
    }

    var files = filePaths.Distinct().ToArray();
    await RunAsync($"Extracting {files.Length} file(s)...", () => {
      ArchiveOperations.Extract(ArchivePath, dlg.SelectedPath, password: null, files: files);
    });
    StatusText = $"Extracted {files.Length} file(s) to {dlg.SelectedPath}";
  }

  private async Task TestArchive() {
    var ok = false;
    string? errorDetail = null;
    var sw = Stopwatch.StartNew();

    try {
      await RunAsync("Testing archive integrity...", () => {
        ok = ArchiveOperations.Test(ArchivePath, password: null);
      });
    } catch (Exception ex) {
      ok = false;
      errorDetail = ex.Message;
    }

    sw.Stop();
    var elapsed = sw.ElapsedMilliseconds;

    if (ok) {
      StatusText = $"Integrity test: OK ({elapsed}ms)";
      MessageBox.Show(
        $"Integrity test passed.\n\nArchive: {System.IO.Path.GetFileName(ArchivePath)}\nEntries: {_allEntries.Count}\nTime: {elapsed}ms",
        "Integrity Test",
        System.Windows.MessageBoxButton.OK,
        System.Windows.MessageBoxImage.Information);
    } else {
      StatusText = $"Integrity test: FAILED ({elapsed}ms)";
      var msg = $"Integrity test FAILED.\n\nArchive: {System.IO.Path.GetFileName(ArchivePath)}";
      if (errorDetail != null)
        msg += $"\n\nError: {errorDetail}";
      MessageBox.Show(msg, "Integrity Test",
        System.Windows.MessageBoxButton.OK,
        System.Windows.MessageBoxImage.Error);
    }
  }

  internal void ViewSelectedAs(bool hex) {
    var entry = SelectedEntries.FirstOrDefault(e => !e.IsDirectory && !e.IsParentEntry);
    if (entry == null) return;

    try {
      StatusText = $"Loading preview of {entry.Name}...";
      var data = ArchiveOperations.ExtractEntry(ArchivePath, entry.Path, password: null);
      var preview = new Views.PreviewWindow { Owner = Application.Current.MainWindow };
      preview.ShowData(entry.Name, data, hex);
      preview.Show();
      StatusText = "Ready";
    }
    catch (Exception ex) {
      StatusText = $"Preview error: {ex.Message}";
    }
  }

  internal IReadOnlyList<ArchiveEntryViewModel> AllEntries => _allEntries;

  internal void ShowProperties() {
    var entry = SelectedEntries.FirstOrDefault();
    if (entry == null || entry.IsParentEntry) return;

    byte[]? data = null;
    if (!entry.IsDirectory) {
      try {
        StatusText = $"Loading {entry.Name}...";
        data = ArchiveOperations.ExtractEntry(ArchivePath, entry.Path, password: null);
        StatusText = "Ready";
      }
      catch {
        // Properties still shown without data statistics
      }
    }

    var dlg = new Views.PropertiesWindow { Owner = Application.Current.MainWindow };
    dlg.ShowProperties(entry, _allEntries, data);
    dlg.ShowDialog();
  }

  internal void ShowBenchmark() {
    var win = new Views.BenchmarkWindow { Owner = Application.Current.MainWindow };
    win.Show();
  }

  internal void ShowFileAssociations() {
    var win = new Views.FileAssociationsWindow { Owner = Application.Current.MainWindow };
    win.ShowDialog();
  }

  internal void ShowAnalyzeFile() {
    var win = new Views.AnalysisWindow { Owner = Application.Current.MainWindow };
    win.Show();
  }

  internal void ShowAnalysis() {
    var entry = SelectedEntries.FirstOrDefault(e => !e.IsDirectory && !e.IsParentEntry);
    if (entry == null) return;

    try {
      StatusText = $"Loading {entry.Name}...";
      var data = ArchiveOperations.ExtractEntry(ArchivePath, entry.Path, password: null);
      StatusText = "Ready";

      var win = new Views.AnalysisWindow { Owner = Application.Current.MainWindow };
      win.RunAnalysis(entry.Path, data);
      win.Show();
    }
    catch (Exception ex) {
      StatusText = $"Analysis error: {ex.Message}";
    }
  }

  internal void HandleFileDrop(string[] files) {
    if (!HasArchive) {
      // No archive open — open the first dropped file as an archive
      if (files.Length > 0) Open(files[0]);
      return;
    }

    // Archive is open — validate against the descriptor's constraints before adding.
    var (allowed, message) = EvaluateDropAgainstCurrentArchive(files);
    if (!allowed) {
      StatusText = message ?? "Some dropped files can't go in this archive.";
      return;
    }
    AddFilesToArchiveImpl(files);
  }

  /// <summary>
  /// Probes the current archive's descriptor: is the drop allowed by writability
  /// (IArchiveCreatable or IArchiveModifiable) and accepted by any declared
  /// IArchiveWriteConstraints? Returns the display message to use in the drop overlay.
  /// </summary>
  internal (bool Allowed, string? Message) EvaluateDropAgainstCurrentArchive(string[] files) {
    if (!HasArchive) return (true, "Drop archive to open");

    var format = FormatDetector.DetectByExtension(ArchivePath);
    if (format == FormatDetector.Format.Unknown) return (true, "Drop to add files to archive");

    Compression.Lib.FormatRegistration.EnsureInitialized();
    var ops = Compression.Registry.FormatRegistry.GetArchiveOps(format.ToString());
    // Read-only descriptors block the drop outright.
    if (ops is not Compression.Registry.IArchiveCreatable &&
        ops is not Compression.Registry.IArchiveModifiable) {
      return (false, "This archive format is read-only (can't add files)");
    }

    // Per-file + cumulative-size check via optional constraints.
    if (ops is Compression.Registry.IArchiveWriteConstraints constraints) {
      long cumulative = 0;
      foreach (var path in files) {
        var entryName = System.IO.Path.GetFileName(path);
        var size = new FileInfo(path).Exists ? new FileInfo(path).Length : 0;
        cumulative += size;
        var input = new Compression.Registry.ArchiveInputInfo(path, entryName, IsDirectory: false);
        if (!constraints.CanAccept(input, out var why))
          return (false, why ?? constraints.AcceptedInputsDescription);
      }
      if (constraints.MaxTotalArchiveSize is long cap && cumulative > cap)
        return (false, $"Total {cumulative} bytes exceeds this format's {cap}-byte ceiling");
    }

    return (true, "Drop to add files to archive");
  }

  private void AddFilesToArchive() {
    var dlg = new Microsoft.Win32.OpenFileDialog {
      Title = "Add files to archive",
      Multiselect = true,
      Filter = "All Files|*.*",
    };
    if (dlg.ShowDialog() != true) return;
    AddFilesToArchiveImpl(dlg.FileNames);
  }

  private void AddFilesToArchiveImpl(string[] paths) {
    var format = FormatDetector.DetectByExtension(ArchivePath);
    if (format == FormatDetector.Format.Unknown || FormatDetector.IsStreamFormat(format)) {
      MessageBox.Show("Cannot add files to this archive format.", "Add Files",
        MessageBoxButton.OK, MessageBoxImage.Warning);
      return;
    }

    // Show options dialog for compression parameters
    var optsDlg = new CreateOptionsWindow(format) { Owner = Application.Current.MainWindow };
    optsDlg.Title = "Add Files — Compression Options";
    if (optsDlg.ShowDialog() != true) return;

    var opts = optsDlg.Options.ToOptions();
    var archivePath = ArchivePath;

    Task.Run(() => {
      Application.Current.Dispatcher.Invoke(() => {
        IsBusy = true;
        StatusText = $"Adding {paths.Length} item(s) to archive...";
      });

      try {
        var sw = Stopwatch.StartNew();

        // Extract existing entries to temp dir
        var tempDir = Path.Combine(Path.GetTempPath(), "cwb_add_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try {
          ArchiveOperations.Extract(archivePath, tempDir, password: null, files: null);

          // Copy new files into temp dir, preserving the current folder context
          foreach (var p in paths) {
            if (Directory.Exists(p)) {
              CopyDirectory(p, Path.Combine(tempDir, _currentFolder.Replace('/', Path.DirectorySeparatorChar), Path.GetFileName(p)));
            }
            else if (File.Exists(p)) {
              var destDir = Path.Combine(tempDir, _currentFolder.Replace('/', Path.DirectorySeparatorChar));
              Directory.CreateDirectory(destDir);
              File.Copy(p, Path.Combine(destDir, Path.GetFileName(p)), overwrite: true);
            }
          }

          // Re-create archive from temp dir
          var inputs = ArchiveInput.Resolve([tempDir]);
          ArchiveOperations.Create(archivePath, inputs, opts);
        }
        finally {
          try { Directory.Delete(tempDir, true); } catch { }
        }

        sw.Stop();
        Application.Current.Dispatcher.Invoke(() => {
          StatusText = $"Added {paths.Length} item(s) ({sw.ElapsedMilliseconds}ms)";
          Open(archivePath);
        });
      }
      catch (Exception ex) {
        Application.Current.Dispatcher.Invoke(() => {
          StatusText = $"Error adding files: {ex.Message}";
          IsBusy = false;
        });
      }
    });
  }

  private static void CopyDirectory(string sourceDir, string destDir) {
    Directory.CreateDirectory(destDir);
    foreach (var file in Directory.GetFiles(sourceDir))
      File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
    foreach (var dir in Directory.GetDirectories(sourceDir))
      CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
  }

  private void CreateDialog() {
    var dlg = new Microsoft.Win32.SaveFileDialog {
      Title = "Create Archive",
      Filter = BuildCreateFilter(),
    };
    if (dlg.ShowDialog() != true) return;

    // Detect format from chosen extension
    var format = FormatDetector.DetectByExtension(dlg.FileName);

    // Show options dialog
    var optsDlg = new CreateOptionsWindow(format) { Owner = Application.Current.MainWindow };
    if (optsDlg.ShowDialog() != true) return;

    // Pick input files/folder
    var inputDlg = new System.Windows.Forms.FolderBrowserDialog {
      Description = "Select folder to archive",
      UseDescriptionForTitle = true,
    };
    if (inputDlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

    var inputs = ArchiveInput.Resolve([inputDlg.SelectedPath]);
    var opts = optsDlg.Options.ToOptions();
    var makeSfx = optsDlg.Options.MakeSfx;
    var sfxStubType = optsDlg.Options.SfxIsGui ? SfxBuilder.StubType.Ui : SfxBuilder.StubType.Cli;
    var sfxTargetRid = optsDlg.Options.ResolvedSfxTargetRid;
    var outputPath = dlg.FileName;

    Task.Run(() => {
      Application.Current.Dispatcher.Invoke(() => {
        IsBusy = true;
        StatusText = $"Creating {Path.GetFileName(outputPath)}...";
      });

      var sw = Stopwatch.StartNew();
      ArchiveOperations.Create(outputPath, inputs, opts);

      if (makeSfx) {
        var sfxPath = Path.ChangeExtension(outputPath, ".exe");
        SfxBuilder.WrapExisting(outputPath, sfxPath, sfxStubType, sfxTargetRid);
        try { File.Delete(outputPath); } catch { }
        outputPath = sfxPath;
      }

      sw.Stop();

      Application.Current.Dispatcher.Invoke(() => {
        IsBusy = false;
        StatusText = $"Created {Path.GetFileName(outputPath)} ({sw.ElapsedMilliseconds}ms)";
        Open(outputPath);
      });
    });
  }

  private async Task RunAsync(string status, Action work) {
    IsBusy = true;
    StatusText = status;
    var sw = Stopwatch.StartNew();
    await Task.Run(work);
    sw.Stop();
    IsBusy = false;
  }

  private static string FormatSize(long bytes) => bytes switch {
    < 1024 => $"{bytes} B",
    < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
    < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
    _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
  };

  private static string BuildOpenFilter() {
    FormatRegistration.EnsureInitialized();
    var exts = new List<string>();
    foreach (var desc in FormatRegistry.All) {
      foreach (var ext in desc.CompoundExtensions) exts.Add("*" + ext);
      foreach (var ext in desc.Extensions) exts.Add("*" + ext);
    }
    return $"All Archives|{string.Join(";", exts.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(e => e))}|All Files|*.*";
  }

  private static string BuildCreateFilter() {
    FormatRegistration.EnsureInitialized();
    var entries = new List<string>();
    foreach (var desc in FormatRegistry.All.Where(d=>d.Capabilities.HasFlag(FormatCapabilities.CanCreate)).OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)) {
      var ext = desc.DefaultExtension;
      entries.Add($"{desc.DisplayName} (*{ext})|*{ext}");
    }
    return string.Join("|", entries);
  }
}

/// <summary>
/// Represents a clickable segment in the breadcrumb navigation bar.
/// </summary>
internal sealed class BreadcrumbSegment {
  public string Label { get; init; } = "";
  public string FolderPath { get; init; } = "";
}
