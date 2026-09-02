using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using Compression.Lib;
using Compression.Registry;

namespace Compression.UI.Views;

/// <summary>
/// Universal live block-map/cancellation layer for maintenance operations that
/// rebuild or re-group a container instead of physically moving blocks in place.
/// Kept as a partial so it can reuse the existing maintenance window controls and
/// helpers without duplicating the large preview/rendering implementation.
/// </summary>
public partial class DefragmentWindow {
  private bool _rebuildProgressHooked;
  private Button? _maintenanceCancelButton;
  private CancellationTokenSource? _maintenanceCancellation;
  private bool _maintenanceIsStaged;
  private bool _maintenanceCommitStarted;
  private string? _maintenanceOperationName;

  protected override void OnContentRendered(EventArgs e) {
    base.OnContentRendered(e);
    if (this._rebuildProgressHooked) return;
    this._rebuildProgressHooked = true;

    // Replace only the main Run dispatch. Shrink/Purge/Wipe/Compact keep their
    // existing handlers; rebuild-backed Defrag/Optimize now share this richer UI.
    RunBtn.Click -= OnRun;
    RunBtn.Click += OnRunWithBlockProgress;
    InsertMaintenanceCancelButton();

    // The original loader sees IArchiveDefragmentable before archive-repack
    // support. When the caller explicitly asked for Optimize, correct that
    // ambiguity here so ZIP/7z can expose their repack UI instead of looking like
    // filesystem-only defraggers.
    if (this._requestedVerb == MaintenanceVerb.Optimize && this._formatId is { Length: > 0 } id) {
      var descriptor = FormatRegistry.GetById(id);
      var ops = FormatRegistry.GetArchiveOps(id);
      if (descriptor?.Category is FormatCategory.Archive or FormatCategory.CompoundTar
          && ops is IArchiveCreatable) {
        this._isArchiveMode = true;
        this._archiveOps = ops;
        this._isSevenZipFormat = string.Equals(id, "SevenZip", StringComparison.Ordinal);
        FsModesGroup.Visibility = Visibility.Collapsed;
        ArchiveRepackGroup.Visibility = Visibility.Visible;
        SmartSolidRepackCheck.Visibility = this._isSevenZipFormat ? Visibility.Visible : Visibility.Collapsed;
        RunBtn.Content = "Optimize";
        RunBtn.IsEnabled = true;
        SupportLbl.Text = "Archive re-layout/repack with live staged-target visualization.";
        SupportLbl.Foreground = System.Windows.Media.Brushes.DarkGreen;
        if (LayoutStatusLbl != null)
          LayoutStatusLbl.Text = "Source + staged-target address spaces share the chart for progress; offsets are projected, not physical equivalence.";
      }
    }
  }

  protected override void OnClosing(CancelEventArgs e) {
    base.OnClosing(e);
    if (e.Cancel || this._maintenanceCancellation == null) return;

    e.Cancel = true;
    RequestMaintenanceCancellation(confirmNativeInPlace: true);
  }

  private void InsertMaintenanceCancelButton() {
    if (this._maintenanceCancelButton != null || RunBtn.Parent is not StackPanel panel)
      return;

    var close = panel.Children.OfType<Button>()
      .FirstOrDefault(button => string.Equals(button.Content?.ToString(), "Close", StringComparison.Ordinal));
    var index = close == null ? panel.Children.Count : panel.Children.IndexOf(close);
    var cancel = new Button {
      Content = "Cancel",
      Width = 80,
      Padding = new Thickness(4),
      Margin = new Thickness(0, 0, 8, 0),
      IsEnabled = false,
      ToolTip = "Cancel the active maintenance pass. Staged rebuilds are discarded and leave the original unchanged; native in-place moves cancel at a safe boundary when supported.",
    };
    cancel.Click += (_, _) => RequestMaintenanceCancellation(confirmNativeInPlace: true);
    panel.Children.Insert(index, cancel);
    this._maintenanceCancelButton = cancel;
  }

