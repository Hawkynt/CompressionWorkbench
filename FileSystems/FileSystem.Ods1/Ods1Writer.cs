#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage;

namespace FileSystem.Ods1;

/// <summary>
/// Writer for DEC VAX/VMS ODS-1 (Files-11 Level 1) disk images. Produces a
/// minimal but spec-shaped Files-11 volume that the companion
/// <see cref="Ods1Reader"/> can round-trip cleanly.
///
/// <para>Layout produced (LBN = 512-byte Logical Block Number):</para>
/// <code>
///   LBN  0          boot block (zero-filled, no PDP-11 bootstrap)
///   LBN  1          home block — DECFILE11A signature, volume name, INDEXF LBN
///   LBN  2          BITMAP.SYS data — allocation bitmap (1 LBN fits ≤ 4096 LBNs)
///   LBN  3          pad / spare
///   LBN  4..67      index-file window (64 LBNs) — one 512-byte file header per
///                   user file (file id 1..N), remaining slots zero-filled
///   LBN  68..       contiguous data extents in allocation order
/// </code>
///
/// <para>The writer matches the existing <see cref="Ods1Reader"/> Stage-1
/// encoding exactly: filenames as raw ASCII (not Radix-50), retrieval
/// pointers in the simplified <c>(count-1, hi, lo)</c> form, file size
/// reported as block-count × 512 (no sub-block <c>fh1$l_efblk</c>). Real
/// VAX/VMS images use Radix-50 and an end-of-file block field; Stage-1 of
/// this format is a pragmatic round-trip-clean subset. The on-disk shape
/// (boot/home/bitmap/index-file layout, DECFILE11A signature at the
/// canonical home-block offset, file headers with idoff/mpoff/fileNum,
/// little-endian everything) follows the Files-11 spec.</para>
/// </summary>
public static class Ods1Writer {

  internal const int LbnSize = Ods1Reader.LbnSize;          // 512
  internal const int HomeBlockLbn = Ods1Reader.HomeBlockLbn; // 1
  internal const int BitmapLbn = 2;
  internal const int IndexfLbn = 4;                          // matches reader's default fallback
  internal const int IndexfHeaderSlots = 64;                 // reader scans 64 LBNs starting at IndexfLbn
  internal const int MaxFileNameStem = 9;                    // ident-area name field
  internal const int MaxFileNameExt = 3;

  /// <summary>
  /// Builds a Files-11 Level 1 disk image containing <paramref name="files"/>.
  /// File headers occupy one LBN each starting at <see cref="IndexfLbn"/>; the
  /// reader's full <see cref="IndexfHeaderSlots"/>-LBN scan window is reserved
  /// (unused slots zero-padded), and data extents follow contiguously beyond
  /// it. Total image size auto-fits the payload, floored to fit the boot/home/
  /// bitmap blocks plus the full index-file window plus one data LBN.
  /// </summary>
  /// <param name="files">Files to store; each entry's name is split on the
  /// last dot into a stem (≤ 9 ASCII chars) and extension (≤ 3 ASCII chars).</param>
  /// <param name="volumeName">Volume label (≤ 12 ASCII chars, space-padded).</param>
  public static byte[] Build(IReadOnlyList<(string Name, byte[] Data)> files, string volumeName = "SCRATCH") {
    ArgumentNullException.ThrowIfNull(files);
    return Build(files.Select(f => (f.Name, FilePayload.FromBytes(f.Data))).ToList(), volumeName);
  }

  /// <summary>Materialises an image from payloads that may be streamed.</summary>
  public static byte[] Build(IReadOnlyList<(string Name, FilePayload Payload)> files, string volumeName = "SCRATCH") {
    var image = BuildCore(files, volumeName, out var payloads, out var totalBytes);
    if (totalBytes > Array.MaxLength)
      throw new InvalidOperationException(
        $"ODS-1: a {totalBytes:N0}-byte volume exceeds the array limit; write it to a seekable stream instead.");
    var full = new byte[totalBytes];
    image.CopyTo(full, 0);
    using var target = new MemoryStream(full, writable: true);
    payloads.FlushTo(target);
    return full;
  }

  /// <summary>
  /// Writes the volume into <paramref name="output" />: the header window, then
  /// each file's bytes at the extent it was allocated. Only the header window is
  /// resident, so a volume past what a byte[] can address is producible.
  /// </summary>
  public static void WriteTo(Stream output, IReadOnlyList<(string Name, FilePayload Payload)> files,
                             string volumeName = "SCRATCH") {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanSeek) {
      var full = Build(files, volumeName);
      output.Write(full, 0, full.Length);
      return;
    }

