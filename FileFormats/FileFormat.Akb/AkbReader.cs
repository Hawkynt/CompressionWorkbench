using System.Buffers.Binary;

namespace FileFormat.Akb;

/// <summary>
/// Reads entries from a Square Enix AKB audio bank (Final Fantasy / Kingdom Hearts era).
/// Surfaces raw per-entry payload bytes; the per-entry codec (HCA, MSADPCM, IMA-ADPCM, raw PCM)
/// is intentionally not decoded — game-specific dispatch belongs to the caller.
/// </summary>
public sealed class AkbReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>Gets the AKB subformat version byte (1 = single-stream v1, 2 = multi-entry v2).</summary>
  public byte VersionByte { get; }

  /// <summary>Gets the channel-mode byte (1 = mono, 2 = stereo). Informational only.</summary>
  public byte ChannelMode { get; }

  /// <summary>Gets the sample rate in Hz declared by the bank header.</summary>
  public uint SampleRate { get; }

  /// <summary>Gets the loop start position in samples; 0 if the bank declares no loop.</summary>
  public uint LoopStart { get; }

  /// <summary>Gets the loop end position in samples; 0 if the bank declares no loop.</summary>
  public uint LoopEnd { get; }

  /// <summary>Gets the absolute offset where entry payload data begins.</summary>
  public uint ContentOffset { get; }

  /// <summary>Gets the total byte length of the content region.</summary>
  public uint ContentSize { get; }

  /// <summary>Gets all audio entries declared in the bank.</summary>
  public IReadOnlyList<AkbEntry> Entries { get; }

  /// <summary>
  /// Initializes a new <see cref="AkbReader"/> from a stream positioned at the start of an AKB file.
  /// </summary>
  /// <param name="stream">The seekable stream containing the AKB bank.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public AkbReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    if (stream.Length < AkbConstants.HeaderSize)
      throw new InvalidDataException("Stream is too small to contain a valid AKB header.");

    Span<byte> header = stackalloc byte[AkbConstants.HeaderSize];
    ReadExact(header);

    if (!header[..AkbConstants.MagicLength].SequenceEqual(AkbConstants.Magic))
      throw new InvalidDataException("Invalid AKB magic; expected ASCII 'AKB1'.");

    var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(header[4..6]);
    this.VersionByte = header[6];
    this.ChannelMode = header[7];
    this.SampleRate = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
    this.LoopStart = BinaryPrimitives.ReadUInt32LittleEndian(header[12..16]);
    this.LoopEnd = BinaryPrimitives.ReadUInt32LittleEndian(header[16..20]);
    this.ContentOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[20..24]);
    this.ContentSize = BinaryPrimitives.ReadUInt32LittleEndian(header[24..28]);
    var entryCount = BinaryPrimitives.ReadUInt32LittleEndian(header[28..32]);
    // header[32..40] — three reserved/padding UInt32 LE words, ignored.

    if (this.VersionByte != AkbConstants.VersionV1 && this.VersionByte != AkbConstants.VersionV2)
      throw new NotSupportedException($"Unsupported AKB version byte: 0x{this.VersionByte:X2}.");

    if (headerSize < AkbConstants.HeaderSize)
      throw new InvalidDataException($"Invalid AKB HeaderSize: {headerSize} (must be at least {AkbConstants.HeaderSize}).");

    // v1 files commonly omit the entry table and store a single payload between
    // ContentOffset and ContentOffset+ContentSize. Synthesize a single entry so the
    // shape matches v2 callers.
    if (this.VersionByte == AkbConstants.VersionV1 && entryCount == 0) {
      ValidateContentBounds(stream.Length);
      this.Entries = [
        new AkbEntry {
          Name = "entry_000.bin",
          Offset = this.ContentOffset,
          Size = this.ContentSize,
          SampleCount = 0,
          Flags = 0,
        },
      ];
      return;
    }

    if (entryCount > int.MaxValue)
      throw new InvalidDataException($"Implausible AKB EntryCount: {entryCount}.");

    var tableBytes = checked((long)entryCount * AkbConstants.EntryRecordSize);
    if (headerSize + tableBytes > this.ContentOffset)
      throw new InvalidDataException("AKB entry table overlaps the content region.");

    ValidateContentBounds(stream.Length);

    this._stream.Position = headerSize;
    this.Entries = ReadEntries((int)entryCount);
  }

  /// <summary>
  /// Reads the raw payload bytes for a given entry. The codec is not decoded — these are the
  /// raw on-disk bytes between <see cref="AkbEntry.Offset"/> and <see cref="AkbEntry.Offset"/> + <see cref="AkbEntry.Size"/>.
  /// </summary>
  /// <param name="entry">The entry to extract.</param>
  /// <returns>The raw entry payload.</returns>
  public byte[] Extract(AkbEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Size == 0)
      return [];

    this._stream.Position = entry.Offset;
    var buffer = new byte[entry.Size];
    ReadExact(buffer);
    return buffer;
  }

  private void ValidateContentBounds(long streamLength) {
    var contentEnd = (long)this.ContentOffset + this.ContentSize;
    if (contentEnd > streamLength)
      throw new InvalidDataException("AKB content region extends past the end of the stream.");
  }

  private List<AkbEntry> ReadEntries(int count) {
    var entries = new List<AkbEntry>(count);
    Span<byte> buf = stackalloc byte[AkbConstants.EntryRecordSize];

    for (var i = 0; i < count; ++i) {
      ReadExact(buf);
      var dataOffsetRel = BinaryPrimitives.ReadUInt32LittleEndian(buf[0..4]);
      var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(buf[4..8]);
      var sampleCount = BinaryPrimitives.ReadUInt32LittleEndian(buf[8..12]);
      var flags = BinaryPrimitives.ReadUInt32LittleEndian(buf[12..16]);

      // DataOffset is stored relative to ContentOffset — translate to absolute up front
      // so callers and Extract() never have to remember which frame of reference they're in.
      var absoluteOffset = (long)this.ContentOffset + dataOffsetRel;
      if (absoluteOffset + dataSize > (long)this.ContentOffset + this.ContentSize)
        throw new InvalidDataException($"AKB entry {i} extends past the content region.");

      entries.Add(new AkbEntry {
        Name = FormatEntryName(i),
        Offset = absoluteOffset,
        Size = dataSize,
        SampleCount = sampleCount,
        Flags = flags,
      });
    }

    return entries;
  }

  internal static string FormatEntryName(int index) => $"entry_{index:D3}.bin";

  private void ReadExact(Span<byte> buffer) {
    var totalRead = 0;
    while (totalRead < buffer.Length) {
      var read = this._stream.Read(buffer[totalRead..]);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of AKB stream.");
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
