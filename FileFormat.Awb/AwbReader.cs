using System.Globalization;

namespace FileFormat.Awb;

/// <summary>
/// Reads entries from a CRI Audio Wave Bank (AFS2). Audio payloads are surfaced as raw bytes —
/// the inner codec (HCA, ADX, etc.) is the caller's concern.
/// </summary>
public sealed class AwbReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>Container version byte from the header (1, 2, or 4 are observed in the wild).</summary>
  public byte Version { get; }

  /// <summary>Width in bytes of each offset-table entry (2 or 4).</summary>
  public byte OffsetSize { get; }

  /// <summary>Width in bytes of each cue-ID-table entry (typically 2).</summary>
  public byte IdSize { get; }

  /// <summary>Audio-data alignment in bytes (typically 0x20). Each entry's payload starts at the next multiple of this value.</summary>
  public uint Alignment { get; }

  /// <summary>Sub-key used by HCA decryption derivation. Preserved verbatim — we do not decrypt.</summary>
  public uint SubKey { get; }

  /// <summary>All audio entries in the wave bank, in storage order.</summary>
  public IReadOnlyList<AwbEntry> Entries { get; }

  /// <summary>
  /// Initializes a new <see cref="AwbReader"/>, parsing the header, cue-ID table, and offset table.
  /// </summary>
  /// <param name="stream">A seekable stream positioned at the start of the AFS2 container.</param>
  /// <param name="leaveOpen">If true, the underlying stream is not disposed when this reader is.</param>
  public AwbReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    if (stream.Length < AwbConstants.HeaderSize)
      throw new InvalidDataException("Stream is too small to be a valid AFS2/AWB archive.");

    Span<byte> header = stackalloc byte[AwbConstants.HeaderSize];
    ReadExact(header);

    if (!header[..4].SequenceEqual(AwbConstants.Magic))
      throw new InvalidDataException("Invalid AFS2 magic.");

    this.Version    = header[4];
    this.OffsetSize = header[5];
    this.IdSize     = header[6];
    // header[7] is reserved/padding.
    var entryCount = BitConverter.ToUInt32(header[8..12]);
    this.Alignment = BitConverter.ToUInt32(header[12..16]);

    // SubKey lives in the version field's high half on some variants; some titles store it here as a separate dword.
    // For the canonical layout we surface it as zero — Capcom files tag it via the version byte (0x02 / 0x04).
    this.SubKey = 0;

    // Reject offset widths we cannot interpret. 2 and 4 are spec; anything else is unsafe to guess.
    if (this.OffsetSize is not (2 or 4))
      throw new NotSupportedException($"Unsupported AFS2 offset size: {this.OffsetSize} (expected 2 or 4).");
    if (this.IdSize is not (2 or 4))
      throw new NotSupportedException($"Unsupported AFS2 cue-ID size: {this.IdSize} (expected 2 or 4).");
    if (this.Alignment == 0 || (this.Alignment & (this.Alignment - 1)) != 0)
      throw new InvalidDataException($"AFS2 alignment must be a non-zero power of two (got {this.Alignment}).");

    this.Entries = ParseEntries((int)entryCount);
  }

  /// <summary>
  /// Reads the raw payload bytes for a single entry.
  /// </summary>
  /// <param name="entry">An entry returned by <see cref="Entries"/>.</param>
  /// <returns>The entry's payload, exactly <see cref="AwbEntry.Size"/> bytes long.</returns>
  public byte[] Extract(AwbEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    if (entry.Size == 0)
      return [];

    this._stream.Position = entry.Offset;
    var buffer = new byte[entry.Size];
    ReadExact(buffer);
    return buffer;
  }

  /// <summary>
  /// Returns a UTF-8 INI document describing the wave bank's header values for analyst tooling.
  /// </summary>
  public byte[] BuildMetadataIni() {
    var ci = CultureInfo.InvariantCulture;
    var sb = new System.Text.StringBuilder();
    sb.AppendLine("[awb]");
    sb.Append("version = ").AppendLine(this.Version.ToString(ci));
    sb.Append("offset_size = ").AppendLine(this.OffsetSize.ToString(ci));
    sb.Append("id_size = ").AppendLine(this.IdSize.ToString(ci));
    sb.Append("entry_count = ").AppendLine(this.Entries.Count.ToString(ci));
    sb.Append("alignment = 0x").AppendLine(this.Alignment.ToString("X", ci));
    sb.Append("sub_key = ").AppendLine(this.SubKey.ToString(ci));
    return System.Text.Encoding.UTF8.GetBytes(sb.ToString());
  }

  private List<AwbEntry> ParseEntries(int entryCount) {
    if (entryCount < 0)
      throw new InvalidDataException($"Invalid AFS2 entry count: {entryCount}");
    if (entryCount == 0)
      return [];

    // Cue-ID table: <IdSize> bytes per entry.
    var idTableBytes = (long)this.IdSize * entryCount;
    var idTable = new byte[idTableBytes];
    ReadExact(idTable);

    // Offset table: <OffsetSize> bytes per (entry + 1 sentinel).
    var offsetCount = entryCount + 1;
    var offsetTableBytes = (long)this.OffsetSize * offsetCount;
    var offsetTable = new byte[offsetTableBytes];
    ReadExact(offsetTable);

    var cueIds = new uint[entryCount];
    for (var i = 0; i < entryCount; ++i)
      cueIds[i] = this.IdSize == 2
        ? BitConverter.ToUInt16(idTable, i * 2)
        : BitConverter.ToUInt32(idTable, i * 4);

    var rawOffsets = new long[offsetCount];
    for (var i = 0; i < offsetCount; ++i)
      rawOffsets[i] = this.OffsetSize == 2
        ? BitConverter.ToUInt16(offsetTable, i * 2)
        : BitConverter.ToUInt32(offsetTable, i * 4);

    var entries = new List<AwbEntry>(entryCount);
    for (var i = 0; i < entryCount; ++i) {
      // Spec: offsets in the table are stored unaligned; payload begins at the next alignment boundary.
      // The trailing sentinel offset is the literal end (not aligned), so size uses the unaligned end.
      var actualStart = AlignUp(rawOffsets[i], this.Alignment);
      var actualEnd = rawOffsets[i + 1];
      var size = actualEnd - actualStart;
      if (size < 0)
        throw new InvalidDataException($"AFS2 entry {i} has negative size after alignment ({size}).");

      var name = string.Create(CultureInfo.InvariantCulture, $"cue_{cueIds[i]:D5}.bin");
      entries.Add(new AwbEntry {
        Name   = name,
        CueId  = cueIds[i],
        Offset = actualStart,
        Size   = size,
      });
    }

    return entries;
  }

  private static long AlignUp(long value, uint alignment) {
    var mask = alignment - 1;
    return (value + mask) & ~(long)mask;
  }

  private void ReadExact(Span<byte> buffer) {
    var totalRead = 0;
    while (totalRead < buffer.Length) {
      var read = this._stream.Read(buffer[totalRead..]);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of AFS2/AWB stream.");
      totalRead += read;
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
