#pragma warning disable CS1591
using System.Buffers.Binary;

namespace CompressionWorkbench.FileFormat.Ico;

/// <summary>
/// In-place modifier for Windows ICO/CUR icon bundles. Performs Add / Remove
/// against the existing ICONDIR header + ICONDIRENTRY table at the head of
/// the image, and the per-image payloads packed after it. No full rebuild,
/// no fresh re-encoding: the payload bytes of every untouched image are
/// preserved byte-identically, only their absolute file offsets change as
/// the directory grows or shrinks.
///
/// <para>Layout reminders (LE throughout):
/// <list type="bullet">
///   <item>Offsets 0..5: ICONDIR (reserved=0 / type=1 or 2 / count).</item>
///   <item>Offsets 6..6+16*count: ICONDIRENTRY table, 16 bytes each.</item>
///   <item>Payloads follow the directory in the order their dir entries reference
///         them via the (size, offset) fields at +8/+12.</item>
/// </list></para>
///
/// <para>Add appends a new ICONDIRENTRY at the end of the directory and the new
/// payload at the end of the file. The first existing payload is shifted by 16
/// bytes (the new directory entry's width); every existing entry's offset field
/// is patched accordingly. Image content bytes are copied verbatim.</para>
///
/// <para>Remove deletes the named entry's payload bytes from the file and
/// collapses its 16-byte directory slot. Surviving entries' offset fields are
/// patched to compensate for the removed bytes. Removed payload bytes are
/// physically wiped from the image — no forensic recovery.</para>
///
/// <para>Out of scope: same-size in-place replacement (the existing reader
/// surfaces images by index — replacement is "remove old + add new" which
/// changes the index but keeps the bundle valid).</para>
///
/// <para>Spec source: Microsoft ICO/CUR documentation (devblogs.microsoft.com
/// "The evolution of the ICO file format") + ICONDIR / ICONDIRENTRY in winuser.h.</para>
/// </summary>
public static class IcoInPlaceModifier {

  private const int DirHeaderSize = 6;
  private const int DirEntrySize = 16;

  /// <summary>
  /// Appends <paramref name="image"/> (PNG or BMP bytes — same input rules as
  /// <see cref="IcoWriter"/>) to <paramref name="archive"/>. The directory entry
  /// is emitted in the same encoding the writer would have produced for a
  /// from-scratch bundle. Existing image payloads are shifted by 16 bytes to
  /// make room for the new directory entry; their byte content is unchanged.
  /// </summary>
  /// <exception cref="ArgumentNullException">Either argument is null.</exception>
  /// <exception cref="InvalidDataException">The archive is not a well-formed ICO/CUR.</exception>
  /// <exception cref="NotSupportedException">The bundle is already at the 65535-image cap.</exception>
  public static void AddImage(Stream archive, byte[] image) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(image);

    var (header, blob) = ReadAll(archive);
    if (header.Count >= ushort.MaxValue)
      throw new NotSupportedException("ICO: bundle already at 65535-image cap.");

    var encoded = EncodeForDir(image, header.IsCursor);

    // Step 1: shift all bytes after the directory by 16 to make room for the
    // new dir entry; step 2: patch every existing entry's offset by +16; step 3:
    // write the new dir entry; step 4: append the new payload at EOF; step 5:
    // bump the count and rewrite the blob to the stream.
    var oldDirEnd = DirHeaderSize + DirEntrySize * header.Count;
    var newDirEnd = oldDirEnd + DirEntrySize;
    var tailLen = blob.Length - oldDirEnd;

    var grown = new byte[blob.Length + DirEntrySize + encoded.Payload.Length];

    // Copy ICONDIR + existing entries.
    blob.AsSpan(0, oldDirEnd).CopyTo(grown);
    // Reserve 16 bytes for the new entry at [oldDirEnd, newDirEnd).
    // Copy existing payload tail to [newDirEnd, newDirEnd + tailLen).
    blob.AsSpan(oldDirEnd, tailLen).CopyTo(grown.AsSpan(newDirEnd));

    // Patch existing entries' payload offsets by +16.
    for (var i = 0; i < header.Count; i++) {
      var entryOff = DirHeaderSize + DirEntrySize * i;
      var oldOff = BinaryPrimitives.ReadUInt32LittleEndian(grown.AsSpan(entryOff + 12, 4));
      BinaryPrimitives.WriteUInt32LittleEndian(grown.AsSpan(entryOff + 12, 4), oldOff + DirEntrySize);
    }

    // Write the new dir entry.
    var newEntryOff = oldDirEnd;
    var newPayloadOff = (uint)(grown.Length - encoded.Payload.Length);
    WriteDirEntry(grown.AsSpan(newEntryOff, DirEntrySize), encoded, newPayloadOff, header.IsCursor);

    // Write the new payload at EOF.
    encoded.Payload.CopyTo(grown.AsSpan((int)newPayloadOff));