  private CancellationToken BeginMaintenanceOperation(string name, bool staged) {
    this._maintenanceCancellation?.Dispose();
    this._maintenanceCancellation = new CancellationTokenSource();
    this._maintenanceOperationName = name;
    this._maintenanceIsStaged = staged;
    this._maintenanceCommitStarted = false;

    if (this._maintenanceCancelButton != null) this._maintenanceCancelButton.IsEnabled = true;
    BrowseBtn.IsEnabled = false;
    RunBtn.IsEnabled = false;
    ShrinkBtn.IsEnabled = false;
    WipeEmptyBtn.IsEnabled = false;
    PurgeBtn.IsEnabled = false;
    CompactBtn.IsEnabled = false;

    if (staged && LayoutStatusLbl != null)
      LayoutStatusLbl.Text = $"{name}: building a staged target — original remains unchanged until commit.";
    return this._maintenanceCancellation.Token;
  }

  private void EndMaintenanceOperation() {
    this._maintenanceCancellation?.Dispose();
    this._maintenanceCancellation = null;
    this._maintenanceOperationName = null;
    this._maintenanceIsStaged = false;
    this._maintenanceCommitStarted = false;
    if (this._maintenanceCancelButton != null) this._maintenanceCancelButton.IsEnabled = false;
    BrowseBtn.IsEnabled = true;

    if (this._imagePath is { Length: > 0 } path && File.Exists(path))
      LoadImage(path);
  }

  private void RequestMaintenanceCancellation(bool confirmNativeInPlace) {
    var cts = this._maintenanceCancellation;
    if (cts == null || cts.IsCancellationRequested) return;

    if (this._maintenanceCommitStarted) {
      Append("Cancellation ignored: verified target commit has started; completing it is safer than interrupting the only commit step.");
      if (this._maintenanceCancelButton != null) this._maintenanceCancelButton.IsEnabled = false;
      return;
    }

    if (!this._maintenanceIsStaged && confirmNativeInPlace) {
      var result = MessageBox.Show(this,
        $"Cancel {this._maintenanceOperationName ?? "maintenance"}?\n\n"
        + "This operation is moving data in place. Moves already completed are not rolled back. "
        + "Cancellation is best-effort at the next safe boundary, so the layout may be partially changed although the container should remain valid.",
        "Cancel in-place maintenance", MessageBoxButton.YesNo, MessageBoxImage.Warning);
      if (result != MessageBoxResult.Yes) return;
      Append("Cancellation requested — already-completed in-place moves will remain moved.");
    } else {
      Append("Cancellation requested — staged target will be discarded; existing archive remains unchanged.");
    }

    if (this._maintenanceCancelButton != null) this._maintenanceCancelButton.IsEnabled = false;
    cts.Cancel();
  }

  private void OnRunWithBlockProgress(object sender, RoutedEventArgs e) {
    if (this._imagePath == null || this._maintenanceCancellation != null) return;

    if (this._isFileInternalMode) {
      OnRunFileInternalOptimize();
      return;
    }

    var ops = this._formatId is { Length: > 0 } id
      ? FormatRegistry.GetArchiveOps(id)
      : this._archiveOps;
    var descriptor = this._formatId is { Length: > 0 } formatId
      ? FormatRegistry.GetById(formatId)
      : null;
    var explicitlyOptimizingArchive = this._requestedVerb == MaintenanceVerb.Optimize
      && descriptor?.Category is FormatCategory.Archive or FormatCategory.CompoundTar
      && ops is IArchiveCreatable;

    if (this._isArchiveMode || explicitlyOptimizingArchive) {
      RunArchiveOptimizeWithBlockProgress(ops);
      return;
    }

    RunDefragWithBlockProgress();
  }

