using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Compression.Analysis.ReverseEngineering;

namespace Compression.UI.ViewModels;

/// <summary>
/// ViewModel for the Format Reverse Engineering wizard.
/// Supports two modes: tool-based probing and static archive analysis.
/// </summary>
internal sealed class ReverseEngineerWizardViewModel : ViewModelBase {

  // ── Wizard navigation ──────────────────────────────────────────────

  public enum WizardStep { ChooseMode, ConfigureTool, AddSamples, Running, Results }

  private WizardStep _currentStep = WizardStep.ChooseMode;
  public WizardStep CurrentStep { get => _currentStep; set => SetField(ref _currentStep, value); }

  public ICommand NextCommand { get; }
  public ICommand BackCommand { get; }
  public ICommand CancelCommand { get; }
  public ICommand BrowseOriginalCommand { get; }
  public ICommand BrowseArchiveCommand { get; }

  /// <summary>Raised when the user clicks Cancel — the host Window subscribes and closes.</summary>
  public event EventHandler? RequestClose;

  // ── Mode selection ─────────────────────────────────────────────────

  private bool _isToolMode = true;
  public bool IsToolMode { get => _isToolMode; set { SetField(ref _isToolMode, value); OnPropertyChanged(nameof(IsStaticMode)); } }
  public bool IsStaticMode { get => !_isToolMode; set => IsToolMode = !value; }

  // ── Tool mode configuration ────────────────────────────────────────

  private string _toolPath = "";
  public string ToolPath { get => _toolPath; set => SetField(ref _toolPath, value); }

  private string _toolArguments = "{input} {output}";
  public string ToolArguments { get => _toolArguments; set => SetField(ref _toolArguments, value); }

  private int _toolTimeout = 30000;
  public int ToolTimeout { get => _toolTimeout; set => SetField(ref _toolTimeout, value); }

  // ── Static mode: samples ───────────────────────────────────────────

  public ObservableCollection<SampleEntry> Samples { get; } = [];

  public sealed class SampleEntry : ViewModelBase {
    private string _originalPath = "";
    public string OriginalPath { get => _originalPath; set => SetField(ref _originalPath, value); }

    private string _archivePath = "";
    public string ArchivePath { get => _archivePath; set => SetField(ref _archivePath, value); }

    public string DisplayName => Path.GetFileName(ArchivePath);
  }

  public ICommand AddSampleCommand { get; }
  public ICommand RemoveSampleCommand { get; }

  // ── Running state ──────────────────────────────────────────────────

  private bool _isRunning;
  public bool IsRunning { get => _isRunning; set => SetField(ref _isRunning, value); }

  private string _statusText = "";
  public string StatusText { get => _statusText; set => SetField(ref _statusText, value); }

  private double _progress;
  public double Progress { get => _progress; set => SetField(ref _progress, value); }

  private string _currentProbe = "";
  public string CurrentProbe { get => _currentProbe; set => SetField(ref _currentProbe, value); }

  public ICommand StartAnalysisCommand { get; }
  private CancellationTokenSource? _cts;

  // ── Results ────────────────────────────────────────────────────────

  private string _reportText = "";
  public string ReportText { get => _reportText; set => SetField(ref _reportText, value); }

  private string _headerHex = "";
  public string HeaderHex { get => _headerHex; set => SetField(ref _headerHex, value); }

  private string _compressionResult = "";
  public string CompressionResult { get => _compressionResult; set => SetField(ref _compressionResult, value); }

  public ObservableCollection<SizeFieldEntry> DetectedSizeFields { get; } = [];

  public sealed class SizeFieldEntry {
    public required int Offset { get; init; }
    public required string Width { get; init; }
    public required string Endianness { get; init; }
    public required string Meaning { get; init; }
  }

  public ObservableCollection<ProbeResultEntry> ProbeResults { get; } = [];

  public sealed class ProbeResultEntry {
    public required string Name { get; init; }
    public required int InputSize { get; init; }
    public required int OutputSize { get; init; }
    public required string Status { get; init; }
  }

  // ── Constructor ────────────────────────────────────────────────────

  public ReverseEngineerWizardViewModel() {
    NextCommand = new RelayCommand(_ => GoNext(), _ => CanGoNext());
    BackCommand = new RelayCommand(_ => GoBack(), _ => CurrentStep > WizardStep.ChooseMode && !IsRunning);
    // Cancel is ALWAYS enabled — closes the wizard. If analysis is running,
    // also signals the cancellation token so the worker stops promptly.
    CancelCommand = new RelayCommand(_ => {
      if (IsRunning) _cts?.Cancel();
      RequestClose?.Invoke(this, EventArgs.Empty);
    });
    AddSampleCommand = new RelayCommand(_ => Samples.Add(new SampleEntry()));
    RemoveSampleCommand = new RelayCommand(p => { if (p is SampleEntry s) Samples.Remove(s); });
    BrowseOriginalCommand = new RelayCommand(p => {
      if (p is not SampleEntry s) return;
      var dlg = new Microsoft.Win32.OpenFileDialog {
        Title = "Pick the ORIGINAL (uncompressed) file",
        Filter = "All files|*.*",
      };
      if (dlg.ShowDialog() == true) s.OriginalPath = dlg.FileName;
    });
    BrowseArchiveCommand = new RelayCommand(p => {
      if (p is not SampleEntry s) return;
      var dlg = new Microsoft.Win32.OpenFileDialog {
        Title = "Pick the ARCHIVE file containing that original",
        Filter = "All files|*.*",
      };
      if (dlg.ShowDialog() == true) s.ArchivePath = dlg.FileName;
    });
    StartAnalysisCommand = new AsyncRelayCommand(async _ => await RunAnalysisAsync());
  }

