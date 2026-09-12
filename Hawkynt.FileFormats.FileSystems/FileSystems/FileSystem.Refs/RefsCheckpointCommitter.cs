#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Refs;

internal readonly record struct RefsPreparedCheckpoint(
  ulong SourceLcn,
  ulong TargetLcn,
  ulong Clock,
  byte[] Bytes);

/// <summary>
/// Implements the ReFS dual-CHKP atomic publication primitive. It does not make
/// metadata mutations CoW by itself; callers must first build an immutable new
/// root graph and durably write any required MLog record. Publication then
/// happens by writing the non-active checkpoint slot with clock+1 and flushing.
/// </summary>
internal sealed class RefsCheckpointCommitter {
  private readonly Stream _image;
  private readonly RefsBootstrapState _bootstrap;

  public RefsCheckpointCommitter(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("ReFS checkpoint commits require a readable, writable, seekable image.", nameof(image));
    this._image = image;
    this._bootstrap = RefsBootstrapState.Open(image);
  }

  /// <summary>
  /// Clones the currently active CHKP into the other SUPB-advertised checkpoint
  /// slot and advances its virtual clock. The returned bytes are not written;
  /// a native transaction can patch root references and oldest-LSN first.
  /// </summary>
  public RefsPreparedCheckpoint PrepareNext() {
    var active = this._bootstrap.Metadata.ActiveCheckpointLcn;
    var source = this._bootstrap.ReadCheckpoint(active);
    if (!source.AsSpan(0, 4).SequenceEqual("CHKP"u8))
      throw new InvalidDataException("Active ReFS checkpoint does not contain CHKP.");

    var target = this.SelectAlternateSlot(active);
    var candidate = source.ToArray();
    var clock = checked(Math.Max(
      this._bootstrap.Metadata.ActiveCheckpointClock,
      ReadCheckpointClock(this.TryReadCheckpoint(target))) + 1);

    StampLocation(candidate, target);
    StampVirtualClock(candidate, clock);
    return new RefsPreparedCheckpoint(active, target, clock, candidate);
  }

  /// <summary>
  /// Replaces one root reference inside an unpublished prepared checkpoint.
  /// Both legacy/direct and v3.14 indirect CHKP layouts ultimately expose a
  /// 4-byte offset table whose entries point at the actual page references.
  /// </summary>
  public void SetRootReference(
      RefsPreparedCheckpoint prepared,
      int rootIndex,
      ReadOnlySpan<byte> pageReference) {
    if (prepared.Bytes == null || !prepared.Bytes.AsSpan(0, Math.Min(4, prepared.Bytes.Length)).SequenceEqual("CHKP"u8))
      throw new ArgumentException("Prepared ReFS checkpoint does not contain CHKP.", nameof(prepared));
    var referenceSize = checked((int)this._bootstrap.Metadata.PageReferenceSize);
    if (pageReference.Length != referenceSize)
      throw new ArgumentException(
        $"ReFS root page reference must be exactly {referenceSize} bytes.", nameof(pageReference));
    var descriptorOffset = GetRootDescriptorOffset(prepared.Bytes, rootIndex, referenceSize);
    pageReference.CopyTo(prepared.Bytes.AsSpan(descriptorOffset, referenceSize));
  }

  public void SetRootReferences(
      RefsPreparedCheckpoint prepared,
      IReadOnlyDictionary<int, byte[]> rootReferences) {
    ArgumentNullException.ThrowIfNull(rootReferences);
    foreach (var (rootIndex, reference) in rootReferences.OrderBy(p => p.Key))
      this.SetRootReference(prepared, rootIndex, reference);
  }

