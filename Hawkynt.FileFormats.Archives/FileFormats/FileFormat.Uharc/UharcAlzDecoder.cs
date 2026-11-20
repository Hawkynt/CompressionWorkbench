namespace FileFormat.Uharc;

/// <summary>
/// UHARC 0.6 ALZ decoder (stream methods 40..87, modes <c>-m1</c>..<c>-m3</c>), implemented from
/// docs/UHARC-ON-DISK.md section "ALZ". The model itself is <see cref="UharcAlzModel{TCoder}"/>,
/// shared with <see cref="UharcAlzEncoder"/>.
/// </summary>
internal static class UharcAlzDecoder {

  // Literal-mode state machine: [(token - 1) * 32 + mode] -> next mode.
  internal static readonly byte[] ModeNext = [
    16, 16, 16, 16, 17, 17, 17, 17, 20, 20, 20, 20, 18, 18, 18, 19, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21,
    22, 22, 22, 22, 24, 23, 23, 24, 27, 27, 27, 27, 25, 25, 25, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28,
    29, 29, 29, 29, 29, 29, 29, 29, 30, 30, 30, 30, 30, 30, 30, 30, 31, 31, 31, 31, 31, 31, 31, 31, 31, 31, 31, 31, 31, 31, 31, 31,
    0, 0, 0, 0, 1, 2, 2, 3, 4, 4, 7, 7, 5, 5, 5, 6, 8, 8, 8, 8, 8, 9, 12, 12, 12, 13, 13, 14, 15, 10, 10, 11,
  ];

  // Search trees: [node * 2 + (answer above node ? 1 : 0)] -> next node, 0 ends.
  internal static readonly byte[] SlotTree = [
    0, 0, 0, 0, 1, 3, 0, 0, 2, 6, 0, 0, 5, 7, 0, 0, 4, 12, 0, 0, 9, 11, 0, 0, 10, 14, 0, 0, 13, 15, 0, 0,
    8, 32, 0, 0, 17, 19, 0, 0, 18, 22, 0, 0, 21, 23, 0, 0, 20, 28, 0, 0, 25, 27, 0, 0, 26, 30, 0, 0, 29, 31, 0, 0,
    24, 40, 0, 0, 33, 35, 0, 0, 34, 38, 0, 0, 37, 39, 0, 0, 36, 44, 0, 0, 41, 43, 0, 0, 42, 0, 0, 0,
  ];

  internal static readonly byte[] RecentTree = [
    0, 0, 0, 2, 0, 4, 0, 0, 3, 8, 0, 0, 5, 7, 0, 0, 6, 10, 0, 0, 9, 0,
  ];

  internal static readonly byte[] LengthTree = [
    0, 0, 0, 0, 0, 4, 0, 0, 3, 5, 0, 0, 2, 8, 0, 0, 7, 9, 0, 0, 6, 18, 0, 0, 11, 13, 0, 0, 12, 16, 0, 0,
    15, 17, 0, 0, 14, 30, 0, 0, 19, 21, 0, 0, 20, 26, 0, 0, 23, 25, 0, 0, 24, 28, 0, 0, 27, 29, 0, 0, 22, 62, 0, 0,
    31, 33, 0, 0, 32, 36, 0, 0, 35, 37, 0, 0, 34, 42, 0, 0, 39, 41, 0, 0, 40, 44, 0, 0, 43, 45, 0, 0, 38, 54, 0, 0,
    47, 49, 0, 0, 48, 52, 0, 0, 51, 53, 0, 0, 50, 58, 0, 0, 55, 57, 0, 0, 56, 60, 0, 0, 59, 61, 0, 0, 46, 254, 0, 0,
    63, 65, 0, 0, 64, 68, 0, 0, 67, 69, 0, 0, 66, 74, 0, 0, 71, 73, 0, 0, 72, 76, 0, 0, 75, 77, 0, 0, 70, 86, 0, 0,
    79, 81, 0, 0, 80, 84, 0, 0, 83, 85, 0, 0, 82, 90, 0, 0, 87, 89, 0, 0, 88, 92, 0, 0, 91, 93, 0, 0, 78, 110, 0, 0,
    95, 97, 0, 0, 96, 100, 0, 0, 99, 101, 0, 0, 98, 106, 0, 0, 103, 105, 0, 0, 104, 108, 0, 0, 107, 109, 0, 0, 102, 118, 0, 0,
    111, 113, 0, 0, 112, 116, 0, 0, 115, 117, 0, 0, 114, 122, 0, 0, 119, 121, 0, 0, 120, 124, 0, 0, 123, 125, 0, 0, 94, 190, 0, 0,
    127, 129, 0, 0, 128, 132, 0, 0, 131, 133, 0, 0, 130, 138, 0, 0, 135, 137, 0, 0, 136, 140, 0, 0, 139, 141, 0, 0, 134, 150, 0, 0,
    143, 145, 0, 0, 144, 148, 0, 0, 147, 149, 0, 0, 146, 154, 0, 0, 151, 153, 0, 0, 152, 156, 0, 0, 155, 157, 0, 0, 142, 174, 0, 0,
    159, 161, 0, 0, 160, 164, 0, 0, 163, 165, 0, 0, 162, 170, 0, 0, 167, 169, 0, 0, 168, 172, 0, 0, 171, 173, 0, 0, 166, 182, 0, 0,
    175, 177, 0, 0, 176, 180, 0, 0, 179, 181, 0, 0, 178, 186, 0, 0, 183, 185, 0, 0, 184, 188, 0, 0, 187, 189, 0, 0, 158, 222, 0, 0,
    191, 193, 0, 0, 192, 196, 0, 0, 195, 197, 0, 0, 194, 202, 0, 0, 199, 201, 0, 0, 200, 204, 0, 0, 203, 205, 0, 0, 198, 214, 0, 0,
    207, 209, 0, 0, 208, 212, 0, 0, 211, 213, 0, 0, 210, 218, 0, 0, 215, 217, 0, 0, 216, 220, 0, 0, 219, 221, 0, 0, 206, 238, 0, 0,
    223, 225, 0, 0, 224, 228, 0, 0, 227, 229, 0, 0, 226, 234, 0, 0, 231, 233, 0, 0, 232, 236, 0, 0, 235, 237, 0, 0, 230, 246, 0, 0,
    239, 241, 0, 0, 240, 244, 0, 0, 243, 245, 0, 0, 242, 250, 0, 0, 247, 249, 0, 0, 248, 252, 0, 0, 251, 253, 0, 0, 126, 0, 0, 0,
  ];

