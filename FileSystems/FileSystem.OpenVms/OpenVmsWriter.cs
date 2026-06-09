#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.OpenVms;

/// <summary>
/// Builds minimal OpenVMS Files-11 ODS-2 volume images that round-trip through
/// <see cref="OpenVmsHomeBlock"/>. The on-disk layout follows the documented
/// Files-11 specification (VAX/VMS Internals &amp; Data Structures, Goldenberg
/// et&#160;al.) for the bytes the reader actually parses: a zero-filled boot
/// block at LBN 0, a real home block at LBN 1 with the
/// <c>"DECFILE11A "</c> format string at offset 0x1E8 and the volume label at
/// 0x1F4, then a CWB-OVMS-WB file-table extension at LBN 2 carrying the
/// caller's files. INDEXF.SYS / BITMAP.SYS / 000000.DIR are out of scope —
/// shipping a real OpenVMS-mountable image would require checksumming through
/// the index-file headers, which is multi-week work.
/// </summary>
/// <remarks>
/// <para>
/// What this writer is honest about: the home block matches the canonical
/// ODS-2 layout for every field a third-party Files-11 tool would inspect
/// during volume identification (structure level, cluster size, max files,
/// owner UIC, IBMAPLBN), and a real OpenVMS system would not silently
/// accept it for mounting — there's no valid INDEXF.SYS file header at
/// the location the home block points at, the home-block checksum1/2
/// fields are not populated, and the storage bitmap is empty. The volume
/// would mount as "structure error" on real VMS. We document this by
/// limiting the descriptor's Description to "ODS-2 home-block parse
/// round-trip + bundled file table" rather than claiming OpenVMS-mountable.
/// </para>
/// <para>
/// What this writer guarantees: writer → <see cref="OpenVmsHomeBlock"/>
/// round-trips every documented home-block field; writer → reader →
/// descriptor.Extract round-trips every input file's bytes; the writer
/// is deterministic (same inputs → byte-identical output).
/// </para>
/// </remarks>
public sealed class OpenVmsWriter : IDisposable {

  // ── On-disk constants ─────────────────────────────────────────────────

  internal const int BlockSize = 512;
  internal const long BootBlockOffset = 0;
  internal const long HomeBlockOffset = BlockSize;                  // LBN 1 = 512
  internal const long FileTableBlockOffset = 2L * BlockSize;        // LBN 2 = 1024
  internal const long DataAreaOffset = 16L * BlockSize;             // first byte after first 16 blocks (8 KB)

  /// <summary>"DECFILE11A " format-string anchor placed at home block + 0x1E8.</summary>
  internal static readonly byte[] FormatStringOds2 = "DECFILE11A "u8.ToArray();

  /// <summary>Eyecatcher for the CWB-OVMS-WB file table at LBN 2.</summary>
  internal static readonly byte[] FileTableEyecatcher = [
    (byte)'O', (byte)'V', (byte)'M', (byte)'S', (byte)'W', (byte)'B', (byte)'F', (byte)'T',
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
  ];

  // ── Mutable build state ───────────────────────────────────────────────

  private readonly Stream _output;
  private readonly bool _leaveOpen;
  private readonly List<(string Name, byte[] Data)> _files = [];
  private string _volumeLabel = "CWBVOL";

  public OpenVmsWriter(Stream output, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(output);
    this._output = output;
    this._leaveOpen = leaveOpen;
  }

  /// <summary>Sets the 12-byte ASCII volume label written into HM2$T_VOLNAME (truncated to 12).</summary>
  public void SetVolumeLabel(string label) {
    ArgumentNullException.ThrowIfNull(label);
    if (label.Length > 12) label = label[..12];
    this._volumeLabel = label;
  }

  /// <summary>Registers a file to be written into the volume. Path may contain '/' separators (translated to ODS-2 conventions in metadata, but our reader treats them as opaque names).</summary>
  public void AddFile(string path, byte[] data) {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(data);
    if (path.Length == 0) throw new ArgumentException("OpenVms: file name is empty.", nameof(path));
    var nameBytes = Encoding.UTF8.GetBytes(path);
    if (nameBytes.Length > 255)
      throw new ArgumentException($"OpenVms: file name '{path}' exceeds 255 UTF-8 bytes.", nameof(path));
    this._files.Add((path, data));
  }

  /// <summary>Convenience: builds the image to a byte array.</summary>
  public static byte[] Build(IEnumerable<(string Name, byte[] Data)> files, string? volumeLabel = null) {
    ArgumentNullException.ThrowIfNull(files);
    using var ms = new MemoryStream();
    using (var w = new OpenVmsWriter(ms, leaveOpen: true)) {
      if (volumeLabel != null) w.SetVolumeLabel(volumeLabel);
      foreach (var (n, d) in files) w.AddFile(n, d);
      w.Finish();
    }
    return ms.ToArray();
  }

  /// <summary>Writes the complete ODS-2 image to <see cref="_output"/>.</summary>
  public void Finish() {
    // 1. Compute payload layout. Data area starts at offset 8192 (LBN 16).
    var fileEntries = new List<(byte[] NameBytes, byte[] Data, long Offset, long Length)>(this._files.Count);
    var nextDataOffset = DataAreaOffset;
    foreach (var (name, data) in this._files) {
      var nameBytes = Encoding.UTF8.GetBytes(name);
      fileEntries.Add((nameBytes, data, nextDataOffset, data.LongLength));
      nextDataOffset += data.LongLength;
    }
    var totalSize = Math.Max(nextDataOffset, DataAreaOffset);

    // 2. Allocate image buffer.
    var image = new byte[totalSize];

    // 3. Write the home block at LBN 1.
    WriteHomeBlock(image);

    // 4. Write the file table at LBN 2.
    WriteFileTable(image, fileEntries);

    // 5. Copy file payloads into the data area.
    foreach (var (_, data, offset, _) in fileEntries) {
      if (data.Length == 0) continue;
      data.CopyTo(image, offset);
    }

    // 6. Flush.
    this._output.Write(image);
  }

