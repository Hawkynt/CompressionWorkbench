namespace FileFormat.Lbr;

/// <summary>
/// Creates CP/M LBR (Library) archive files.
/// </summary>
public sealed class LbrWriter : IDisposable {

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<(string Name, byte[] Data)> _files = [];
  private bool _finished;

  /// <summary>
  /// Creates a new LBR writer that writes to the given stream.
  /// </summary>
  /// <param name="stream">A writable stream.</param>
  /// <param name="leaveOpen">If <see langword="true"/>, the stream is not disposed when this writer is disposed.</param>
  public LbrWriter(Stream stream, bool leaveOpen = false) {
    _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    _leaveOpen = leaveOpen;
  }

  /// <summary>
  /// Adds a file to the archive.
  /// </summary>
  /// <param name="name">Filename in 8.3 format (e.g., "README.TXT"). Converted to uppercase automatically.</param>
  /// <param name="data">The raw file data.</param>
  /// <exception cref="ArgumentException">The filename is invalid for CP/M 8.3 format.</exception>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (_finished)
      throw new InvalidOperationException("Archive has already been finished.");

    var upperName = name.ToUpperInvariant();
    _ValidateFileName(upperName);
    _files.Add((upperName, data));
  }

  /// <summary>
  /// Writes the archive to the underlying stream. Called automatically on <see cref="Dispose"/>.
  /// </summary>
  public void Finish() {
    if (_finished)
      return;

    _finished = true;
    _WriteArchive();
  }

  /// <inheritdoc />
  public void Dispose() {
    Finish();
    if (!_leaveOpen)
      _stream.Dispose();
  }

  private void _WriteArchive() {
    var fileCount = _files.Count;
    var totalDirEntries = 1 + fileCount; // directory entry + file entries
    var dirSectors = (totalDirEntries * LbrConstants.DirectoryEntrySize + LbrConstants.SectorSize - 1) / LbrConstants.SectorSize;
    var totalDirSlots = (dirSectors * LbrConstants.SectorSize) / LbrConstants.DirectoryEntrySize;

    // Build entries and compute offsets
    var currentSector = (ushort)dirSectors;
    Span<byte> entryBuf = stackalloc byte[LbrConstants.DirectoryEntrySize];

    // Write directory entry 0 (self-referencing)
    var dirSelfEntry = new LbrEntry {
      FileName = string.Empty,
      Status = LbrConstants.StatusActive,
      SectorOffset = 0,
      SectorCount = (ushort)dirSectors,
    };
    dirSelfEntry.WriteTo(entryBuf);
    _stream.Write(entryBuf);

    // Write file directory entries
    var fileEntries = new List<(ushort SectorOffset, ushort SectorCount, byte[] Data)>(fileCount);
    for (var i = 0; i < fileCount; ++i) {
      var (name, data) = _files[i];
      var sectorCount = (ushort)((data.Length + LbrConstants.SectorSize - 1) / LbrConstants.SectorSize);
      // Ensure at least 1 sector even for empty files
      if (sectorCount == 0)
        sectorCount = 1;

      var padCount = (byte)((sectorCount * LbrConstants.SectorSize) - data.Length);

      var entry = new LbrEntry {
        FileName = name,
        Status = LbrConstants.StatusActive,
        SectorOffset = currentSector,
        SectorCount = sectorCount,
        PadCount = padCount,
      };

      entry.WriteTo(entryBuf);
      _stream.Write(entryBuf);

      fileEntries.Add((currentSector, sectorCount, data));
      currentSector = (ushort)(currentSector + sectorCount);
    }

    // Fill remaining directory slots with deleted entries
    entryBuf.Clear();
    entryBuf[0] = LbrConstants.StatusDeleted;
    for (var i = totalDirEntries; i < totalDirSlots; ++i)
      _stream.Write(entryBuf);

    // Write file data, each padded to sector boundary with 0x1A
    var fillSector = new byte[LbrConstants.SectorSize];
    Array.Fill(fillSector, LbrConstants.FillByte);

    for (var i = 0; i < fileCount; ++i) {
      var (_, sectorCount, data) = fileEntries[i];
      _stream.Write(data);

      // Pad remaining bytes in last sector
      var totalSize = sectorCount * LbrConstants.SectorSize;
      var remaining = totalSize - data.Length;
      if (remaining > 0)
        _stream.Write(fillSector, 0, remaining);
    }
  }

  private static void _ValidateFileName(string name) {
    if (string.IsNullOrWhiteSpace(name))
      throw new ArgumentException("Filename must not be empty.", nameof(name));

    LbrEntry.SplitFileName(name, out var baseName, out var ext);

    if (baseName.Length == 0 || baseName.Length > LbrConstants.MaxFileNameLength)
      throw new ArgumentException($"Filename base must be 1-{LbrConstants.MaxFileNameLength} characters: '{baseName}'.", nameof(name));

    if (ext.Length > LbrConstants.MaxExtensionLength)
      throw new ArgumentException($"Extension must be 0-{LbrConstants.MaxExtensionLength} characters: '{ext}'.", nameof(name));

    foreach (var c in name) {
      if (c == '.')
        continue;

      // Valid CP/M filename characters: A-Z, 0-9, and a few specials
      if (c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-' or '$' or '#' or '!' or '@')
        continue;

      throw new ArgumentException($"Invalid character '{c}' in CP/M filename: '{name}'.", nameof(name));
    }
  }

}
