using System.Diagnostics;
using Compression.Lib;
using Compression.Registry;
using Compression.Registry.Layout;

namespace Compression.NativeUI.Maintenance;

/// <summary>A live update from a running operation, for the block view.</summary>
internal sealed record MaintenanceProgress(
  IReadOnlyList<DefragBlockInfo>? Map,
  long ImageSize,
  long ReadHead,
  long WriteHead,
  double Fraction,
  string? Status,
  bool CommitStarted = false);

internal enum MaintenanceOutcomeKind { Succeeded, Refused, Failed, Cancelled }

/// <summary>How an operation ended, in one line, and whether the image on disk changed.</summary>
internal sealed record MaintenanceOutcome(MaintenanceOutcomeKind Kind, string Summary, bool Mutated);

/// <summary>
/// The Defragment tab without the tab: one target image, what its format supports, what the user
/// picked, and the code that runs the picked operation off the UI thread. The view binds to this and
/// marshals the callbacks; nothing here touches a control.
/// </summary>
internal sealed class MaintenancePresenter {
  /// <summary>The operations in the order the ribbon offers them; the first supported one is preselected.</summary>
  public static readonly MaintenanceVerb[] VerbOrder = [
    MaintenanceVerb.Defragment, MaintenanceVerb.Optimize, MaintenanceVerb.Shrink, MaintenanceVerb.Compact,
    MaintenanceVerb.WipeEmpty, MaintenanceVerb.Purge, MaintenanceVerb.Scramble,
  ];

  public MaintenancePresenter(string imagePath, string formatId) {
    this.ImagePath = imagePath;
    this.FormatId = formatId;
    this.Capabilities = TargetCapabilities.For(formatId);
    this.Verb = VerbOrder.Cast<MaintenanceVerb?>().FirstOrDefault(v => this.Capabilities.Verb(v!.Value).Supported);
    this.Strategy = Enum.GetValues<DefragStrategy>().FirstOrDefault(s => this.Capabilities.Strategy(s).Supported);
    this.OptimizeMethod = this.Capabilities.OptimizeMethods.FirstOrDefault();
  }

  public string ImagePath { get; }
  public string FormatId { get; }
  public TargetCapabilities Capabilities { get; }
  public IArchiveFormatOperations? Operations => FormatRegistry.GetArchiveOps(this.FormatId);

  // ── what the user picked ────────────────────────────────────────────────────────────────────

  public MaintenanceVerb? Verb { get; set; }
  public DefragStrategy Strategy { get; set; }
  public bool PackAtEnd { get; set; }
  public string InterleaveText { get; set; } = "1";
  public MetadataZone MetadataZone { get; set; } = MetadataZone.Unchanged;
  public LayoutTemplate? LayoutProfile { get; set; }
  public string HoleSizeText { get; set; } = "64m";
  public string HoleAtText { get; set; } = "auto";
  public string SeedText { get; set; } = "1";
  public OptimizeMethod OptimizeMethod { get; set; }

  /// <summary>Whether the picked defragmentation moves extents, as against sorting directory entries.</summary>
  public bool MovesExtents => this.Strategy != DefragStrategy.SortEntries;

  public bool IsRunning { get; private set; }

  /// <summary>
  /// Why Start cannot run as things stand, or null when it can: no operation, one the format does
  /// not support, a strategy it does not support, or a value that does not read.
  /// </summary>
  public string? StartBlocker {
    get {
      if (this.IsRunning) return "An operation is already running.";
      if (this.Verb is not { } verb) return "Pick an operation.";
      if (this.Capabilities.Verb(verb) is { Supported: false } refused) return refused.Reason;
      if (verb == MaintenanceVerb.Defragment && this.Capabilities.Strategy(this.Strategy) is { Supported: false } strategy) return strategy.Reason;
      if (verb == MaintenanceVerb.Optimize && !this.Capabilities.OptimizeMethods.Contains(this.OptimizeMethod))
        return $"This format does not offer {TargetCapabilities.Describe(this.OptimizeMethod)}.";
      return this.Validate().FirstOrDefault();
    }
  }

