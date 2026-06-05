#pragma warning disable CS1591
namespace Codec.Nes2a03;

/// <summary>
/// The 2A03 length-counter lookup table. A 5-bit index written into the high bits of a
/// channel's length register selects an initial length-counter value from these 32 entries.
/// </summary>
internal static class ApuLengthTable {

  private static readonly byte[] Table = [
    10, 254, 20, 2, 40, 4, 80, 6, 160, 8, 60, 10, 14, 12, 26, 14,
    12, 16, 24, 18, 48, 20, 96, 22, 192, 24, 72, 26, 16, 28, 32, 30,
  ];

  public static int Lookup(int index) => Table[index & 0x1F];
}
