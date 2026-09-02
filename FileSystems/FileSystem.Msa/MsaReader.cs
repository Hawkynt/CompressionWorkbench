#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Msa;

/// <summary>
/// Reads Atari ST MSA (Magic Shadow Archiver) disk images.
/// MSA uses simple RLE compression on individual tracks.
/// </summary>
public sealed class MsaReader : IDisposable {
    /// <summary>
  /// Defines the msa magic constant value.
  /// </summary>
public const ushort MsaMagic = 0x0E0F;
  private const int SectorSize = 512;

  private readonly byte[] _diskData;
  private readonly List<MsaEntry> _entries = [];

    /// <summary>
  /// Gets the sectors per track.
  /// </summary>
public ushort SectorsPerTrack { get; }
    /// <summary>
  /// Gets the sides.
  /// </summary>
public ushort Sides { get; } // 0=single, 1=double
    /// <summary>
  /// Gets the start track.
  /// </summary>
public ushort StartTrack { get; }
    /// <summary>
  /// Gets the end track.
  /// </summary>
public ushort EndTrack { get; }
    /// <summary>
  /// Gets the entries.
  /// </summary>
public IReadOnlyList<MsaEntry> Entries => _entries;

    /// <summary>
  /// Initializes a new instance of <see cref="MsaReader"/>.
  /// </summary>
public MsaReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var data = ms.ToArray();

    if (data.Length < 10)
      throw new InvalidDataException("MSA: file too small.");

    var magic = BinaryPrimitives.ReadUInt16BigEndian(data);
    if (magic != MsaMagic)
      throw new InvalidDataException("MSA: invalid magic.");

    SectorsPerTrack = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2));
    Sides = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));
    StartTrack = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6));
    EndTrack = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(8));

    var trackSize = SectorsPerTrack * SectorSize;
    var numSides = Sides + 1;
    var numTracks = EndTrack - StartTrack + 1;
    var totalTracks = numTracks * numSides;

    _diskData = new byte[totalTracks * trackSize];
    var pos = 10;
    var outPos = 0;

    for (var t = 0; t < totalTracks; t++) {
      if (pos + 2 > data.Length) break;
      var compressedLen = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos));
      pos += 2;

      if (compressedLen == trackSize) {
        // Uncompressed track
        if (pos + trackSize <= data.Length)
          data.AsSpan(pos, trackSize).CopyTo(_diskData.AsSpan(outPos));
        pos += trackSize;
      } else {
        // RLE compressed track
        DecompressRle(data.AsSpan(pos, Math.Min(compressedLen, data.Length - pos)),
          _diskData.AsSpan(outPos, trackSize));
        pos += compressedLen;
      }
      outPos += trackSize;
    }

    _entries.Add(new MsaEntry {
      Name = "disk.st",
      Size = _diskData.Length,
    });
  }

  private static void DecompressRle(ReadOnlySpan<byte> src, Span<byte> dst) {
    var si = 0;
    var di = 0;
    while (si < src.Length && di < dst.Length) {
      var b = src[si++];
      if (b == 0xE5 && si + 2 < src.Length) {
        // RLE: 0xE5, byte, count (BE 16-bit)
        var repeatByte = src[si++];
        var count = (src[si] << 8) | src[si + 1];
        si += 2;
        for (var i = 0; i < count && di < dst.Length; i++)
          dst[di++] = repeatByte;
      } else {
        dst[di++] = b;
      }
    }
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Extract(MsaEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return _diskData.ToArray();
  }

    /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() { }
}