  private void RunDefragWithBlockProgress() {
    if (this._imagePath == null || this._defragmentable == null) return;

    var path = this._imagePath;
    var defragmentable = this._defragmentable;
    var mode = SelectedMode();
    var holeSize = mode == DefragMode.CarveHole ? ParseSize(HoleSizeBox.Text) : 0L;
    var holeAt = mode == DefragMode.CarveHole ? ParseHoleAt(HoleAtBox.Text) : -1L;
    var interleaveStride = ParseInterleaveStride(InterleaveStrideBox.Text);
    var metadataZone = SelectedMetadataZone();
    var layoutProfile = this._selectedLayoutProfile;
    var staged = UsesGenericStagedDefrag(defragmentable);
    var cancellationToken = BeginMaintenanceOperation("Defragment / re-layout", staged);

    Append($"=== {DateTime.Now:HH:mm:ss}  Defragmenting {Path.GetFileName(path)} ===");
    Append($"Mode: {mode}");
    if (staged)
      Append("Strategy: verified staged rebuild; original is unchanged until commit.");

    Progress.IsIndeterminate = false;
    Progress.Value = 0;

    Task.Run(() => {
      var sw = Stopwatch.StartNew();
      Exception? error = null;
      var cancelled = false;
      string? finalStatus = null;

      void OnProgress(DefragProgressEvent ev) {
        if (ev.Phase == "complete" && !string.IsNullOrWhiteSpace(ev.Status))
          finalStatus = ev.Status;
        Dispatcher.BeginInvoke(() => {
          if (ev.Phase == "committing") {
            this._maintenanceCommitStarted = true;
            if (this._maintenanceCancelButton != null) this._maintenanceCancelButton.IsEnabled = false;
          }
          if (ev.BlockMap != null) {
            BlockMap.BlockMap = ev.BlockMap;
            BlockMap.ImageSize = ev.ImageSize;
          } else if (ev.ImageSize > 0 && BlockMap.ImageSize <= 0) {
            BlockMap.ImageSize = ev.ImageSize;
          }
          BlockMap.ReadHead = ev.CurrentReadOffset;
          BlockMap.WriteHead = ev.CurrentWriteOffset;
          if (ev.Fraction >= 0) Progress.Value = Math.Clamp(ev.Fraction, 0, 1) * 100;
          if (!string.IsNullOrWhiteSpace(ev.Status) && LayoutStatusLbl != null)
            LayoutStatusLbl.Text = ev.Status;
        });
      }

      try {
        using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
        defragmentable.Defragment(stream, new DefragOptions {
          Mode = mode,
          HoleSize = holeSize,
          HoleAt = holeAt,
          InterleaveStride = interleaveStride,
          MetadataZonePlacement = metadataZone,
          LayoutTemplate = layoutProfile,
          OnProgress = OnProgress,
          CancellationToken = cancellationToken,
        });
      } catch (OperationCanceledException) {
        cancelled = true;
      } catch (Exception ex) {
        error = ex;
      }
      sw.Stop();

      Dispatcher.Invoke(() => {
        BlockMap.ReadHead = -1;
        BlockMap.WriteHead = -1;
        Progress.Value = 100;
        if (cancelled) {
          Append(staged
            ? $"CANCELLED ({sw.ElapsedMilliseconds} ms) — staged rebuild discarded; existing container unchanged."
            : $"CANCELLED ({sw.ElapsedMilliseconds} ms) — completed native moves remain in place.");
        } else if (error != null) {
          Append($"FAILED ({sw.ElapsedMilliseconds} ms): {error.GetType().Name}: {error.Message}");
        } else {
          Append($"OK ({sw.ElapsedMilliseconds} ms)");
          if (cancellationToken.IsCancellationRequested)
            Append("Cancellation was requested, but the native operation completed before reaching a cancellable boundary.");
          if (!string.IsNullOrWhiteSpace(finalStatus)) Append($"Status: {finalStatus}");
          NotifyMutated(path);
        }
        Append("");
        EndMaintenanceOperation();
      });
    });
  }

