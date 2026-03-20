using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace FileFormat.Wim;

/// <summary>
/// Represents a reshuffled table entry referencing a region of data within a WIM file.
/// </summary>
/// <param name="CompressedSize">Compressed size of the resource data in bytes.</param>
/// <param name="OriginalSize">Uncompressed size of the resource data in bytes.</param>
/// <param name="Offset">Absolute byte offset of the resource within the WIM file.</param>
/// <param name="Flags">Resource flags (see <see cref="WimConstants.ResourceFlagCompressed"/>).</param>
public sealed record WimResourceEntry(
  long CompressedSize,
  long OriginalSize,
  long Offset,
  uint Flags) {
  /// <summary>
  /// Gets a value indicating whether the resource is stored in compressed form.
  /// </summary>
  public bool IsCompressed => (this.Flags & WimConstants.ResourceFlagCompressed) != 0;
}

/// <summary>
/// Represents the WIM file header (208 bytes, version 1.13).
/// </summary>
public sealed class WimHeader {
  /// <summary>Gets the WIM format version number.</summary>
  public uint Version { get; init; } = WimConstants.Version;

  /// <summary>Gets the header flags field (encodes compression type and other attributes).</summary>
  public uint WimFlags { get; init; }

  /// <summary>Gets the compression type for resources in this WIM.</summary>
  public uint CompressionType { get; init; }

  /// <summary>Gets the uncompressed chunk size for compressed resources.</summary>
  public uint ChunkSize { get; init; } = WimConstants.DefaultChunkSize;

  /// <summary>Gets the total number of parts in a split WIM (1 for non-split).</summary>
  public ushort TotalParts { get; init; } = 1;

  /// <summary>Gets the index of this part within a split WIM (1-based).</summary>
  public ushort PartNumber { get; init; } = 1;

  /// <summary>Gets the number of images contained in the WIM.</summary>
  public uint ImageCount { get; init; }

  /// <summary>Gets the resource entry describing the resource table location.</summary>
  public WimResourceEntry? OffsetTableResource { get; init; }

  /// <summary>Gets the resource entry describing the XML metadata location.</summary>
  public WimResourceEntry? XmlDataResource { get; init; }

  /// <summary>Gets the resource entry describing the boot metadata location (may be absent).</summary>
  public WimResourceEntry? BootMetadataResource { get; init; }

  /// <summary>Gets the index of the bootable image (0 = none).</summary>
  public uint BootIndex { get; init; }

  /// <summary>Gets the resource entry describing the integrity table (may be absent).</summary>
  public WimResourceEntry? IntegrityTableResource { get; init; }

  /// <summary>
  /// Writes this header to the given stream at its current position.
  /// The stream must be positioned at the start of the file.
  /// Exactly <see cref="WimConstants.HeaderSize"/> bytes are written.
  /// </summary>
  /// <param name="stream">The stream to write the header to.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
  public void Write(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);

    Span<byte> buf = stackalloc byte[WimConstants.HeaderSize];
    buf.Clear();

    // Magic: "MSWIM\0\0\0"
    WimConstants.Magic.CopyTo(buf);

    // Header size (4 bytes LE at offset 8)
    BinaryPrimitives.WriteUInt32LittleEndian(buf[8..], WimConstants.HeaderSize);

    // Version (4 bytes LE at offset 12)
    BinaryPrimitives.WriteUInt32LittleEndian(buf[12..], this.Version);

    // WIM flags (4 bytes LE at offset 16)
    BinaryPrimitives.WriteUInt32LittleEndian(buf[16..], this.WimFlags);

    // Chunk size (4 bytes LE at offset 20)
    BinaryPrimitives.WriteUInt32LittleEndian(buf[20..], this.ChunkSize);

    // GUID: 16 bytes at offset 24 — left as zeros

    // Part number (2 bytes LE at offset 40)
    BinaryPrimitives.WriteUInt16LittleEndian(buf[40..], this.PartNumber);

    // Total parts (2 bytes LE at offset 42)
    BinaryPrimitives.WriteUInt16LittleEndian(buf[42..], this.TotalParts);

    // Image count (4 bytes LE at offset 44)
    BinaryPrimitives.WriteUInt32LittleEndian(buf[44..], this.ImageCount);

    // Offset table resource entry (24 bytes at offset 48)
    WriteResourceEntry(buf[48..], this.OffsetTableResource);

    // XML data resource entry (24 bytes at offset 72)
    WriteResourceEntry(buf[72..], this.XmlDataResource);

    // Boot metadata resource entry (24 bytes at offset 96)
    WriteResourceEntry(buf[96..], this.BootMetadataResource);

    // Boot index (4 bytes LE at offset 120)
    BinaryPrimitives.WriteUInt32LittleEndian(buf[120..], this.BootIndex);

    // Integrity table resource entry (24 bytes at offset 124)
    WriteResourceEntry(buf[124..], this.IntegrityTableResource);

    // Bytes 148–207 are reserved / unused — already zeroed

