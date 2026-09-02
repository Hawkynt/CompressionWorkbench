#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using Compression.Registry;

namespace FileSystem.RomFs;

/// <summary>
/// Moves whole ROMFS records and rewrites whatever pointed at them.
/// </summary>
/// <remarks>
/// <para>A ROMFS file's bytes sit immediately behind its header at an offset
/// nothing records — the header's position is the data's position. So the unit
/// that can move is the record, not the payload, and moving one means finding
/// every field that named its old home: the previous record's <c>next</c>, or a
/// directory's <c>spec</c>, or the <c>spec</c> of a "." or ".." that pointed at
/// the chain it opens.</para>
///
/// <para>Every record's header sums to zero on its own, and the superblock's
/// checksum covers the first 512 bytes of the image, so both are taken again
/// wherever a pointer is rewritten. This used to be an empty method that
/// accepted every move and recorded none of them.</para>
/// </remarks>
public sealed class RomFsBlockMover : IFilesystemBlockMover {

  /// <summary>Bytes the superblock's checksum covers.</summary>
  private const int ChecksumSpan = 512;

  /// <summary>A field naming a record, and the record whose checksum covers it.</summary>
  /// <param name="At">Where the four-byte field sits.</param>
  /// <param name="Owner">Header offset of the record holding the field.</param>
  /// <param name="KeepsTypeBits">Whether the low nibble is a type rather than part of the offset.</param>
  private readonly record struct Site(long At, long Owner, bool KeepsTypeBits);

  private readonly List<Site> _sites = [];
  private readonly Dictionary<long, int> _headerLengthOf = [];
  private long _firstRecord = -1;
  private long _imageLength;

  /// <summary>Reads every chain once and notes each field that names a record.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    this._sites.Clear();
    this._headerLengthOf.Clear();
    this._imageLength = image.Length;

    var data = new ImageAccessor(image, leaveOpen: true);
    if (data.Length < 16) return;
    this._firstRecord = RomFsRecordMap.FirstRecord(data);