  private static bool UsesGenericStagedDefrag(IArchiveDefragmentable defragmentable) {
    var type = defragmentable.GetType();
    return type.GetMethod(nameof(IArchiveDefragmentable.Defragment), [typeof(Stream)]) == null
      && type.GetMethod(nameof(IArchiveDefragmentable.Defragment), [typeof(Stream), typeof(DefragOptions)]) == null;
  }

  private void RunArchiveOptimizeWithBlockProgress(IArchiveFormatOperations? ops) {
    if (this._imagePath == null || ops == null) return;
    if (this._isSevenZipFormat && SmartSolidRepackCheck?.IsChecked == true) {
      RunSmartSevenZipWithBlockProgress(ops);
      return;
    }

    var path = this._imagePath;
    var formatId = this._formatId;
    if (formatId is "DoubleSpace" or "DriveSpace" or "DriveSpace3") {
      // CVF has a dedicated per-cluster optimizer. Keep that implementation;
      // its writer already chooses compress/store per cluster.
      OnRunCvfOptimize(path, formatId);
      return;
    }

    var originalSize = new FileInfo(path).Length;
    var tempOut = path + ".opt.tmp";
    AtomicFileWriter.TryDelete(tempOut);
    var cancellationToken = BeginMaintenanceOperation("Archive optimize / repack", staged: true);
    SetStagedArchiveMap(path, ops, originalSize);

    Append($"=== {DateTime.Now:HH:mm:ss}  Optimizing {Path.GetFileName(path)} ===");
    Append("Staged rebuild: green head = projected source consumption; orange head = staged-target bytes written.");
    Progress.IsIndeterminate = false;
    Progress.Value = 0;

    Task.Run(() => {
      var sw = Stopwatch.StartNew();
      Exception? error = null;
      var cancelled = false;
      long newSize = originalSize;
      var entriesOptimized = 0;

      try {
        var worker = Task.Run(() => ArchiveOperations.Optimize(path, tempOut, password: null));
        while (!worker.Wait(100)) {
          var stagedBytes = FindStagedOutputLength(tempOut);
          var fraction = originalSize > 0
            ? Math.Clamp((double)stagedBytes / originalSize, 0, 0.95)
            : 0;
          Dispatcher.BeginInvoke(() => {
            Progress.Value = fraction * 100;
            var displaySize = Math.Max(1L, BlockMap.ImageSize > 0 ? BlockMap.ImageSize : originalSize);
            BlockMap.ReadHead = Math.Clamp((long)(fraction * displaySize), 0, displaySize - 1);
            BlockMap.WriteHead = stagedBytes > 0 ? Math.Clamp(stagedBytes, 0, displaySize - 1) : -1;
            if (LayoutStatusLbl != null)
              LayoutStatusLbl.Text = cancellationToken.IsCancellationRequested
                ? "Cancellation pending — current codec unit will finish, then the staged target is discarded."
                : $"Rebuilding staged target — {FormatSize(stagedBytes)} written; original unchanged.";
          });
        }

        var result = worker.GetAwaiter().GetResult();
        newSize = result.OptimizedSize;
        entriesOptimized = result.EntriesOptimized;
        if (cancellationToken.IsCancellationRequested) {
          cancelled = true;
        } else {
          Dispatcher.Invoke(() => {
            this._maintenanceCommitStarted = true;
            if (this._maintenanceCancelButton != null) this._maintenanceCancelButton.IsEnabled = false;
            Progress.Value = 99;
            if (LayoutStatusLbl != null)
              LayoutStatusLbl.Text = "Staged target complete — committing; cancellation is no longer safe.";
          });
          AtomicFileWriter.ReplaceTarget(tempOut, path);
        }
      } catch (OperationCanceledException) {
        cancelled = true;
      } catch (Exception ex) {
        error = ex;
      } finally {
        AtomicFileWriter.TryDelete(tempOut);
      }
      sw.Stop();

      Dispatcher.Invoke(() => {
        BlockMap.ReadHead = -1;
        BlockMap.WriteHead = -1;
        Progress.Value = 100;
        if (cancelled) {
          Append($"CANCELLED ({sw.ElapsedMilliseconds} ms) — staged target discarded; existing archive unchanged.");
        } else if (error != null) {
          Append($"FAILED ({sw.ElapsedMilliseconds} ms): {error.GetType().Name}: {error.Message}");
        } else {
          var delta = newSize - originalSize;
          var pct = originalSize > 0 ? (double)delta / originalSize * 100 : 0;
          Append($"OK ({sw.ElapsedMilliseconds} ms) — {entriesOptimized} entries re-encoded");
          Append($"Archive size: {originalSize:N0} -> {newSize:N0} bytes (Δ {delta:+#,#;-#,#;0}, {pct:+0.0;-0.0;0.0}%)");
          NotifyMutated(path);
        }
        Append("");
        EndMaintenanceOperation();
      });
    });
  }

