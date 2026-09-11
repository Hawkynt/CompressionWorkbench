using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Nrg;

/// <summary>
/// Reads the ISO 9660 data track embedded in a Nero Burning ROM NRG image.
/// NRG stores disc sectors first, then a chunked session/track descriptor, and
/// finally a footer pointing back to that descriptor.
/// </summary>
public sealed class NrgReader : IDisposable {
  private const int Iso9660SectorSize = 2048;
  private const int RawSectorSize = 2352;
  private const int SectorSize2336 = 2336;
  private const int RawSectorWithSubchannelSize = 2448;
  private const int PvdLba = 16;
  private const int Mode1DataOffset = 16;
  private const int Mode2Form1DataOffset = 24;

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly int _sectorSize;
  private readonly int _dataOffset;
  private readonly long _trackOffset;
  private readonly long _dataAreaEnd;
  private bool _disposed;

  private readonly record struct FooterInfo(int Version, long TrailerOffset, long FooterOffset);
  private readonly record struct TrackCandidate(long Offset, long Length, int SectorSize, int DataOffset);

  /// <summary>Gets the NRG format version detected from the footer (1 or 2), or 0 if no valid footer was found.</summary>
  public int Version { get; }

  /// <summary>Gets all file and directory entries found in the ISO 9660 file system.</summary>
  public IReadOnlyList<NrgEntry> Entries { get; }

  /// <summary>Initializes a new <see cref="NrgReader"/> from an NRG stream.</summary>
  public NrgReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    if (!stream.CanRead || !stream.CanSeek)
      throw new ArgumentException("NRG reading requires a readable, seekable stream.", nameof(stream));
    this._leaveOpen = leaveOpen;

    var footer = ReadFooter(stream);
    this.Version = footer.Version;

    if (TryFindIsoTrack(stream, footer, out var track)) {
      this._trackOffset = track.Offset;
      this._sectorSize = track.SectorSize;
      this._dataOffset = track.DataOffset;
      this._dataAreaEnd = TrackEnd(track, footer.FooterOffset > 0 ? footer.FooterOffset : stream.Length);
    } else {
      var end = footer.Version != 0 ? footer.TrailerOffset : stream.Length;
      (this._sectorSize, this._dataOffset) = DetectSectorGeometry(stream, 0, end);
      this._trackOffset = 0;
      this._dataAreaEnd = end;
    }

