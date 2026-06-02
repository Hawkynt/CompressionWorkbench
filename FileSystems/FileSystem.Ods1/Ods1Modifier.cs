#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Ods1;

/// <summary>
/// In-place random-access modifier for DEC ODS-1 (Files-11 Level 1) disk
/// images. Mutates an existing image without rebuilding it: add allocates a
/// free 512-byte file-header slot in the INDEXF window plus a contiguous
/// run of data LBNs from BITMAP.SYS; remove frees the file's data extents
/// in BITMAP.SYS and zero-fills its file-header slot. The home block's
/// additive checksums are recomputed on every mutation so spec-validating
/// readers see clean values.
///
/// <para>This is the read+write counterpart of <see cref="Ods1Writer"/>: the
/// writer rebuilds an image from scratch, the modifier touches only the
/// affected LBNs (header slot + BITMAP byte(s) + data extent + home-block
/// checksum fields).</para>
///
/// <para><b>Capacity limits</b> match the Stage-1 layout the writer
/// established: at most <c>IndexfHeaderSlots</c> (= 64) live files,
/// allocations must be contiguous (single retrieval pointer per file), and
/// data LBNs must live within the image's existing bounds.
/// <see cref="AddFile"/> throws <see cref="NotSupportedException"/> when
/// the INDEXF window is full or when BITMAP.SYS lacks a contiguous free
/// run of the required size.</para>
/// </summary>
public static class Ods1Modifier {

  private const int LbnSize = Ods1Writer.LbnSize;            // 512
  private const int HomeBlockLbn = Ods1Writer.HomeBlockLbn;  // 1
  private const int BitmapLbn = Ods1Writer.BitmapLbn;        // 2
  private const int IndexfLbn = Ods1Writer.IndexfLbn;        // 4
  private const int IndexfHeaderSlots = Ods1Writer.IndexfHeaderSlots; // 64
  private const int MaxNameStem = Ods1Writer.MaxFileNameStem;         // 9
  private const int MaxNameExt = Ods1Writer.MaxFileNameExt;           // 3

  private const int IdOffWords = 32;
  private const int MpOffWords = 64;
  private const int IdByteOff = IdOffWords * 2;     // 64
  private const int MpByteOff = MpOffWords * 2;     // 128

  /// <summary>
  /// Appends <paramref name="name"/>/<paramref name="data"/> to an existing
  /// ODS-1 image. The header is written into the lowest free slot in the
  /// 64-slot INDEXF window; data is placed at the lowest free contiguous
  /// run of LBNs in BITMAP.SYS (starting at LBN 68, the first byte past
  /// the index-file window). The image grows when the allocated run
  /// extends past the current image length; BITMAP.SYS still covers the
  /// new range because a single 512-byte bitmap LBN tracks 4096 LBNs.
  /// </summary>
  /// <param name="image">Existing ODS-1 image stream (must be seekable + writable).</param>
  /// <param name="name">8.3-style filename ("HELLO.TXT"); same trimming rules as the writer.</param>
  /// <param name="data">File contents; zero bytes is allowed (one LBN is allocated).</param>
  /// <exception cref="NotSupportedException">The INDEXF window is full or BITMAP.SYS
  /// lacks a contiguous run of the required size.</exception>
  public static void AddFile(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var (stem, ext) = Ods1Writer.SplitName(name);
    if (stem.Length == 0) throw new InvalidOperationException($"ODS-1: filename '{name}' produces an empty stem.");

    var img = ReadImage(image);

    // Find a free header slot in INDEXF window.
    var freeSlot = -1;
    var maxFileNum = 0;
    for (var i = 0; i < IndexfHeaderSlots; i++) {
      var slotFileNum = BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan((IndexfLbn + i) * LbnSize + 2));
      if (slotFileNum == 0) {
        if (freeSlot < 0) freeSlot = i;
      } else if (slotFileNum > maxFileNum) {
        maxFileNum = slotFileNum;
      }
    }
    if (freeSlot < 0)
      throw new NotSupportedException("ODS-1: INDEXF window is full (64 header slots).");

    // Plan data extent: at least one LBN even for zero-byte files (matches writer).
    var blocks = (uint)((data.Length + LbnSize - 1) / LbnSize);
    if (blocks == 0) blocks = 1;

    // Find lowest contiguous free run in BITMAP starting from the data area.
    // A single 512-byte bitmap block can track 4096 LBNs (= 2 MB of volume),
    // which is the hard ceiling for this Stage-1 modifier — beyond that the
    // volume would need a multi-block bitmap which the writer doesn't emit.
    var bitmap = img.AsSpan(BitmapLbn * LbnSize, LbnSize).ToArray();
    var dataStart = (uint)(IndexfLbn + IndexfHeaderSlots);
    var bitmapCapacity = (uint)(LbnSize * 8);
    var startLbn = FindContiguousFreeRun(bitmap, dataStart, bitmapCapacity, blocks);
    if (startLbn == uint.MaxValue)
      throw new NotSupportedException(
        $"ODS-1: BITMAP has no contiguous run of {blocks} free LBNs in the data area.");