  private void RunSmartSevenZipWithBlockProgress(IArchiveFormatOperations ops) {
    if (this._imagePath == null) return;
    var path = this._imagePath;
    var originalSize = new FileInfo(path).Length;
    var cancellationToken = BeginMaintenanceOperation("7z solid-block re-group", staged: true);
    SetStagedArchiveMap(path, ops, originalSize);

    Append($"=== {DateTime.Now:HH:mm:ss}  Smart solid-block repack: {Path.GetFileName(path)} ===");
    Append("All candidate layouts are staged. Cancel discards them and leaves the current 7z untouched.");
    Progress.IsIndeterminate = false;
    Progress.Value = 0;

    Task.Run(() => {
      var sw = Stopwatch.StartNew();
      Exception? error = null;
      var cancelled = false;
      FileFormat.SevenZip.SolidBlockOptimizer.OptimizeResult? result = null;

      try {
        using var fs = File.OpenRead(path);
        result = FileFormat.SevenZip.SolidBlockOptimizer.Optimize(
          fs,
          maxTrials: 5,
          onProgress: (index, total, name) => Dispatcher.BeginInvoke(() =>
            Append($"  Trying strategy {index + 1}/{total}: {name}...")),
          onDetailedProgress: detail => Dispatcher.BeginInvoke(() => {
            var displaySize = Math.Max(1L, BlockMap.ImageSize > 0 ? BlockMap.ImageSize : originalSize);
            double fraction;
            switch (detail.Phase) {
              case "extracting":
                fraction = 0.30 * detail.BytesDone / Math.Max(1.0, detail.BytesTotal);
                BlockMap.ReadHead = Math.Clamp((long)(detail.BytesDone / Math.Max(1.0, detail.BytesTotal) * displaySize), 0, displaySize - 1);
                BlockMap.WriteHead = -1;
                break;
              case "strategy":
                fraction = 0.30 + 0.10 * detail.Current / Math.Max(1.0, detail.Total);
                BlockMap.ReadHead = -1;
                break;
              case "building":
                fraction = 0.40 + 0.55 * detail.Current / Math.Max(1.0, detail.Total);
                BlockMap.ReadHead = -1;
                BlockMap.WriteHead = Math.Clamp((long)(detail.Current / Math.Max(1.0, detail.Total) * displaySize), 0, displaySize - 1);
                break;
              default:
                fraction = 0;
                break;
            }
            Progress.Value = Math.Clamp(fraction, 0, 0.95) * 100;
            if (LayoutStatusLbl != null)
              LayoutStatusLbl.Text = detail.Phase switch {
                "extracting" => $"Reading source entry: {detail.Name}",
                "strategy" => $"Planning solid grouping: {detail.Name}",
                "building" => $"Building staged solid candidate: {detail.Name}",
                _ => "Staged 7z regrouping",
              };
          }),
          cancellationToken: cancellationToken);
      } catch (OperationCanceledException) {
        cancelled = true;
      } catch (Exception ex) {
        error = ex;
      }
      sw.Stop();

      Dispatcher.Invoke(() => {
        BlockMap.ReadHead = -1;
        BlockMap.WriteHead = -1;
        Progress.Value = 100;

        if (cancelled) {
          Append($"CANCELLED ({sw.ElapsedMilliseconds} ms) — candidate regroup discarded; existing 7z unchanged.");
        } else if (error != null) {
          Append($"FAILED ({sw.ElapsedMilliseconds} ms): {error.GetType().Name}: {error.Message}");
        } else if (result != null) {
          foreach (var trial in result.Trials)
            Append($"    {trial.StrategyName}: {FormatSize(trial.OutputSize)} ({trial.Elapsed.TotalMilliseconds:F0} ms)");

          var newSize = (long)result.Data.Length;
          var delta = newSize - originalSize;
          var pct = originalSize > 0 ? (double)delta / originalSize * 100 : 0;
          Append($"  Winner: {result.WinningStrategy}");
          Append($"Archive size: {originalSize:N0} -> {newSize:N0} bytes ({delta:+#,#;-#,#;0}, {pct:+0.0;-0.0;0.0}%)");

          if (newSize < originalSize) {
            this._maintenanceCommitStarted = true;
            if (this._maintenanceCancelButton != null) this._maintenanceCancelButton.IsEnabled = false;
            if (LayoutStatusLbl != null)
              LayoutStatusLbl.Text = "Winning staged layout selected — committing; cancellation disabled.";
            try {
              AtomicFileWriter.WriteAllBytesAtomic(path, result.Data);
              Append("Optimized archive written.");
              NotifyMutated(path);
            } catch (Exception writeError) {
              Append($"FAILED while committing winner: {writeError.Message}");
            }
          } else {
            Append("No strategy improved on the original size; archive unchanged.");
          }
        }
        Append("");
        EndMaintenanceOperation();
      });
    });
  }