    // Bump count.
    BinaryPrimitives.WriteUInt16LittleEndian(grown.AsSpan(4, 2), (ushort)(header.Count + 1));

    WriteBlob(archive, grown);
  }

  /// <summary>
  /// Removes the image with the given <paramref name="entryName"/> (matching
  /// the reader's computed display name, e.g. <c>icon_00_32x32x32.png</c>) from
  /// the bundle. The directory slot collapses, the payload bytes are physically
  /// deleted from the file, and surviving payload offsets are patched down to
  /// reflect the freed 16+payload byte savings.
  /// </summary>
  /// <exception cref="ArgumentNullException">Either argument is null.</exception>
  /// <exception cref="InvalidDataException">The archive is not a well-formed ICO/CUR.</exception>
  /// <exception cref="FileNotFoundException">The named entry doesn't exist in the bundle.</exception>
  public static void RemoveImage(Stream archive, string entryName) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);

    var (header, blob) = ReadAll(archive);
    var bundle = IcoReader.Read(blob);

    var targetIndex = -1;
    for (var i = 0; i < bundle.Entries.Count; i++) {
      if (string.Equals(bundle.Entries[i].Name, entryName, StringComparison.OrdinalIgnoreCase)) {
        targetIndex = i;
        break;
      }
    }
    if (targetIndex < 0)
      throw new FileNotFoundException($"ICO: no entry named '{entryName}' in the bundle.");

    // Read the directory entry for the target so we know which byte range
    // inside the blob to delete (size + offset).
    var entryDirOff = DirHeaderSize + DirEntrySize * targetIndex;
    var payloadLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(entryDirOff + 8, 4));
    var payloadOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(entryDirOff + 12, 4));

    // New blob = old blob minus the 16-byte dir slot minus the payloadLen bytes.
    var newSize = blob.Length - DirEntrySize - payloadLen;
    var shrunk = new byte[newSize];

    // Bytes preserved before the deleted dir entry: [0, entryDirOff).
    blob.AsSpan(0, entryDirOff).CopyTo(shrunk);
    // Bytes preserved between the deleted dir slot and the deleted payload: this
    // is the rest of the directory + the payload bytes that come before payloadOff.
    var midStart = entryDirOff + DirEntrySize;
    var midLen = payloadOff - midStart;
    if (midLen > 0)
      blob.AsSpan(midStart, midLen).CopyTo(shrunk.AsSpan(entryDirOff));
    // Bytes preserved after the deleted payload.
    var tailStart = payloadOff + payloadLen;
    var tailLen = blob.Length - tailStart;
    if (tailLen > 0)
      blob.AsSpan(tailStart, tailLen).CopyTo(shrunk.AsSpan(entryDirOff + midLen));

    // Patch surviving entries' offsets:
    //   - every entry now sits 16 bytes earlier inside the directory area
    //     (because we collapsed one slot), so subtract 16 from every entry
    //     whose dir-slot index was > targetIndex (they shifted left by 16).
    //   - every payload that lived after the deleted payload is now payloadLen
    //     bytes closer to the start of the file.
    var survivorCount = header.Count - 1;
    BinaryPrimitives.WriteUInt16LittleEndian(shrunk.AsSpan(4, 2), (ushort)survivorCount);

    for (var i = 0; i < survivorCount; i++) {
      var entryOff = DirHeaderSize + DirEntrySize * i;
      var oldOff = BinaryPrimitives.ReadUInt32LittleEndian(shrunk.AsSpan(entryOff + 12, 4));

      // Every payload moved up by 16 because the dir shrunk by 16.
      var newOff = oldOff - DirEntrySize;
      // Payloads after the removed one also moved up by payloadLen.
      if (oldOff > (uint)payloadOff)
        newOff -= (uint)payloadLen;

      BinaryPrimitives.WriteUInt32LittleEndian(shrunk.AsSpan(entryOff + 12, 4), newOff);
    }

    WriteBlob(archive, shrunk);
  }

  // ── helpers ────────────────────────────────────────────────────────────────

  private readonly record struct IcoHeader(bool IsCursor, int Count);

  private static (IcoHeader Header, byte[] Blob) ReadAll(Stream archive) {
    if (archive.CanSeek) archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    var blob = ms.ToArray();

    if (blob.Length < DirHeaderSize) throw new InvalidDataException("ICO: truncated header.");
    var reserved = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0, 2));
    var type = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(2, 2));
    var count = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(4, 2));
    if (reserved != 0) throw new InvalidDataException($"ICO: reserved field is 0x{reserved:X4}.");
    if (type is not 1 and not 2) throw new InvalidDataException($"ICO: unknown type {type}.");
    if (DirHeaderSize + DirEntrySize * count > blob.Length)
      throw new InvalidDataException("ICO: truncated directory.");

    return (new IcoHeader(type == 2, count), blob);
  }

  private static void WriteBlob(Stream archive, byte[] blob) {
    if (!archive.CanSeek)
      throw new NotSupportedException("ICO in-place modifier requires a seekable stream.");
    archive.Position = 0;
    archive.SetLength(blob.Length);
    archive.Write(blob, 0, blob.Length);
    archive.Flush();
  }

  private readonly record struct EncodedImage(byte[] Payload, int Width, int Height, int Bpp, bool IsPng);

  private static EncodedImage EncodeForDir(byte[] image, bool isCursor) {
    // Mirror IcoWriter's encoding decisions so a bundle round-trips through the
    // existing reader without behavioural drift.
    if (IsPng(image)) {
      var (w, h, bpp) = ReadPngDimensions(image);
      return new EncodedImage(image, w, h, bpp, true);
    }
    if (IsBmp(image)) {
      var dib = ConvertBmpToIconDib(image, out var w, out var h, out var bpp);
      return new EncodedImage(dib, w, h, bpp, false);
    }
    throw new ArgumentException("ICO: image is neither a PNG nor a BMP.");
  }

  private static void WriteDirEntry(Span<byte> dest, EncodedImage e, uint payloadOff, bool isCursor) {
    dest[0] = (byte)(e.Width == 256 ? 0 : e.Width);
    dest[1] = (byte)(e.Height == 256 ? 0 : e.Height);
    dest[2] = (byte)(e.Bpp <= 8 ? (1 << e.Bpp) & 0xFF : 0);
    dest[3] = 0;
    BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(4, 2), (ushort)(isCursor ? 0 : 1));
    BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(6, 2), (ushort)(isCursor ? 0 : e.Bpp));
    BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(8, 4), (uint)e.Payload.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(12, 4), payloadOff);
  }

  // ── PNG / BMP helpers mirrored from IcoWriter ─────────────────────────────

  private static bool IsPng(ReadOnlySpan<byte> data) =>
    data.Length >= 8
    && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
    && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A;

  private static bool IsBmp(ReadOnlySpan<byte> data) =>
    data.Length >= 14 && data[0] == (byte)'B' && data[1] == (byte)'M';

  private static (int W, int H, int Bpp) ReadPngDimensions(ReadOnlySpan<byte> png) {
    if (png.Length < 8 + 8 + 13) throw new InvalidDataException("PNG: truncated");
    if (png[12] != 'I' || png[13] != 'H' || png[14] != 'D' || png[15] != 'R')
      throw new InvalidDataException("PNG: first chunk is not IHDR");
    var w = (int)BinaryPrimitives.ReadUInt32BigEndian(png[16..]);
    var h = (int)BinaryPrimitives.ReadUInt32BigEndian(png[20..]);
    var depth = png[24];
    var colorType = png[25];
    var channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 4 };
    return (w, h, depth * channels);
  }

  private static byte[] ConvertBmpToIconDib(ReadOnlySpan<byte> bmp, out int width, out int height, out int bpp) {
    if (bmp.Length < 14 + 12) throw new InvalidDataException("BMP: truncated");
    var pixelOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bmp.Slice(10, 4));
    var dibStart = 14;
    var dibHeaderSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(bmp.Slice(dibStart, 4));
    if (dibHeaderSize < 12) throw new InvalidDataException("BMP: invalid DIB header size");

    int rawW, rawH, rawBpp;
    if (dibHeaderSize == 12) {
      rawW = BinaryPrimitives.ReadInt16LittleEndian(bmp.Slice(dibStart + 4, 2));
      rawH = BinaryPrimitives.ReadInt16LittleEndian(bmp.Slice(dibStart + 6, 2));
      rawBpp = BinaryPrimitives.ReadUInt16LittleEndian(bmp.Slice(dibStart + 10, 2));
    } else {
      rawW = BinaryPrimitives.ReadInt32LittleEndian(bmp.Slice(dibStart + 4, 4));
      rawH = BinaryPrimitives.ReadInt32LittleEndian(bmp.Slice(dibStart + 8, 4));
      rawBpp = BinaryPrimitives.ReadUInt16LittleEndian(bmp.Slice(dibStart + 14, 2));
    }
    if (rawH < 0) throw new NotSupportedException("BMP: top-down bitmaps not supported for ICO encoding");

    width = rawW;
    height = rawH;
    bpp = rawBpp;

    var pixelLen = bmp.Length - pixelOffset;
    var dibTailLen = pixelOffset - 14;
    var maskRowBytes = ((rawW + 31) / 32) * 4;
    var maskLen = maskRowBytes * rawH;

    var output = new byte[dibTailLen + pixelLen + maskLen];
    bmp.Slice(14, dibTailLen).CopyTo(output);
    bmp.Slice(pixelOffset, pixelLen).CopyTo(output.AsSpan(dibTailLen));

    if (dibHeaderSize == 12) {
      BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(6), (short)(rawH * 2));
    } else {
      BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8), rawH * 2);
    }
    if (dibHeaderSize >= 24) {
      BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(20), (uint)(pixelLen + maskLen));
    }

    return output;
  }
}