    var basePosition = output.Position;
    var image = BuildCore(files, volumeName, out var payloads, out var totalBytes);
    output.Write(image, 0, image.Length);
    output.SetLength(basePosition + totalBytes);
    payloads.FlushTo(output, basePosition);
    output.Position = basePosition + totalBytes;
    output.Flush();
  }

  private static byte[] BuildCore(IReadOnlyList<(string Name, FilePayload Payload)> files, string volumeName,
                                  out DeferredPayloads payloads, out long totalBytes) {
    ArgumentNullException.ThrowIfNull(files);
    ArgumentNullException.ThrowIfNull(volumeName);

    // Validate filenames up-front so we don't half-emit then throw.
    var split = new (string Stem, string Ext)[files.Count];
    for (var i = 0; i < files.Count; i++) {
      var (stem, ext) = SplitName(files[i].Name);
      if (stem.Length == 0) throw new InvalidOperationException($"ODS-1: filename '{files[i].Name}' produces an empty stem.");
      if (stem.Length > MaxFileNameStem) throw new InvalidOperationException($"ODS-1: filename stem '{stem}' exceeds {MaxFileNameStem} chars.");
      if (ext.Length > MaxFileNameExt) throw new InvalidOperationException($"ODS-1: filename extension '{ext}' exceeds {MaxFileNameExt} chars.");
      split[i] = (stem, ext);
    }
    // Reader scans IndexfHeaderSlots (=64) consecutive LBNs starting at IndexfLbn.
    // Beyond that window the reader stops, so we have at most 64 header slots
    // and must keep data extents OUT of that window (otherwise data bytes get
    // interpreted as ghost file headers via random nonzero bytes at the
    // fileNum offset).
    if (files.Count > IndexfHeaderSlots)
      throw new ArgumentException(
        $"ODS-1 Stage-1 writer: too many files ({files.Count} > {IndexfHeaderSlots} header slots).",
        nameof(files));

    // ── Plan layout ────────────────────────────────────────────────────────
    // Headers: 1 LBN per file starting at IndexfLbn, unused slots zero-padded
    //          to the end of the reader's scan window.
    // Data:    contiguous extents starting right after the index-file window.
    // The allocation bitmap is one bit per LBN, so it grows with the volume: a
    // single LBN covers 4096 blocks (2 MB), and writing past that ran off the end
    // of the block. Its size shifts the index-file window along, and the home
    // block records both, so the reader follows.
    var dataBlocks = 0L;
    for (var i = 0; i < files.Count; i++)
      dataBlocks += Math.Max(1, (files[i].Payload.Size + LbnSize - 1) / LbnSize);

    var bitmapLbns = 1;
    int indexfLbn, totalLbnPlan;
    for (var iteration = 0; ; ++iteration) {
      indexfLbn = BitmapLbn + bitmapLbns;
      totalLbnPlan = (int)(indexfLbn + IndexfHeaderSlots + dataBlocks);
      var need = (totalLbnPlan + LbnSize * 8 - 1) / (LbnSize * 8);
      if (need <= bitmapLbns || iteration > 8) break;
      bitmapLbns = need;
    }

    var dataStart = (uint)(indexfLbn + IndexfHeaderSlots);

    var extents = new (uint StartLbn, uint Blocks)[files.Count];
    var nextData = dataStart;
    for (var i = 0; i < files.Count; i++) {
      var blocks = (uint)((files[i].Payload.Size + LbnSize - 1) / LbnSize);
      if (blocks == 0) blocks = 1; // every file owns at least one LBN
      extents[i] = (nextData, blocks);
      nextData += blocks;
    }

    // Floor: enough room for boot/home/bitmap + full index-file window + data.
    var totalLbn = Math.Max((int)nextData, indexfLbn + IndexfHeaderSlots + 1);
    totalBytes = (long)totalLbn * LbnSize;
    // Only the header window is materialised: data extents start beyond it and
    // are placed by seek, so a volume past what a byte[] can address costs its
    // metadata rather than its size.
    payloads = new DeferredPayloads();
    var image = new byte[(long)dataStart * LbnSize];

    // ── LBN 1: home block ──────────────────────────────────────────────────
    WriteHomeBlock(image, volumeName, bitmapLbns, indexfLbn);

    // ── BITMAP.SYS data — mark every allocated LBN ─────────────────────────
    var bitmap = image.AsSpan(BitmapLbn * LbnSize, bitmapLbns * LbnSize);
    for (var lbn = 0u; lbn < nextData; lbn++)
      bitmap[(int)(lbn / 8)] |= (byte)(1 << (int)(lbn % 8));

    // ── LBN 4..: user-file headers + data ──────────────────────────────────
    for (var i = 0; i < files.Count; i++) {
      var (stem, ext) = split[i];
      var fileNum = (ushort)(1 + i); // first user file = id 1 (matches reader's "active != 0" check)
      var headerLbn = (uint)(indexfLbn + i);
      var (start, blocks) = extents[i];

      WriteFileHeader(image, (int)headerLbn, fileNum, stem, ext,
        isDirectory: false, dataStartLbn: start, dataBlocks: blocks,
        fileSize: files[i].Payload.Size);

      // The payload belongs at its allocated extent (zero-padded to the block
      // boundary); it is written after the header window.
      payloads.Add((long)start * LbnSize, files[i].Payload);
    }

    return image;
  }

  private static void WriteHomeBlock(byte[] image, string volumeName, int bitmapLbns, int indexfLbn) {
    var hb = HomeBlockLbn * LbnSize;
    var span = image.AsSpan(hb, LbnSize);

    // +0x000  hm1$w_ibmapsize        u16 — bitmap size in LBNs
    BinaryPrimitives.WriteUInt16LittleEndian(span[0x000..], (ushort)bitmapLbns);
    // +0x002  hm1$l_ibmaplbn         u32 — first LBN of allocation bitmap
    BinaryPrimitives.WriteUInt32LittleEndian(span[0x002..], (uint)BitmapLbn);
    // +0x006  hm1$w_maxfiles         u16
    BinaryPrimitives.WriteUInt16LittleEndian(span[0x006..], 64);
    // +0x008  hm1$w_cluster          u16
    BinaryPrimitives.WriteUInt16LittleEndian(span[0x008..], 1);
    // +0x00A  hm1$w_devtype          u16
    BinaryPrimitives.WriteUInt16LittleEndian(span[0x00A..], 0);
    // +0x00C  hm1$w_structlev        u16 — 0x0101 = Files-11 Level 1
    BinaryPrimitives.WriteUInt16LittleEndian(span[0x00C..], 0x0101);
    // +0x00E  hm1$t_volname          12 ASCII (NUL/space-padded — reader trims both)
    WriteFixedAscii(span[0x00E..(0x00E + 12)], volumeName);

    // +0x040  pointer to INDEXF.SYS first header LBN (custom slot used by reader)
    BinaryPrimitives.WriteUInt16LittleEndian(span[0x040..], (ushort)indexfLbn);

    // +0x1F0  hm1$t_format           "DECFILE11A " (12 bytes)
    Encoding.ASCII.GetBytes("DECFILE11A").CopyTo(span[0x1F0..]);

    // First/second-half additive checksums for spec hygiene. The Stage-1
    // reader does not verify these so a value of 0 is also accepted; we
    // compute them anyway so real Files-11 tools that do verify see clean
    // values.
    ushort sum1 = 0;
    for (var i = 0; i < 0x2C; i += 2)
      sum1 += BinaryPrimitives.ReadUInt16LittleEndian(span[i..]);
    BinaryPrimitives.WriteUInt16LittleEndian(span[0x02C..], sum1);

    ushort sum2 = 0;
    for (var i = 0; i < 0x1FE; i += 2)
      sum2 += BinaryPrimitives.ReadUInt16LittleEndian(span[i..]);
    BinaryPrimitives.WriteUInt16LittleEndian(span[0x1FE..], sum2);
  }

  private static void WriteFileHeader(
    byte[] image, int headerLbn, ushort fileNum,
    string name, string ext, bool isDirectory,
    uint dataStartLbn, uint dataBlocks, long fileSize) {

    // The Stage-1 reader expects (matching its synthetic test image):
    //   +0    idOffWords = 32 (=> 64-byte offset to ident area)
    //   +1    mpOffWords = 64 (=> 128-byte offset to map area)
    //   +2    fileNum    nonzero
    //   +0x0A fileChar   0x40 if directory else 0
    //   +64   name(9) ASCII + ext(3) ASCII + 2 bytes version
    //   +128  map: a run of retrieval pointers, each u16 count_minus_1 + u16 hi + u16 lo
    //   +0x0C exact byte size (u64). A retrieval pointer's count is 16-bit, so a
    //         file is described by several of them and its logical size cannot be
    //         derived from a single count; ODS-1 keeps the end-of-file position in
    //         the record-attributes bundle, which this Stage-1 layout does not
    //         emit, so the size lives in the header's spare words instead.
    const int IdOffWords = 32;
    const int MpOffWords = 64;
    const int IdByteOff = IdOffWords * 2;   // = 64
    const int MpByteOff = MpOffWords * 2;   // = 128

    var fh = (long)headerLbn * LbnSize;
    var span = image.AsSpan((int)fh, LbnSize);

    span[0] = IdOffWords;
    span[1] = MpOffWords;
    BinaryPrimitives.WriteUInt16LittleEndian(span[2..], fileNum);
    BinaryPrimitives.WriteUInt16LittleEndian(span[4..], fileNum); // fh1$w_fid_seq mirrored
    BinaryPrimitives.WriteUInt16LittleEndian(span[6..], 0x0101);  // fh1$w_struclev = Files-11 L1
    BinaryPrimitives.WriteUInt16LittleEndian(span[8..], 0);       // fh1$w_fid_volume — relative volume 0
    span[0x0A] = (byte)(isDirectory ? 0x40 : 0x00);                // fh1$b_filechar (F11_DIRECTORY=0x40)
    BinaryPrimitives.WriteInt64LittleEndian(span[0x0C..], fileSize); // exact logical size

    // Ident area at +64: 9-byte name + 3-byte ext + 2-byte version
    WriteFixedAscii(span[IdByteOff..(IdByteOff + MaxFileNameStem)], name);
    WriteFixedAscii(span[(IdByteOff + MaxFileNameStem)..(IdByteOff + MaxFileNameStem + MaxFileNameExt)], ext);
    // version field at IdByteOff + 12 = 0x4C: keep 0 (uninterpreted by reader)

    // Map area at +128: retrieval pointers covering the file's extent. A pointer's
    // count is 16-bit, so a run longer than 65536 blocks takes several — writing
    // one truncated the count and the file read back short.
    // Reader formula per pointer: count = read_u16 + 1, lbn = (hi << 16) | lo
    var pointerSlots = (LbnSize - MpByteOff) / 6;
    var remaining = dataBlocks;
    var lbn = dataStartLbn;
    var slot = 0;
    do {
      if (slot >= pointerSlots)
        throw new InvalidOperationException(
          $"ODS-1: '{name}' needs more than {pointerSlots} retrieval pointers.");
      var count = Math.Min(remaining, 1u << 16);
      var at = MpByteOff + slot * 6;
      BinaryPrimitives.WriteUInt16LittleEndian(span[at..], (ushort)(count == 0 ? 0 : count - 1));
      BinaryPrimitives.WriteUInt16LittleEndian(span[(at + 2)..], (ushort)(lbn >> 16));
      BinaryPrimitives.WriteUInt16LittleEndian(span[(at + 4)..], (ushort)(lbn & 0xFFFF));
      lbn += count;
      remaining -= count;
      ++slot;
    } while (remaining > 0);
  }

  /// <summary>Splits "FOO.BAR" → ("FOO","BAR"); "NOEXT" → ("NOEXT","");
  /// upper-cases and trims to ODS-1 field widths.</summary>
  internal static (string Stem, string Ext) SplitName(string fullName) {
    if (string.IsNullOrEmpty(fullName)) return ("", "");
    var leaf = Path.GetFileName(fullName);
    var dot = leaf.LastIndexOf('.');
    var stem = dot < 0 ? leaf : leaf[..dot];
    var ext = dot < 0 ? "" : leaf[(dot + 1)..];
    if (stem.Length > MaxFileNameStem) stem = stem[..MaxFileNameStem];
    if (ext.Length > MaxFileNameExt) ext = ext[..MaxFileNameExt];
    return (stem.ToUpperInvariant(), ext.ToUpperInvariant());
  }

  /// <summary>Writes <paramref name="value"/> as ASCII into <paramref name="dest"/>,
  /// padding remaining bytes with NUL (matches the reader's TrimEnd handling).</summary>
  private static void WriteFixedAscii(Span<byte> dest, string value) {
    dest.Clear();
    var max = Math.Min(value.Length, dest.Length);
    for (var i = 0; i < max; i++) {
      var c = value[i];
      dest[i] = c is >= (char)0x20 and < (char)0x7F ? (byte)c : (byte)'?';
    }
  }
}
