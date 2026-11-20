using System.Buffers.Binary;

namespace FileFormat.Uharc;

/// <summary>
/// The writer's choice of a multimedia predictor (docs/UHARC-ON-DISK.md, "Multimedia
/// selectors" and "Writing"): a 24-bit BMP gets selector 3, a PCM WAV 5 (8-bit mono), 7 (8-bit
/// stereo), 8 (16-bit mono) or 9 (16-bit stereo), anything else 0. Which data gets which
/// predictor is the encoder's choice; the decoder only follows the stream.
/// </summary>
internal static class UharcMediaClass {

  /// <summary>The selector for one file, recognised by its header.</summary>
  public static int Of(ReadOnlySpan<byte> file) {
    if (file.Length >= 54 && file[0] == 'B' && file[1] == 'M'
        && BinaryPrimitives.ReadUInt16LittleEndian(file[28..]) == 24 && BinaryPrimitives.ReadUInt32LittleEndian(file[30..]) == 0)
      return 3;

    if (file.Length < 44 || !file[..4].SequenceEqual("RIFF"u8) || !file[8..16].SequenceEqual("WAVEfmt "u8)
        || BinaryPrimitives.ReadUInt16LittleEndian(file[20..]) != 1)
      return 0;

    return (BinaryPrimitives.ReadUInt16LittleEndian(file[22..]), BinaryPrimitives.ReadUInt16LittleEndian(file[34..])) switch {
      (1, 8) => 5,
      (2, 8) => 7,
      (1, 16) => 8,
      (2, 16) => 9,
      _ => 0,
    };
  }

  /// <summary>
  /// The selector for the stream bytes <c>[start, end)</c>: the class of the file that covers
  /// most of them, when it covers at least half; otherwise 0.
  /// </summary>
  public static int ForBlock(ReadOnlySpan<byte> stream, IReadOnlyList<long> fileEnds, long start, long end) {
    if (end <= start)
      return 0;

    long previous = 0, bestOverlap = 0;
    var best = 0;
    foreach (var fileEnd in fileEnds) {
      var overlap = Math.Min(fileEnd, end) - Math.Max(previous, start);
      if (overlap > bestOverlap) {
        bestOverlap = overlap;
        best = Of(stream[(int)previous..(int)fileEnd]);
      }

      previous = fileEnd;
      if (previous >= end)
        break;
    }

    return bestOverlap * 2 >= end - start ? best : 0;
  }
}