  /// <summary>Every value that would have to change before the picked operation could run.</summary>
  public IReadOnlyList<string> Validate() {
    var errors = new List<string>();
    switch (this.Verb) {
      case MaintenanceVerb.Defragment:
        if (this.MovesExtents && this.Capabilities.Interleave.Supported && MaintenanceInput.ParseInterleave(this.InterleaveText).Error is { } stride) errors.Add(stride);
        if (this.Strategy == DefragStrategy.CarveHole) {
          if (MaintenanceInput.ParseSize(this.HoleSizeText).Error is { } size) errors.Add(size);
          if (MaintenanceInput.ParseHoleAt(this.HoleAtText).Error is { } at) errors.Add(at);
        }

        break;
      case MaintenanceVerb.Scramble:
        if (MaintenanceInput.ParseSeed(this.SeedText).Error is { } seed) errors.Add(seed);
        break;
    }

    return errors;
  }

  /// <summary>Whether the picked operation can be stopped part-way: only an extent move can.</summary>
  public bool CanCancel => this.Verb == MaintenanceVerb.Defragment && this.MovesExtents;

  /// <summary>
  /// Whether stopping discards a staged copy (the original untouched) rather than leaving in-place
  /// moves where they got to. Extent moves are always in place.
  /// </summary>
  public bool IsStaged => false;

  /// <summary>The engine mode the picked strategy stands for.</summary>
  public DefragMode EngineMode => this.Strategy switch {
    DefragStrategy.Defrag => DefragMode.FillHolesLazy,
    DefragStrategy.Reorder => DefragMode.AscendingOrder,
    DefragStrategy.CarveHole => DefragMode.CarveHole,
    // Pack at the end when asked to, or when that is the only packing the format knows.
    _ => this.PackAtEnd && this.Capabilities.PackAtEnd.Supported || !this.Capabilities.PackAtStart.Supported
      ? DefragMode.ConsolidateAtEnd
      : DefragMode.ConsolidateAtStart,
  };

  /// <summary>
  /// The options a defragmentation runs with. Only what the format supports is set: an option the
  /// ribbon disabled stays at its default rather than reaching the engine to be refused.
  /// </summary>
  public DefragOptions BuildDefragOptions(Action<DefragProgressEvent>? onProgress = null, CancellationToken token = default) {
    var mode = this.EngineMode;
    var carve = mode == DefragMode.CarveHole;
    return new() {
      Mode = mode,
      HoleSize = carve ? MaintenanceInput.ParseSize(this.HoleSizeText).Value : 0,
      HoleAt = carve ? MaintenanceInput.ParseHoleAt(this.HoleAtText).Value : -1,
      InterleaveStride = this.Capabilities.Interleave.Supported && MaintenanceInput.ParseInterleave(this.InterleaveText) is { IsValid: true } stride ? stride.Value : 1,
      MetadataZonePlacement = this.Capabilities.MetadataZone.Supported ? this.MetadataZone : MetadataZone.Unchanged,
      LayoutTemplate = this.Capabilities.LayoutProfile.Supported ? this.LayoutProfile : null,
      OnProgress = onProgress,
      CancellationToken = token,
    };
  }

  /// <summary>
  /// The question to ask before running, for the operations that destroy or deliberately degrade
  /// something; null for those that only improve the image or leave it as it was.
  /// </summary>
  public string? ConfirmationFor() {
    var file = Path.GetFileName(this.ImagePath);
    switch (this.Verb) {
      case MaintenanceVerb.Purge:
        var count = 0;
        try {
          using var stream = File.OpenRead(this.ImagePath);
          count = this.Operations?.List(stream, password: null).Count(e => !e.IsDirectory) ?? 0;
        } catch {
          // The count is for the question only; the operation itself reports any failure.
        }

        return $"Erase ALL {count} file(s) from {file}?\n\nThis leaves a valid but empty container. The data cannot be recovered.";
      case MaintenanceVerb.Scramble:
        return $"Fragment {file} on purpose?\n\nEvery file's blocks are scattered across the volume. The contents are preserved exactly "
             + "-- only the layout changes, and Defragment undoes it -- but this is a testing tool, not a repair.";
      default:
        return null;
    }
  }

