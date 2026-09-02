namespace FileFormat.Awb;

/// <summary>
/// Builds a CRI Audio Wave Bank (AFS2) container from in-memory payloads.
/// Writes <see cref="AwbConstants.DefaultVersion"/> with 4-byte offsets and 2-byte cue IDs for maximum compatibility.
/// </summary>
public sealed class AwbWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<(uint CueId, byte[] Data)> _entries = [];
  private uint _alignment = AwbConstants.DefaultAlignment;
  private bool _finished;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="AwbWriter"/>.
  /// </summary>
  /// <param name="stream">A writable, seekable stream to receive the AFS2 container.</param>
  /// <param name="leaveOpen">If true, the underlying stream is not disposed when this writer is.</param>
  public AwbWriter(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
  }

  /// <summary>Audio-data alignment in bytes. Must be a non-zero power of two. Defaults to 0x20.</summary>
  public uint Alignment {
    get => this._alignment;
    set {
      if (this._finished)
        throw new InvalidOperationException("Cannot change alignment after Finish().");
      if (value == 0 || (value & (value - 1)) != 0)
        throw new ArgumentException("Alignment must be a non-zero power of two.", nameof(value));
      this._alignment = value;
    }
  }

  /// <summary>
  /// Adds an entry with an explicit cue ID.
  /// </summary>
  public void AddEntry(uint cueId, byte[] data) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");
    ArgumentNullException.ThrowIfNull(data);
    this._entries.Add((cueId, data));
  }

  /// <summary>
  /// Adds an entry with an auto-assigned sequential cue ID (next available, starting from 0 if empty).
  /// </summary>
  public void AddEntry(byte[] data) {
    var nextId = this._entries.Count == 0 ? 0u : checked(this._entries[^1].CueId + 1);
    this.AddEntry(nextId, data);
  }

  /// <summary>Writes the AFS2 container and finalizes the stream.</summary>
  public void Finish() {
    if (this._finished)
      return;
    this._finished = true;

    var entryCount = this._entries.Count;

    // Layout of the fixed-width tables — pin to version 1's 4-byte offsets / 2-byte IDs to keep readers happy.
    const int idSize = AwbConstants.DefaultIdSize;
    const int offsetSize = AwbConstants.DefaultOffsetSize;
    var idTableSize = idSize * entryCount;
    var offsetTableSize = offsetSize * (entryCount + 1);
    var tablesEnd = AwbConstants.HeaderSize + idTableSize + offsetTableSize;

    // Reader applies AlignUp to every offset[i]. We must hand it values that round-trip; the simplest invariant is:
    //   offset[i]   = aligned start of entry i  (already aligned, AlignUp is a no-op)
    //   offset[i+1] = unaligned end of entry i  (also = unaligned next-start that AlignUp will lift to alignment[i+1])
    // To keep this consistent, the first entry's start must sit at an alignment boundary >= tablesEnd.
    var firstDataStart = AlignUp(tablesEnd, this._alignment);

    // Reader contract: rawOffsets[i] is stored unaligned (the cursor at the
    // boundary between entry i-1 and i, or the initial aligned start for i=0);
    // reader does AlignUp(rawOffsets[i]) to find the actual data start. The
    // sentinel rawOffsets[N] is the literal unaligned end of the last entry —
    // so size_i = rawOffsets[i+1] - AlignUp(rawOffsets[i]).
    var rawOffsets = new long[entryCount + 1];
    var aligned = new long[entryCount];
    var cursor = firstDataStart;
    for (var i = 0; i < entryCount; ++i) {
      rawOffsets[i] = cursor;
      aligned[i] = AlignUp(cursor, this._alignment);
      cursor = aligned[i] + this._entries[i].Data.Length;
    }
    rawOffsets[entryCount] = cursor;

    // Header
    Span<byte> header = stackalloc byte[AwbConstants.HeaderSize];
    AwbConstants.Magic.CopyTo(header[..4]);
    header[4] = AwbConstants.DefaultVersion;
    header[5] = AwbConstants.DefaultOffsetSize;
    header[6] = AwbConstants.DefaultIdSize;
    header[7] = 0;
    BitConverter.TryWriteBytes(header[8..12], (uint)entryCount);
    BitConverter.TryWriteBytes(header[12..16], this._alignment);
    this._stream.Write(header);

    // Cue-ID table (UInt16 LE)
    Span<byte> idBuf = stackalloc byte[2];
    foreach (var (cueId, _) in this._entries) {
      // The container only stores 2 bytes per cue ID at our chosen IdSize=2 — caller-supplied IDs >65535 truncate.
      BitConverter.TryWriteBytes(idBuf, (ushort)cueId);
      this._stream.Write(idBuf);
    }

    // Offset table (UInt32 LE × N+1)
    Span<byte> offBuf = stackalloc byte[4];
    for (var i = 0; i < rawOffsets.Length; ++i) {
      BitConverter.TryWriteBytes(offBuf, (uint)rawOffsets[i]);
      this._stream.Write(offBuf);
    }

    // Pad up to the first aligned data offset.
    PadTo(firstDataStart);

    // Audio payloads, padding between entries to keep each one alignment-anchored.
    for (var i = 0; i < entryCount; ++i) {
      // The reader recomputes aligned[i] from rawOffsets[i] via AlignUp, so we must write at exactly aligned[i].
      var expected = aligned[i];
      if (this._stream.Position != expected)
        PadTo(expected);
      var data = this._entries[i].Data;
      if (data.Length > 0)
        this._stream.Write(data);
    }
  }

  private void PadTo(long target) {
    var current = this._stream.Position;
    if (target < current)
      throw new InvalidOperationException("Internal AFS2 layout error: target is behind cursor.");
    var delta = (int)(target - current);
    if (delta == 0)
      return;
    Span<byte> zeros = stackalloc byte[64];
    zeros.Clear();
    while (delta > 0) {
      var chunk = Math.Min(delta, zeros.Length);
      this._stream.Write(zeros[..chunk]);
      delta -= chunk;
    }
  }

  private static long AlignUp(long value, uint alignment) {
    var mask = alignment - 1;
    return (value + mask) & ~(long)mask;
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed)
      return;
    this._disposed = true;
    if (!this._finished)
      this.Finish();
    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}
