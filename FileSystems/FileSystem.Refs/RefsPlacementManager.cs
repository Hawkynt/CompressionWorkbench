#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;

namespace FileSystem.Refs;

/// <summary>
/// ReFS-specific placement coordinator. ReFS has multiple address spaces and
/// allocator ownership classes, so filesystem-structure placement is planned by
/// ReFS itself while byte copying and ordinary file layout remain shared. Each
/// pass re-opens the live checkpoint and rebuilds the extent map.
/// </summary>
internal sealed class RefsPlacementManager {
  private readonly Stream _image;

  public RefsPlacementManager(Stream image) {
    this._image = image;
  }

  public int Execute(DefragOptions options) {
    if (!this._image.CanRead || !this._image.CanWrite || !this._image.CanSeek)
      throw new ArgumentException("ReFS placement requires a readable, writable, seekable image stream.", nameof(this._image));
    if (options.InterleaveStride is < 1 or > 256)
      throw new ArgumentOutOfRangeException(nameof(options.InterleaveStride));
    if (options.InterleaveStride > 1 && options.LayoutTemplate != null)
      throw new NotSupportedException(
        "A ReFS layout template and block interleave are two independent final-placement policies; apply them as separate operations.");

    using var transaction = RefsMutationTransactions.Begin(this._image, RefsMutationMode.OfflineQuiescent);
    var image = transaction.Image;
    var mover = new RefsBlockMover(image);
    mover.PrepareResidentFiles(image);
    transaction.Flush();

    var totalMoves = 0;
    var hasMetadataZone = options.MetadataZonePlacement != MetadataZone.Unchanged;
    var hasTemplate = options.LayoutTemplate != null;

    // A layout template is a global placement policy and therefore continues to
    // use the common planner. Plain metadata-zone placement is different: ReFS
    // must select targets from the owning Medium/Container/Small allocator tier
    // before ordinary file placement is planned around the resulting structure
    // reservations.
    if (hasTemplate) {
      var placementOptions = options with { InterleaveStride = 1 };
      totalMoves += ExecuteGeneralPass(image, placementOptions, moveMetadata: true);
      transaction.Flush();
    } else if (hasMetadataZone) {
      totalMoves += new RefsMetadataPlacementPlanner(image).Execute(options);
      transaction.Flush();
    }

    if (options.InterleaveStride > 1) {
      totalMoves += ExecuteInterleavePass(image, options);
    } else if (!hasTemplate) {
      // The metadata pass above intentionally moves only filesystem structures.
      // Re-scan and apply the requested data strategy around those new fixed
      // reservations. MetadataZone is cleared so the generic planner cannot
      // second-guess the tier-aware placement that has just been committed.
      var dataOptions = hasMetadataZone
        ? options with { MetadataZonePlacement = MetadataZone.Unchanged }
        : options;
      totalMoves += ExecuteGeneralPass(image, dataOptions, moveMetadata: false);
    }

    transaction.Commit();
    var finalMap = RefsExtentMap.Enumerate(image).Where(e => e.Length > 0).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, image.Length, finalMap,
      $"ReFS layout complete: {totalMoves:N0} physical move(s)."));
    return totalMoves;
  }

  private static int ExecuteGeneralPass(Stream image, DefragOptions options, bool moveMetadata) {
    var metadata = RefsMetadataReader.Open(image);
    var imageEnd = options.ImageEnd > 0 ? Math.Min(options.ImageEnd, image.Length) : image.Length;
    if (imageEnd <= 0) return 0;
    var extents = RefsExtentMap.Enumerate(image).Where(e => e.Length > 0 && e.Offset < imageEnd).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "scanning", 0, -1, -1, image.Length, extents,
      $"ReFS {metadata.Header.MajorVersion}.{metadata.Header.MinorVersion}: planning {(moveMetadata ? "data + metadata" : "data")} placement"));

    RefsMetadataMover? metadataMover = moveMetadata ? new RefsMetadataMover(image) : null;
    var moves = DefragPlanner.Plan(
      extents,
      dataOrigin: Math.Max(0, options.Origin),
      imageSize: imageEnd,
      clusterSize: metadata.ClusterSize,
      profile: options.Profile,
      mode: options.Mode,
      interleaveStride: 1,
      holeSize: options.HoleSize,
      holeAt: options.HoleAt,
      metadataZone: moveMetadata ? options.MetadataZonePlacement : MetadataZone.Unchanged,
      layoutTemplate: options.LayoutTemplate,
      movableMetadata: metadataMover?.RelocatableMetadata,
      allowMemoryStaging: true);

    if (moves.Count == 0) return 0;
    var mover = new RefsBlockMover(image);
    DefragPlannerExecutor.Execute(
      image,
      options,
      mover,
      moves,
      image.Length,
      reinitAfterMove: null,
      metadataMover: metadataMover);
    return moves.Count;
  }

  private static int ExecuteInterleavePass(Stream image, DefragOptions options) {
    var metadata = RefsMetadataReader.Open(image);
    var imageEnd = options.ImageEnd > 0 ? Math.Min(options.ImageEnd, image.Length) : image.Length;
    if (imageEnd <= 0) return 0;
    var extents = RefsExtentMap.Enumerate(image).Where(e => e.Length > 0 && e.Offset < imageEnd).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "scanning", 0, -1, -1, image.Length, extents,
      $"ReFS {metadata.Header.MajorVersion}.{metadata.Header.MinorVersion}: planning {options.InterleaveStride}:1 data interleave"));

    var mover = new RefsBlockMover(image);
    var runBudget = mover.GetMaximumExtentRuns(image);
    var moves = RefsInterleavePlanner.Plan(
      extents,
      metadata,
      imageEnd,
      options.Mode,
      options.InterleaveStride,
      runBudget);
    if (moves.Count == 0) return 0;

    DefragPlannerExecutor.Execute(
      image,
      options,
      mover,
      moves,
      image.Length,
      reinitAfterMove: null,
      metadataMover: null);
    return moves.Count;
  }
}
