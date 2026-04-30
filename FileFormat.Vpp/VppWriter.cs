using System.Text;

namespace FileFormat.Vpp;

/// <summary>
/// Creates a Volition Package (VPP_PC v1) archive — Red Faction 1 / Summoner era.
/// </summary>
/// <remarks>
/// Layout: 2048-byte header → index block padded to 2048-byte boundary → each payload padded to a
/// 2048-byte boundary. The writer backpatches the total-file-size field in the header on Finish().
/// </remarks>
public sealed class VppWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<(string Name, byte[] Data)> _entries = [];
  private bool _finished;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="VppWriter"/>.
  /// </summary>
  /// <param name="stream">The stream to write the archive to. Must be writable and seekable.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public VppWriter(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanWrite)
      throw new ArgumentException("Stream must be writable.", nameof(stream));
    if (!stream.CanSeek)
      throw new ArgumentException("Stream must be seekable (writer backpatches the header).", nameof(stream));

    this._stream    = stream;
    this._leaveOpen = leaveOpen;
  }

  /// <summary>
  /// Adds an entry to the archive.
  /// </summary>
  /// <param name="name">The entry name. Must be ≤ 59 ASCII bytes (60-byte field minus null terminator).</param>
  /// <param name="data">The raw entry data.</param>
  public void AddEntry(string name, byte[] data) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");

    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var nameByteLength = Encoding.ASCII.GetByteCount(name);
    if (nameByteLength > VppConstants.MaxNameLength)
      throw new ArgumentException(
        $"Entry name '{name}' is {nameByteLength} bytes; VPP_PC v1 allows at most {VppConstants.MaxNameLength}.",
        nameof(name));

    this._entries.Add((name, data));
  }

  /// <summary>
  /// Writes the VPP_PC archive to the stream and finalises the header.
  /// </summary>
  public void Finish() {
    if (this._finished)
      return;
    this._finished = true;

    var startPosition = this._stream.Position;

    // Step 1: write a placeholder 2048-byte header — total_file_size is unknown until everything is written.
    WriteHeaderPlaceholder();

    // Step 2: index block, padded out to a 2048-byte boundary.
    WriteIndex();

    // Step 3: each payload, each padded to the next 2048-byte boundary.
    foreach (var (_, data) in this._entries) {
      if (data.Length > 0)
        this._stream.Write(data);
      PadToAlignment();
    }

    // Step 4: backpatch total_file_size at offset 12 of the header — this is the final stream length
    // (relative to the writer's start position) so callers writing into a larger container still get
    // a well-formed VPP block.
    var endPosition = this._stream.Position;
    var totalSize   = endPosition - startPosition;
    if (totalSize > uint.MaxValue)
      throw new InvalidOperationException($"VPP_PC v1 archive exceeds 4 GiB total size ({totalSize} bytes).");

    this._stream.Position = startPosition + 12;
    this._stream.Write(BitConverter.GetBytes((uint)totalSize));
    this._stream.Position = endPosition;
  }

  private void WriteHeaderPlaceholder() {
    Span<byte> header = stackalloc byte[VppConstants.HeaderSize];
    BitConverter.TryWriteBytes(header[0..4],   VppConstants.Magic);
    BitConverter.TryWriteBytes(header[4..8],   VppConstants.SupportedVersion);
    BitConverter.TryWriteBytes(header[8..12],  (uint)this._entries.Count);
    BitConverter.TryWriteBytes(header[12..16], 0u); // total_file_size — backpatched in Finish().
    this._stream.Write(header);
  }

  private void WriteIndex() {
    var indexEntriesSize = this._entries.Count * VppConstants.IndexEntrySize;
    var indexBlockSize   = AlignUp(indexEntriesSize, VppConstants.Alignment);

    Span<byte> entryBuf = stackalloc byte[VppConstants.IndexEntrySize];
    foreach (var (name, data) in this._entries) {
      entryBuf.Clear();
      WriteEntryName(entryBuf[..VppConstants.NameFieldSize], name);
      BitConverter.TryWriteBytes(entryBuf[VppConstants.NameFieldSize..VppConstants.IndexEntrySize], (uint)data.Length);
      this._stream.Write(entryBuf);
    }

    // Pad the partially filled index block out to a 2048-byte boundary.
    var trailing = indexBlockSize - indexEntriesSize;
    if (trailing > 0)
      WriteZeros(trailing);
  }

  private void PadToAlignment() {
    var position  = this._stream.Position;
    var remainder = position % VppConstants.Alignment;
    if (remainder == 0)
      return;
    WriteZeros((int)(VppConstants.Alignment - remainder));
  }

  private void WriteZeros(int count) {
    Span<byte> zeros = stackalloc byte[VppConstants.Alignment];
    while (count > 0) {
      var chunk = Math.Min(count, zeros.Length);
      this._stream.Write(zeros[..chunk]);
      count -= chunk;
    }
  }

  private static int AlignUp(int value, int alignment) {
    var remainder = value % alignment;
    return remainder == 0 ? value : value + (alignment - remainder);
  }

  private static void WriteEntryName(Span<byte> destination, string name) {
    // Re-validate at write time — AddEntry already checked but defence-in-depth keeps the field invariant local.
    var byteCount = Encoding.ASCII.GetByteCount(name);
    if (byteCount > VppConstants.MaxNameLength)
      throw new InvalidOperationException($"Internal: name '{name}' too long.");

    var written = Encoding.ASCII.GetBytes(name, destination);
    // Remaining bytes in the 60-byte field stay zero (Clear()'d by caller), guaranteeing the null terminator.
    _ = written;
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
