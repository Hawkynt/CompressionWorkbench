using System.Buffers.Binary;

namespace FileFormat.Pasti;

/// <summary>One sector as the WD1772 reported it: its ID field, FDC status and data.</summary>
public sealed record PastiSector(byte IdTrack, byte IdSide, byte IdNumber, byte IdSize, ushort IdCrc, byte FdcStatus,
  int BitPosition, int ReadTime, byte[] Data, byte[]? FuzzyMask);

/// <summary>One track record of a Pasti image.</summary>
public sealed record PastiTrack(int Cylinder, int Head, ushort Flags, int TrackLength, byte[]? TrackImage, IReadOnlyList<PastiSector> Sectors);

/// <summary>
/// Pasti (<c>.stx</c>) Atari ST observational floppy image reader. Layout from the Pasti file
/// documentation as implemented by MAME's <c>pasti_dsk.cpp</c> (BSD-3-Clause, Olivier Galibert;
/// see <c>THIRD-PARTY-NOTICE.Mame.txt</c>): a 16-byte "RSY\0" header with the track count, then per
/// track a 16-byte header, 16-byte sector descriptors, optional fuzzy masks, an optional sync offset
/// and track image, and the sector data.
/// </summary>
public static class PastiImage {
  private const ushort FlagSectorDescriptors = 0x0001;
  private const ushort FlagTrackImage = 0x0040;
  private const ushort FlagTrackImageSync = 0x0080;

  /// <exception cref="InvalidDataException">Not a Pasti image, or a record runs out of the file.</exception>
  public static (int Version, int Revision, List<PastiTrack> Tracks) Read(ReadOnlySpan<byte> d) {
    if (d.Length < 16 || !d[..4].SequenceEqual("RSY\0"u8))
      throw new InvalidDataException("Pasti: \"RSY\\0\" signature missing.");
    var version = BinaryPrimitives.ReadUInt16LittleEndian(d[4..]);
    if (version != 3) throw new NotSupportedException($"Pasti: file version {version} is not supported (3 is).");
    int count = d[10];
    var tracks = new List<PastiTrack>(count);
    var pos = 16;
    for (var i = 0; i < count; ++i) {
      if (d.Length - pos < 16) throw new InvalidDataException($"Pasti: track record {i} header truncated.");
      var th = d.Slice(pos, 16);
      var entryLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(th);
      var fuzzLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(th[4..]);
      int sectorCount = BinaryPrimitives.ReadUInt16LittleEndian(th[8..]);
      var flags = BinaryPrimitives.ReadUInt16LittleEndian(th[10..]);
      int trackLength = BinaryPrimitives.ReadUInt16LittleEndian(th[12..]);
      var number = th[14];
      if (entryLength < 16 || entryLength > d.Length - pos)
        throw new InvalidDataException($"Pasti: track record {i} length {entryLength} runs out of the file.");
      var rec = d.Slice(pos + 16, entryLength - 16);
      var sectors = new List<PastiSector>(sectorCount);

      if ((flags & FlagSectorDescriptors) == 0) {
        // No descriptors: the record holds the standard 512-byte sectors 1..n in order.
        if ((long)sectorCount * 512 > rec.Length) throw new InvalidDataException($"Pasti: track record {i} is shorter than its sectors.");
        for (var s = 0; s < sectorCount; ++s)
          sectors.Add(new((byte)(number & 0x7F), (byte)(number >> 7), (byte)(s + 1), 2, 0, 0, 0, 0, rec.Slice(512 * s, 512).ToArray(), null));
        tracks.Add(new(number & 0x7F, number >> 7, flags, trackLength, null, sectors));
        pos += entryLength;
        continue;
      }

      var descriptors = 16 * sectorCount;
      if (descriptors + fuzzLength > rec.Length) throw new InvalidDataException($"Pasti: track record {i} descriptors run out of the record.");
      var fuzz = rec.Slice(descriptors, fuzzLength);
      var data = rec[(descriptors + fuzzLength)..];
      var t = data;
      if ((flags & FlagTrackImageSync) != 0) t = t[2..];
      byte[]? image = null;
      if ((flags & FlagTrackImage) != 0) {
        if (t.Length < 2) throw new InvalidDataException($"Pasti: track record {i} image size missing.");
        int imageSize = BinaryPrimitives.ReadUInt16LittleEndian(t);
        if (imageSize > t.Length - 2) throw new InvalidDataException($"Pasti: track record {i} image runs out of the record.");
        image = t.Slice(2, imageSize).ToArray();
      }
      var fuzzOffset = 0;
      for (var s = 0; s < sectorCount; ++s) {
        var sh = rec.Slice(16 * s, 16);
        var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(sh);
        var size = 128 << (sh[11] & 3);
        if (offset < 0 || (long)offset + size > data.Length)
          throw new InvalidDataException($"Pasti: track record {i} sector {s} data runs out of the record.");
        var status = sh[14];
        byte[]? mask = null;
        if ((status & 0x80) != 0 && fuzzLength > 0) {
          // Fuzzy sectors carry a mask of the same size, in descriptor order.
          if (fuzzOffset + size > fuzz.Length) throw new InvalidDataException($"Pasti: track record {i} fuzzy mask truncated.");
          mask = fuzz.Slice(fuzzOffset, size).ToArray();
          fuzzOffset += size;
        }
        sectors.Add(new(sh[8], sh[9], sh[10], sh[11], BinaryPrimitives.ReadUInt16BigEndian(sh[12..]), status,
          BinaryPrimitives.ReadUInt16LittleEndian(sh[4..]), BinaryPrimitives.ReadUInt16LittleEndian(sh[6..]),
          data.Slice(offset, size).ToArray(), mask));
      }
      tracks.Add(new(number & 0x7F, number >> 7, flags, trackLength, image, sectors));
      pos += entryLength;
    }
    return (version, d[11], tracks);
  }

  /// <summary>
  /// The plain ST sector image (cylinder-major, sides interleaved, sectors 1..n of 512 bytes), when
  /// every track is a clean standard one with the same sector count; otherwise null.
  /// </summary>
  public static byte[]? StandardImage(IReadOnlyList<PastiTrack> tracks) {
    if (tracks.Count == 0) return null;
    var spt = tracks[0].Sectors.Count;
    if (spt is < 8 or > 11) return null;
    var ordered = tracks.OrderBy(t => t.Cylinder).ThenBy(t => t.Head).ToList();
    using var ms = new MemoryStream();
    foreach (var t in ordered) {
      if (t.Sectors.Count != spt) return null;
      for (var n = 1; n <= spt; ++n) {
        var matches = t.Sectors.Where(s => s.IdNumber == n).ToList();
        if (matches.Count != 1 || matches[0].IdSize != 2 || matches[0].FdcStatus != 0 || matches[0].FuzzyMask is not null) return null;
        ms.Write(matches[0].Data);
      }
    }
    return ms.ToArray();
  }
}