  // Distance slots: slot k covers [SlotBase[k], SlotBase[k] + SlotSpan[k]).
  internal static readonly uint[] SlotBase = BuildSlotBases();
  internal static readonly uint[] SlotSpan = [
    1, 2, 4, 8, 16, 32, 32, 32, 64, 64, 128, 128, 256, 256, 512, 512,
    1024, 1024, 2048, 2048, 4096, 4096, 8192, 8192, 16384, 16384, 32768, 32768, 65536, 65536, 131072, 131072,
    262144, 262144, 524288, 524288, 1048576, 1048576, 2097152, 2097152, 4194304, 4194304, 8388608, 8388608,
  ];

  /// <summary>The slot whose range holds <paramref name="distance"/>.</summary>
  internal static int SlotOf(uint distance) {
    var slot = SlotBase.Length - 1;
    while (SlotBase[slot] > distance)
      --slot;
    return slot;
  }

  /// <summary>
  /// Whether a new distance can be coded at <paramref name="position"/>: the decoder narrows a
  /// slot at the start of the stream and halves it around a low bit, which leaves some odd
  /// offsets out.
  /// </summary>
  internal static bool CanCodeDistance(uint distance, int position) {
    if (distance == 0 || distance > (uint)position)
      return false;
    var k = SlotOf(distance);
    if (k == 0)
      return true;

    var low = SlotBase[k];
    var span = SlotSpan[k];
    if (low + span - 1 > (uint)position)
      span = (uint)position - low + 1;
    var offset = distance - low;
    return span >= 4 ? offset >> 1 < span >> 1 : offset < span;
  }

  /// <summary>Decodes <paramref name="unpackedSize"/> bytes of an ALZ stream.</summary>
  public static byte[] Decode(byte[] packed, int offset, int length, int method, int unpackedSize, IReadOnlyList<long> fileEnds) {
    if (method is < UharcMethods.AlzFirst or > UharcMethods.AlzLast)
      throw new ArgumentOutOfRangeException(nameof(method));

    var output = new byte[unpackedSize];
    if (unpackedSize == 0)
      return output;

    var model = new UharcAlzModel<UharcDecoding>(new(new UharcRangeDecoder(packed, offset, length)), method, selector: 0);
    var resets = new UharcModelResets(fileEnds, unpackedSize, UharcModelResets.AlzSpan, mergeSmallFiles: false);
    var position = 0;
    while (position < unpackedSize) {
      if (resets.IsResetPoint(position))
        model.ResetForNextFile(selector: 0);

      position = model.Step(output, position, default);
    }

    return output;
  }

  private static uint[] BuildSlotBases() {
    var bases = new uint[44];
    for (var i = 0; i < bases.Length; ++i)
      bases[i] = i < 6
        ? 1u << i
        : ((i - 6) & 1) == 0 ? 1u << (6 + (i - 6) / 2) : 3u << (5 + (i - 6) / 2);
    return bases;
  }
}
