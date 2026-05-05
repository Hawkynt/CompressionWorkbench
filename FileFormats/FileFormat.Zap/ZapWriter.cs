#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Zap;

/// <summary>
/// Writes ZAP (Amiga Disk Archiver) images. Tracks are stored uncompressed --
/// the reader's <c>IsCompressed = compSize &lt; TrackSize</c> heuristic treats
/// <c>compSize == TrackSize</c> as a stored track and skips the LZ77+RLE
/// backward-bitstream decoder. Implementing the encoder isn't necessary for
/// WORM creation; tracks just round-trip verbatim.
/// </summary>
public sealed class ZapWriter {
  public const int TrackSize = 11 * 512;
  private static readonly byte[] Magic = "ZAP\0"u8.ToArray();

  private readonly List<(int trackNum, byte[] data)> _tracks = [];

  public void AddTrack(int trackNumber, ReadOnlySpan<byte> data) {
    var buf = new byte[TrackSize];
    var copyLen = Math.Min(data.Length, TrackSize);
    data[..copyLen].CopyTo(buf);
    _tracks.Add((trackNumber, buf));
  }

  public void WriteTo(Stream output) {
    if (_tracks.Count > ushort.MaxValue)
      throw new InvalidOperationException($"ZAP supports at most {ushort.MaxValue} tracks.");

    output.Write(Magic);
    Span<byte> u16 = stackalloc byte[2];
    Span<byte> u32 = stackalloc byte[4];
    BinaryPrimitives.WriteUInt16BigEndian(u16, (ushort)_tracks.Count);
    output.Write(u16);
    BinaryPrimitives.WriteUInt16BigEndian(u16, 0); // reserved/flags
    output.Write(u16);

    foreach (var (trackNum, data) in _tracks) {
      BinaryPrimitives.WriteUInt16BigEndian(u16, (ushort)trackNum);
      output.Write(u16);
      BinaryPrimitives.WriteInt32BigEndian(u32, data.Length); // == TrackSize -> reader treats as stored
      output.Write(u32);
      output.Write(data);
    }
  }
}
