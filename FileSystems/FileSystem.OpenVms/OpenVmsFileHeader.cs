#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.OpenVms;

/// <summary>
/// In-memory mirror of a Files-11 File Header (a single 512-byte block
/// inside INDEXF.SYS, anchored at a known LBN). Carries the file's name
/// (ident area, ≤ 20 chars ASCII for ODS-2) and the retrieval pointers
/// (map area) for its data extents. Serialization preserves the on-disk
/// invariants documented at <see cref="OpenVmsLayout"/>:
/// <list type="bullet">
///   <item>FH header words (IDOFFSET, MPOFFSET, …) at the documented offsets.</item>
///   <item>Ident area at byte 128, 20 bytes of NUL-padded ASCII file name.</item>
///   <item>Map area at byte 256, sequence of (LBN, count) 8-byte tuples,
///         zero-terminated by a (0, 0) sentinel.</item>
///   <item>FH2$W_CHECKSUM at byte 510, additive 16-bit sum of words 0..254.</item>
/// </list>
///
/// <para>The "in use" predicate is anchored on the structure-level word
/// at +6: a non-zero value means the FH carries a live file. Freeing a
/// File Header (via <see cref="ClearInUse"/>) zeros the structure-level
/// word so a subsequent scan reports it free.</para>
///
/// <para>The header layout we emit is consistent between writer / reader /
/// in-place modifier but is NOT a complete ODS-2 FH — the user-attribute
/// FILECHAR bits, the RECATTR FAT bundle, and the FH2$L_OWN_UIC field are
/// emitted as zeros. That's why <c>OpenVmsFormatDescriptor.Description</c>
/// continues to note that the emitted volume isn't OpenVMS-mountable.</para>
/// </summary>
public sealed class OpenVmsFileHeader {

  /// <summary>1-based File-ID number. 0 means "not allocated".</summary>
  public int FileId { get; set; }

  /// <summary>File-ID sequence number (incremented on slot reuse).</summary>
  public ushort Sequence { get; set; }

  /// <summary>File name (ASCII; padded with NULs to <see cref="OpenVmsLayout.FhFileNameLength"/>).</summary>
  public string Name { get; set; } = "";

  /// <summary>Logical file size in bytes.</summary>
  public long Size { get; set; }

  /// <summary>Retrieval pointers — each (LBN, count) tuple describes one contiguous data extent.</summary>
  public List<RetrievalPointer> Extents { get; } = [];

  /// <summary>True when the header is in use (structure-level word non-zero).</summary>
  public bool InUse { get; set; }

  /// <summary>Returns the sum of <c>Count</c> across every retrieval pointer.</summary>
  public int AllocatedBlocks => this.Extents.Sum(e => e.Count);

