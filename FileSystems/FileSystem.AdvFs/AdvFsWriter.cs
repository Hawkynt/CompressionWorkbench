#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.AdvFs;

/// <summary>
/// Builds minimal AdvFS (Tru64 UNIX) volume images that round-trip cleanly through
/// <see cref="AdvFsReader"/>. The on-disk layout is a clean-room subset of the
/// HP-2008 open-sourced AdvFS storage-domain model: bootstrap pages 0..15 are
/// zero, RBMT page 0 starts at byte offset <c>131072</c> with the 16-byte
/// detection cookie <c>"ADVFS\0RBMT0\0\0\0\0\0"</c> followed by the
/// <c>BSR_DMN_ATTR</c> / <c>BSR_VD_ATTR</c> / <c>BSR_DMN_MATTR</c> field bundle
/// the reader documents. A trailing AdvFS-WB file table extension (eyecatcher
/// <c>"ADVFSWBFT\0\0\0\0\0\0\0"</c>) follows the volume tag; the reader picks
/// it up when present so file payloads survive a write→read round-trip.
/// </summary>
/// <remarks>
/// <para>
/// This is honestly scoped: walking the real BMT B-tree to reconstruct user
/// files from an arbitrary Tru64 image is multi-week work — we don't claim to.
/// What this writer does claim is a self-consistent, deterministic image whose
/// reader counterpart recovers every byte of every file. The layout intentionally
/// shares the cookie + DMN/VD/MATTR field order with the existing read path so
/// the descriptor's detection magic still matches, the metadata.ini still parses,
/// and the rbmt_page0.bin capture still surfaces the documented fields.
/// </para>
/// <para>
/// Per-file storage: each file's payload is appended to a continuous data area
/// that begins at the first 8 KB page boundary after the RBMT page (offset
/// <c>139264</c> = 17 × 8192). The file table inside RBMT page 0 records
/// (name length, name, payload offset, payload length) triples; payload bytes
/// are stored as-is, no compression. Names are UTF-8, capped at 255 bytes.
/// </para>
/// </remarks>
public sealed class AdvFsWriter : IDisposable {

  // ── On-disk constants (cross-checked with AdvFsReader) ────────────────

  internal const int PageSize = AdvFsReader.PageSize;                          // 8192
  internal const long RbmtPageOffset = AdvFsReader.RbmtPageOffset;             // 131072
  internal const long DataAreaOffset = RbmtPageOffset + PageSize;              // 139264 — first byte after RBMT page

  /// <summary>16-byte AdvFS-WB file-table eyecatcher placed inside RBMT page 0.</summary>
  internal static readonly byte[] FileTableEyecatcher = [
    (byte)'A', (byte)'D', (byte)'V', (byte)'F', (byte)'S', (byte)'W', (byte)'B', (byte)'F',
    (byte)'T', 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
  ];

  /// <summary>Offset within RBMT page 0 where the AdvFS-WB file table starts (after the 16-byte cookie + DMN/VD/MATTR fields + 64-byte volume tag).</summary>
  /// <remarks>16 (cookie) + 16 (domain UUID) + 8 (mountId) + 4 (onDiskVersion) + 4 (vdIndex) + 4 (vdCount) + 4 (state) + 8 (vdBlkCnt) + 4 (vdMetaBlkCnt) + 64 (volume tag) = 132. The reader skips the same prefix when parsing, so the eyecatcher is at a stable offset.</remarks>
  internal const int FileTableOffsetInPage = 132;

  // ── Mutable build state ───────────────────────────────────────────────

  private readonly Stream _output;
  private readonly bool _leaveOpen;
  private readonly List<(string Name, byte[] Data)> _files = [];
  private string _volumeTag = "CWB-ADVFS";

  public AdvFsWriter(Stream output, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(output);
    this._output = output;
    this._leaveOpen = leaveOpen;
  }

  /// <summary>Sets the textual volume tag surfaced in the BSR_VD_ATTR record (capped at 63 ASCII bytes).</summary>
  public void SetVolumeTag(string tag) {
    ArgumentNullException.ThrowIfNull(tag);
    if (tag.Length > 63) tag = tag[..63];
    this._volumeTag = tag;
  }

