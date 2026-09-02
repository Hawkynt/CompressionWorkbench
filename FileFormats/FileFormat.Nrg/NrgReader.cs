using System.Text;

namespace FileFormat.Nrg;

/// <summary>
/// Reads the ISO 9660 file system embedded in a Nero Burning ROM NRG disc image.
/// NRG images carry a footer at the end of the file identifying the format version
/// and providing a chunk table that describes the track layout.
/// <para>
/// Footer layout:
/// <list type="bullet">
///   <item>NRG v2: last 12 bytes — "NER5" (4 bytes) + uint64 BE offset to chunk table.</item>
///   <item>NRG v1: last 8 bytes — "NERO" (4 bytes) + uint32 BE offset to chunk table.</item>
/// </list>
/// This reader parses the footer to locate the data area, then heuristically detects
/// the sector geometry and parses the ISO 9660 file system.
/// </para>
/// </summary>
public sealed class NrgReader : IDisposable {
  // Footer magic
  private static readonly byte[] MagicNer5 = [(byte)'N', (byte)'E', (byte)'R', (byte)'5'];
  private static readonly byte[] MagicNero = [(byte)'N', (byte)'E', (byte)'R', (byte)'O'];

  // ISO 9660 constants
  private const int Iso9660SectorSize = 2048;
  private const int RawSectorSize = 2352;
  private const int SectorSize2336 = 2336;
  private const int PvdLba = 16;
  private const int Mode1DataOffset = 16;
  private const int Mode2Form1DataOffset = 24;

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  private readonly int _sectorSize;
  private readonly int _dataOffset;

  // Offset within the stream where the data (track) area begins
  private readonly long _dataAreaOffset;

  /// <summary>Gets the NRG format version detected from the footer (1 or 2), or 0 if no valid footer was found.</summary>
  public int Version { get; }

  /// <summary>Gets all file and directory entries found in the ISO 9660 file system.</summary>
  public IReadOnlyList<NrgEntry> Entries { get; }

  /// <summary>
  /// Initializes a new <see cref="NrgReader"/> from an NRG stream.
  /// </summary>
  /// <param name="stream">The stream containing the NRG image data.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public NrgReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    (this.Version, this._dataAreaOffset) = ReadFooter(stream);

    // Treat the data area as a sub-stream starting at _dataAreaOffset
    (this._sectorSize, this._dataOffset) = DetectSectorGeometry(stream, this._dataAreaOffset);