  private bool CanGoNext() => CurrentStep switch {
    WizardStep.ChooseMode => true,
    WizardStep.ConfigureTool => !string.IsNullOrWhiteSpace(ToolPath) && ToolArguments.Contains("{input}") && ToolArguments.Contains("{output}"),
    WizardStep.AddSamples => Samples.Count > 0 && Samples.All(s => !string.IsNullOrWhiteSpace(s.OriginalPath) && !string.IsNullOrWhiteSpace(s.ArchivePath)),
    _ => false
  };

  private void GoNext() {
    CurrentStep = CurrentStep switch {
      WizardStep.ChooseMode => IsToolMode ? WizardStep.ConfigureTool : WizardStep.AddSamples,
      WizardStep.ConfigureTool or WizardStep.AddSamples => WizardStep.Running,
      _ => CurrentStep
    };

    if (CurrentStep == WizardStep.Running)
      StartAnalysisCommand.Execute(null);
  }

  private void GoBack() {
    CurrentStep = CurrentStep switch {
      WizardStep.ConfigureTool or WizardStep.AddSamples => WizardStep.ChooseMode,
      WizardStep.Results => IsToolMode ? WizardStep.ConfigureTool : WizardStep.AddSamples,
      _ => CurrentStep
    };
  }

  private async Task RunAnalysisAsync() {
    IsRunning = true;
    _cts = new CancellationTokenSource();
    StatusText = "Starting analysis...";
    Progress = 0;
    ProbeResults.Clear();
    DetectedSizeFields.Clear();

    try {
      Compression.Lib.FormatRegistration.EnsureInitialized();

      if (IsToolMode)
        await RunToolAnalysisAsync(_cts.Token);
      else
        RunStaticAnalysis();
    } catch (OperationCanceledException) {
      StatusText = "Analysis cancelled.";
    } catch (Exception ex) {
      StatusText = $"Error: {ex.Message}";
    } finally {
      IsRunning = false;
      CurrentStep = WizardStep.Results;
      _cts?.Dispose();
      _cts = null;
    }
  }

  private async Task RunToolAnalysisAsync(CancellationToken ct) {
    var reverser = new FormatReverser(ToolPath, ToolArguments, ToolTimeout);
    var report = await reverser.AnalyzeAsync(
      (name, current, total) => {
        System.Windows.Application.Current?.Dispatcher?.Invoke(() => {
          CurrentProbe = name;
          Progress = 100.0 * current / total;
          StatusText = $"Probe {current}/{total}: {name}";
        });
      },
      ct
    );

    System.Windows.Application.Current?.Dispatcher?.Invoke(() => PopulateResults(report));
  }

  private void PopulateResults(FormatReverser.ReverseEngineeringReport report) {
    ReportText = report.Summary;

    HeaderHex = report.Header != null
      ? string.Join(" ", report.Header.Bytes.Select(b => $"{b:X2}"))
      : "(no common header detected)";

    CompressionResult = report.CompressionAnalysis.BestGuess ?? "Unknown";

    foreach (var sf in report.SizeFields)
      DetectedSizeFields.Add(new() { Offset = sf.Offset, Width = $"{sf.Width} bytes", Endianness = sf.Endianness, Meaning = sf.Meaning });

    foreach (var r in report.AllRuns)
      ProbeResults.Add(new() { Name = r.ProbeName, InputSize = r.InputSize, OutputSize = r.OutputSize, Status = r.Success ? "OK" : r.Error ?? "Failed" });
  }

  private void RunStaticAnalysis() {
    var samples = new List<StaticFormatAnalyzer.Sample>();
    foreach (var entry in Samples) {
      if (!File.Exists(entry.OriginalPath) || !File.Exists(entry.ArchivePath)) continue;
      samples.Add(new() {
        Name = Path.GetFileName(entry.ArchivePath),
        OriginalContent = File.ReadAllBytes(entry.OriginalPath),
        ArchiveBytes = File.ReadAllBytes(entry.ArchivePath),
        OriginalFileName = Path.GetFileName(entry.OriginalPath)
      });
    }

    if (samples.Count == 0) {
      StatusText = "No valid samples found.";
      return;
    }

    var report = StaticFormatAnalyzer.Analyze(samples, (desc, current, total) => {
      System.Windows.Application.Current?.Dispatcher?.Invoke(() => {
        StatusText = desc;
        Progress = 100.0 * current / total;
      });
    });

    System.Windows.Application.Current?.Dispatcher?.Invoke(() => PopulateStaticResults(report));
  }

  private void PopulateStaticResults(StaticFormatAnalyzer.StaticAnalysisReport report) {
    ReportText = report.Summary;

    HeaderHex = report.CommonHeader != null
      ? string.Join(" ", report.CommonHeader.Bytes.Select(b => $"{b:X2}"))
      : "(no common header detected)";

    CompressionResult = report.CompressionAnalysis?.BestGuess ?? "Unknown";

    foreach (var sf in report.SizeFields)
      DetectedSizeFields.Add(new() { Offset = sf.Offset, Width = $"{sf.Width} bytes", Endianness = sf.Endianness, Meaning = sf.Meaning });

    foreach (var loc in report.ContentLocations)
      ProbeResults.Add(new() { Name = loc.SampleName, InputSize = loc.Length, OutputSize = loc.ArchiveOffset, Status = loc.StorageMethod });
  }
}
