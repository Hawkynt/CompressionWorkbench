#pragma warning disable CS1591

namespace FileSystem.Refs;

internal sealed record RefsNativeCowCommitResult(
  ulong CheckpointPhysicalLcn,
  ulong CheckpointClock,
  ulong MLogLsn,
  ulong MLogOldestLsn,
  IReadOnlyDictionary<int, byte[]> PublishedRoots,
  IReadOnlyList<RefsCowAllocatorPublication> AllocatorPublications);

/// <summary>
/// Low-level native ReFS publication coordinator. Callers first build immutable
/// replacement trees through <see cref="Tree"/> and register their checkpoint
/// roots. Commit then performs the only safe publication order:
///
///  1. rebuild allocator roots so every transaction CoW page, including the
///     allocator trees themselves, is already marked allocated;
///  2. prepare the alternate checkpoint with all replacement roots;
///  3. durably append the caller-supplied native redo transaction to MLog;
///  4. publish the alternate checkpoint and verify it wins bootstrap selection.
///
/// This class intentionally does not synthesize opcode payloads. Higher-level
/// namespace/stream operations may use it only after their exact redo payload
/// grammar is implemented for the target ReFS version.
/// </summary>
internal sealed class RefsNativeCowPublisher {
  private readonly Stream _image;
  private readonly RefsMetadataReader _metadata;
  private readonly RefsCowPageStore _store;
  private readonly RefsCowBTree _tree;
  private readonly Dictionary<int, byte[]> _rootReplacements = [];
  private bool _committed;

  public RefsNativeCowPublisher(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("Native ReFS publication requires a readable, writable, seekable stream.", nameof(image));
    this._image = image;
    this._metadata = RefsMetadataReader.Open(image);
    this._store = new RefsCowPageStore(image, this._metadata);
    this._tree = new RefsCowBTree(image, this._metadata, this._store);
  }

  public RefsMetadataReader Metadata => this._metadata;
  public RefsCowPageStore Store => this._store;
  public RefsCowBTree Tree => this._tree;

  public void RegisterRoot(int rootIndex, RefsCowTreeResult replacement) {
    ArgumentNullException.ThrowIfNull(replacement);
    if (this._committed) throw new InvalidOperationException("ReFS native transaction has already been committed.");
    if (rootIndex is < 0 or >= 13) throw new ArgumentOutOfRangeException(nameof(rootIndex));
    if (rootIndex is 1 or 2 or 12)
      throw new InvalidOperationException(
        "ReFS allocator roots are transaction-owned and are rebuilt automatically from CoW reservations.");
    this.RegisterRoot(rootIndex, replacement.RootReference);
  }

  public void RegisterRoot(int rootIndex, ReadOnlySpan<byte> pageReference) {
    if (this._committed) throw new InvalidOperationException("ReFS native transaction has already been committed.");
    if (rootIndex is < 0 or >= 13) throw new ArgumentOutOfRangeException(nameof(rootIndex));
    if (rootIndex is 1 or 2 or 12)
      throw new InvalidOperationException(
        "ReFS allocator roots are transaction-owned and are rebuilt automatically from CoW reservations.");
    if (pageReference.Length != this._metadata.PageReferenceSize)
      throw new ArgumentException(
        $"ReFS root page reference must be exactly {this._metadata.PageReferenceSize} bytes.", nameof(pageReference));
    var parsed = RefsPageReference.Parse(pageReference);
    if (parsed.Lcns.Count == 0) throw new InvalidDataException("Replacement ReFS root has no page address.");
    this._rootReplacements[rootIndex] = pageReference.ToArray();
  }

  public RefsNativeCowCommitResult Commit(IReadOnlyList<RefsRedoRecord> redoRecords) {
    if (this._committed) throw new InvalidOperationException("ReFS native transaction has already been committed.");
    RefsNativeCowValidation.ValidateRedoTransaction(redoRecords);
    if (this._rootReplacements.Count == 0)
      throw new InvalidOperationException("ReFS native transaction has no replacement metadata root to publish.");

    var allocatorPublications = new List<RefsCowAllocatorPublication>();
    var allocatorPublisher = new RefsCowAllocatorPublisher(this._image, this._metadata, this._store);
    foreach (var tier in Enum.GetValues<RefsAllocatorTier>()) {
      if (this._store.GetReservedClusters(tier).Count == 0) continue;
      var publication = allocatorPublisher.Publish(tier);
      allocatorPublications.Add(publication);
      this._rootReplacements[publication.RootIndex] = publication.Tree.RootReference.ToArray();
    }

    RequireAllocatorCoverage(allocatorPublications, this._store);
    var frozenReservationCount = this._store.ReservedClusters.Count;

    var checkpoint = new RefsCheckpointCommitter(this._image);
    var prepared = checkpoint.PrepareNext();
    checkpoint.SetRootReferences(prepared, this._rootReplacements);

    // MLog is durable before CHKP. If anything fails before the checkpoint write,
    // all replacement pages are unreachable and the old checkpoint remains the
    // authoritative tree. Recovery may observe the redo transaction but never a
    // half-published root graph.
    var log = new RefsMLogWriter(this._image, this._metadata);
    var append = log.Append(redoRecords);
    this._image.Flush();

    if (this._store.ReservedClusters.Count != frozenReservationCount)
      throw new InvalidOperationException(
        "ReFS CoW reservations changed after allocator publication; refusing checkpoint commit.");

    var oldestLsn = log.State.ActiveControl.OldestLsn;
    checkpoint.Commit(
      prepared,
      oldestRequiredLsn: oldestLsn,
      allocatorChanged: allocatorPublications.Count > 0);

    var verify = RefsMetadataReader.Open(this._image);
    foreach (var (rootIndex, rawReference) in this._rootReplacements) {
      var expected = RefsPageReference.Parse(rawReference);
      var actual = verify.Roots[rootIndex];
      if (!expected.Lcns.SequenceEqual(actual.Lcns))
        throw new IOException($"ReFS checkpoint committed but root #{rootIndex} does not match the replacement tree.");
    }

    this._committed = true;
    return new RefsNativeCowCommitResult(
      verify.ActiveCheckpointLcn,
      verify.ActiveCheckpointClock,
      append.Lsn,
      oldestLsn,
      this._rootReplacements.ToDictionary(item => item.Key, item => item.Value.ToArray()),
      allocatorPublications);
  }

  private static void RequireAllocatorCoverage(
      IReadOnlyList<RefsCowAllocatorPublication> publications,
      RefsCowPageStore store) {
    foreach (var tier in Enum.GetValues<RefsAllocatorTier>()) {
      var reserved = store.GetReservedClusters(tier);
      if (reserved.Count == 0) continue;
      var publication = publications.SingleOrDefault(item => item.Tier == tier)
        ?? throw new InvalidOperationException($"ReFS {tier} CoW reservations have no replacement allocator root.");
      var accounted = publication.AccountedPhysicalClusters.ToHashSet();
      foreach (var lcn in reserved)
        if (!accounted.Contains(lcn))
          throw new InvalidOperationException(
            $"ReFS {tier} CoW reservation PLCN 0x{lcn:X} is absent from the replacement allocator accounting set.");
    }
  }
}