  /// <summary>Registers a file to be written into the storage domain.</summary>
  /// <exception cref="ArgumentException">Empty name, or UTF-8 encoding exceeds 255 bytes.</exception>
  public void AddFile(string path, byte[] data) {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(data);
    if (path.Length == 0) throw new ArgumentException("AdvFs: file name is empty.", nameof(path));
    var nameBytes = Encoding.UTF8.GetBytes(path);
    if (nameBytes.Length > 255)
      throw new ArgumentException($"AdvFs: file name '{path}' exceeds 255 UTF-8 bytes.", nameof(path));
    this._files.Add((path, data));
  }

  /// <summary>Convenience: builds the image to a byte array.</summary>
  public static byte[] Build(IEnumerable<(string Name, byte[] Data)> files, string? volumeTag = null) {
    ArgumentNullException.ThrowIfNull(files);
    using var ms = new MemoryStream();
    using (var w = new AdvFsWriter(ms, leaveOpen: true)) {
      if (volumeTag != null) w.SetVolumeTag(volumeTag);
      foreach (var (n, d) in files) w.AddFile(n, d);
      w.Finish();
    }
    return ms.ToArray();
  }

  /// <summary>Writes the complete image to <see cref="_output"/>.</summary>
  public void Finish() {
    // 1. Compute payload offsets. The data area begins immediately after the
    //    RBMT page. Each file's payload runs back-to-back, no alignment beyond
    //    1-byte granularity — read by absolute offset/length from the file table.
    var fileEntries = new List<(byte[] NameBytes, byte[] Data, long Offset, long Length)>(this._files.Count);
    var nextDataOffset = DataAreaOffset;
    foreach (var (name, data) in this._files) {
      var nameBytes = Encoding.UTF8.GetBytes(name);
      fileEntries.Add((nameBytes, data, nextDataOffset, data.LongLength));
      nextDataOffset += data.LongLength;
    }
    var totalSize = nextDataOffset;
    if (totalSize < DataAreaOffset) totalSize = DataAreaOffset;

    // 2. Allocate the image buffer. Bootstrap pages stay zero; the RBMT page
    //    gets the cookie + parsed fields + file table.
    var image = new byte[totalSize];

    // 3. Write the RBMT page header at offset 131072.
    var rbmt = image.AsSpan((int)RbmtPageOffset, PageSize);
    AdvFsReader.DetectionCookie.AsSpan().CopyTo(rbmt);

    var cursor = AdvFsReader.DetectionCookie.Length;

    // BSR_DMN_ATTR: 16-byte domain UUID (deterministic — derive from file list
    // so the same inputs yield identical bytes, useful for round-trip diffing).
    var domainUuid = DeriveDomainUuid(this._volumeTag, fileEntries);
    domainUuid.CopyTo(rbmt.Slice(cursor, 16));
    cursor += 16;

    // 8-byte MountId — we encode (count, sentinel) so it's stable and easily
    // distinguishable in hex dumps without leaking the host clock.
    BinaryPrimitives.WriteUInt64LittleEndian(rbmt.Slice(cursor, 8),
      ((ulong)fileEntries.Count << 32) | 0xADF50001UL);
    cursor += 8;

    // 4-byte onDiskVersion — AdvFS_VERSION = 4 in the HP 2008 source.
    BinaryPrimitives.WriteUInt32LittleEndian(rbmt.Slice(cursor, 4), 4u);
    cursor += 4;

    // BSR_VD_ATTR: vdIndex, vdCount, state, vdBlkCnt, vdMetaBlkCnt.
    BinaryPrimitives.WriteUInt32LittleEndian(rbmt.Slice(cursor, 4), 1u); cursor += 4;          // vdIndex
    BinaryPrimitives.WriteUInt32LittleEndian(rbmt.Slice(cursor, 4), 1u); cursor += 4;          // vdCount
    BinaryPrimitives.WriteUInt32LittleEndian(rbmt.Slice(cursor, 4), 0u); cursor += 4;          // state = clean
    var blkCnt = (ulong)((totalSize + 511) / 512);
    BinaryPrimitives.WriteUInt64LittleEndian(rbmt.Slice(cursor, 8), blkCnt); cursor += 8;      // vdBlkCnt
    BinaryPrimitives.WriteUInt32LittleEndian(rbmt.Slice(cursor, 4), 16u); cursor += 4;         // vdMetaBlkCnt = 16 (8 KB page / 512 sector)

    // Volume tag: 64 ASCII bytes, NUL-padded.
    var tagBytes = Encoding.ASCII.GetBytes(this._volumeTag);
    var tagCopy = Math.Min(tagBytes.Length, 63);
    tagBytes.AsSpan(0, tagCopy).CopyTo(rbmt.Slice(cursor, tagCopy));
    cursor += 64;

    // 4. Write the AdvFS-WB file table.
    if (cursor != FileTableOffsetInPage)
      throw new InvalidOperationException(
        $"AdvFs writer offset sync error: file table offset {cursor} != expected {FileTableOffsetInPage}. " +
        $"Header layout has drifted from AdvFsReader.Parse — keep these in sync.");

    FileTableEyecatcher.AsSpan().CopyTo(rbmt.Slice(cursor, FileTableEyecatcher.Length));
    cursor += FileTableEyecatcher.Length;

    // 4-byte file count.
    BinaryPrimitives.WriteUInt32LittleEndian(rbmt.Slice(cursor, 4), (uint)fileEntries.Count);
    cursor += 4;

    // Per-file: 8-byte offset, 8-byte length, 2-byte name length, name bytes.
    foreach (var (nameBytes, _, offset, length) in fileEntries) {
      if (cursor + 8 + 8 + 2 + nameBytes.Length > PageSize)
        throw new InvalidOperationException(
          $"AdvFs writer: file table overflows RBMT page (cursor={cursor}). " +
          $"Reduce the number of files or shorten names.");
      BinaryPrimitives.WriteInt64LittleEndian(rbmt.Slice(cursor, 8), offset); cursor += 8;
      BinaryPrimitives.WriteInt64LittleEndian(rbmt.Slice(cursor, 8), length); cursor += 8;
      BinaryPrimitives.WriteUInt16LittleEndian(rbmt.Slice(cursor, 2), (ushort)nameBytes.Length); cursor += 2;
      nameBytes.CopyTo(rbmt.Slice(cursor, nameBytes.Length));
      cursor += nameBytes.Length;
    }

    // 5. Copy each file's payload into the data area.
    foreach (var (_, data, offset, _) in fileEntries) {
      if (data.Length == 0) continue;
      data.CopyTo(image, offset);
    }

    // 6. Flush to output.
    this._output.Write(image);
  }