    var entries = new List<NrgEntry>();
    TryParseIso9660(entries);
    this.Entries = entries;
  }

  /// <summary>Extracts the raw data for a file entry.</summary>
  public byte[] Extract(NrgEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory)
      throw new ArgumentException("Cannot extract a directory entry.", nameof(entry));
    if (entry.Size == 0)
      return [];
    if (entry.Size > int.MaxValue)
      throw new NotSupportedException("NRG entries larger than 2 GiB require a streaming extraction API.");

    return ReadFileData(entry.StartLba, checked((int)entry.Size));
  }

  private static FooterInfo ReadFooter(Stream stream) {
    if (stream.Length >= 12) {
      stream.Position = stream.Length - 12;
      Span<byte> footer = stackalloc byte[12];
      if (ReadExactly(stream, footer) && footer[..4].SequenceEqual("NER5"u8)) {
        var offset = BinaryPrimitives.ReadUInt64BigEndian(footer[4..]);
        var footerOffset = stream.Length - 12;
        if (offset <= (ulong)footerOffset)
          return new(2, checked((long)offset), footerOffset);
      }
    }

    if (stream.Length >= 8) {
      stream.Position = stream.Length - 8;
      Span<byte> footer = stackalloc byte[8];
      if (ReadExactly(stream, footer) && footer[..4].SequenceEqual("NERO"u8)) {
        var offset = BinaryPrimitives.ReadUInt32BigEndian(footer[4..]);
        var footerOffset = stream.Length - 8;
        if (offset <= footerOffset)
          return new(1, offset, footerOffset);
      }
    }

    return new(0, stream.Length, stream.Length);
  }

  private static bool TryFindIsoTrack(Stream stream, FooterInfo footer, out TrackCandidate track) {
    track = default;
    if (footer.Version == 0 || footer.TrailerOffset >= footer.FooterOffset)
      return false;

    foreach (var candidate in ReadTrackCandidates(stream, footer)) {
      var end = TrackEnd(candidate, footer.TrailerOffset);
      if (TryProbe(stream, candidate.Offset, candidate.SectorSize, candidate.DataOffset, end)) {
        track = candidate;
        return true;
      }
    }

    return false;
  }

  private static IEnumerable<TrackCandidate> ReadTrackCandidates(Stream stream, FooterInfo footer) {
    var position = footer.TrailerOffset;
    while (position <= footer.FooterOffset - 8) {
      stream.Position = position;
      Span<byte> header = stackalloc byte[8];
      if (!ReadExactly(stream, header))
        yield break;

      var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
      var payloadStart = position + 8;
      if (payloadLength > (ulong)(footer.FooterOffset - payloadStart))
        yield break;
      var payloadEnd = payloadStart + payloadLength;

      if (header[..4].SequenceEqual("ETN2"u8)) {
        foreach (var candidate in ReadEtn2Candidates(stream, payloadStart, payloadLength))
          yield return candidate;
      } else if (header[..4].SequenceEqual("ETNF"u8)) {
        foreach (var candidate in ReadEtnfCandidates(stream, payloadStart, payloadLength))
          yield return candidate;
      } else if (header[..4].SequenceEqual("DAOX"u8)) {
        foreach (var candidate in ReadDaoCandidates(stream, payloadStart, payloadLength, isV2: true))
          yield return candidate;
      } else if (header[..4].SequenceEqual("DAOI"u8)) {
        foreach (var candidate in ReadDaoCandidates(stream, payloadStart, payloadLength, isV2: false))
          yield return candidate;
      }

      position = payloadEnd;
      if (header[..4].SequenceEqual("END!"u8))
        yield break;
    }
  }

  private static IEnumerable<TrackCandidate> ReadEtn2Candidates(Stream stream, long payloadStart, uint payloadLength) {
    const int recordSize = 32;
    Span<byte> record = stackalloc byte[recordSize];
    for (long relative = 0; relative + recordSize <= payloadLength; relative += recordSize) {
      stream.Position = payloadStart + relative;
      if (!ReadExactly(stream, record))
        yield break;

      var offset = BinaryPrimitives.ReadUInt64BigEndian(record);
      var length = BinaryPrimitives.ReadUInt64BigEndian(record[8..]);
      if (offset > long.MaxValue || length > long.MaxValue)
        continue;
      if (TryDecodeMode(record[19], declaredSectorSize: 0, out var sectorSize, out var dataOffset))
        yield return new(checked((long)offset), checked((long)length), sectorSize, dataOffset);
    }
  }

  private static IEnumerable<TrackCandidate> ReadEtnfCandidates(Stream stream, long payloadStart, uint payloadLength) {
    const int recordSize = 20;
    Span<byte> record = stackalloc byte[recordSize];
    for (long relative = 0; relative + recordSize <= payloadLength; relative += recordSize) {
      stream.Position = payloadStart + relative;
      if (!ReadExactly(stream, record))
        yield break;

      var offset = BinaryPrimitives.ReadUInt32BigEndian(record);
      var length = BinaryPrimitives.ReadUInt32BigEndian(record[4..]);
      if (TryDecodeMode(record[11], declaredSectorSize: 0, out var sectorSize, out var dataOffset))
        yield return new(offset, length, sectorSize, dataOffset);
    }
  }

  private static IEnumerable<TrackCandidate> ReadDaoCandidates(Stream stream, long payloadStart, uint payloadLength, bool isV2) {
    const int daoHeaderSize = 22;
    var recordSize = isV2 ? 42 : 30;
    if (payloadLength < daoHeaderSize)
      yield break;

    var record = new byte[recordSize];
    for (long relative = daoHeaderSize; relative + recordSize <= payloadLength; relative += recordSize) {
      stream.Position = payloadStart + relative;
      if (!ReadExactly(stream, record))
        yield break;

      var declaredSectorSize = BinaryPrimitives.ReadUInt16BigEndian(record.AsSpan(12, 2));
      var mode = record[14];
      ulong start;
      ulong end;
      if (isV2) {
        start = BinaryPrimitives.ReadUInt64BigEndian(record.AsSpan(26, 8));
        end = BinaryPrimitives.ReadUInt64BigEndian(record.AsSpan(34, 8));
      } else {
        start = BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(22, 4));
        end = BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(26, 4));
      }

      if (start > long.MaxValue || end > long.MaxValue || end < start)
        continue;
      if (TryDecodeMode(mode, declaredSectorSize, out var sectorSize, out var dataOffset))
        yield return new(checked((long)start), checked((long)(end - start)), sectorSize, dataOffset);
    }
  }

  private static bool TryDecodeMode(byte mode, int declaredSectorSize, out int sectorSize, out int dataOffset) {
    (sectorSize, dataOffset) = mode switch {
      0x00 or 0x02 => (Iso9660SectorSize, 0),
      0x03 => (SectorSize2336, 0),
      0x05 => (RawSectorSize, Mode1DataOffset),
      0x06 => (RawSectorSize, Mode2Form1DataOffset),
      0x0F => (RawSectorWithSubchannelSize, Mode1DataOffset),
      0x11 => (RawSectorWithSubchannelSize, Mode2Form1DataOffset),
      _ => declaredSectorSize switch {
        Iso9660SectorSize => (Iso9660SectorSize, 0),
        _ => (0, 0),
      },
    };
    return sectorSize != 0;
  }

  private static long TrackEnd(TrackCandidate candidate, long hardEnd) {
    if (candidate.Offset < 0 || candidate.Offset >= hardEnd)
      return candidate.Offset;
    if (candidate.Length <= 0)
      return hardEnd;
    var remaining = hardEnd - candidate.Offset;
    return candidate.Offset + Math.Min(candidate.Length, remaining);
  }

  private static (int SectorSize, int DataOffset) DetectSectorGeometry(Stream stream, long trackOffset, long dataEnd) {
    if (TryProbe(stream, trackOffset, RawSectorSize, Mode1DataOffset, dataEnd))
      return (RawSectorSize, Mode1DataOffset);
    if (TryProbe(stream, trackOffset, RawSectorSize, Mode2Form1DataOffset, dataEnd))
      return (RawSectorSize, Mode2Form1DataOffset);
    if (TryProbe(stream, trackOffset, SectorSize2336, 8, dataEnd))
      return (SectorSize2336, 8);
    if (TryProbe(stream, trackOffset, Iso9660SectorSize, 0, dataEnd))
      return (Iso9660SectorSize, 0);
    return (RawSectorSize, Mode1DataOffset);
  }

  private static bool TryProbe(Stream stream, long trackOffset, int sectorSize, int dataOffset, long dataEnd) {
    if (trackOffset < 0 || sectorSize <= 0 || dataOffset < 0)
      return false;
    var pvdPosition = trackOffset + (long)PvdLba * sectorSize + dataOffset;
    if (pvdPosition < trackOffset || pvdPosition > dataEnd - 6)
      return false;

    Span<byte> signature = stackalloc byte[6];
    stream.Position = pvdPosition;
    return ReadExactly(stream, signature) &&
           signature[0] == 1 && signature[1..].SequenceEqual("CD001"u8);
  }

  private void TryParseIso9660(List<NrgEntry> entries) {
    var pvd = ReadSector(PvdLba);
    if (pvd == null || pvd[0] != 1 || !pvd.AsSpan(1, 5).SequenceEqual("CD001"u8))
      return;

    var rootLba = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(158, 4));
    var rootSize = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(166, 4));
    if (rootLba > int.MaxValue || rootSize > int.MaxValue)
      return;

    WalkDirectory((int)rootLba, (int)rootSize, "", entries, []);
  }

  private void WalkDirectory(int dirLba, int dirSize, string parentPath, List<NrgEntry> entries, HashSet<int> visited) {
    if (dirLba <= 0 || dirSize <= 0 || !visited.Add(dirLba))
      return;

    var bytesRead = 0;
    var currentLba = dirLba;
    var bufferOffset = 0;
    byte[]? sector = null;

    while (bytesRead < dirSize) {
      if (sector == null || bufferOffset >= Iso9660SectorSize) {
        sector = ReadSector(currentLba);
        if (sector == null)
          return;
        ++currentLba;
        bufferOffset = 0;
      }

      var recordLength = sector[bufferOffset];
      if (recordLength == 0) {
        var remaining = Iso9660SectorSize - bufferOffset;
        bytesRead += remaining;
        bufferOffset = Iso9660SectorSize;
        continue;
      }

      if (recordLength < 34 || bufferOffset + recordLength > Iso9660SectorSize) {
        var remaining = Iso9660SectorSize - bufferOffset;
        bytesRead += remaining;
        bufferOffset = Iso9660SectorSize;
        continue;
      }

      var record = sector.AsSpan(bufferOffset, recordLength);
      ParseDirectoryRecord(record, parentPath, entries, visited);
      bytesRead += recordLength;
      bufferOffset += recordLength;
    }
  }

  private void ParseDirectoryRecord(ReadOnlySpan<byte> record, string parentPath, List<NrgEntry> entries, HashSet<int> visited) {
    var idLength = record[32];
    if (idLength == 0 || 33 + idLength > record.Length)
      return;
    if (idLength == 1 && (record[33] == 0x00 || record[33] == 0x01))
      return;

    var dataLba = BinaryPrimitives.ReadUInt32LittleEndian(record[2..6]);
    var dataLength = BinaryPrimitives.ReadUInt32LittleEndian(record[10..14]);
    if (dataLba > int.MaxValue || dataLength > int.MaxValue)
      return;

    var isDirectory = (record[25] & 0x02) != 0;
    var rawName = Encoding.ASCII.GetString(record.Slice(33, idLength));
    var name = StripVersionSuffix(rawName);
    if (string.IsNullOrEmpty(name))
      return;

    var fullPath = parentPath.Length > 0 ? $"{parentPath}/{name}" : name;
    entries.Add(new NrgEntry {
      Name = name,
      FullPath = fullPath,
      IsDirectory = isDirectory,
      Size = isDirectory ? 0 : dataLength,
      StartLba = (int)dataLba,
    });

    if (isDirectory)
      WalkDirectory((int)dataLba, (int)dataLength, fullPath, entries, visited);
  }

  private byte[] ReadFileData(int startLba, int size) {
    var result = new byte[size];
    var written = 0;
    for (var lba = startLba; written < size; ++lba) {
      var sector = ReadSector(lba);
      if (sector == null)
        throw new InvalidDataException("NRG file extent runs past the selected data track.");

      var toCopy = Math.Min(Iso9660SectorSize, size - written);
      sector.AsSpan(0, toCopy).CopyTo(result.AsSpan(written));
      written += toCopy;
    }
    return result;
  }

  private byte[]? ReadSector(int lba) {
    if (lba < 0)
      return null;
    var sectorStart = this._trackOffset + (long)lba * this._sectorSize;
    var dataStart = sectorStart + this._dataOffset;
    if (sectorStart < this._trackOffset || dataStart < sectorStart || dataStart > this._dataAreaEnd - Iso9660SectorSize)
      return null;

    this._stream.Position = dataStart;
    var buffer = new byte[Iso9660SectorSize];
    return ReadExactly(this._stream, buffer) ? buffer : null;
  }

  private static bool ReadExactly(Stream stream, Span<byte> buffer) {
    var offset = 0;
    while (offset < buffer.Length) {
      var read = stream.Read(buffer[offset..]);
      if (read == 0)
        return false;
      offset += read;
    }
    return true;
  }

  private static string StripVersionSuffix(string name) {
    var separator = name.IndexOf(';');
    return separator >= 0 ? name[..separator] : name;
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
