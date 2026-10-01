using System.Collections.Concurrent;
using Compression.Lib;
using Compression.Registry;
using Compression.Registry.Layout;

namespace Compression.NativeUI.Maintenance;

/// <summary>One answer to "can this target do that?": yes or no, and the sentence that says why.</summary>
internal readonly record struct Capability(bool Supported, string Reason) {
  public static Capability Yes(string detail) => new(true, detail);
  public static Capability No(string reason) => new(false, reason);
}

/// <summary>
/// What one format offers the Defragment tab: which operations, which defragmentation strategies and
/// which layout options. The ribbon enables exactly what this says and nothing else, so an option is
/// never offered only to be ignored or refused.
/// </summary>
/// <remarks>
/// This is the single place that turns the registry's capability interfaces into those answers.
/// When the registry grows a first-class capability model, this file is the one that changes.
/// </remarks>
internal sealed record MaintenanceCapabilities(
  string FormatId,
  IReadOnlyDictionary<MaintenanceVerb, Capability> Verbs,
  IReadOnlyDictionary<DefragStrategy, Capability> Strategies,
  Capability PackAtStart,
  Capability PackAtEnd,
  Capability Interleave,
  Capability MetadataZone,
  Capability LayoutProfile,
  Capability ChunkPlacement,
  Capability MinimalGeometry,
  OptimizeRoute OptimizeRoute,
  bool StagedDefragment) {

  private static readonly ConcurrentDictionary<string, MaintenanceCapabilities> Cache = new(StringComparer.Ordinal);

  /// <summary>Whether any operation at all can run on the target.</summary>
  public bool AnySupported => this.Verbs.Values.Any(v => v.Supported);

  public Capability Verb(MaintenanceVerb verb) => this.Verbs.TryGetValue(verb, out var c) ? c : Capability.No("Not offered.");
  public Capability Strategy(DefragStrategy strategy) => this.Strategies.TryGetValue(strategy, out var c) ? c : Capability.No("Not offered.");

  /// <summary>
  /// The capabilities of <paramref name="formatId"/>. Answers depend on the descriptor alone, so they
  /// are worked out once per format and shared.
  /// </summary>
  public static MaintenanceCapabilities For(string formatId) {
    ArgumentNullException.ThrowIfNull(formatId);
    return Cache.GetOrAdd(formatId, Evaluate);
  }

  private static MaintenanceCapabilities Evaluate(string formatId) {
    var ops = FormatRegistry.GetArchiveOps(formatId);
    var descriptor = FormatRegistry.GetById(formatId);
    var name = descriptor?.DisplayName is { Length: > 0 } display ? display : formatId;

    var defragmentable = ops as IArchiveDefragmentable;
    var modes = defragmentable is null ? [] : ProbeModes(defragmentable);
    var ownOptionsPath = defragmentable is not null && DeclaresOwnOptionsPath(defragmentable);

    var route = OptimizeRouteOf(formatId, ops);
    var verbs = new Dictionary<MaintenanceVerb, Capability> {
      [MaintenanceVerb.Defragment] = modes.Count > 0
        ? Capability.Yes($"Lays {name} out again; size and contents unchanged.")
        : Capability.No($"{name} has no lossless defragmenter."),
      [MaintenanceVerb.Optimize] = route switch {
        OptimizeRoute.FileInternal => Capability.Yes("Reorders the chunks inside the file (e.g. fast-start)."),
        OptimizeRoute.CompressedVolume => Capability.Yes("Re-encodes every cluster, keeping whichever of compressed or stored is smaller."),
        OptimizeRoute.SolidBlocks => Capability.Yes("Tries five solid-block groupings and keeps the smallest; the original stays until the winner is committed."),
        OptimizeRoute.Reencode => Capability.Yes("Re-encodes with the strongest settings into a staged copy, then replaces the original."),
        _ => Capability.No(defragmentable is not null
          ? $"{name} has no optimizer; Defragment is its layout operation."
          : $"{name} has no optimizer."),
      },
      [MaintenanceVerb.Shrink] = formatId is "Fat" or "Ext" or "Ext1" or "Vhd" || ops is IArchiveShrinkable
        ? Capability.Yes("Minimises the stored footprint, keeping every file.")
        : Capability.No($"{name} cannot shrink."),
      [MaintenanceVerb.Purge] = ops is IArchiveModifiable
        ? Capability.Yes("Erases every file, leaving a valid empty container. Asks first.")
        : Capability.No($"{name} cannot remove entries, so it cannot be purged."),
      [MaintenanceVerb.WipeEmpty] = ops is IWipeEmpty
        ? Capability.Yes("Zeroes unused space — free clusters, slack, dead bytes — and nothing else.")
        : Capability.No($"{name} exposes no layout to tell free space from live data."),
      [MaintenanceVerb.Compact] = ops is IArchiveDefragmentable or IArchiveShrinkable or IArchiveCreatable
        ? Capability.Yes("Defragment, optimize and shrink in one pass.")
        : Capability.No($"{name} offers none of the steps Compact is made of."),
      [MaintenanceVerb.Scramble] = ops is IFilesystemScrambleable
        ? Capability.Yes("Scatters every block on purpose, so Defragment has something to undo. Contents preserved; asks first.")
        : Capability.No($"{name} cannot scatter a volume in place."),
    };

    var start = modes.Contains(DefragMode.ConsolidateAtStart);
    var end = modes.Contains(DefragMode.ConsolidateAtEnd);
    var strategies = new Dictionary<DefragStrategy, Capability> {
      [DefragStrategy.Consolidate] = start || end
        ? Capability.Yes("Packs every file at one end; free space ends up in one piece.")
        : Capability.No($"{name} cannot pack its files together."),
      [DefragStrategy.Defrag] = modes.Contains(DefragMode.FillHolesLazy)
        ? Capability.Yes("Fills existing holes from the tail, moving as few bytes as possible.")
        : Capability.No($"{name} cannot fill holes lazily."),
      [DefragStrategy.Reorder] = modes.Contains(DefragMode.AscendingOrder)
        ? Capability.Yes("Moves only what reads backwards, so every file's blocks ascend.")
        : Capability.No($"{name} cannot reorder blocks into ascending order."),
      // No descriptor offers a directory-entry sort through the registry yet, so the honest answer
      // for every format is no. It is still a capability answer: the day a descriptor gains it, this
      // is the line that turns it on.
      [DefragStrategy.SortEntries] = Capability.No($"{name} does not offer sorting directory entries."),
      [DefragStrategy.CarveHole] = modes.Contains(DefragMode.CarveHole)
        ? Capability.Yes("Reserves one contiguous free region of the size you choose.")
        : Capability.No($"{name} cannot carve a hole."),
    };

    var probeMode = modes.Count > 0 ? modes[0] : DefragMode.ConsolidateAtStart;
    Capability Option(string what, DefragOptions options) {
      if (defragmentable is null) return Capability.No($"{name} has no defragmenter.");
      if (!ownOptionsPath) return Capability.No($"{name} defragments by rebuilding, which ignores {what}.");
      return Accepts(defragmentable, options) ? Capability.Yes($"{name} honours {what}.") : Capability.No($"{name} refuses {what}.");
    }

    return new(
      formatId,
      verbs,
      strategies,
      PackAtStart: start ? Capability.Yes("Packs at the front; free space ends up after the last file.") : Capability.No($"{name} cannot pack at the start."),
      PackAtEnd: end ? Capability.Yes("Packs at the back instead; free space stays at the front.") : Capability.No($"{name} cannot pack at the end."),
      Interleave: Option("block interleave", new() { Mode = probeMode, InterleaveStride = 2 }),
      MetadataZone: Option("metadata placement", new() { Mode = probeMode, MetadataZonePlacement = Registry.MetadataZone.Front }),
      LayoutProfile: Option("layout profiles", new() { Mode = probeMode, LayoutTemplate = new LayoutTemplate { Name = "probe" } }),
      ChunkPlacement: ops is IFileInternalChunkMover
        ? Capability.Yes("Where metadata chunks go relative to the payload.")
        : Capability.No($"{name} has no chunk placement to choose."),
      MinimalGeometry: ops is IArchiveCreatable and IFormatOptionsSchema
        ? Capability.Yes("Rebuilds at the smallest geometry the format allows; the result may not be a standard image.")
        : Capability.No($"{name} has no geometry to minimise."),
      route,
      StagedDefragment: defragmentable is not null && !DeclaresOwnOptionsPath(defragmentable) && !DeclaresOwnPlainPath(defragmentable));
  }

  private static OptimizeRoute OptimizeRouteOf(string formatId, IArchiveFormatOperations? ops) {
    if (ops is IFileInternalChunkMover) return OptimizeRoute.FileInternal;
    if (formatId is "DoubleSpace" or "DriveSpace" or "DriveSpace3") return OptimizeRoute.CompressedVolume;
    if (formatId == "SevenZip") return OptimizeRoute.SolidBlocks;

    // ArchiveOperations.Optimize re-encodes these; anything else it copies through unchanged, which
    // is not an operation worth offering.
    if (formatId is "Zip" or "Gzip" or "Zlib" or "MacBinary") return OptimizeRoute.Reencode;
    return Enum.TryParse<FormatDetector.Format>(formatId, out var format) && FormatDetector.GetTarCompression(format).HasValue
      ? OptimizeRoute.Reencode
      : OptimizeRoute.None;
  }

  // ── probing ─────────────────────────────────────────────────────────────────────────────────

  /// <summary>The modes the descriptor accepts, in <see cref="DefragMode"/> order.</summary>
  private static List<DefragMode> ProbeModes(IArchiveDefragmentable defragmentable)
    => [.. Enum.GetValues<DefragMode>().Where(mode => Accepts(defragmentable, new() { Mode = mode }))];

  /// <summary>
  /// Asks the descriptor whether it takes <paramref name="options"/>, without touching any image.
  /// There is no way to ask directly, so it is handed a stream that throws on first touch: reaching
  /// the I/O means the request was accepted, while <see cref="NotSupportedException"/> before any I/O
  /// means it was refused — the guard every lossless defragmenter runs first.
  /// </summary>
  private static bool Accepts(IArchiveDefragmentable defragmentable, DefragOptions options) {
    try {
      using var probe = new ProbeStream();
      defragmentable.Defragment(probe, options);
      return true; // Unlikely: the probe should have been touched first.
    } catch (ProbeAbortException) {
      return true;
    } catch {
      // NotSupportedException is the refusal; anything else is not taken as a yes either.
      return false;
    }
  }

  /// <summary>
  /// Whether the descriptor implements the options overload itself. The interface default only
  /// knows <see cref="DefragMode.ConsolidateAtStart"/> and rebuilds, so it would accept layout options
  /// without applying them; only a descriptor's own implementation can be asked about those.
  /// </summary>
  private static bool DeclaresOwnOptionsPath(IArchiveDefragmentable defragmentable)
    => defragmentable.GetType().GetMethod(nameof(IArchiveDefragmentable.Defragment), [typeof(Stream), typeof(DefragOptions)]) is not null;

  private static bool DeclaresOwnPlainPath(IArchiveDefragmentable defragmentable)
    => defragmentable.GetType().GetMethod(nameof(IArchiveDefragmentable.Defragment), [typeof(Stream)]) is not null;

  private sealed class ProbeAbortException : Exception;

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
