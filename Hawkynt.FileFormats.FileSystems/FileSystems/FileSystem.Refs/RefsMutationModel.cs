#pragma warning disable CS1591

namespace FileSystem.Refs;

/// <summary>
/// Describes the durability semantics under which a ReFS mutation is executed.
/// Parsers, allocators and namespace editors must not assume a particular mode;
/// that keeps them reusable by both the offline image editor and a future
/// mounted filesystem driver.
/// </summary>
internal enum RefsMutationMode {
  /// <summary>
  /// The volume is quiescent and unmounted. Live metadata may be updated in its
  /// current page after the replacement bytes and checksum path are known. This
  /// is the maintenance/defrag backend used by CompressionWorkbench.
  /// </summary>
  OfflineQuiescent,

  /// <summary>
  /// Native ReFS semantics: allocate replacement metadata pages, emit redo
  /// records, propagate CoW parents and commit by alternating CHKP. No live
  /// metadata page is overwritten before the checkpoint commit.
  /// </summary>
  NativeCow,
}

/// <summary>
/// Transaction boundary shared by higher-level ReFS mutations. The offline
/// backend exists now; the native backend intentionally throws until MLog/CoW
/// emission is complete rather than silently degrading to direct writes.
/// </summary>
internal interface IRefsMutationTransaction : IDisposable {
  RefsMutationMode Mode { get; }
  Stream Image { get; }
  void Flush();
  void Commit();
}

internal static class RefsMutationTransactions {
  public static IRefsMutationTransaction Begin(Stream image, RefsMutationMode mode)
    => mode switch {
      RefsMutationMode.OfflineQuiescent => new OfflineTransaction(image),
      RefsMutationMode.NativeCow => throw new NotSupportedException(
        "Native ReFS CoW/MLog transactions are not implemented yet; refusing to expose offline direct-write semantics as a mounted-driver transaction."),
      _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

  private sealed class OfflineTransaction : IRefsMutationTransaction {
    private bool _committed;

    public OfflineTransaction(Stream image) {
      ArgumentNullException.ThrowIfNull(image);
      if (!image.CanRead || !image.CanWrite || !image.CanSeek)
        throw new ArgumentException("A ReFS offline transaction requires a readable, writable, seekable stream.", nameof(image));
      this.Image = image;
    }

    public RefsMutationMode Mode => RefsMutationMode.OfflineQuiescent;
    public Stream Image { get; }

    public void Flush() => this.Image.Flush();

    public void Commit() {
      this.Image.Flush();
      this._committed = true;
    }

    public void Dispose() {
      // The stream belongs to the caller. There is no rollback after direct
      // writes; fail-closed writers perform allocation/repoint ordering so a
      // pre-commit exception leaves either the old state reachable or leaked
      // allocation, never deliberately frees live data.
      if (!this._committed) this.Image.Flush();
    }
  }
}