    var entries = new List<NrgEntry>();
    TryParseIso9660(entries);
    this.Entries = entries;
  }

  /// <summary>
  /// Extracts the raw data for a file entry.
  /// </summary>
  /// <param name="entry">The file entry to extract. Must not be a directory.</param>
  /// <returns>The file data bytes.</returns>
  public byte[] Extract(NrgEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory)
      throw new ArgumentException("Cannot extract a directory entry.", nameof(entry));
    if (entry.Size == 0)
      return [];

    return ReadFileData(entry.StartLba, (int)entry.Size);
  }

  // -------------------------------------------------------------------------
  // Footer parsing
  // -------------------------------------------------------------------------

  private static (int Version, long DataAreaOffset) ReadFooter(Stream stream) {
    if (stream.Length < 12)
      return (0, 0);

    // Check NRG v2: last 12 bytes = "NER5" + 8-byte BE offset
    stream.Position = stream.Length - 12;
    Span<byte> footer12 = stackalloc byte[12];
    if (stream.Read(footer12) == 12 &&
        footer12[0] == MagicNer5[0] && footer12[1] == MagicNer5[1] &&
        footer12[2] == MagicNer5[2] && footer12[3] == MagicNer5[3]) {
      var chunkOffset = ReadUInt64BE(footer12, 4);
      // Data area starts at beginning of file; chunk table is at chunkOffset
      // The data area offset is 0 (data precedes the chunk table)
      return (2, 0);
    }

    // Check NRG v1: last 8 bytes = "NERO" + 4-byte BE offset
    if (stream.Length >= 8) {
      stream.Position = stream.Length - 8;
      Span<byte> footer8 = stackalloc byte[8];
      if (stream.Read(footer8) == 8 &&
          footer8[0] == MagicNero[0] && footer8[1] == MagicNero[1] &&
          footer8[2] == MagicNero[2] && footer8[3] == MagicNero[3]) {
        return (1, 0);
      }
    }

    // No recognized footer — treat entire stream as raw sector data starting at offset 0
    return (0, 0);
  }

  // -------------------------------------------------------------------------
  // Sector geometry detection
  // -------------------------------------------------------------------------

  private static (int SectorSize, int DataOffset) DetectSectorGeometry(Stream stream, long dataAreaOffset) {
    if (TryProbe(stream, dataAreaOffset, RawSectorSize, Mode1DataOffset))
      return (RawSectorSize, Mode1DataOffset);

    if (TryProbe(stream, dataAreaOffset, RawSectorSize, Mode2Form1DataOffset))
      return (RawSectorSize, Mode2Form1DataOffset);

    if (TryProbe(stream, dataAreaOffset, SectorSize2336, 8))
      return (SectorSize2336, 8);

    if (TryProbe(stream, dataAreaOffset, Iso9660SectorSize, 0))
      return (Iso9660SectorSize, 0);

    return (RawSectorSize, Mode1DataOffset);
  }

  private static bool TryProbe(Stream stream, long dataAreaOffset, int sectorSize, int dataOffset) {
    var pvdPos = dataAreaOffset + (long)PvdLba * sectorSize + dataOffset;
    if (pvdPos + 6 > stream.Length)
      return false;

    Span<byte> sig = stackalloc byte[6];
    stream.Position = pvdPos;
    var read = stream.Read(sig);
    if (read < 6)
      return false;

    return sig[0] == 1 &&
           sig[1] == (byte)'C' &&
           sig[2] == (byte)'D' &&
           sig[3] == (byte)'0' &&
           sig[4] == (byte)'0' &&
           sig[5] == (byte)'1';
  }

  // -------------------------------------------------------------------------
  // ISO 9660 parsing
  // -------------------------------------------------------------------------

  private void TryParseIso9660(List<NrgEntry> entries) {
    var pvd = ReadSector(PvdLba);
    if (pvd == null)
      return;

    if (pvd[0] != 1 ||
        pvd[1] != (byte)'C' || pvd[2] != (byte)'D' ||
        pvd[3] != (byte)'0' || pvd[4] != (byte)'0' || pvd[5] != (byte)'1')
      return;

    var rootLba = (int)ReadUInt32LE(pvd, 156 + 2);
    var rootSize = (int)ReadUInt32LE(pvd, 156 + 10);

    WalkDirectory(rootLba, rootSize, "", entries);
  }

  private void WalkDirectory(int dirLba, int dirSize, string parentPath, List<NrgEntry> entries) {
    if (dirLba <= 0 || dirSize <= 0)
      return;

    var bytesRead = 0;
    var currentLba = dirLba;
    var bufOffset = 0;
    byte[]? sector = null;

    while (bytesRead < dirSize) {
      if (sector == null || bufOffset >= Iso9660SectorSize) {
        sector = ReadSector(currentLba);
        if (sector == null)
          return;
        currentLba++;
        bufOffset = 0;
      }

      var recordLen = sector[bufOffset];

      if (recordLen == 0) {
        var remaining = Iso9660SectorSize - bufOffset;
        bytesRead += remaining;
        bufOffset = Iso9660SectorSize;
        continue;
      }

      if (bufOffset + recordLen > Iso9660SectorSize) {
        var remaining = Iso9660SectorSize - bufOffset;
        bytesRead += remaining;
        bufOffset = Iso9660SectorSize;
        continue;
      }

      var record = sector.AsSpan(bufOffset, recordLen);
      ParseDirectoryRecord(record, parentPath, entries);

      bytesRead += recordLen;
      bufOffset += recordLen;
    }
  }

  private void ParseDirectoryRecord(ReadOnlySpan<byte> record, string parentPath, List<NrgEntry> entries) {
    if (record.Length < 34)
      return;

    var dataLba = (int)ReadUInt32LE(record, 2);
    var dataLen = (int)ReadUInt32LE(record, 10);
    var flags = record[25];
    var idLen = record[32];

    if (idLen == 0 || record.Length < 33 + idLen)
      return;

    if (idLen == 1 && (record[33] == 0x00 || record[33] == 0x01))
      return;

    var isDirectory = (flags & 0x02) != 0;
    var rawName = Encoding.ASCII.GetString(record.Slice(33, idLen));
    var name = StripVersionSuffix(rawName);

    if (string.IsNullOrEmpty(name))
      return;

    var fullPath = parentPath.Length > 0 ? parentPath + "/" + name : name;

    entries.Add(new NrgEntry {
      Name = name,
      FullPath = fullPath,
      IsDirectory = isDirectory,
      Size = isDirectory ? 0 : dataLen,
      StartLba = dataLba,
    });

    if (isDirectory)
      WalkDirectory(dataLba, dataLen, fullPath, entries);
  }

  // -------------------------------------------------------------------------
  // Data extraction
  // -------------------------------------------------------------------------

  private byte[] ReadFileData(int startLba, int size) {
    var result = new byte[size];
    var written = 0;
    var lba = startLba;

    while (written < size) {
      var sector = ReadSector(lba);
      if (sector == null)
        break;

      var toCopy = Math.Min(Iso9660SectorSize, size - written);
      sector.AsSpan(0, toCopy).CopyTo(result.AsSpan(written));
      written += toCopy;
      lba++;
    }

    return result;
  }

  private byte[]? ReadSector(int lba) {
    var sectorStart = this._dataAreaOffset + (long)lba * this._sectorSize;
    var dataStart = sectorStart + this._dataOffset;

    if (dataStart + Iso9660SectorSize > this._stream.Length)
      return null;

    this._stream.Position = dataStart;
    var buf = new byte[Iso9660SectorSize];
    var totalRead = 0;
    while (totalRead < buf.Length) {
      var read = this._stream.Read(buf, totalRead, buf.Length - totalRead);
      if (read == 0)
        return null;
      totalRead += read;
    }

    return buf;
  }

  // -------------------------------------------------------------------------
  // Helpers
  // -------------------------------------------------------------------------

  private static uint ReadUInt32LE(ReadOnlySpan<byte> data, int offset) =>
    (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

  private static ulong ReadUInt64BE(ReadOnlySpan<byte> data, int offset) =>
    ((ulong)data[offset] << 56) | ((ulong)data[offset + 1] << 48) |
    ((ulong)data[offset + 2] << 40) | ((ulong)data[offset + 3] << 32) |
    ((ulong)data[offset + 4] << 24) | ((ulong)data[offset + 5] << 16) |
    ((ulong)data[offset + 6] << 8)  | data[offset + 7];

  private static string StripVersionSuffix(string name) {
    var semi = name.IndexOf(';');
    return semi >= 0 ? name[..semi] : name;
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }
}