  private void SetStagedArchiveMap(string path, IArchiveFormatOperations ops, long displaySize) {
    try {
      using var stream = File.OpenRead(path);
      var entries = ops.List(stream, null).Where(e => !e.IsDirectory).ToArray();
      if (entries.Length == 0) return;
      var weights = entries.Select(e => Math.Max(1L, e.OriginalSize)).ToArray();
      var totalWeight = Math.Max(1L, weights.Sum());
      var size = Math.Max(1L, displaySize);
      var map = new List<DefragBlockInfo>(entries.Length);
      long cumulative = 0;
      long offset = 0;
      for (var i = 0; i < entries.Length; i++) {
        cumulative += weights[i];
        var end = i == entries.Length - 1
          ? size
          : (long)((double)cumulative / totalWeight * size);
        end = Math.Clamp(end, offset, size);
        if (end > offset)
          map.Add(new DefragBlockInfo(offset, end - offset, DefragBlockKind.Used,
            entries[i].Name, ClassifyByMethod(entries[i].Method)));
        offset = end;
      }
      BlockMap.BlockMap = map;
      BlockMap.ImageSize = size;
      BlockMap.ReadHead = 0;
      BlockMap.WriteHead = -1;
      if (LayoutStatusLbl != null)
        LayoutStatusLbl.Text = "Staged rebuild projection — colored target blocks; green source head / orange target head use separate byte-spaces.";
    } catch {
      // The existing preview remains usable if a synthetic target map cannot be built.
    }
  }

  private static long FindStagedOutputLength(string target) {
    try {
      var best = File.Exists(target) ? new FileInfo(target).Length : 0L;
      var directory = Path.GetDirectoryName(target);
      if (string.IsNullOrEmpty(directory)) directory = Directory.GetCurrentDirectory();
      var pattern = Path.GetFileName(target) + ".tmp.*";
      foreach (var candidate in Directory.EnumerateFiles(directory, pattern))
        best = Math.Max(best, new FileInfo(candidate).Length);
      return best;
    } catch {
      return 0;
    }
  }
}
