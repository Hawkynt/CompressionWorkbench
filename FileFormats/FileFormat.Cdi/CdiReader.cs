using System.Text;

namespace FileFormat.Cdi;

/// <summary>
/// Reads DiscJuggler CDI images, including mixed sector geometries and
/// multisession layouts. The trailing descriptor is used when present; legacy
/// CompressionWorkbench footer-only images retain geometry probing as a
/// compatibility fallback.
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
  private readonly long _dataAreaLength;
  private readonly int _legacySectorSize;
  private readonly int _legacyDataOffset;
  private readonly CdiTrackInfo? _activeDataTrack;
  private bool _isoExtentsAreAbsolute;
  private bool _disposed;

  /// <summary>Gets the CDI version identifier read from the footer, or 0 if no valid CDI footer was found.</summary>
  public uint CdiVersion { get; }

  /// <summary>Gets the parsed physical track table. Legacy footer-only images expose an empty list.</summary>
  public IReadOnlyList<CdiTrackInfo> Tracks { get; }

  /// <summary>Gets the number of parsed sessions.</summary>
  public int SessionCount => this.Tracks.Count == 0 ? 0 : this.Tracks.Max(static track => track.SessionNumber);

  /// <summary>
  /// Gets the latest data track whose sector 16 contains an ISO 9660 primary
  /// volume descriptor. This is normally the second-session data track on a
  /// self-booting Dreamcast CDI.
  /// </summary>
  public CdiTrackInfo? ActiveDataTrack => this._activeDataTrack;

  /// <summary>Gets all file and directory entries found in the selected ISO 9660 filesystem.</summary>
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
    IReadOnlyList<CdiTrackInfo> tracks = [];
    if (CdiDescriptor.TryReadFooter(stream, out var footer)) {
      this.CdiVersion = footer.Version;
      this._dataAreaLength = footer.DescriptorOffset;
      if (!footer.IsLegacyFooterOnly)
        CdiDescriptor.TryReadTrackTable(stream, footer, out tracks);
    } else {
      this.CdiVersion = 0;
      this._dataAreaLength = stream.Length;
    }

    this.Tracks = tracks;
    this._activeDataTrack = tracks
      .Where(static track => track.IsData)
      .Reverse()
      .FirstOrDefault(this.HasPrimaryVolumeDescriptor);

    if (this._activeDataTrack == null)
      (this._legacySectorSize, this._legacyDataOffset) = DetectLegacySectorGeometry(stream, this._dataAreaLength);

    var entries = new List<CdiEntry>();
    TryParseIso9660(entries);
    this.Entries = entries;
  }

  /// <summary>
  /// Reads one physical stored sector from a parsed track. Sector zero is index
  /// 1/the first post-pregap sector; negative indices address index 0/pregap
  /// sectors down to <c>-PregapSectors</c>. Audio tracks therefore return their
  /// native 2352-byte samples, while read modes 3/4 also include their appended
  /// subchannel bytes.
  /// </summary>
  public byte[] ReadTrackSector(CdiTrackInfo track, int sectorIndex) {
    ArgumentNullException.ThrowIfNull(track);
    if (!this.Tracks.Contains(track))
      throw new ArgumentException("The supplied track does not belong to this CDI reader.", nameof(track));
    if (sectorIndex < -track.PregapSectors || sectorIndex >= track.DataSectorCount)
      throw new ArgumentOutOfRangeException(nameof(sectorIndex));

    var at = checked(track.DataOffset + (long)sectorIndex * track.StoredSectorSize);
    if (at < track.FileOffset || at > this._dataAreaLength - track.StoredSectorSize)
      throw new EndOfStreamException("CDI track sector points outside the sector-data body.");

    var result = new byte[track.StoredSectorSize];
    this._stream.Position = at;
    this._stream.ReadExactly(result);
    return result;
  }

  /// <summary>
  /// Extracts the raw logical data for a filesystem file entry.
  /// </summary>
  public byte[] Extract(CdiEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory)
      throw new ArgumentException("Cannot extract a directory entry.", nameof(entry));
    if (entry.Size == 0)
      return [];
    if (entry.Size > int.MaxValue)
      throw new NotSupportedException("CDI buffered extraction cannot materialize a file larger than 2 GiB.");

    return ReadFileData(entry.StartLba, checked((int)entry.Size));
  }

  private bool HasPrimaryVolumeDescriptor(CdiTrackInfo track) {
    var pvd = ReadTrackUserDataSector(track, PvdLba);
    return HasPvdSignature(pvd);
  }

  private static bool HasPvdSignature(byte[]? sector)
    => sector is { Length: >= 6 } &&
       sector[0] == 1 &&
       sector[1] == (byte)'C' && sector[2] == (byte)'D' &&
       sector[3] == (byte)'0' && sector[4] == (byte)'0' && sector[5] == (byte)'1';

  private byte[]? ReadTrackUserDataSector(CdiTrackInfo track, int relativeSector) {
    if (!track.IsData || relativeSector < 0 || relativeSector >= track.DataSectorCount)
      return null;

    byte[] stored;
    try {
      stored = ReadTrackSector(track, relativeSector);
    } catch (EndOfStreamException) {
      return null;
    }

    var userDataOffset = (track.Mode, track.ReadMode) switch {
      (CdiTrackMode.Mode1, CdiReadMode.Mode1_2048) => 0,
      (CdiTrackMode.Mode1, CdiReadMode.Raw2352 or CdiReadMode.Raw2352_Q16 or CdiReadMode.Raw2352_Pw96) => 16,
      (CdiTrackMode.Mode2, CdiReadMode.Mode2_2336) => 8,
      (CdiTrackMode.Mode2, CdiReadMode.Raw2352 or CdiReadMode.Raw2352_Q16 or CdiReadMode.Raw2352_Pw96) => 24,
      _ => -1,
    };
    if (userDataOffset < 0 || userDataOffset > stored.Length - Iso9660SectorSize)
      return null;

    return stored.AsSpan(userDataOffset, Iso9660SectorSize).ToArray();
  }

  private static (int SectorSize, int DataOffset) DetectLegacySectorGeometry(Stream stream, long dataAreaLength) {
    if (TryLegacyProbe(stream, RawSectorSize, Mode1DataOffset, dataAreaLength))
      return (RawSectorSize, Mode1DataOffset);
    if (TryLegacyProbe(stream, RawSectorSize, Mode2Form1DataOffset, dataAreaLength))
      return (RawSectorSize, Mode2Form1DataOffset);
    if (TryLegacyProbe(stream, SectorSize2336, 8, dataAreaLength))
      return (SectorSize2336, 8);
    if (TryLegacyProbe(stream, Iso9660SectorSize, 0, dataAreaLength))
      return (Iso9660SectorSize, 0);
    return (RawSectorSize, Mode1DataOffset);
  }

  private static bool TryLegacyProbe(Stream stream, int sectorSize, int dataOffset, long dataAreaLength) {
    var pvdPos = (long)PvdLba * sectorSize + dataOffset;
    if (pvdPos + 6 > dataAreaLength)
      return false;

    Span<byte> sig = stackalloc byte[6];
    stream.Position = pvdPos;
    if (stream.Read(sig) < sig.Length)
      return false;

    return sig[0] == 1 &&
           sig[1] == (byte)'C' && sig[2] == (byte)'D' &&
           sig[3] == (byte)'0' && sig[4] == (byte)'0' && sig[5] == (byte)'1';
  }

  private void TryParseIso9660(List<CdiEntry> entries) {
    byte[]? pvd;
    if (this._activeDataTrack != null)
      pvd = ReadTrackUserDataSector(this._activeDataTrack, PvdLba);
    else
      pvd = ReadLegacySector(PvdLba);

    if (!HasPvdSignature(pvd))
      return;

    var rootLba = checked((int)ReadUInt32LE(pvd!, 156 + 2));
    var rootSize = checked((int)ReadUInt32LE(pvd!, 156 + 10));

    if (this._activeDataTrack is { StartLba: > 0 } active)
      this._isoExtentsAreAbsolute = FindTrackForDiscLba(rootLba) != null && rootLba >= active.StartLba;

    WalkDirectory(rootLba, rootSize, "", entries, []);
  }

  private void WalkDirectory(
    int dirLba,
    int dirSize,
    string parentPath,
    List<CdiEntry> entries,
    HashSet<int> visitedDirectoryLbas
  ) {
    if (dirLba < 0 || dirSize <= 0 || !visitedDirectoryLbas.Add(dirLba))
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

      if (recordLen < 34 || bufOffset + recordLen > Iso9660SectorSize) {
        var remaining = Iso9660SectorSize - bufOffset;
        bytesRead += remaining;
        bufOffset = Iso9660SectorSize;
        continue;
      }

      var record = sector.AsSpan(bufOffset, recordLen);
      ParseDirectoryRecord(record, parentPath, entries, visitedDirectoryLbas);

      bytesRead += recordLen;
      bufOffset += recordLen;
    }
  }

  private void ParseDirectoryRecord(
    ReadOnlySpan<byte> record,
    string parentPath,
    List<CdiEntry> entries,
    HashSet<int> visitedDirectoryLbas
  ) {
    var dataLba = checked((int)ReadUInt32LE(record, 2));
    var dataLen = checked((int)ReadUInt32LE(record, 10));
    var flags = record[25];
    var idLen = record[32];

    if (idLen == 0 || record.Length < 33 + idLen)
      return;
    if (idLen == 1 && record[33] is 0x00 or 0x01)
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
      WalkDirectory(dataLba, dataLen, fullPath, entries, visitedDirectoryLbas);
  }

  private byte[] ReadFileData(int startLba, int size) {
    var result = new byte[size];
    var written = 0;
    var lba = startLba;

    while (written < size) {
      var sector = ReadSector(lba);
      if (sector == null)
        throw new InvalidDataException($"CDI ISO extent at LBA {lba} is truncated or points outside a readable data track.");

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

    if (this._activeDataTrack != null) {
      if (this._isoExtentsAreAbsolute) {
        var physicalTrack = FindTrackForDiscLba(lba);
        if (physicalTrack == null)
          return null;
        return ReadTrackUserDataSector(physicalTrack, checked(lba - physicalTrack.StartLba));
      }

      return ReadTrackUserDataSector(this._activeDataTrack, lba);
    }

    return ReadLegacySector(lba);
  }

  private CdiTrackInfo? FindTrackForDiscLba(int lba)
    => this.Tracks.FirstOrDefault(track =>
      track.IsData && lba >= track.StartLba && (long)lba < track.EndLbaExclusive);

  private byte[]? ReadLegacySector(int lba) {
    if (lba < 0)
      return null;

    var dataStart = checked((long)lba * this._legacySectorSize + this._legacyDataOffset);
    if (dataStart < 0 || dataStart > this._dataAreaLength - Iso9660SectorSize)
      return null;

    this._stream.Position = dataStart;
    var result = new byte[Iso9660SectorSize];
    try {
      this._stream.ReadExactly(result);
      return result;
    } catch (EndOfStreamException) {
      return null;
    }
  }

  private static uint ReadUInt32LE(ReadOnlySpan<byte> data, int offset)
    => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);

  private static string StripVersionSuffix(string name) {
    var semicolon = name.IndexOf(';');
    return semicolon >= 0 ? name[..semicolon] : name;
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
