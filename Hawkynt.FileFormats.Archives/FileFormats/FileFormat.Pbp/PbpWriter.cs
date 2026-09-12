namespace FileFormat.Pbp;

/// <summary>
/// Writes a PSP PBP archive containing up to eight fixed-name sections.
/// </summary>
public sealed class PbpWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly byte[]?[] _payloads = new byte[]?[PbpConstants.SectionCount];
  private bool _finished;
  private bool _disposed;

  /// <summary>Gets or sets the version word written into the PBP header. Defaults to 0x00010000.</summary>
  public uint Version { get; set; } = PbpConstants.DefaultVersion;

  /// <summary>
  /// Initializes a new <see cref="PbpWriter"/>.
  /// </summary>
  /// <param name="stream">The stream to write the PBP archive to.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public PbpWriter(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
  }

  /// <summary>
  /// Adds a section payload by its fixed name. Each section may only be added once.
  /// </summary>
  /// <param name="name">The section name (must be one of the eight valid names).</param>
  /// <param name="data">The raw section payload.</param>
  public void AddEntry(string name, byte[] data) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");

    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var index = Array.IndexOf(PbpConstants.SectionNames, name);
    if (index < 0)
      throw new ArgumentException($"Unknown PBP section name '{name}'. Must be one of: {string.Join(", ", PbpConstants.SectionNames)}.", nameof(name));
    if (this._payloads[index] != null)
      throw new ArgumentException($"PBP section '{name}' has already been added.", nameof(name));

    this._payloads[index] = data;
  }

  /// <summary>
  /// Writes the PBP archive to the stream and finishes writing.
  /// </summary>
  public void Finish() {
    if (this._finished)
      return;
    this._finished = true;

    var sizes = new long[PbpConstants.SectionCount];
    for (var i = 0; i < PbpConstants.SectionCount; ++i)
      sizes[i] = this._payloads[i]?.LongLength ?? 0L;

    // Compute the offset where each section's data WOULD start (whether present or not).
    // For empty sections, the offset is the start of the next section's range — which equals
    // the next non-empty section's offset (or EOF for trailing empties).
    var startOffsets = new long[PbpConstants.SectionCount + 1];
    startOffsets[0] = PbpConstants.HeaderSize;
    for (var i = 0; i < PbpConstants.SectionCount; ++i)
      startOffsets[i + 1] = startOffsets[i] + sizes[i];

    var headerOffsets = new uint[PbpConstants.SectionCount];
    for (var i = 0; i < PbpConstants.SectionCount; ++i)
      headerOffsets[i] = checked((uint)startOffsets[i]);

    Span<byte> header = stackalloc byte[PbpConstants.HeaderSize];
    PbpConstants.Magic.CopyTo(header);
    BitConverter.TryWriteBytes(header[4..8], this.Version);
    for (var i = 0; i < PbpConstants.SectionCount; ++i)
      BitConverter.TryWriteBytes(header.Slice(8 + i * 4, 4), headerOffsets[i]);

    this._stream.Write(header);

    for (var i = 0; i < PbpConstants.SectionCount; ++i) {
      var data = this._payloads[i];
      if (data is { Length: > 0 })
        this._stream.Write(data);
    }
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
}
