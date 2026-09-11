using System.Text;

namespace FileFormat.Cdi;

/// <summary>
/// Reads the ISO 9660 file system embedded in a DiscJuggler CDI disc image.
/// CDI files store CD-sector data followed by a session/track descriptor. The
/// last eight bytes identify the CDI version and locate that descriptor.
/// </summary>
public sealed class CdiReader : IDisposable {
  private const int Iso9660SectorSize = 2048;
  private const int RawSectorSize = 2352;
  private const int SectorSize2336 = 2336;
  private const int PvdLba = 16;
  private const int Mode1DataOffset = 16;
  private const int Mode2Form1DataOffset = 24;

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly int _sectorSize;
  private readonly int _dataOffset;
  private readonly long _dataAreaLength;
  private bool _disposed;

  /// <summary>Gets the CDI version identifier read from the footer, or 0 if no valid CDI footer was found.</summary>
  public uint CdiVersion { get; }

  /// <summary>Gets all file and directory entries found in the ISO 9660 file system.</summary>
  public IReadOnlyList<CdiEntry> Entries { get; }

  /// <summary>
  /// Initializes a new <see cref="CdiReader"/> from a CDI stream.
  /// </summary>
  /// <param name="stream">The stream containing the CDI image data.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public CdiReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    if (!stream.CanRead || !stream.CanSeek)
      throw new ArgumentException("CDI reading requires a readable, seekable stream.", nameof(stream));

    this._leaveOpen = leaveOpen;
    if (CdiDescriptor.TryReadFooter(stream, out var footer)) {
      this.CdiVersion = footer.Version;
      this._dataAreaLength = footer.DescriptorOffset;
    } else {
      this.CdiVersion = 0;
      this._dataAreaLength = stream.Length;
    }

    (this._sectorSize, this._dataOffset) = DetectSectorGeometry(stream, this._dataAreaLength);

    var entries = new List<CdiEntry>();
    TryParseIso9660(entries);
    this.Entries = entries;
  }

  /// <summary>
  /// Extracts the raw data for a file entry.
  /// </summary>
  /// <param name="entry">The file entry to extract. Must not be a directory.</param>
  /// <returns>The file data bytes.</returns>
  public byte[] Extract(CdiEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory)
      throw new ArgumentException("Cannot extract a directory entry.", nameof(entry));
    if (entry.Size == 0)
      return [];

    return ReadFileData(entry.StartLba, checked((int)entry.Size));
  }

  private static (int SectorSize, int DataOffset) DetectSectorGeometry(Stream stream, long dataAreaLength) {
    if (TryProbe(stream, RawSectorSize, Mode1DataOffset, dataAreaLength))
      return (RawSectorSize, Mode1DataOffset);

    if (TryProbe(stream, RawSectorSize, Mode2Form1DataOffset, dataAreaLength))
      return (RawSectorSize, Mode2Form1DataOffset);

    if (TryProbe(stream, SectorSize2336, 8, dataAreaLength))
      return (SectorSize2336, 8);

    if (TryProbe(stream, Iso9660SectorSize, 0, dataAreaLength))
      return (Iso9660SectorSize, 0);

    return (RawSectorSize, Mode1DataOffset);
  }

  private static bool TryProbe(Stream stream, int sectorSize, int dataOffset, long dataAreaLength) {
    var pvdPos = (long)PvdLba * sectorSize + dataOffset;
    if (pvdPos + 6 > dataAreaLength)
      return false;

    Span<byte> sig = stackalloc byte[6];
    stream.Position = pvdPos;
    if (stream.Read(sig) < sig.Length)
      return false;

    return sig[0] == 1 &&
           sig[1] == (byte)'C' &&
           sig[2] == (byte)'D' &&
           sig[3] == (byte)'0' &&
           sig[4] == (byte)'0' &&
           sig[5] == (byte)'1';
  }

  private void TryParseIso9660(List<CdiEntry> entries) {
    var pvd = ReadSector(PvdLba);
    if (pvd == null)
      return;

    if (pvd[0] != 1 ||
        pvd[1] != (byte)'C' || pvd[2] != (byte)'D' ||
        pvd[3] != (byte)'0' || pvd[4] != (byte)'0' || pvd[5] != (byte)'1')
      return;

    var rootLba = checked((int)ReadUInt32LE(pvd.AsSpan(), 156 + 2));
    var rootSize = checked((int)ReadUInt32LE(pvd.AsSpan(), 156 + 10));

    WalkDirectory(rootLba, rootSize, "", entries);
  }

  private void WalkDirectory(int dirLba, int dirSize, string parentPath, List<CdiEntry> entries) {
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

  private void ParseDirectoryRecord(ReadOnlySpan<byte> record, string parentPath, List<CdiEntry> entries) {
    if (record.Length < 34)
      return;

    var dataLba = checked((int)ReadUInt32LE(record, 2));
    var dataLen = checked((int)ReadUInt32LE(record, 10));
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

    entries.Add(new CdiEntry {
      Name = name,
      FullPath = fullPath,
      IsDirectory = isDirectory,
      Size = isDirectory ? 0 : dataLen,
      StartLba = dataLba,
    });

    if (isDirectory)
      WalkDirectory(dataLba, dataLen, fullPath, entries);
  }

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
    if (lba < 0)
      return null;

    var sectorStart = (long)lba * this._sectorSize;
    var dataStart = sectorStart + this._dataOffset;
    if (dataStart < 0 || dataStart + Iso9660SectorSize > this._dataAreaLength)
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

  private static uint ReadUInt32LE(ReadOnlySpan<byte> data, int offset) =>
    (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

  private static string StripVersionSuffix(string name) {
    var semi = name.IndexOf(';');
    return semi >= 0 ? name[..semi] : name;
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
