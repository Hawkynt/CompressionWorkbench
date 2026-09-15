#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Ntfs;

/// <summary>
/// Positional read-only handle over a validated NTFS data layout. Resident bytes
/// are copied directly; non-resident runs are read from their physical clusters;
/// sparse and uninitialized ranges synthesize zeroes without touching backing data.
/// </summary>
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
    if (clusterSize <= 0)
      throw new ArgumentOutOfRangeException(nameof(clusterSize));
    if (layout.Compressed)
      throw new ArgumentException("Compressed NTFS data must use the LZNT1 fallback handle.", nameof(layout));

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
    if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
    if (destination.IsEmpty || offset >= _layout.DataLength) return 0;

    var count = checked((int)Math.Min(destination.Length, _layout.DataLength - offset));
    var result = destination[..count];
    result.Clear();

    var initializedRemaining = _layout.InitializedLength - offset;
    if (initializedRemaining <= 0)
      return count;
    var initializedCount = checked((int)Math.Min(count, initializedRemaining));

    if (_layout.ResidentData is { } resident) {
      if (offset > resident.LongLength - initializedCount)
        throw new InvalidDataException("NTFS resident data layout is shorter than its initialized range.");
      resident.AsSpan(checked((int)offset), initializedCount).CopyTo(result);
      return count;
    }

    var requestStart = offset;
    var requestEnd = checked(offset + initializedCount);
    foreach (var run in _layout.Runs) {
      var runStart = checked(run.Vcn * (long)_clusterSize);
      var runLength = checked(run.ClusterCount * (long)_clusterSize);
      var runEnd = checked(runStart + runLength);
      if (runEnd <= requestStart) continue;
      if (runStart >= requestEnd) break;

      var overlapStart = Math.Max(runStart, requestStart);
      var overlapEnd = Math.Min(runEnd, requestEnd);
      var overlapLength = checked((int)(overlapEnd - overlapStart));
      if (overlapLength <= 0 || run.Sparse) continue;

      var destinationOffset = checked((int)(overlapStart - requestStart));
      var withinRun = overlapStart - runStart;
      var physical = checked(run.Lcn * (long)_clusterSize + withinRun);
      ReadExactlyAt(physical, result.Slice(destinationOffset, overlapLength));
    }

    return count;
  }

  public void Write(long offset, ReadOnlySpan<byte> source)
    => throw new NotSupportedException("The NTFS mounted session is read-only.");

  public void SetLength(long length)
    => throw new NotSupportedException("The NTFS mounted session is read-only.");

  public void Flush() {
    ThrowIfDisposed();
  }

  public void Dispose() => _disposed = true;

  private void ReadExactlyAt(long offset, Span<byte> destination) {
    if (offset < 0 || offset > _image.Length - destination.Length)
      throw new InvalidDataException("NTFS data run points outside the backing image.");
    lock (_ioGate) {
      _image.Position = offset;
      _image.ReadExactly(destination);
    }
  }

  private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

internal sealed record NtfsMountedDataLayout(
  long DataLength,
  long InitializedLength,
  byte[]? ResidentData,
  NtfsMountedDataRun[] Runs,
  bool Compressed);

internal readonly record struct NtfsMountedDataRun(
  long Vcn,
  long Lcn,
  long ClusterCount,
  bool Sparse);