  /// <summary>Reads the image's layout for the block view.</summary>
  public BlockMapSnapshot Analyze() => BlockMapSnapshot.Read(this.ImagePath, this.Operations);

  // ── running ─────────────────────────────────────────────────────────────────────────────────

  /// <summary>
  /// Runs the picked operation on a worker thread. <paramref name="progress"/> and
  /// <paramref name="log"/> are called from that thread; the caller marshals them. A format refusing
  /// the request (<see cref="NotSupportedException"/>) ends as <see cref="MaintenanceOutcomeKind.Refused"/>
  /// with the image as it was.
  /// </summary>
  public Task<MaintenanceOutcome> RunAsync(Action<MaintenanceProgress> progress, Action<string> log, CancellationToken token = default) {
    if (this.StartBlocker is { } blocker) return Task.FromResult(new MaintenanceOutcome(MaintenanceOutcomeKind.Refused, blocker, Mutated: false));

    var verb = this.Verb!.Value;
    var path = this.ImagePath;
    this.IsRunning = true;
    log($"=== {DateTime.Now:HH:mm:ss}  {Label(verb)} {Path.GetFileName(path)} ===");

    // Everything the worker needs is read here, on the caller's thread, so a later ribbon change
    // cannot alter a run already under way.
    var defragOptions = verb == MaintenanceVerb.Defragment && this.MovesExtents ? this.BuildDefragOptions(ev => progress(FromEvent(ev)), token) : null;
    var seed = MaintenanceInput.ParseSeed(this.SeedText).Value;
    var method = this.OptimizeMethod;

    return Task.Run(() => {
      var clock = Stopwatch.StartNew();
      MaintenanceOutcome outcome;
      try {
        outcome = verb switch {
          MaintenanceVerb.Defragment when defragOptions is null => this.RunSortEntries(path),
          MaintenanceVerb.Defragment => this.RunDefragment(path, defragOptions, log),
          MaintenanceVerb.Optimize => RunOptimize(path, method),
          MaintenanceVerb.Shrink => this.RunShrink(path),
          MaintenanceVerb.WipeEmpty => this.RunWipe(path),
          MaintenanceVerb.Purge => this.RunPurge(path),
          MaintenanceVerb.Compact => RunCompact(path, log),
          MaintenanceVerb.Scramble => this.RunScramble(path, seed, progress),
          _ => new(MaintenanceOutcomeKind.Refused, $"{verb} is not an operation.", false),
        };
      } catch (OperationCanceledException) {
        outcome = this.IsStaged
          ? new(MaintenanceOutcomeKind.Cancelled, "Cancelled — the staged copy was discarded; the image is unchanged.", false)
          : new(MaintenanceOutcomeKind.Cancelled, "Cancelled — moves already completed stay where they are; the contents are unchanged.", true);
      } catch (NotSupportedException ex) {
        outcome = new(MaintenanceOutcomeKind.Refused, ex.Message, false);
      } catch (Exception ex) {
        outcome = new(MaintenanceOutcomeKind.Failed, $"{ex.GetType().Name}: {ex.Message}", false);
      } finally {
        this.IsRunning = false;
      }

      log($"{outcome.Kind.ToString().ToUpperInvariant()} ({clock.ElapsedMilliseconds} ms) — {outcome.Summary}");
      return outcome;
    }, CancellationToken.None);
  }

