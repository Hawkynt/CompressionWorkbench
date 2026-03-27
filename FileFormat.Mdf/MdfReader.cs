using System.Text;

namespace FileFormat.Mdf;

/// <summary>
/// Reads the ISO 9660 file system embedded in an Alcohol 120% MDF disc image.
/// MDF files contain raw CD/DVD sector data, typically in 2352-byte raw sectors
/// (Mode 1 with user data at offset 16) or plain 2048-byte ISO sectors.
/// The accompanying MDS file describes track layout; this reader detects the
/// sector geometry heuristically by probing for the ISO 9660 PVD signature.
/// </summary>
public sealed class MdfReader : IDisposable {
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

  /// <summary>Gets all file and directory entries found in the ISO 9660 file system.</summary>
  public IReadOnlyList<MdfEntry> Entries { get; }

  /// <summary>
  /// Initializes a new <see cref="MdfReader"/> from an MDF stream.
  /// </summary>
  /// <param name="stream">The stream containing MDF sector data.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public MdfReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    (this._sectorSize, this._dataOffset) = DetectSectorGeometry(stream);

    var entries = new List<MdfEntry>();
    TryParseIso9660(entries);
    this.Entries = entries;
  }

  /// <summary>
  /// Extracts the raw data for a file entry.
  /// </summary>
  /// <param name="entry">The file entry to extract. Must not be a directory.</param>
  /// <returns>The file data bytes.</returns>
  public byte[] Extract(MdfEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory)
      throw new ArgumentException("Cannot extract a directory entry.", nameof(entry));
    if (entry.Size == 0)
      return [];

    return ReadFileData(entry.StartLba, (int)entry.Size);
  }

  // -------------------------------------------------------------------------
  // Sector geometry detection
  // -------------------------------------------------------------------------

  private static (int SectorSize, int DataOffset) DetectSectorGeometry(Stream stream) {
    if (TryProbe(stream, RawSectorSize, Mode1DataOffset))
      return (RawSectorSize, Mode1DataOffset);

    if (TryProbe(stream, RawSectorSize, Mode2Form1DataOffset))
      return (RawSectorSize, Mode2Form1DataOffset);

    if (TryProbe(stream, SectorSize2336, 8))
      return (SectorSize2336, 8);

    if (TryProbe(stream, Iso9660SectorSize, 0))
      return (Iso9660SectorSize, 0);

    return (RawSectorSize, Mode1DataOffset);
  }

  private static bool TryProbe(Stream stream, int sectorSize, int dataOffset) {
    var pvdSectorOffset = (long)PvdLba * sectorSize;
    if (pvdSectorOffset + dataOffset + 6 > stream.Length)
      return false;

    Span<byte> sig = stackalloc byte[6];
    stream.Position = pvdSectorOffset + dataOffset;
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

  private void TryParseIso9660(List<MdfEntry> entries) {
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

  private void WalkDirectory(int dirLba, int dirSize, string parentPath, List<MdfEntry> entries) {
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

  private void ParseDirectoryRecord(ReadOnlySpan<byte> record, string parentPath, List<MdfEntry> entries) {
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

    entries.Add(new MdfEntry {
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
    var sectorStart = (long)lba * this._sectorSize;
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