    foreach (var record in RomFsRecordMap.Records(image)) {
      var nextAndType = BinaryPrimitives.ReadUInt32BigEndian(data.Read(record.Offset, 4));
      var spec = BinaryPrimitives.ReadUInt32BigEndian(data.Read(record.Offset + 4, 4));

      var nameEnd = record.Offset + 16;
      while (nameEnd < data.Length && data.ReadByte(nameEnd) != 0) ++nameEnd;
      this._headerLengthOf[record.Offset] = 16 + Align16((int)(nameEnd - record.Offset - 16 + 1));

      if ((nextAndType & 0xFFFFFFF0u) != 0)
        this._sites.Add(new Site(record.Offset, record.Offset, KeepsTypeBits: true));

      // spec names a record only for a directory or a hard link; for a device
      // it is a pair of numbers and for a file it is nothing at all.
      if (record.Type is 0 or 1 && spec != 0)
        this._sites.Add(new Site(record.Offset + 4, record.Offset, KeepsTypeBits: false));
    }
  }

  /// <summary>A record starts on a sixteen-byte boundary, and so does its data.</summary>
  public int BlockSize => 16;

  /// <summary>First byte a record may occupy: past the superblock.</summary>
  public long FirstDataByte => this._firstRecord < 0 ? 0 : this._firstRecord;

  /// <summary>
  /// Each call rewrites the fields naming the run it is given and leaves the
  /// rest of the volume alone.
  /// </summary>
  public bool RepointsRunsIndependently => true;

  /// <summary>
  /// A record may be held outside the image while the rest of the layout moves,
  /// which is what lets a full volume be rearranged at all.
  /// </summary>
  public bool SupportsHeldRuns => true;

  /// <inheritdoc />
  /// <summary>
  /// Performs the move extent operation.
  /// </summary>
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
  /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    ArgumentNullException.ThrowIfNull(image);
    if (this._firstRecord < 0) this.Init(image);
    if (oldOffset == newOffset) return;

    if (newOffset % 16 != 0)
      throw new NotSupportedException(
        $"ROMFS: {newOffset} is not on a sixteen-byte boundary, which is all a record pointer holds.");
    if (oldOffset == this._firstRecord)
      throw new NotSupportedException(
        "ROMFS: the record just past the superblock is where the kernel looks for the root " +
        "inode, so it cannot move.");

    var moved = false;
    for (var i = 0; i < this._sites.Count; ++i) {
      var site = this._sites[i];
      var value = ReadUInt32(image, site.At);
      var target = site.KeepsTypeBits ? value & 0xFFFFFFF0u : value;
      if (target != (uint)oldOffset) continue;

      var replacement = site.KeepsTypeBits
        ? ((uint)newOffset & 0xFFFFFFF0u) | (value & 0x0Fu)
        : (uint)newOffset;
      WriteUInt32(image, site.At, replacement);
      this.PatchHeaderChecksum(image, site.Owner);
      moved = true;
    }

    if (!moved)
      throw new InvalidOperationException(
        $"ROMFS: nothing names the record at {oldOffset}, so '{fileName}' cannot be repointed.");

    // The fields inside the record moved with it.
    for (var i = 0; i < this._sites.Count; ++i) {
      var site = this._sites[i];
      if (site.Owner != oldOffset) continue;
      this._sites[i] = site with { At = site.At - oldOffset + newOffset, Owner = newOffset };
    }

    if (this._headerLengthOf.Remove(oldOffset, out var headerLength))
      this._headerLengthOf[newOffset] = headerLength;

    this.PatchSuperblockChecksum(image);
    image.Flush();
  }

  /// <summary>
  /// Writes the size the superblock records and takes its checksum again.
  /// </summary>
  /// <remarks>
  /// Called once a layout pass has finished. The recorded size bounds what a
  /// reader will look at, so a pass that moves a record towards the end of the
  /// image has to carry it along.
  /// </remarks>
  public void SettleSuperblock(Stream image, long contentEnd) {
    ArgumentNullException.ThrowIfNull(image);
    var recorded = ReadUInt32(image, 8);
    var wanted = (uint)Math.Min(image.Length, Align16(contentEnd));
    if (wanted > recorded) WriteUInt32(image, 8, wanted);

    this.PatchSuperblockChecksum(image);
    image.Flush();
  }

  /// <summary>Rewrites a record header's checksum so the header sums to zero.</summary>
  private void PatchHeaderChecksum(Stream image, long recordOffset) {
    if (!this._headerLengthOf.TryGetValue(recordOffset, out var headerLength)) return;
    if (recordOffset + headerLength > this._imageLength) return;

    WriteUInt32(image, recordOffset + 12, 0);
    var header = new byte[headerLength];
    image.Position = recordOffset;
    image.ReadExactly(header);

    uint sum = 0;
    for (var i = 0; i < headerLength; i += 4)
      sum += BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(i));
    WriteUInt32(image, recordOffset + 12, (uint)(-(int)sum));
  }

  /// <summary>Rewrites the superblock checksum, which covers the head of the image.</summary>
  private void PatchSuperblockChecksum(Stream image) {
    var covered = (int)(Math.Min(ChecksumSpan, image.Length) & ~3L);
    if (covered <= 16) return;

    WriteUInt32(image, 12, 0);
    var head = new byte[covered];
    image.Position = 0;
    image.ReadExactly(head);

    uint sum = 0;
    for (var i = 0; i < covered; i += 4)
      sum += BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(i));
    WriteUInt32(image, 12, (uint)(-(int)sum));
  }

  private static uint ReadUInt32(Stream image, long at) {
    Span<byte> field = stackalloc byte[4];
    image.Position = at;
    image.ReadExactly(field);
    return BinaryPrimitives.ReadUInt32BigEndian(field);
  }

  private static void WriteUInt32(Stream image, long at, uint value) {
    Span<byte> field = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(field, value);
    image.Position = at;
    image.Write(field);
  }

  private static int Align16(int length) => (length + 15) & ~15;

  private static long Align16(long length) => (length + 15) & ~15;
}