  internal static string Label(MaintenanceVerb verb) => verb switch {
    MaintenanceVerb.WipeEmpty => "Clearing free space in",
    MaintenanceVerb.Defragment => "Defragmenting",
    MaintenanceVerb.Optimize => "Optimizing",
    MaintenanceVerb.Shrink => "Shrinking",
    MaintenanceVerb.Purge => "Purging",
    MaintenanceVerb.Compact => "Compacting",
    MaintenanceVerb.Scramble => "Scrambling",
    _ => verb.ToString(),
  };

  private static MaintenanceProgress FromEvent(DefragProgressEvent ev)
    => new(ev.BlockMap, ev.ImageSize, ev.CurrentReadOffset, ev.CurrentWriteOffset, ev.Fraction, ev.Status, CommitStarted: ev.Phase == "committing");

  private MaintenanceOutcome RunDefragment(string path, DefragOptions options, Action<string> log) {
    if (TargetCapabilities.Carrier(this.FormatId, MaintenanceCapability.DefragmentExtents) is not IArchiveDefragmentable defragmentable)
      throw new NotSupportedException($"{this.FormatId} does not move extents in place.");

    log($"Mode: {options.Mode}{(options.InterleaveStride > 1 ? $", interleave {options.InterleaveStride}" : "")}"
      + $"{(options.MetadataZonePlacement != MetadataZone.Unchanged ? $", metadata {options.MetadataZonePlacement}" : "")}"
      + $"{(options.LayoutTemplate is { } t ? $", profile {t.Name}" : "")}"
      + $"{(options.Mode == DefragMode.CarveHole ? $", hole {options.HoleSize:N0} bytes at {(options.HoleAt < 0 ? "auto" : options.HoleAt.ToString("N0"))}" : "")}");

    string? finalStatus = null;
    var reporting = options with {
      OnProgress = ev => {
        if (ev.Phase == "complete" && !string.IsNullOrWhiteSpace(ev.Status)) finalStatus = ev.Status;
        options.OnProgress?.Invoke(ev);
      },
    };

    using (var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite))
      defragmentable.Defragment(stream, reporting);