  /// <summary>Serializes the file header into a fresh 512-byte block ready to be written into INDEXF.SYS.</summary>
  public byte[] Serialize() {
    var block = new byte[OpenVmsLayout.BlockSize];

    // Header words at documented offsets.
    block[OpenVmsLayout.FhIdOffset] = OpenVmsLayout.FhIdentAreaOffset / 2;       // ident area at word 64 = byte 128
    block[OpenVmsLayout.FhMpOffset] = OpenVmsLayout.FhMapAreaOffset / 2;         // map area at word 128 = byte 256
    block[OpenVmsLayout.FhAcOffset] = OpenVmsLayout.FhChecksum / 2;              // access-control area "empty" — points at checksum
    block[OpenVmsLayout.FhRsOffset] = OpenVmsLayout.FhChecksum / 2;              // reserved area "empty"

    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(OpenVmsLayout.FhSegNum, 2), 0);
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(OpenVmsLayout.FhStrucLev, 2),
      (ushort)(this.InUse ? 0x0201 : 0x0000));
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(OpenVmsLayout.FhFidNum, 2), (ushort)(this.FileId & 0xFFFF));
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(OpenVmsLayout.FhFidSeq, 2), this.Sequence);
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(OpenVmsLayout.FhFidRvnNmx, 2),
      (ushort)((this.FileId >> 16) & 0xFFFF));
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(OpenVmsLayout.FhExtFid, 2), 0);

    // Internal: file size + allocation. These slots are reserved-for-RECATTR-and-up in the
    // real spec; we reuse them as scratch so reader/writer agree without a real RECATTR.
    BinaryPrimitives.WriteInt64LittleEndian(block.AsSpan(OpenVmsLayout.FhUsedSize, 8), this.Size);
    BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(OpenVmsLayout.FhAllocSize, 4), this.AllocatedBlocks);

    // Ident area — 20-byte ASCII file name, NUL-padded.
    var nameBytes = Encoding.ASCII.GetBytes(this.Name);
    var nameLen = Math.Min(nameBytes.Length, OpenVmsLayout.FhFileNameLength);
    nameBytes.AsSpan(0, nameLen).CopyTo(block.AsSpan(OpenVmsLayout.FhIdentAreaOffset));

    // Map area — sequence of (LBN, count) tuples zero-terminated.
    var mapPos = OpenVmsLayout.FhMapAreaOffset;
    foreach (var ext in this.Extents) {
      if (mapPos + 8 > OpenVmsLayout.FhChecksum) break;
      BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(mapPos, 4), (uint)ext.StartLbn);
      BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(mapPos + 4, 4), (uint)ext.Count);
      mapPos += 8;
    }
    // Zero-sentinel: a (0, 0) tuple. Already zero from the cleared block.

    // Checksum is computed over words 0..254 (the first 510 bytes) and stored at byte 510.
    var checksum = OpenVmsChecksum.Compute(block, 255);
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(OpenVmsLayout.FhChecksum, 2), checksum);

    return block;
  }

  /// <summary>Parses a 512-byte File Header from <paramref name="block"/>.</summary>
  public static OpenVmsFileHeader Deserialize(ReadOnlySpan<byte> block) {
    if (block.Length < OpenVmsLayout.BlockSize)
      throw new ArgumentException("File Header block must be 512 bytes", nameof(block));

    var fh = new OpenVmsFileHeader {
      Sequence = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(OpenVmsLayout.FhFidSeq, 2)),
      InUse = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(OpenVmsLayout.FhStrucLev, 2)) != 0,
    };
    var fidLow = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(OpenVmsLayout.FhFidNum, 2));
    var fidHigh = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(OpenVmsLayout.FhFidRvnNmx, 2));
    fh.FileId = (fidHigh << 16) | fidLow;

    fh.Size = BinaryPrimitives.ReadInt64LittleEndian(block.Slice(OpenVmsLayout.FhUsedSize, 8));

    // Ident area — read 20 bytes, strip trailing NULs/spaces.
    var nameRaw = block.Slice(OpenVmsLayout.FhIdentAreaOffset, OpenVmsLayout.FhFileNameLength).ToArray();
    var nameEnd = Array.IndexOf(nameRaw, (byte)0);
    if (nameEnd < 0) nameEnd = nameRaw.Length;
    fh.Name = Encoding.ASCII.GetString(nameRaw, 0, nameEnd).TrimEnd();

    // Map area — read until (0, 0) sentinel.
    var mapPos = OpenVmsLayout.FhMapAreaOffset;
    while (mapPos + 8 <= OpenVmsLayout.FhChecksum) {
      var lbn = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(mapPos, 4));
      var count = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(mapPos + 4, 4));
      if (lbn == 0 && count == 0) break;
      fh.Extents.Add(new RetrievalPointer((int)lbn, (int)count));
      mapPos += 8;
    }
    return fh;
  }

  /// <summary>Returns the deserialized FH after zeroing its in-use marker (for "freed" semantics).</summary>
  public void ClearInUse() {
    this.InUse = false;
    this.Extents.Clear();
    this.Name = "";
    this.Size = 0;
  }

  /// <summary>One retrieval pointer — describes a single contiguous data extent.</summary>
  public readonly record struct RetrievalPointer(int StartLbn, int Count);
}