    stream.Write(buf);
  }

  /// <summary>
  /// Reads a <see cref="WimHeader"/> from the given stream.
  /// The stream must be positioned at the start of the file.
  /// </summary>
  /// <param name="stream">The stream to read from.</param>
  /// <returns>The parsed WIM header.</returns>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
  /// <exception cref="InvalidDataException">
  /// Thrown when the magic bytes do not match or the header is truncated.
  /// </exception>
  public static WimHeader Read(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);

    Span<byte> buf = stackalloc byte[WimConstants.HeaderSize];
    var bytesRead = stream.ReadAtLeast(buf, WimConstants.HeaderSize, throwOnEndOfStream: false);
    if (bytesRead < WimConstants.HeaderSize)
      ThrowTruncatedHeader();

    // Verify magic
    if (!buf[..WimConstants.MagicLength].SequenceEqual(WimConstants.Magic))
      ThrowInvalidMagic();

    // Read header size field (we accept any value >= our expected 208)
    // uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(buf[8..]);

    var version   = BinaryPrimitives.ReadUInt32LittleEndian(buf[12..]);
    var wimFlags  = BinaryPrimitives.ReadUInt32LittleEndian(buf[16..]);
    var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(buf[20..]);

    var partNumber  = BinaryPrimitives.ReadUInt16LittleEndian(buf[40..]);
    var totalParts  = BinaryPrimitives.ReadUInt16LittleEndian(buf[42..]);
    var imageCount  = BinaryPrimitives.ReadUInt32LittleEndian(buf[44..]);

    var offsetTableResource   = ReadResourceEntry(buf[48..]);
    var xmlDataResource       = ReadResourceEntry(buf[72..]);
    var bootMetadataResource  = ReadResourceEntry(buf[96..]);
    var bootIndex             = BinaryPrimitives.ReadUInt32LittleEndian(buf[120..]);
    var integrityResource     = ReadResourceEntry(buf[124..]);

    // Determine compression type from flags
    uint compressionType;
    if ((wimFlags & WimConstants.FlagLzxCompression) != 0)
      compressionType = WimConstants.CompressionLzx;
    else if ((wimFlags & WimConstants.FlagXpressHuffmanCompression) != 0)
      compressionType = WimConstants.CompressionXpressHuffman;
    else if ((wimFlags & WimConstants.FlagXpressCompression) != 0)
      compressionType = WimConstants.CompressionXpress;
    else if ((wimFlags & WimConstants.FlagLzmsCompression) != 0)
      compressionType = WimConstants.CompressionLzms;
    else
      compressionType = WimConstants.CompressionNone;

    return new WimHeader {
      Version                = version,
      WimFlags               = wimFlags,
      CompressionType        = compressionType,
      ChunkSize              = chunkSize == 0 ? WimConstants.DefaultChunkSize : chunkSize,
      PartNumber             = partNumber == 0 ? (ushort)1 : partNumber,
      TotalParts             = totalParts == 0 ? (ushort)1 : totalParts,
      ImageCount             = imageCount,
      OffsetTableResource    = offsetTableResource,
      XmlDataResource        = xmlDataResource,
      BootMetadataResource   = bootMetadataResource,
      BootIndex              = bootIndex,
      IntegrityTableResource = integrityResource,
    };
  }

  // -------------------------------------------------------------------------
  // Resource entry serialisation (24 bytes in header, 28 bytes in table)
  // -------------------------------------------------------------------------

  // Header resource entries are 24 bytes: 8 compressed, 8 original, 8 offset
  // (no flags field — flags are implied as uncompressed for metadata entries stored in header)
  private static void WriteResourceEntry(Span<byte> dest, WimResourceEntry? entry) {
    if (entry is null)
      return;

    BinaryPrimitives.WriteInt64LittleEndian(dest,      entry.CompressedSize);
    BinaryPrimitives.WriteInt64LittleEndian(dest[8..], entry.OriginalSize);
    BinaryPrimitives.WriteInt64LittleEndian(dest[16..], entry.Offset);
    // 24 bytes total; flags omitted (header entries have no flags word)
  }

  private static WimResourceEntry? ReadResourceEntry(ReadOnlySpan<byte> src) {
    var compressedSize = BinaryPrimitives.ReadInt64LittleEndian(src);
    var originalSize   = BinaryPrimitives.ReadInt64LittleEndian(src[8..]);
    var offset         = BinaryPrimitives.ReadInt64LittleEndian(src[16..]);

    if (compressedSize == 0 && originalSize == 0 && offset == 0)
      return null;

    return new WimResourceEntry(compressedSize, originalSize, offset, WimConstants.ResourceFlagUncompressed);
  }

  // -------------------------------------------------------------------------
  // Throw helpers
  // -------------------------------------------------------------------------

  [DoesNotReturn][StackTraceHidden][MethodImpl(MethodImplOptions.NoInlining)]
  internal static void ThrowInvalidMagic() =>
    throw new InvalidDataException(
      "Not a valid WIM file: magic bytes do not match \"MSWIM\\0\\0\\0\".");

  [DoesNotReturn][StackTraceHidden][MethodImpl(MethodImplOptions.NoInlining)]
  private static void ThrowTruncatedHeader() =>
    throw new InvalidDataException(
      $"WIM file header is truncated (expected {WimConstants.HeaderSize} bytes).");
}
