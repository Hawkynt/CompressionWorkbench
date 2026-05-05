#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.Dictionary.Lzh;

namespace FileFormat.LhF;

/// <summary>
/// Writes LhF (LhFloppy) Amiga disk archives. Each input becomes one 5632-byte
/// track; smaller inputs are zero-padded, larger inputs are truncated. Tracks
/// are LZH-compressed (lh5 layout, 8 KB window). Stored uncompressed when
/// compression doesn't help -- mirrors the reader's "compSize == TrackSize"
/// shortcut.
/// </summary>
public sealed class LhFWriter {
  public const int TrackSize = 11 * 512;
  private static readonly byte[] Magic = "LhF\0"u8.ToArray();

  private readonly List<(int trackNum, byte[] data)> _tracks = [];

  /// <summary>
  /// Adds a track. <paramref name="trackNumber"/> is written verbatim into the
  /// per-track header; the order of <see cref="AddTrack"/> calls determines the
  /// physical order in the output file.
  /// </summary>
  public void AddTrack(int trackNumber, ReadOnlySpan<byte> data) {
    var buf = new byte[TrackSize];
    var copyLen = Math.Min(data.Length, TrackSize);
    data[..copyLen].CopyTo(buf);
    _tracks.Add((trackNumber, buf));
  }

  public void WriteTo(Stream output) {
    if (_tracks.Count > ushort.MaxValue)
      throw new InvalidOperationException($"LhF supports at most {ushort.MaxValue} tracks.");

    output.Write(Magic);
    Span<byte> u16 = stackalloc byte[2];
    Span<byte> u32 = stackalloc byte[4];
    BinaryPrimitives.WriteUInt16BigEndian(u16, (ushort)_tracks.Count);
    output.Write(u16);
    BinaryPrimitives.WriteUInt16BigEndian(u16, 0); // flags
    output.Write(u16);

    foreach (var (trackNum, data) in _tracks) {
      var encoder = new LzhEncoder(positionBits: 13);
      var compressed = encoder.Encode(data);
      // Reader treats compSize == TrackSize as "stored uncompressed". Do the same
      // when compression makes things bigger (or breaks even).
      if (compressed.Length >= data.Length)
        compressed = data;

      BinaryPrimitives.WriteUInt16BigEndian(u16, (ushort)trackNum);
      output.Write(u16);
      BinaryPrimitives.WriteInt32BigEndian(u32, compressed.Length);
      output.Write(u32);
      // Checksum: reader skips this field, but write something deterministic
      // so the file isn't littered with uninitialised bytes.
      var sum = 0;
      foreach (var b in data) sum = (sum + b) & 0xFFFF;
      BinaryPrimitives.WriteUInt16BigEndian(u16, (ushort)sum);
      output.Write(u16);
      output.Write(compressed);
    }
  }
}
