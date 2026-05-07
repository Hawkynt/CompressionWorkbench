using System.Diagnostics;
using System.IO;
using System.Windows;
using Compression.Lib;
using Compression.Registry;

namespace Compression.UI.Views;

/// <summary>
/// User-initiated defragmentation pass over a filesystem image. Shows the
/// detected format + capability, lets the user pick one of four layout
/// strategies (mirroring the CLI's <c>cwb defragment --mode</c> options),
/// and runs the descriptor's <see cref="IArchiveDefragmentable"/> path.
/// </summary>
public partial class DefragmentWindow : Window {

  private string? _imagePath;
  private IArchiveDefragmentable? _defragmentable;

  public DefragmentWindow() {
    InitializeComponent();
  }

  public DefragmentWindow(string preselectedImage) : this() {
    if (!string.IsNullOrEmpty(preselectedImage) && File.Exists(preselectedImage))
      LoadImage(preselectedImage);
  }

  private void OnBrowse(object sender, RoutedEventArgs e) {
    var dlg = new Microsoft.Win32.OpenFileDialog {
      Title = "Select filesystem image",
      Filter = "Filesystem images|*.img;*.iso;*.d64;*.d71;*.d81;*.adf;*.dsk;*.po;*.atr;*.ssd;*.dsd|All Files|*.*",
    };
    if (dlg.ShowDialog() != true) return;
    LoadImage(dlg.FileName);
  }

  private void LoadImage(string path) {
    this._imagePath = path;
    ImagePathBox.Text = path;
    ImagePathBox.Foreground = System.Windows.Media.Brushes.Black;

    Compression.Lib.FormatRegistration.EnsureInitialized();
    var format = FormatDetector.Detect(path);
    FormatLbl.Text = format.ToString();

    var ops = FormatRegistry.GetArchiveOps(format.ToString());
    this._defragmentable = ops as IArchiveDefragmentable;

    if (this._defragmentable == null) {
      SupportLbl.Text = "Not supported by this format.";
      SupportLbl.Foreground = System.Windows.Media.Brushes.OrangeRed;
      RunBtn.IsEnabled = false;
    } else {
      // Probe the descriptor: which non-default modes does it accept?
      var supported = ProbeSupportedModes(this._defragmentable);
      SupportLbl.Text = supported.Count >= 4
        ? "All four modes (in-place defragment)."
        : $"{string.Join(", ", supported)} (other modes throw NotSupported)";
      SupportLbl.Foreground = System.Windows.Media.Brushes.DarkGreen;
      RunBtn.IsEnabled = true;
    }

    var fi = new FileInfo(path);
    SizeLbl.Text = $"{FormatSize(fi.Length)} ({fi.Length:N0} bytes)";
  }

  /// <summary>
  /// Runs each of the four <see cref="DefragMode"/> values through a probe
  /// stream that throws on first read so we don't actually mutate the image
  /// — we only want to know whether the descriptor's overload accepts the
  /// mode at all (i.e. didn't throw <see cref="NotSupportedException"/> at
  /// the dispatch boundary).
  /// </summary>
  private static List<string> ProbeSupportedModes(IArchiveDefragmentable defragmentable) {
    var supported = new List<string>();
    foreach (var mode in Enum.GetValues<DefragMode>()) {
      try {
        using var probe = new ProbeStream();
        defragmentable.Defragment(probe, new DefragOptions { Mode = mode });
        supported.Add(mode.ToString()); // unlikely — probe should throw before completion
      } catch (NotSupportedException) {
        // Mode unsupported by descriptor — don't add.
      } catch (ProbeAbortException) {
        // Descriptor started reading: it accepts the mode.
        supported.Add(mode.ToString());
      } catch {
        // Any other exception: assume unsupported (safer than guessing).
      }
    }
    return supported;
  }

  private void OnModeChanged(object sender, RoutedEventArgs e) {
    if (CarveOptsGroup == null) return; // not yet inflated
    CarveOptsGroup.Visibility = ModeCarveHole != null && ModeCarveHole.IsChecked == true
      ? Visibility.Visible
      : Visibility.Collapsed;
  }

