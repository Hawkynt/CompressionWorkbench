namespace FileFormat.Lbr;

/// <summary>
/// Reads CP/M LBR (Library) archive files.
/// </summary>
public sealed class LbrReader : IDisposable {

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<LbrEntry> _entries;

  /// <summary>
  /// Creates a new LBR reader over the given stream.
  /// </summary>
  /// <param name="stream">A readable and seekable stream containing the LBR archive.</param>
  /// <param name="leaveOpen">If <see langword="true"/>, the stream is not disposed when this reader is disposed.</param>
  /// <exception cref="InvalidDataException">The stream does not contain a valid LBR directory.</exception>
  public LbrReader(Stream stream, bool leaveOpen = false) {
    _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    _leaveOpen = leaveOpen;
    _entries = [];
    _ReadDirectory();
  }

  /// <summary>
  /// Active file entries in the archive (excludes the directory entry and deleted entries).
  /// </summary>
  public IReadOnlyList<LbrEntry> Entries => _entries;

  /// <summary>
  /// Extracts the raw data for the given entry.
  /// </summary>
  /// <param name="entry">An entry obtained from <see cref="Entries"/>.</param>
  /// <returns>The file data. If the entry has a pad count, trailing padding bytes are removed.</returns>
  public byte[] Extract(LbrEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    _stream.Position = entry.DataOffset;
    var totalBytes = (int)entry.DataLength;
    var buffer = new byte[totalBytes];
    _ReadExactly(buffer);

    // Trim trailing padding if pad count is specified
    if (entry.PadCount > 0 && entry.PadCount < LbrConstants.SectorSize) {
      var actualLength = totalBytes - entry.PadCount;
      if (actualLength > 0)
        return buffer[..actualLength];
    }

    return buffer;
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!_leaveOpen)
      _stream.Dispose();
  }

  private void _ReadDirectory() {
    // Read the first directory entry (must be the self-referencing directory record)
    _stream.Position = 0;
    Span<byte> entryBuf = stackalloc byte[LbrConstants.DirectoryEntrySize];
    _ReadExactly(entryBuf);

    var dirEntry = LbrEntry.Parse(entryBuf);

    // The first entry must be active with offset 0 (self-referencing directory)
    if (dirEntry.Status != LbrConstants.StatusActive || dirEntry.SectorOffset != 0)
      throw new InvalidDataException("Not a valid LBR file: first directory entry is not the self-referencing directory record.");

    var dirSectors = dirEntry.SectorCount;
    if (dirSectors == 0)
      throw new InvalidDataException("Not a valid LBR file: directory has zero sectors.");

    // Calculate number of directory entries (including the directory entry itself)
    var totalSlots = (dirSectors * LbrConstants.SectorSize) / LbrConstants.DirectoryEntrySize;

    // Read remaining entries (slot 0 was the directory itself)
    for (var i = 1; i < totalSlots; ++i) {
      _stream.Position = (long)i * LbrConstants.DirectoryEntrySize;
      _ReadExactly(entryBuf);

      var entry = LbrEntry.Parse(entryBuf);

      // Skip deleted/unused slots and entries with zero sector count
      if (!entry.IsActive || entry.SectorCount == 0)
        continue;

      _entries.Add(entry);
    }
  }

  private void _ReadExactly(Span<byte> buffer) {
    var offset = 0;
    while (offset < buffer.Length) {
      var read = _stream.Read(buffer[offset..]);
      if (read == 0)
        throw new InvalidDataException("Unexpected end of LBR stream.");

      offset += read;
    }
  }

}