  /// <summary>
  /// Derives a deterministic 16-byte domain UUID from the volume tag + file
  /// list. Same inputs → same UUID, which keeps round-trip tests stable
  /// without leaking the host clock.
  /// </summary>
  private static byte[] DeriveDomainUuid(string volumeTag, List<(byte[] NameBytes, byte[] Data, long Offset, long Length)> files) {
    var seed = new List<byte>();
    seed.AddRange(Encoding.ASCII.GetBytes(volumeTag));
    foreach (var (n, _, _, length) in files) {
      seed.AddRange(n);
      var lengthBytes = new byte[8];
      BinaryPrimitives.WriteInt64LittleEndian(lengthBytes, length);
      seed.AddRange(lengthBytes);
    }
    // FNV-1a 128-bit (4 × 32-bit lanes) to keep the dependency surface zero —
    // no System.Security.Cryptography hash, no allocation beyond the seed list.
    var lanes = new uint[] { 0x811C9DC5, 0x811C9DC5, 0x811C9DC5, 0x811C9DC5 };
    var laneIdx = 0;
    foreach (var b in seed) {
      lanes[laneIdx] ^= b;
      lanes[laneIdx] *= 0x01000193;
      laneIdx = (laneIdx + 1) & 3;
    }
    var uuid = new byte[16];
    for (var i = 0; i < 4; i++)
      BinaryPrimitives.WriteUInt32LittleEndian(uuid.AsSpan(i * 4, 4), lanes[i]);
    return uuid;
  }

  public void Dispose() {
    if (!this._leaveOpen) this._output.Dispose();
  }
}
