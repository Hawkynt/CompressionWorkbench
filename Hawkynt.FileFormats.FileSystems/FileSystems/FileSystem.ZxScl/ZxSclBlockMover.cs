#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.ZxScl;

/// <summary>
/// Moves a whole payload inside an SCL container and puts the directory back
/// into the order the payloads ended up in.
/// </summary>
/// <remarks>
/// <para>A payload's position is written down nowhere: the reader starts a
/// cursor just past the directory and adds each entry's sector count to it. So
/// where a payload sits <em>is</em> which directory entry describes it, and the
/// only way to repoint one is to move the fourteen bytes that name it into the
/// matching place in the directory. That cannot be done one run at a time —
/// during a pass two payloads may briefly share an order — so it is done once,
/// afterwards, by <see cref="Settle" />.</para>
///
/// <para>What settling cannot fix is a layout the walk does not reach. The
/// payloads have to start where the directory ends and follow each other with
/// nothing in between; a pass that leaves a gap is refused there, and the
/// container is written out again instead.</para>
/// </remarks>
public sealed class ZxSclBlockMover : IFilesystemBlockMover {

  /// <summary>Each directory entry, kept by where its payload currently is.</summary>
  private readonly Dictionary<long, byte[]> _entryAt = [];

  private long _payloadStart;
  private long _checksumAt;
  private int _count;

  /// <summary>Reads the directory once and notes which entry describes each payload.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    var magic = new byte[ZxSclReader.Magic.Length + 1];
    image.Position = 0;
    image.ReadExactly(magic);
    if (!magic.AsSpan(0, ZxSclReader.Magic.Length).SequenceEqual(ZxSclReader.Magic))
      throw new InvalidDataException("SCL: the container is not one this reads.");

    this._count = magic[ZxSclRecordMap.CountOffset];
    this._payloadStart = ZxSclRecordMap.PayloadStart(this._count);
    this._checksumAt = image.Length - ZxSclRecordMap.ChecksumSize;

    this._entryAt.Clear();
    var cursor = this._payloadStart;
    for (var i = 0; i < this._count; ++i) {
      var entry = new byte[ZxSclReader.HeaderSize];
      image.Position = ZxSclRecordMap.CountOffset + 1 + (long)i * ZxSclReader.HeaderSize;
      image.ReadExactly(entry);

      var length = (long)entry[13] * ZxSclReader.SectorSize;
      if (length <= 0 || cursor + length > this._checksumAt)
        throw new InvalidDataException($"SCL: entry {i} reaches past the container.");

      this._entryAt[cursor] = entry;
      cursor += length;
    }
  }

  /// <summary>
  /// A byte. Payloads are sector multiples, but the directory in front of them
  /// is not, so where they start is not a multiple of anything.
  /// </summary>
  public int BlockSize => 1;

  /// <summary>First byte a payload may occupy: immediately past the directory.</summary>
  public long FirstDataByte => this._payloadStart;

  /// <summary>
  /// Each call takes note of one payload; the directory that follows from all
  /// of them is written once the pass is over.
  /// </summary>
  public bool RepointsRunsIndependently => true;

  /// <summary>
  /// A payload may be held outside the container while the rest of the layout
  /// moves, which is what lets a full one be rearranged at all.
  /// </summary>
  public bool SupportsHeldRuns => true;

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    if (length <= 0 || srcOffset == dstOffset) return;

    // Overlap-safe: a run shifted forward by less than its own length
    // overwrites its own tail, and copying that front to back reads bytes
    // the copy has already replaced.
    Compression.Core.DiskImage.ExtentCopy.Move(image, srcOffset, dstOffset, length);
    if (zeroSource)
      Compression.Core.DiskImage.ExtentCopy.Zero(image, srcOffset, length);
  }

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(fileName);
    if (this._entryAt.Count == 0) this.Init(image);
    if (oldOffset == newOffset) return;

    if (newOffset < this._payloadStart)
      throw new NotSupportedException(
        "SCL: a payload cannot start before the directory ends — the reader begins its cursor there " +
        "and has no way to be told otherwise.");

    if (newOffset + length > this._checksumAt)
      throw new NotSupportedException(
        "SCL: a payload cannot reach into the trailing checksum.");

    if (!this._entryAt.Remove(oldOffset, out var entry))
      throw new InvalidOperationException(
        $"SCL: the directory names no payload at {oldOffset}, so '{fileName}' cannot be repointed.");

    this._entryAt[newOffset] = entry;
  }

  /// <summary>
  /// Writes the directory in the order the payloads now lie in and sums the
  /// container again.
  /// </summary>
  /// <remarks>
  /// One run's old home is routinely another's new one, so this cannot happen
  /// while the payloads are still being moved: the order is only settled once
  /// all of them have landed.
  /// </remarks>
  /// <exception cref="NotSupportedException">
  /// The payloads no longer follow each other from the end of the directory,
  /// which is the one layout the reader's cursor can walk.
  /// </exception>
  public void Settle(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (this._entryAt.Count == 0) return;

    var directoryStart = ZxSclRecordMap.CountOffset + 1L;
    var cursor = this._payloadStart;
    var slot = 0;
    foreach (var (offset, entry) in this._entryAt.OrderBy(p => p.Key)) {
      if (offset != cursor)
        throw new NotSupportedException(
          $"SCL: a payload sits at {offset} where the walk arrives at {cursor}; the reader adds " +
          "lengths to a cursor and cannot step over a gap.");

      image.Position = directoryStart + (long)slot * ZxSclReader.HeaderSize;
      image.Write(entry);
      cursor += (long)entry[13] * ZxSclReader.SectorSize;
      ++slot;
    }

    this.WriteChecksum(image);
    image.Flush();
  }

  /// <summary>Sums every byte before the checksum, which is what the writer records.</summary>
  private void WriteChecksum(Stream image) {
    var sum = 0u;
    var buffer = new byte[64 * 1024];
    image.Position = 0;
    for (var remaining = this._checksumAt; remaining > 0;) {
      var wanted = (int)Math.Min(buffer.Length, remaining);
      image.ReadExactly(buffer, 0, wanted);
      for (var i = 0; i < wanted; ++i) sum += buffer[i];
      remaining -= wanted;
    }

    Span<byte> value = stackalloc byte[ZxSclRecordMap.ChecksumSize];
    BinaryPrimitives.WriteUInt32LittleEndian(value, sum);
    image.Position = this._checksumAt;
    image.Write(value);
  }
}