    // Grow the image if the allocated extent extends past the current length.
    var endByte = (long)(startLbn + blocks) * LbnSize;
    if (endByte > img.Length) {
      var grown = new byte[endByte];
      Buffer.BlockCopy(img, 0, grown, 0, img.Length);
      img = grown;
    }

    // Mark BITMAP allocated.
    for (var lbn = startLbn; lbn < startLbn + blocks; lbn++)
      SetBitmapBit(img, lbn, allocated: true);

    // Write file header in the chosen slot.
    var newFileNum = (ushort)(maxFileNum + 1);
    WriteFileHeader(img, IndexfLbn + freeSlot, newFileNum, stem, ext, isDirectory: false,
      dataStartLbn: startLbn, dataBlocks: blocks);

    // Copy payload + zero-fill tail of the allocated extent so any forensic-recovery
    // attempt sees zero padding (matches the writer's behaviour).
    var dataByte = (long)startLbn * LbnSize;
    Buffer.BlockCopy(data, 0, img, (int)dataByte, data.Length);
    var tail = (int)((blocks * LbnSize) - data.Length);
    if (tail > 0) img.AsSpan((int)dataByte + data.Length, tail).Clear();

    RecomputeHomeChecksums(img);
    WriteImage(image, img);
  }

  /// <summary>
  /// Removes the named file from an existing ODS-1 image. The file's data
  /// extents are freed in BITMAP.SYS, the data bytes are zero-filled (no
  /// forensic recovery from the resulting image), and the file-header slot
  /// in INDEXF is zero-filled so the reader skips it via its
  /// <c>fileNum == 0</c> check.
  /// </summary>
  /// <returns><c>true</c> when the file was found and removed; <c>false</c>
  /// when no matching entry existed.</returns>
  public static bool RemoveFile(Stream image, string name) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var img = ReadImage(image);
    var (stem, ext) = Ods1Writer.SplitName(name);
    var slot = FindHeaderSlotByName(img, stem, ext);
    if (slot < 0) return false;

    var headerOffset = (IndexfLbn + slot) * LbnSize;

    // Read retrieval pointer (count-1, hi, lo) to discover the data extent.
    var countMinus1 = BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(headerOffset + MpByteOff));
    var hi = BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(headerOffset + MpByteOff + 2));
    var lo = BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(headerOffset + MpByteOff + 4));
    var blocks = (uint)countMinus1 + 1u;
    var startLbn = ((uint)hi << 16) | lo;

    // Free data extents in BITMAP and zero-fill the data bytes.
    for (var lbn = startLbn; lbn < startLbn + blocks; lbn++)
      SetBitmapBit(img, lbn, allocated: false);

    var dataByte = (long)startLbn * LbnSize;
    var dataLen = (long)blocks * LbnSize;
    if (dataByte + dataLen <= img.Length)
      img.AsSpan((int)dataByte, (int)dataLen).Clear();

    // Zero-fill the file-header slot — the reader's fileNum==0 check will skip it.
    img.AsSpan(headerOffset, LbnSize).Clear();

    RecomputeHomeChecksums(img);
    WriteImage(image, img);
    return true;
  }

  // ── internals ─────────────────────────────────────────────────────────────

  private static int FindHeaderSlotByName(byte[] img, string stem, string ext) {
    Span<byte> nameBuf = stackalloc byte[MaxNameStem];
    Span<byte> extBuf = stackalloc byte[MaxNameExt];
    for (var i = 0; i < IndexfHeaderSlots; i++) {
      var headerOffset = (IndexfLbn + i) * LbnSize;
      var fileNum = BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(headerOffset + 2));
      if (fileNum == 0) continue;
      img.AsSpan(headerOffset + IdByteOff, MaxNameStem).CopyTo(nameBuf);
      img.AsSpan(headerOffset + IdByteOff + MaxNameStem, MaxNameExt).CopyTo(extBuf);
      var slotStem = Encoding.ASCII.GetString(nameBuf).TrimEnd('\0', ' ');
      var slotExt = Encoding.ASCII.GetString(extBuf).TrimEnd('\0', ' ');
      if (string.Equals(slotStem, stem, StringComparison.OrdinalIgnoreCase) &&
          string.Equals(slotExt, ext, StringComparison.OrdinalIgnoreCase))
        return i;
    }
    return -1;
  }

  private static uint FindContiguousFreeRun(ReadOnlySpan<byte> bitmap, uint startLbn, uint capacity, uint runLength) {
    uint runStart = 0;
    uint runFree = 0;
    for (var lbn = startLbn; lbn < capacity; lbn++) {
      var byteIdx = (int)(lbn / 8);
      var bit = (int)(lbn % 8);
      var allocated = (bitmap[byteIdx] & (1 << bit)) != 0;
      if (allocated) {
        runFree = 0;
      } else {
        if (runFree == 0) runStart = lbn;
        runFree++;
        if (runFree >= runLength) return runStart;
      }
    }
    return uint.MaxValue;
  }

  private static void SetBitmapBit(byte[] img, uint lbn, bool allocated) {
    var byteIdx = BitmapLbn * LbnSize + (int)(lbn / 8);
    if (byteIdx >= img.Length) return;
    var bit = (byte)(1 << (int)(lbn % 8));
    if (allocated) img[byteIdx] |= bit;
    else img[byteIdx] = (byte)(img[byteIdx] & ~bit);
  }

  private static void WriteFileHeader(
    byte[] image, int headerLbn, ushort fileNum,
    string name, string ext, bool isDirectory,
    uint dataStartLbn, uint dataBlocks) {

    var fh = headerLbn * LbnSize;
    var span = image.AsSpan(fh, LbnSize);
    span.Clear();

    span[0] = IdOffWords;
    span[1] = MpOffWords;
    BinaryPrimitives.WriteUInt16LittleEndian(span[2..], fileNum);
    BinaryPrimitives.WriteUInt16LittleEndian(span[4..], fileNum);  // fid_seq mirrored
    BinaryPrimitives.WriteUInt16LittleEndian(span[6..], 0x0101);   // struclev = Files-11 L1
    BinaryPrimitives.WriteUInt16LittleEndian(span[8..], 0);        // fid_volume
    span[0x0A] = (byte)(isDirectory ? 0x40 : 0x00);

    // Ident area at +64: 9-byte name + 3-byte ext + 2-byte version.
    WriteFixedAscii(span[IdByteOff..(IdByteOff + MaxNameStem)], name);
    WriteFixedAscii(span[(IdByteOff + MaxNameStem)..(IdByteOff + MaxNameStem + MaxNameExt)], ext);

    // Map area at +128: single retrieval pointer (count-1, hi, lo).
    var countMinus1 = dataBlocks == 0 ? (ushort)0 : (ushort)(dataBlocks - 1);
    BinaryPrimitives.WriteUInt16LittleEndian(span[MpByteOff..], countMinus1);
    BinaryPrimitives.WriteUInt16LittleEndian(span[(MpByteOff + 2)..], (ushort)(dataStartLbn >> 16));
    BinaryPrimitives.WriteUInt16LittleEndian(span[(MpByteOff + 4)..], (ushort)(dataStartLbn & 0xFFFF));
  }

  private static void WriteFixedAscii(Span<byte> dest, string value) {
    dest.Clear();
    var max = Math.Min(value.Length, dest.Length);
    for (var i = 0; i < max; i++) {
      var c = value[i];
      dest[i] = c is >= (char)0x20 and < (char)0x7F ? (byte)c : (byte)'?';
    }
  }

  /// <summary>
  /// Recomputes the home block's two additive checksums after mutation —
  /// hm1$w_checksum1 (first-half, bytes 0..0x2C, written at +0x02C) and
  /// hm1$w_checksum2 (second-half, bytes 0..0x1FE, written at +0x1FE).
  /// Matches the algorithm used by <see cref="Ods1Writer.WriteHomeBlock"/>.
  /// </summary>
  private static void RecomputeHomeChecksums(byte[] image) {
    var hb = HomeBlockLbn * LbnSize;
    var span = image.AsSpan(hb, LbnSize);

    // Zero out checksum slots first so they don't contribute to the running sum.
    BinaryPrimitives.WriteUInt16LittleEndian(span[0x02C..], 0);
    BinaryPrimitives.WriteUInt16LittleEndian(span[0x1FE..], 0);

    ushort sum1 = 0;
    for (var i = 0; i < 0x2C; i += 2)
      sum1 += BinaryPrimitives.ReadUInt16LittleEndian(span[i..]);
    BinaryPrimitives.WriteUInt16LittleEndian(span[0x02C..], sum1);

    ushort sum2 = 0;
    for (var i = 0; i < 0x1FE; i += 2)
      sum2 += BinaryPrimitives.ReadUInt16LittleEndian(span[i..]);
    BinaryPrimitives.WriteUInt16LittleEndian(span[0x1FE..], sum2);
  }

  private static byte[] ReadImage(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static void WriteImage(Stream stream, byte[] image) {
    if (stream.CanSeek) {
      stream.Position = 0;
      stream.SetLength(image.Length);
    }
    stream.Write(image, 0, image.Length);
    if (stream.CanSeek) stream.Position = 0;
  }
}