  /// <summary>
  /// Lays out the home block at LBN 1 per the Files-11 ODS-2 spec. Only the
  /// fields <see cref="OpenVmsHomeBlock"/> reads are populated — checksum1,
  /// checksum2, volume timestamps, and the index-file ID stay zero, which is
  /// safe because the reader doesn't validate them. A real OpenVMS would
  /// reject this volume at mount; we document that in <see cref="OpenVmsFormatDescriptor.Description"/>.
  /// </summary>
  private void WriteHomeBlock(byte[] image) {
    var hb = image.AsSpan((int)HomeBlockOffset, BlockSize);

    // 0x000  HM2$L_HOMELBN     u32 LE — home block LBN = 1
    BinaryPrimitives.WriteUInt32LittleEndian(hb.Slice(0x000, 4), 1u);
    // 0x004  HM2$L_ALHOMELBN   u32 LE — alternate home LBN (0 = none)
    BinaryPrimitives.WriteUInt32LittleEndian(hb.Slice(0x004, 4), 0u);
    // 0x008  HM2$L_ALTIDXLBN   u32 LE — alternate index LBN
    BinaryPrimitives.WriteUInt32LittleEndian(hb.Slice(0x008, 4), 0u);
    // 0x00C  HM2$W_STRUCLEV    u16 LE — 0x0202 = ODS-2
    BinaryPrimitives.WriteUInt16LittleEndian(hb.Slice(0x00C, 2), 0x0202);
    // 0x00E  HM2$W_CLUSTER     u16 LE — cluster size in blocks (1)
    BinaryPrimitives.WriteUInt16LittleEndian(hb.Slice(0x00E, 2), 1);
    // 0x010  HM2$W_HOMEVBN     u16 LE — home VBN (1)
    BinaryPrimitives.WriteUInt16LittleEndian(hb.Slice(0x010, 2), 1);
    // 0x028  HM2$L_IBMAPLBN    u32 LE — index-file bitmap LBN
    BinaryPrimitives.WriteUInt32LittleEndian(hb.Slice(0x028, 4), 2u);
    // 0x02C  HM2$L_MAXFILES    u32 LE — max files in the volume
    BinaryPrimitives.WriteUInt32LittleEndian(hb.Slice(0x02C, 4), (uint)Math.Max(16, this._files.Count + 16));
    // 0x030  HM2$W_IBMAPSIZE   u16 LE — index bitmap size in blocks
    BinaryPrimitives.WriteUInt16LittleEndian(hb.Slice(0x030, 2), 1);
    // 0x036  HM2$L_OWNUIC      u32 LE — owner UIC (group:member, [1,1] = system)
    BinaryPrimitives.WriteUInt32LittleEndian(hb.Slice(0x036, 4), 0x00010001u);

    // 0x1E8  HM2$T_FORMAT      12 ASCII — "DECFILE11A "
    FormatStringOds2.AsSpan().CopyTo(hb.Slice(0x1E8, FormatStringOds2.Length));
    // remaining byte in the 12-byte field stays 0 (NUL pad).

    // 0x1F4  HM2$T_VOLNAME     12 ASCII — volume label, padded with NUL
    var label = Encoding.ASCII.GetBytes(this._volumeLabel);
    var labelCopy = Math.Min(label.Length, 12);
    label.AsSpan(0, labelCopy).CopyTo(hb.Slice(0x1F4, labelCopy));
  }

  /// <summary>
  /// Writes the CWB-OVMS-WB file table at LBN 2. Layout: 16-byte eyecatcher,
  /// 4-byte file count, then per-file (8-byte offset, 8-byte length, 2-byte
  /// name length, name bytes). One LBN holds 512 bytes; we cap at a single
  /// block today.
  /// </summary>
  private static void WriteFileTable(byte[] image,
      List<(byte[] NameBytes, byte[] Data, long Offset, long Length)> files) {
    var ft = image.AsSpan((int)FileTableBlockOffset, (int)(DataAreaOffset - FileTableBlockOffset));
    FileTableEyecatcher.AsSpan().CopyTo(ft);
    var cursor = FileTableEyecatcher.Length;
    BinaryPrimitives.WriteUInt32LittleEndian(ft.Slice(cursor, 4), (uint)files.Count);
    cursor += 4;
    foreach (var (nameBytes, _, offset, length) in files) {
      if (cursor + 8 + 8 + 2 + nameBytes.Length > ft.Length)
        throw new InvalidOperationException(
          $"OpenVms writer: file table overflows reserved 14-block region (cursor={cursor}, region={ft.Length}). " +
          $"Reduce the number of files or shorten names.");
      BinaryPrimitives.WriteInt64LittleEndian(ft.Slice(cursor, 8), offset); cursor += 8;
      BinaryPrimitives.WriteInt64LittleEndian(ft.Slice(cursor, 8), length); cursor += 8;
      BinaryPrimitives.WriteUInt16LittleEndian(ft.Slice(cursor, 2), (ushort)nameBytes.Length); cursor += 2;
      nameBytes.CopyTo(ft.Slice(cursor, nameBytes.Length));
      cursor += nameBytes.Length;
    }
  }

  public void Dispose() {
    if (!this._leaveOpen) this._output.Dispose();
  }
}
