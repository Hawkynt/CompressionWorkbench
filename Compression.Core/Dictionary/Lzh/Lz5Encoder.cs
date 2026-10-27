namespace Compression.Core.Dictionary.Lzh;

/// <summary>
/// Encodes LArc <c>-lz5-</c> (see <see cref="Lz5Decoder"/> for the stream layout).
/// </summary>
/// <remarks>
/// Matches only reach back into bytes already emitted (never into the predefined initial ring) and are
/// written as absolute ring positions. Verified by lhasa extracting what this writes.
/// </remarks>
public static class Lz5Encoder {
  private const int MaxLength = 15 + Lz5Decoder.Threshold;

  /// <summary>Encodes <paramref name="data"/>.</summary>
  public static byte[] Encode(ReadOnlySpan<byte> data) => Encode(data.ToArray());

  /// <summary>Encodes <paramref name="data"/>.</summary>
  public static byte[] Encode(byte[] data) {
    ArgumentNullException.ThrowIfNull(data);
    using var ms = new MemoryStream();
    var finder = new LhaMatchFinder(data, Lz5Decoder.RingSize - 1, Lz5Decoder.Threshold, MaxLength);
    var group = new List<byte>(17);
    var pos = 0;
    while (pos < data.Length) {
      byte flags = 0;
      group.Clear();
      for (var bit = 0; bit < 8 && pos < data.Length; ++bit) {
        var (length, distance) = finder.Find(pos);
        if (length == 0) {
          flags |= (byte)(1 << bit);
          group.Add(data[pos++]);
          continue;
        }
        var ringPos = (Lz5Decoder.RingSize - Lz5Decoder.StartOffset + pos - distance) % Lz5Decoder.RingSize;
        group.Add((byte)ringPos);
        group.Add((byte)(((ringPos >> 4) & 0xF0) | (length - Lz5Decoder.Threshold)));
        pos += length;
      }
      ms.WriteByte(flags);
      ms.Write(group.ToArray());
    }
    return ms.ToArray();
  }
}
