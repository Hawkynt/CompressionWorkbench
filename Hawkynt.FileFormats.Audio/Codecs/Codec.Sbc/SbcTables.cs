#pragma warning disable CS1591
namespace Codec.Sbc;

/// <summary>
/// Fixed-point synthesis and bit-allocation tables for the Bluetooth SBC decoder, ported verbatim
/// from FFmpeg (libavcodec). The prototype filter and synthesis-matrix coefficients come from
/// <c>libavcodec/sbcdec_data.h</c> with the reference's <c>SS4 / SS8 / SN4 / SN8</c> right-shifts
/// already applied (the synthesis-matrix shift folds in <c>SBCDEC_FIXED_EXTRA_BITS = 2</c>, so both
/// <c>SN4</c> and <c>SN8</c> shift the 32-bit constant right by 14). The bit-allocation offset
/// tables come from <c>libavcodec/sbc.c</c> (A2DP specification, Appendix B, page 69).
/// </summary>
internal static class SbcTables {

  // libavcodec/sbcdec_data.h: sbc_proto_4_40m0 (SS4: int32 >> 12).
  internal static readonly int[] Proto4M0 = [
    0, -1431, -17773, 17772, 1430, -71, -2679, -25558, 10177, 401,
    -196, -3785, -32328, 3777, -245, -359, -4220, -36940, -804, -511,
  ];

  // libavcodec/sbcdec_data.h: sbc_proto_4_40m1 (SS4: int32 >> 12).
  internal static readonly int[] Proto4M1 = [
    -503, -3392, -38577, -3392, -503, -511, -804, -36940, -4220, -359,
    -245, 3777, -32328, -3785, -196, 401, 10177, -25558, -2679, -71,
  ];

  // libavcodec/sbcdec_data.h: sbc_proto_8_80m0 (SS8: int32 >> 14).
  internal static readonly int[] Proto8M0 = [
    0, -1484, -17826, 17825, 1483, -42, -2105, -21754, 13942, 916,
    -90, -2742, -25579, 10243, 432, -146, -3342, -29150, 6844, 46,
    -216, -3842, -32314, 3837, -237, -299, -4170, -34935, 1288, -424,
    -388, -4253, -36898, -767, -523, -468, -4016, -38114, -2322, -552,
  ];

  // libavcodec/sbcdec_data.h: sbc_proto_8_80m1 (SS8: int32 >> 14).
  internal static readonly int[] Proto8M1 = [
    -528, -3392, -38524, -3392, -528, -552, -2322, -38114, -4016, -468,
    -523, -767, -36898, -4253, -388, -424, 1288, -34935, -4170, -299,
    -237, 3837, -32314, -3842, -216, 46, 6844, -29150, -3342, -146,
    432, 10243, -25579, -2742, -90, 916, 13942, -21754, -2105, -42,
  ];

  // libavcodec/sbcdec_data.h: synmatrix4 (SN4: int32 >> 14, i.e. 11 + 1 + SBCDEC_FIXED_EXTRA_BITS).
  internal static readonly int[][] SynMatrix4 = [
    [5792, -5793, -5793, 5792],
    [3134, -7569, 7568, -3135],
    [0, 0, 0, 0],
    [-3135, 7568, -7569, 3134],
    [-5793, 5792, 5792, -5793],
    [-7569, -3135, 3134, 7568],
    [-8192, -8192, -8192, -8192],
    [-7569, -3135, 3134, 7568],
  ];

  // libavcodec/sbcdec_data.h: synmatrix8 (SN8: int32 >> 14, i.e. 11 + 1 + SBCDEC_FIXED_EXTRA_BITS).
  internal static readonly int[][] SynMatrix8 = [
    [5792, -5793, -5793, 5792, 5792, -5793, -5793, 5792],
    [4551, -8035, 1598, 6811, -6812, -1599, 8034, -4552],
    [3134, -7569, 7568, -3135, -3135, 7568, -7569, 3134],
    [1598, -4552, 6811, -8035, 8034, -6812, 4551, -1599],
    [0, 0, 0, 0, 0, 0, 0, 0],
    [-1599, 4551, -6812, 8034, -8035, 6811, -4552, 1598],
    [-3135, 7568, -7569, 3134, 3134, -7569, 7568, -3135],
    [-4552, 8034, -1599, -6812, 6811, 1598, -8035, 4551],
    [-5793, 5792, 5792, -5793, -5793, 5792, 5792, -5793],
    [-6812, 1598, 8034, 4551, -4552, -8035, -1599, 6811],
    [-7569, -3135, 3134, 7568, 7568, 3134, -3135, -7569],
    [-8035, -6812, -4552, -1599, 1598, 4551, 6811, 8034],
    [-8192, -8192, -8192, -8192, -8192, -8192, -8192, -8192],
    [-8035, -6812, -4552, -1599, 1598, 4551, 6811, 8034],
    [-7569, -3135, 3134, 7568, 7568, 3134, -3135, -7569],
    [-6812, 1598, 8034, 4551, -4552, -8035, -1599, 6811],
  ];

  // libavcodec/sbc.c: sbc_offset4[4][4] (A2DP spec Appendix B, page 69).
  internal static readonly int[][] Offset4 = [
    [-1, 0, 0, 0],
    [-2, 0, 0, 1],
    [-2, 0, 0, 1],
    [-2, 0, 0, 1],
  ];

  // libavcodec/sbc.c: sbc_offset8[4][8] (A2DP spec Appendix B, page 69).
  internal static readonly int[][] Offset8 = [
    [-2, 0, 0, 0, 0, 0, 0, 1],
    [-3, 0, 0, 0, 0, 0, 1, 2],
    [-4, 0, 0, 0, 0, 0, 1, 2],
    [-4, 0, 0, 0, 0, 0, 1, 2],
  ];

  /// <summary>
  /// AV_CRC_8_EBU table (polynomial 0x1D, MSB-first, no reflection), matching FFmpeg's
  /// <c>av_crc_get_table(AV_CRC_8_EBU)</c> used by <c>ff_sbc_crc8</c>.
  /// </summary>
  internal static readonly byte[] Crc8Table = BuildCrc8Table();

  private static byte[] BuildCrc8Table() {
    var table = new byte[256];
    for (var i = 0; i < 256; ++i) {
      var c = i;
      for (var b = 0; b < 8; ++b)
        c = (c & 0x80) != 0 ? ((c << 1) ^ 0x1D) & 0xFF : (c << 1) & 0xFF;
      table[i] = (byte)c;
    }
    return table;
  }
}
