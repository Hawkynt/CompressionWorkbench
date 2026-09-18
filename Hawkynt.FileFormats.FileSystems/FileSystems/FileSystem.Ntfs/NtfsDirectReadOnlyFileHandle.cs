#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Ntfs;

/// <summary>
/// Positional read-only handle over a validated NTFS $DATA layout. It reads the
/// requested range and nothing else: resident bytes are copied from the FILE
/// record, non-resident ranges are read from the clusters their runs name, and
/// holes — sparse runs and everything past the initialized length — are answered
/// with zeroes without touching the image at all.
/// </summary>
/// <remarks>
/// This replaces spooling the whole decoded stream per open for every profile
/// the scanner can map. LZNT1-compressed streams are not one of those yet: a
/// compression unit has to be decompressed as a whole, so random access needs a
/// unit cache rather than a run walk, and they keep the decoded-stream fallback.
/// </remarks>
internal sealed class NtfsDirectReadOnlyFileHandle : IFilesystemFileHandle {
  private readonly Stream _image;
  private readonly object _ioGate;
  private readonly NtfsMountedDataLayout _layout;
  private readonly int _clusterSize;
  private bool _disposed;

  public NtfsDirectReadOnlyFileHandle(
      Stream image,
      object ioGate,
      FilesystemNodeId nodeId,
      NtfsMountedDataLayout layout,
      int clusterSize) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(ioGate);
    ArgumentNullException.ThrowIfNull(layout);
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("NTFS positional reads require a readable, seekable image.", nameof(image));
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clusterSize);

    _image = image;
    _ioGate = ioGate;
    NodeId = nodeId;
    _layout = layout;
    _clusterSize = clusterSize;
  }

  public FilesystemNodeId NodeId { get; }

  public long Length {
    get {
      ThrowIfDisposed();
      return _layout.DataLength;
    }
  }

  public int Read(long offset, Span<byte> destination) {
    ThrowIfDisposed();
    ArgumentOutOfRangeException.ThrowIfNegative(offset);
    if (destination.IsEmpty || offset >= _layout.DataLength) return 0;

    // Everything the file logically covers is answered; the parts no cluster
    // backs stay at the zero this clear establishes.
    var count = checked((int)Math.Min(destination.Length, _layout.DataLength - offset));
    var result = destination[..count];
    result.Clear();

    // Past the initialized length a file reads as zeroes even where clusters are
    // allocated: those clusters still hold whatever the volume last put there.
    var initialized = Math.Min((long)count, _layout.InitializedLength - offset);
    if (initialized <= 0) return count;
    var initializedCount = checked((int)initialized);

    if (_layout.ResidentData is { } resident) {
      resident.AsSpan(checked((int)offset), initializedCount).CopyTo(result);
      return count;
    }

    var requestEnd = checked(offset + initializedCount);
    foreach (var run in _layout.Runs) {
      var runStart = checked(run.Vcn * (long)_clusterSize);
      if (runStart >= requestEnd) break;
      var runEnd = checked(runStart + run.ClusterCount * (long)_clusterSize);
      if (runEnd <= offset || run.Sparse) continue;

      var overlapStart = Math.Max(runStart, offset);
      var overlapLength = checked((int)(Math.Min(runEnd, requestEnd) - overlapStart));
      if (overlapLength <= 0) continue;

      ReadExactlyAt(
        checked(run.Lcn * (long)_clusterSize + (overlapStart - runStart)),
        result.Slice(checked((int)(overlapStart - offset)), overlapLength));
    }

    return count;
  }

  public void Write(long offset, ReadOnlySpan<byte> source)
    => throw new NotSupportedException("The NTFS mounted session is read-only.");

  public void SetLength(long length)
    => throw new NotSupportedException("The NTFS mounted session is read-only.");

  public void Flush() => ThrowIfDisposed();

  public void Dispose() => _disposed = true;

  private void ReadExactlyAt(long offset, Span<byte> destination) {
    lock (_ioGate) {
      if (offset < 0 || offset > _image.Length - destination.Length)
        throw new InvalidDataException("NTFS data run points outside the backing image.");
      _image.Position = offset;
      _image.ReadExactly(destination);
    }
  }

  private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

/// <summary>
/// What a mounted NTFS file's unnamed $DATA stream is made of, as the identity
/// scanner proved it: its logical length, how much of that was ever written, and
/// either the resident bytes or the cluster runs that hold them.
/// </summary>
internal sealed record NtfsMountedDataLayout(
  long DataLength,
  long InitializedLength,
  byte[]? ResidentData,
  NtfsMountedDataRun[] Runs);

/// <summary>One VCN→LCN mapping of a non-resident stream; a sparse run is a hole.</summary>
internal readonly record struct NtfsMountedDataRun(
  long Vcn,
  long Lcn,
  long ClusterCount,
  bool Sparse);
