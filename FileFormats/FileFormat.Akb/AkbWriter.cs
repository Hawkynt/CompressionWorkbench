using System.Buffers.Binary;

namespace FileFormat.Akb;

/// <summary>
/// Creates a Square Enix AKB v2 audio bank from caller-supplied raw audio payloads.
/// The codec is not encoded — supplied bytes are stored verbatim into the content region.
/// </summary>
public sealed class AkbWriter : IDisposable {

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<PendingEntry> _entries = [];
  private bool _finished;
  private bool _disposed;

  /// <summary>Gets or sets the bank-wide sample rate written to the header. Defaults to 44100 Hz.</summary>
  public uint SampleRate { get; set; } = 44100;

  /// <summary>Gets or sets the channel-mode byte (1 = mono, 2 = stereo). Defaults to mono.</summary>
  public byte ChannelMode { get; set; } = AkbConstants.ChannelMono;

  /// <summary>Gets or sets the loop start position (samples). 0 means no loop.</summary>
  public uint LoopStart { get; set; }

  /// <summary>Gets or sets the loop end position (samples). 0 means no loop.</summary>
  public uint LoopEnd { get; set; }

  /// <summary>
  /// Initializes a new <see cref="AkbWriter"/> that will write AKB v2 to <paramref name="stream"/>.
  /// </summary>
  /// <param name="stream">The seekable, writable destination stream.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public AkbWriter(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    if (!stream.CanWrite) throw new ArgumentException("Stream must be writable.", nameof(stream));
    if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable for AKB backpatching.", nameof(stream));
    this._leaveOpen = leaveOpen;
  }

  /// <summary>
  /// Adds an entry to the bank. The supplied bytes are stored verbatim — caller is responsible
  /// for any codec encoding (HCA, MSADPCM, etc.).
  /// </summary>
  /// <param name="name">Display name; preserved on the in-memory entry but the AKB on-disk format
  /// has no name table, so it is not persisted. Names are regenerated as <c>entry_NNN.bin</c> on read.</param>
  /// <param name="data">Raw audio payload bytes.</param>
  /// <param name="sampleCount">Duration in samples (codec-dependent).</param>
  /// <param name="flags">Per-entry flags; bit 0 marks looping.</param>
  public void AddEntry(string name, byte[] data, uint sampleCount = 0, uint flags = 0) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    this._entries.Add(new PendingEntry(name, data, sampleCount, flags));
  }

  /// <summary>
  /// Serializes the bank to the underlying stream. Called automatically on Dispose.
  /// </summary>
  public void Finish() {
    if (this._finished)
      return;
    this._finished = true;

    var entryCount = this._entries.Count;
    var contentOffset = (uint)(AkbConstants.HeaderSize + entryCount * AkbConstants.EntryRecordSize);

    var headerStart = this._stream.Position;

    // Pass 1: write header with placeholder ContentSize. ContentOffset and EntryCount are
    // already known up front; ContentSize is patched after we know how much payload we wrote.
    Span<byte> header = stackalloc byte[AkbConstants.HeaderSize];
    AkbConstants.Magic.CopyTo(header);
    BinaryPrimitives.WriteUInt16LittleEndian(header[4..6], (ushort)AkbConstants.HeaderSize);
    header[6] = AkbConstants.VersionV2;
    header[7] = this.ChannelMode;
    BinaryPrimitives.WriteUInt32LittleEndian(header[8..12], this.SampleRate);
    BinaryPrimitives.WriteUInt32LittleEndian(header[12..16], this.LoopStart);
    BinaryPrimitives.WriteUInt32LittleEndian(header[16..20], this.LoopEnd);
    BinaryPrimitives.WriteUInt32LittleEndian(header[20..24], contentOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(header[24..28], 0u); // ContentSize placeholder
    BinaryPrimitives.WriteUInt32LittleEndian(header[28..32], (uint)entryCount);
    // header[32..40] — three reserved UInt32 LE zeros (already cleared by stackalloc default).
    this._stream.Write(header);

    // Pass 2: write a placeholder entry table. We don't know per-entry DataOffset values until
    // we've laid out the payloads (entries can have varying sizes), so this is overwritten in pass 4.
    var entryTablePos = this._stream.Position;
    Span<byte> zeroRecord = stackalloc byte[AkbConstants.EntryRecordSize];
    for (var i = 0; i < entryCount; ++i)
      this._stream.Write(zeroRecord);

    // Pass 3: write payloads, recording per-entry relative offsets from ContentOffset.
    var contentStart = this._stream.Position;
    var relativeOffsets = new uint[entryCount];
    for (var i = 0; i < entryCount; ++i) {
      relativeOffsets[i] = (uint)(this._stream.Position - contentStart);
      var data = this._entries[i].Data;
      if (data.Length > 0)
        this._stream.Write(data);
    }
    var contentEnd = this._stream.Position;
    var contentSize = (uint)(contentEnd - contentStart);

    // Pass 4: backpatch the entry table now that DataOffset values are known.
    this._stream.Position = entryTablePos;
    Span<byte> recordBuf = stackalloc byte[AkbConstants.EntryRecordSize];
    for (var i = 0; i < entryCount; ++i) {
      var entry = this._entries[i];
      BinaryPrimitives.WriteUInt32LittleEndian(recordBuf[0..4], relativeOffsets[i]);
      BinaryPrimitives.WriteUInt32LittleEndian(recordBuf[4..8], (uint)entry.Data.Length);
      BinaryPrimitives.WriteUInt32LittleEndian(recordBuf[8..12], entry.SampleCount);
      BinaryPrimitives.WriteUInt32LittleEndian(recordBuf[12..16], entry.Flags);
      this._stream.Write(recordBuf);
    }

    // Pass 5: backpatch ContentSize in the header.
    this._stream.Position = headerStart + 24;
    Span<byte> sizeBuf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(sizeBuf, contentSize);
    this._stream.Write(sizeBuf);

    this._stream.Position = contentEnd;
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

  private readonly record struct PendingEntry(string Name, byte[] Data, uint SampleCount, uint Flags);
}