  private void OnRun(object sender, RoutedEventArgs e) {
    if (this._defragmentable == null || this._imagePath == null) return;

    var mode = SelectedMode();
    var holeSize = mode == DefragMode.CarveHole ? ParseSize(HoleSizeBox.Text) : 0L;
    var holeAt = mode == DefragMode.CarveHole ? ParseHoleAt(HoleAtBox.Text) : -1L;

    var path = this._imagePath;
    var defragmentable = this._defragmentable;

    Append($"=== {DateTime.Now:HH:mm:ss}  Defragmenting {Path.GetFileName(path)} ===");
    Append($"Mode: {mode}");
    if (mode == DefragMode.CarveHole) {
      Append($"Hole size: {holeSize:N0} bytes");
      Append($"Hole at: {(holeAt < 0 ? "auto (end)" : holeAt.ToString("N0"))}");
    }

    RunBtn.IsEnabled = false;
    Progress.IsIndeterminate = true;

    Task.Run(() => {
      var sw = Stopwatch.StartNew();
      Exception? err = null;
      var origSize = new FileInfo(path).Length;
      try {
        using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
        defragmentable.Defragment(stream, new DefragOptions {
          Mode = mode,
          HoleSize = holeSize,
          HoleAt = holeAt,
        });
      } catch (Exception ex) {
        err = ex;
      }
      sw.Stop();
      var newSize = File.Exists(path) ? new FileInfo(path).Length : 0L;

      Dispatcher.Invoke(() => {
        Progress.IsIndeterminate = false;
        RunBtn.IsEnabled = true;
        if (err != null) {
          Append($"FAILED ({sw.ElapsedMilliseconds} ms): {err.GetType().Name}: {err.Message}");
        } else {
          Append($"OK ({sw.ElapsedMilliseconds} ms)");
          Append($"Image size: {origSize:N0} -> {newSize:N0} bytes (Δ {newSize - origSize:+#,#;-#,#;0})");
        }
        Append("");
      });
    });
  }

  private DefragMode SelectedMode() {
    if (ModePackEnd.IsChecked == true) return DefragMode.ConsolidateAtEnd;
    if (ModeFillHoles.IsChecked == true) return DefragMode.FillHolesLazy;
    if (ModeCarveHole.IsChecked == true) return DefragMode.CarveHole;
    return DefragMode.ConsolidateAtStart;
  }

  private static long ParseSize(string s) {
    if (string.IsNullOrWhiteSpace(s)) return 0;
    s = s.Trim().ToLowerInvariant();
    long mult = 1;
    if (s.EndsWith('k')) { mult = 1024L; s = s[..^1]; }
    else if (s.EndsWith('m')) { mult = 1024L * 1024; s = s[..^1]; }
    else if (s.EndsWith('g')) { mult = 1024L * 1024 * 1024; s = s[..^1]; }
    return long.TryParse(s, out var n) ? n * mult : 0;
  }

  private static long ParseHoleAt(string s) {
    if (string.IsNullOrWhiteSpace(s) || s.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
      return -1;
    return long.TryParse(s, out var n) ? n : -1;
  }

  private void Append(string line) {
    OutputBox.AppendText(line + Environment.NewLine);
    OutputBox.ScrollToEnd();
  }

  private void OnClose(object sender, RoutedEventArgs e) => Close();

  private static string FormatSize(long bytes) => bytes switch {
    < 1024 => $"{bytes} B",
    < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
    < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
    _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
  };

  // ── Probe machinery ─────────────────────────────────────────────────
  // We can't ask a descriptor "do you support this mode?" without invoking
  // it. So we feed it a stream that throws ProbeAbortException on the first
  // Read/Seek — if the descriptor got that far, it accepted the mode; if it
  // threw NotSupportedException before any I/O, the mode is unsupported.

  private sealed class ProbeAbortException : Exception { }
  private sealed class ProbeStream : Stream {
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => 1024;
    public override long Position { get => 0; set => throw new ProbeAbortException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new ProbeAbortException();
    public override long Seek(long offset, SeekOrigin origin) => throw new ProbeAbortException();
    public override void SetLength(long value) => throw new ProbeAbortException();
    public override void Write(byte[] buffer, int offset, int count) => throw new ProbeAbortException();
  }
}
