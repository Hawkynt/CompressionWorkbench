using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Narc;

/// <summary>
/// Reads files from a Nintendo NARC (Archive Resource Compound) container.
/// Supports the flat BTNF variant directly; for nested directory trees the names
/// are synthesized so payload extraction still works.
/// </summary>
public sealed class NarcReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>Gets the entries in this archive in the order BTAF lists them (== file ID order).</summary>
  public IReadOnlyList<NarcEntry> Entries { get; }

  /// <summary>
  /// Opens a NARC archive from a stream.
  /// </summary>
  /// <param name="stream">A seekable stream positioned anywhere; the NITRO header must start at offset 0.</param>
  /// <param name="leaveOpen">If true, do not dispose the underlying stream when this reader is disposed.</param>
  public NarcReader(Stream stream, bool leaveOpen = false) {
    this._stream    = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    if (stream.Length < NarcConstants.NitroHeaderSize)
      throw new InvalidDataException("Stream is too small to be a valid NARC archive.");

    stream.Position = 0;

    Span<byte> nitro = stackalloc byte[NarcConstants.NitroHeaderSize];
    ReadExact(nitro);

    if (!nitro[..4].SequenceEqual(NarcConstants.MagicNarc))
      throw new InvalidDataException("Invalid NARC magic.");

    var bom = BinaryPrimitives.ReadUInt16LittleEndian(nitro[4..6]);
    if (bom != NarcConstants.BomLittleEndian)
      throw new InvalidDataException($"Unsupported NARC byte-order mark: 0x{bom:X4} (expected 0xFFFE).");

    // Version, fileSize, headerSize parsed but not strictly enforced — real-world NARCs sometimes
    // have file_size truncated to 32-bit truncation of the actual size; tolerate that.
    var headerSize   = BinaryPrimitives.ReadUInt16LittleEndian(nitro[12..14]);
    var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(nitro[14..16]);

    if (sectionCount != NarcConstants.SectionCount)
      throw new InvalidDataException($"NARC must declare exactly 3 sections, found {sectionCount}.");

    if (headerSize < NarcConstants.NitroHeaderSize)
      throw new InvalidDataException($"NARC header size too small: {headerSize}.");

    // Sections follow the NITRO header in fixed order: BTAF, BTNF, GMIF.
    var btafOffset = (long)headerSize;
    var (btafCount, btafEntries, btafEnd) = this.ReadBtaf(btafOffset);
    var (names, btnfEnd) = this.ReadBtnf(btafEnd, btafCount);
    var gmifDataStart = this.ReadGmifHeader(btnfEnd);

    var list = new List<NarcEntry>(btafCount);
    for (var i = 0; i < btafCount; ++i) {
      var (start, end) = btafEntries[i];
      // BTAF stores offsets relative to the start of the GMIF data region, not absolute.
      // Translate to absolute stream offsets here so callers don't need to know.
      list.Add(new NarcEntry {
        Name   = names[i],
        Offset = gmifDataStart + start,
        Size   = end - start,
      });
    }

    this.Entries = list;
  }

  /// <summary>Reads the raw bytes for a given entry from the stream.</summary>
  public byte[] Extract(NarcEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Size == 0)
      return [];

    this._stream.Position = entry.Offset;
    var buf = new byte[entry.Size];
    ReadExact(buf);
    return buf;
  }

  private (int Count, (uint Start, uint End)[] Entries, long EndOffset) ReadBtaf(long offset) {
    this._stream.Position = offset;
    Span<byte> hdr = stackalloc byte[NarcConstants.SectionHeaderSize + 4];
    ReadExact(hdr);

    if (!hdr[..4].SequenceEqual(NarcConstants.MagicBtaf))
      throw new InvalidDataException("Missing BTAF section.");

    var sectionSize = BinaryPrimitives.ReadUInt32LittleEndian(hdr[4..8]);
    var count       = (int)BinaryPrimitives.ReadUInt32LittleEndian(hdr[8..12]);
    if (count < 0)
      throw new InvalidDataException($"Invalid BTAF entry count: {count}.");

    var entriesBytes = count * NarcConstants.BtafEntrySize;
    var buf = new byte[entriesBytes];
    ReadExact(buf);

    var entries = new (uint Start, uint End)[count];
    for (var i = 0; i < count; ++i) {
      var s = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i * 8,     4));
      var e = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i * 8 + 4, 4));
      if (e < s)
        throw new InvalidDataException($"BTAF entry {i} has end < start.");
      entries[i] = (s, e);
    }

    return (count, entries, offset + sectionSize);
  }

  private (string[] Names, long EndOffset) ReadBtnf(long offset, int fileCount) {
    this._stream.Position = offset;
    Span<byte> hdr = stackalloc byte[NarcConstants.SectionHeaderSize];
    ReadExact(hdr);

    if (!hdr[..4].SequenceEqual(NarcConstants.MagicBtnf))
      throw new InvalidDataException("Missing BTNF section.");

    var sectionSize = BinaryPrimitives.ReadUInt32LittleEndian(hdr[4..8]);
    var entriesRegionStart = offset + NarcConstants.SectionHeaderSize;
    var entriesRegionEnd   = offset + sectionSize;
    var entriesRegionLen   = entriesRegionEnd - entriesRegionStart;
    if (entriesRegionLen < 8)
      throw new InvalidDataException("BTNF section too small to hold root directory entry.");

    Span<byte> rootEntry = stackalloc byte[8];
    ReadExact(rootEntry);

    var subTableOffset = BinaryPrimitives.ReadUInt32LittleEndian(rootEntry[..4]);
    // We don't trust dirCount alone — non-flat trees would also have entries past the first 8 bytes.
    var dirCount       = BinaryPrimitives.ReadUInt16LittleEndian(rootEntry[6..8]);

    var isFlat = dirCount == 1 && subTableOffset == 8;
    if (!isFlat)
      // Bail to synthesized names rather than implementing the recursive tree walker.
      // Callers can still extract bytes by index; the BTAF offsets are always usable.
      return (SynthesizeNames(fileCount), entriesRegionEnd);

    this._stream.Position = entriesRegionStart + subTableOffset;
    var listBytes = (int)(entriesRegionEnd - this._stream.Position);
    if (listBytes <= 0)
      return (SynthesizeNames(fileCount), entriesRegionEnd);

    var listBuf = new byte[listBytes];
    ReadExact(listBuf);

    var names = new List<string>(fileCount);
    var pos = 0;
    while (pos < listBuf.Length) {
      var lengthByte = listBuf[pos++];
      if (lengthByte == 0x00)
        break;
      // High bit set means subdirectory entry — falling back keeps the reader robust against
      // a malformed flat-tree claim.
      if ((lengthByte & 0x80) != 0)
        return (SynthesizeNames(fileCount), entriesRegionEnd);

      var len = lengthByte & 0x7F;
      if (pos + len > listBuf.Length)
        throw new InvalidDataException("BTNF name list truncated.");
      names.Add(Encoding.ASCII.GetString(listBuf, pos, len));
      pos += len;

      if (names.Count >= fileCount)
        break;
    }

    while (names.Count < fileCount)
      names.Add($"file_{names.Count:D4}.bin");

    return (names.ToArray(), entriesRegionEnd);
  }

  private long ReadGmifHeader(long offset) {
    this._stream.Position = offset;
    Span<byte> hdr = stackalloc byte[NarcConstants.SectionHeaderSize];
    ReadExact(hdr);

    if (!hdr[..4].SequenceEqual(NarcConstants.MagicGmif))
      throw new InvalidDataException("Missing GMIF section.");

    // Section size is read for completeness but not used; BTAF offsets are relative to the byte
    // immediately after this header, which is exactly where the stream is now.
    _ = BinaryPrimitives.ReadUInt32LittleEndian(hdr[4..8]);
    return this._stream.Position;
  }

  private static string[] SynthesizeNames(int count) {
    var arr = new string[count];
    for (var i = 0; i < count; ++i)
      arr[i] = $"file_{i:D4}.bin";
    return arr;
  }

  private void ReadExact(Span<byte> buffer) {
    var total = 0;
    while (total < buffer.Length) {
      var read = this._stream.Read(buffer[total..]);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of NARC stream.");
      total += read;
    }
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed)
      return;
    this._disposed = true;
    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}