  /// <summary>
  /// Publishes a prepared checkpoint. The only bytes written here are the
  /// alternate CHKP slot; SUPB already advertises both slots, so the higher
  /// valid clock becomes active without mutating the superblock.
  /// </summary>
  public void Commit(
      RefsPreparedCheckpoint prepared,
      ulong? oldestRequiredLsn = null,
      bool allocatorChanged = false) {
    if (prepared.SourceLcn != this._bootstrap.Metadata.ActiveCheckpointLcn)
      throw new InvalidOperationException("ReFS active checkpoint changed after this transaction was prepared.");
    if (!this._bootstrap.CheckpointLcns.Contains(prepared.TargetLcn))
      throw new InvalidOperationException("Prepared ReFS checkpoint target is not advertised by the winning SUPB.");
    if (prepared.TargetLcn == prepared.SourceLcn)
      throw new InvalidOperationException("ReFS alternate-checkpoint commit cannot overwrite the active CHKP slot.");
    if (prepared.Bytes.Length != this._bootstrap.Metadata.PageSize)
      throw new InvalidOperationException("Prepared ReFS checkpoint has the wrong metadata-page size.");
    if (!prepared.Bytes.AsSpan(0, 4).SequenceEqual("CHKP"u8))
      throw new InvalidDataException("Prepared ReFS checkpoint does not contain CHKP.");

    var bytes = prepared.Bytes.ToArray();
    StampLocation(bytes, prepared.TargetLcn);
    StampVirtualClock(bytes, prepared.Clock);
    if (allocatorChanged)
      BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x68, 8), prepared.Clock);
    else if (BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x68, 8)) > prepared.Clock)
      throw new InvalidDataException("ReFS allocator clock cannot exceed the checkpoint virtual clock.");
    if (oldestRequiredLsn.HasValue)
      BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x70, 8), oldestRequiredLsn.Value);

    var selfOffset = checked((int)ReadU32(bytes, 0x58));
    var selfLength = checked((int)ReadU32(bytes, 0x5C));
    RefsChecksum.RefreshSelfChecksum(
      bytes,
      this._bootstrap.Metadata.ClusterSize,
      selfOffset,
      selfLength);

    this._bootstrap.WriteCheckpoint(prepared.TargetLcn, bytes);
    this._image.Flush();

    // Re-open through the normal bootstrap chooser. If the new slot is not the
    // winning valid checkpoint, publication did not happen and must not be
    // reported as committed.
    var verify = RefsMetadataReader.Open(this._image);
    if (verify.ActiveCheckpointLcn != prepared.TargetLcn || verify.ActiveCheckpointClock != prepared.Clock)
      throw new IOException(
        $"ReFS alternate checkpoint 0x{prepared.TargetLcn:X} was written but did not become the active clock {prepared.Clock}.");
  }

  internal static int GetRootDescriptorOffset(
      ReadOnlySpan<byte> checkpoint,
      int rootIndex,
      int referenceSize) {
    if (checkpoint.Length < 0x98 || !checkpoint[..4].SequenceEqual("CHKP"u8))
      throw new InvalidDataException("ReFS checkpoint root list is unavailable.");
    if (rootIndex < 0) throw new ArgumentOutOfRangeException(nameof(rootIndex));
    if (referenceSize <= 0) throw new ArgumentOutOfRangeException(nameof(referenceSize));

    var rootCount = checked((int)ReadU32(checkpoint, 0x90));
    if (rootIndex >= rootCount)
      throw new ArgumentOutOfRangeException(nameof(rootIndex), $"ReFS checkpoint exposes only {rootCount} roots.");
    var flags = ReadU32(checkpoint, 0x78);
    var offsetListBase = (flags & 0x0200) != 0
      ? checked((int)ReadU32(checkpoint, 0x94))
      : 0x94;
    var listBytes = checked(rootCount * 4);
    if (offsetListBase < 0x94 || offsetListBase > checkpoint.Length - listBytes)
      throw new InvalidDataException("ReFS checkpoint root offset list lies outside the page.");

    var descriptorOffset = checked((int)ReadU32(checkpoint, offsetListBase + rootIndex * 4));
    if (descriptorOffset <= 0 || descriptorOffset > checkpoint.Length - referenceSize)
      throw new InvalidDataException($"ReFS checkpoint root #{rootIndex} descriptor lies outside the page.");
    return descriptorOffset;
  }

  private ulong SelectAlternateSlot(ulong active) {
    foreach (var candidate in this._bootstrap.CheckpointLcns) {
      if (candidate == active) continue;
      var bytes = this.TryReadCheckpoint(candidate);
      if (bytes != null && bytes.AsSpan(0, 4).SequenceEqual("CHKP"u8)) return candidate;
    }
    throw new NotSupportedException(
      "ReFS native atomic commit requires two SUPB-advertised checkpoint slots; no alternate CHKP is available.");
  }

  private byte[]? TryReadCheckpoint(ulong lcn) {
    try { return this._bootstrap.ReadCheckpoint(lcn); }
    catch (InvalidDataException) { return null; }
  }

  private static ulong ReadCheckpointClock(byte[]? checkpoint)
    => checkpoint is { Length: >= 0x68 } && checkpoint.AsSpan(0, 4).SequenceEqual("CHKP"u8)
      ? BinaryPrimitives.ReadUInt64LittleEndian(checkpoint.AsSpan(0x60, 8))
      : 0;

  private static void StampVirtualClock(Span<byte> checkpoint, ulong clock) {
    if (checkpoint.Length < 0x68)
      throw new InvalidDataException("ReFS checkpoint is shorter than its virtual-clock fields.");
    BinaryPrimitives.WriteUInt64LittleEndian(checkpoint.Slice(0x10, 8), clock);
    BinaryPrimitives.WriteUInt64LittleEndian(checkpoint.Slice(0x60, 8), clock);
  }

  private static void StampLocation(Span<byte> checkpoint, ulong lcn) {
    if (checkpoint.Length < 0x40)
      throw new InvalidDataException("ReFS checkpoint is shorter than its page header.");
    BinaryPrimitives.WriteUInt64LittleEndian(checkpoint.Slice(0x20, 8), lcn);
    for (var i = 1; i < 4; ++i)
      BinaryPrimitives.WriteUInt64LittleEndian(checkpoint.Slice(0x20 + i * 8, 8), 0UL);
  }

  private static uint ReadU32(ReadOnlySpan<byte> bytes, int offset)
    => offset >= 0 && offset + 4 <= bytes.Length
      ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4))
      : throw new InvalidDataException("ReFS checkpoint field lies outside CHKP.");
}