    return new(MaintenanceOutcomeKind.Succeeded, finalStatus ?? "Defragmented; size and contents unchanged.", true);
  }

  private MaintenanceOutcome RunSortEntries(string path) {
    var before = new FileInfo(path).Length;
    MaintenanceOperations.SortDirectoryEntries(path);
    return new(MaintenanceOutcomeKind.Succeeded, $"Every directory is in name order; {MaintenanceInput.FormatSize(before)}, no data moved.", true);
  }

  /// <summary>Optimizes through the registry's staged, verified file operation; the original is replaced atomically.</summary>
  private static MaintenanceOutcome RunOptimize(string path, OptimizeMethod method) {
    var result = method switch {
      OptimizeMethod.Compress => MaintenanceOperations.Compress(path, path),
      OptimizeMethod.Repack => MaintenanceOperations.Repack(path, path),
      _ => MaintenanceOperations.Canonicalize(path, path),
    };

    return new(MaintenanceOutcomeKind.Succeeded,
      result.Changed ? $"{method}: {SizeChange(result.OriginalSize, result.NewSize)}" : $"{method}: nothing to gain; the file is unchanged.",
      result.Changed);
  }

  private MaintenanceOutcome RunShrink(string path) {
    if (TargetCapabilities.Carrier(this.FormatId, MaintenanceCapability.Shrink) is not IArchiveShrinkable shrinkable)
      throw new NotSupportedException($"{this.FormatId} cannot shrink losslessly.");

    // Shrink into a staged file, then swap it in, so a crash mid-write cannot corrupt the source.
    var originalSize = new FileInfo(path).Length;
    var staged = path + ".shrink.tmp";
    try {
      using (var input = File.OpenRead(path))
      using (var output = File.Create(staged))
        shrinkable.Shrink(input, output);

      var newSize = new FileInfo(staged).Length;
      if (newSize >= originalSize)
        return new(MaintenanceOutcomeKind.Succeeded, "Already compact; nothing to trim.", false);

      AtomicFileWriter.ReplaceTarget(staged, path);
      return new(MaintenanceOutcomeKind.Succeeded, SizeChange(originalSize, newSize), true);
    } finally {
      AtomicFileWriter.TryDelete(staged);
    }
  }

  private MaintenanceOutcome RunWipe(string path) {
    var ops = TargetCapabilities.Carrier(this.FormatId, MaintenanceCapability.WipeUnused);
    var totalUnused = -1L;
    long wiped;
    using (var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite)) {
      switch (ops) {
        // Every extent and layout map is a wiper too; the interface default wipes through the map.
        case IWipeEmpty wiper:
          // The wiper reports only the bytes it had to overwrite; on a mostly empty image that and the
          // free space diverge sharply, so the total is worked out first where the layout allows.
          if (ops is IFilesystemExtentMap fsMap) totalUnused = UnusedSpaceWiper.ComputeUnusedBytes(fsMap.EnumerateExtents(stream), stream.Length);
          else if (ops is IArchiveLayoutMap arMap) totalUnused = UnusedSpaceWiper.ComputeUnusedBytes(arMap.EnumerateLayout(stream), stream.Length);
          stream.Position = 0;
          wiped = wiper.WipeUnusedSpace(stream);
          break;
        default:
          throw new NotSupportedException($"{this.FormatId} exposes no layout to tell free space from live data.");
      }
    }

    var summary = totalUnused >= 0
      ? $"{MaintenanceInput.FormatSize(totalUnused)} unused; {MaintenanceInput.FormatSize(wiped)} newly zeroed, {MaintenanceInput.FormatSize(Math.Max(0, totalUnused - wiped))} already zero."
      : $"{MaintenanceInput.FormatSize(wiped)} zeroed.";
    return new(MaintenanceOutcomeKind.Succeeded, summary, wiped > 0);
  }

  private MaintenanceOutcome RunPurge(string path) {
    if (TargetCapabilities.Carrier(this.FormatId, MaintenanceCapability.Purge) is not IArchivePurgeable purgeable)
      throw new NotSupportedException($"{this.FormatId} has no valid empty state to purge to.");

    var originalSize = new FileInfo(path).Length;
    using (var image = File.Open(path, FileMode.Open, FileAccess.ReadWrite))
      purgeable.Purge(image);
    return new(MaintenanceOutcomeKind.Succeeded, $"Every file erased; {SizeChange(originalSize, new FileInfo(path).Length)}", true);
  }

  private static MaintenanceOutcome RunCompact(string path, Action<string> log) {
    var result = CompactOperation.Compact(path, new CompactOperation.CompactOptions { Log = line => log("  " + line) });
    var steps = result.StepsRun.Count > 0 ? string.Join(", ", result.StepsRun) : "none";
    return new(MaintenanceOutcomeKind.Succeeded, $"steps: {steps}; {SizeChange(result.OriginalSize, result.NewSize)}", result.StepsRun.Count > 0);
  }

  private MaintenanceOutcome RunScramble(string path, int seed, Action<MaintenanceProgress> progress) {
    if (this.Operations is not IFilesystemScrambleable scrambler) throw new NotSupportedException($"{this.FormatId} cannot scatter a volume in place.");

    using (var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite))
      scrambler.Scramble(stream, new ScrambleOptions { Seed = seed, OnProgress = ev => progress(FromEvent(ev)) });
    return new(MaintenanceOutcomeKind.Succeeded, $"seed {seed}: the volume is fragmented; Defragment puts it back.", true);
  }

  private static string SizeChange(long before, long after) {
    var pct = before > 0 ? 100.0 * (after - before) / before : 0;
    return $"{MaintenanceInput.FormatSize(before)} → {MaintenanceInput.FormatSize(after)} ({pct:+0.0;-0.0;0.0}%)";
  }
}
