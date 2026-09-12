using System.IO.Compression;
using System.Text;

namespace FileFormat.VppV2;

/// <summary>
/// Creates a Volition Package v2 archive (Saint's Row 2 era, .vpp_pc).
/// </summary>
/// <remarks>
/// Per-entry zlib compression (raw deflate via <see cref="ZLibStream"/>) is attempted on every entry;
/// when the compressed result is not smaller than the original the entry is stored uncompressed.
/// The archive-level Compressed flag is set whenever at least one entry ends up zlib-compressed.
/// Whole-archive Condensed mode is not produced.
/// </remarks>
public sealed class VppV2Writer : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly bool _attemptCompression;
  private readonly List<(string Name, byte[] Data)> _entries = [];
  private long _startPosition;
  private bool _finished;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="VppV2Writer"/>.
  /// </summary>
  /// <param name="stream">The stream to write the archive to. Must be writable and seekable.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  /// <param name="compressionLevel">When <see cref="CompressionLevel.NoCompression"/> all entries are stored.</param>
  public VppV2Writer(Stream stream, bool leaveOpen = false, CompressionLevel compressionLevel = CompressionLevel.Optimal) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanWrite)
      throw new ArgumentException("Stream must be writable.", nameof(stream));
    if (!stream.CanSeek)
      throw new ArgumentException("Stream must be seekable (writer backpatches the header).", nameof(stream));

    this._stream             = stream;
    this._leaveOpen          = leaveOpen;
    this._attemptCompression = compressionLevel != CompressionLevel.NoCompression;
  }

  /// <summary>Adds an entry to the archive.</summary>
  /// <param name="name">The full entry name/path (UTF-8 encodable).</param>
  /// <param name="data">The raw uncompressed entry data.</param>
  public void AddEntry(string name, byte[] data) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");

    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    this._entries.Add((name, data));
  }

  /// <summary>Writes the archive contents to the stream and finalises the header.</summary>
  public void Finish() {
    if (this._finished)
      return;
    this._finished = true;

    this._startPosition = this._stream.Position;
    var startPosition = this._startPosition;

    var encoded = EncodeEntries();
    var nameTable = BuildNameTable(encoded, out var nameOffsets);

    var fileCount       = encoded.Count;
    var tocSize         = (uint)(fileCount * VppV2Constants.TocEntrySize);
    var nameTableSize   = (uint)nameTable.Length;
    var tocBlockSize    = (int)AlignUp(tocSize, VppV2Constants.SectionAlignment);
    var nameBlockSize   = (int)AlignUp(nameTableSize, VppV2Constants.SectionAlignment);
    var dataRegionStart = (long)VppV2Constants.RequiredHeaderSizeField + tocBlockSize + nameBlockSize;

    var entryDataOffsets = new uint[fileCount];
    long totalDataSize = 0;
    long totalCompressedSize = 0;
    var anyCompressed = false;

    {
      long cursor = 0;
      for (var i = 0; i < fileCount; ++i) {
        var e = encoded[i];
        if (cursor > uint.MaxValue)
          throw new InvalidOperationException("VPP v2 data region exceeds 4 GiB.");
        entryDataOffsets[i] = (uint)cursor;

        cursor += e.Stored.Length;
        cursor = AlignUp(cursor, VppV2Constants.DataAlignment);

        totalDataSize += e.UncompressedSize;
        totalCompressedSize += e.Stored.Length;
        if (e.IsCompressed)
          anyCompressed = true;
      }

      if (cursor > uint.MaxValue)
        throw new InvalidOperationException("VPP v2 data region exceeds 4 GiB after alignment.");
    }

    // Header (placeholder for ArchiveSize).
    Span<byte> header = stackalloc byte[VppV2Constants.HeaderSize];
    BitConverter.TryWriteBytes(header[0..4], VppV2Constants.Magic);
    BitConverter.TryWriteBytes(header[4..8], VppV2Constants.SupportedVersion);
    BitConverter.TryWriteBytes(header.Slice(VppV2Constants.HeaderSizeFieldOffset, 4), VppV2Constants.RequiredHeaderSizeField);
    BitConverter.TryWriteBytes(header.Slice(VppV2Constants.FileCountFieldOffset, 4), (uint)fileCount);
    BitConverter.TryWriteBytes(header.Slice(VppV2Constants.ArchiveSizeFieldOffset, 4), 0u); // backpatched.
    BitConverter.TryWriteBytes(header.Slice(VppV2Constants.TocSizeFieldOffset, 4), tocSize);
    BitConverter.TryWriteBytes(header.Slice(VppV2Constants.NameTableSizeFieldOffset, 4), nameTableSize);
    BitConverter.TryWriteBytes(header.Slice(VppV2Constants.DataSizeFieldOffset, 4), checked((uint)totalDataSize));
    BitConverter.TryWriteBytes(header.Slice(VppV2Constants.CompressedSizeFieldOffset, 4), checked((uint)totalCompressedSize));
    BitConverter.TryWriteBytes(header.Slice(VppV2Constants.FlagsFieldOffset, 4),
      anyCompressed ? VppV2Constants.FlagArchiveCompressed : 0u);

    // Write the populated header followed by zero-padding out to the 0x800 boundary.
    Span<byte> headerBlock = stackalloc byte[VppV2Constants.SectionAlignment];
    header.CopyTo(headerBlock);
    this._stream.Write(headerBlock);

    // TOC.
    Span<byte> tocEntry = stackalloc byte[VppV2Constants.TocEntrySize];
    for (var i = 0; i < fileCount; ++i) {
      tocEntry.Clear();
      var e = encoded[i];
      var nameOffset = nameOffsets[i];

      BitConverter.TryWriteBytes(tocEntry[0..4], nameOffset);
      BitConverter.TryWriteBytes(tocEntry[4..8], ComputeExtensionOffset(nameTable, nameOffset));
      BitConverter.TryWriteBytes(tocEntry[8..12], entryDataOffsets[i]);
      BitConverter.TryWriteBytes(tocEntry[12..16], (uint)e.UncompressedSize);
      BitConverter.TryWriteBytes(tocEntry[16..20], (uint)e.Stored.Length);
      BitConverter.TryWriteBytes(tocEntry[20..24], e.IsCompressed ? VppV2Constants.FlagEntryCompressed : 0u);
      // Bytes 24..28 stay zero (Padding field).
      this._stream.Write(tocEntry);
    }

    PadToAlignment(VppV2Constants.SectionAlignment);

    // Name table.
    if (nameTable.Length > 0)
      this._stream.Write(nameTable);
    PadToAlignment(VppV2Constants.SectionAlignment);

    // Data region.
    var dataRegionActual = this._stream.Position - startPosition;
    if (dataRegionActual != dataRegionStart)
      throw new InvalidOperationException(
        $"VPP v2 layout drift: expected data region at {dataRegionStart}, actually at {dataRegionActual}.");

    for (var i = 0; i < fileCount; ++i) {
      var e = encoded[i];
      if (e.Stored.Length > 0)
        this._stream.Write(e.Stored);
      // Pad each entry to DataAlignment so the next entry's recorded offset matches the cursor.
      PadToAlignment(VppV2Constants.DataAlignment);
    }

    var endPosition = this._stream.Position;
    var totalSize   = endPosition - startPosition;
    if (totalSize > uint.MaxValue)
      throw new InvalidOperationException($"VPP v2 archive exceeds 4 GiB total size ({totalSize} bytes).");

    // Backpatch ArchiveSize.
    this._stream.Position = startPosition + VppV2Constants.ArchiveSizeFieldOffset;
    this._stream.Write(BitConverter.GetBytes((uint)totalSize));
    this._stream.Position = endPosition;
  }

  private List<EncodedEntry> EncodeEntries() {
    var result = new List<EncodedEntry>(this._entries.Count);
    foreach (var (name, data) in this._entries) {
      if (this._attemptCompression && data.Length > 0) {
        var compressed = TryCompress(data);
        if (compressed != null && compressed.Length < data.Length) {
          result.Add(new EncodedEntry(name, data.Length, compressed, true));
          continue;
        }
      }
      result.Add(new EncodedEntry(name, data.Length, data, false));
    }
    return result;
  }

  private static byte[]? TryCompress(byte[] data) {
    using var ms = new MemoryStream();
    using (var zs = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
      zs.Write(data, 0, data.Length);
    return ms.ToArray();
  }

  private static byte[] BuildNameTable(IReadOnlyList<EncodedEntry> entries, out uint[] offsets) {
    offsets = new uint[entries.Count];
    using var ms = new MemoryStream();
    for (var i = 0; i < entries.Count; ++i) {
      if (ms.Length > uint.MaxValue)
        throw new InvalidOperationException("VPP v2 name table exceeds 4 GiB.");
      offsets[i] = (uint)ms.Length;
      var nameBytes = Encoding.UTF8.GetBytes(entries[i].Name);
      ms.Write(nameBytes, 0, nameBytes.Length);
      ms.WriteByte(0);
    }
    return ms.ToArray();
  }

  private static uint ComputeExtensionOffset(byte[] nameTable, uint nameOffset) {
    // Point at the extension within the name (post-last-dot), or the null terminator if none.
    var i = (int)nameOffset;
    var lastDot = -1;
    while (i < nameTable.Length && nameTable[i] != 0) {
      if (nameTable[i] == (byte)'.')
        lastDot = i;
      ++i;
    }
    if (lastDot >= 0)
      return (uint)(lastDot + 1);
    return (uint)i; // null terminator position (extension-less file).
  }

  private void PadToAlignment(int alignment) {
    var relative = this._stream.Position - this._startPosition;
    var remainder = relative % alignment;
    if (remainder == 0)
      return;
    WriteZeros((int)(alignment - remainder));
  }

  private void WriteZeros(int count) {
    Span<byte> zeros = stackalloc byte[256];
    while (count > 0) {
      var chunk = Math.Min(count, zeros.Length);
      this._stream.Write(zeros[..chunk]);
      count -= chunk;
    }
  }

  private static long AlignUp(long value, long alignment) {
    var remainder = value % alignment;
    return remainder == 0 ? value : value + (alignment - remainder);
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed)
      return;
    this._disposed = true;
    if (!this._finished)
      Finish();
    if (!this._leaveOpen)
      this._stream.Dispose();
  }

  private sealed record EncodedEntry(string Name, long UncompressedSize, byte[] Stored, bool IsCompressed);
}
