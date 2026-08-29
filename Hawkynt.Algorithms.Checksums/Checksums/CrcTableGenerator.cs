using System.Numerics;

namespace Compression.Core.Checksums;

/// <summary>
/// Generates CRC lookup tables for any integer width.
/// </summary>
internal static class CrcTableGenerator {
  /// <summary>
  /// Generates a 256-entry CRC lookup table for the given reflected polynomial.
  /// </summary>
  internal static T[] Generate<T>(T polynomial) where T : IBinaryInteger<T> {
    var table = new T[256];
    for (var i = 0; i < 256; ++i) {
      var crc = T.CreateTruncating(i);
      for (var j = 0; j < 8; ++j)
        crc = (crc & T.One) != T.Zero ? (crc >>> 1) ^ polynomial : crc >>> 1;

      table[i] = crc;
    }
    return table;
  }

  /// <summary>
  /// Generates 4 slicing tables (256 entries each) for slicing-by-4 CRC acceleration.
  /// Table 0 is the standard table; tables 1-3 are derived for multi-byte processing.
  /// </summary>
  internal static T[][] GenerateSlicingTables<T>(T polynomial) where T : IBinaryInteger<T> {
    var tables = new T[4][];
    tables[0] = Generate(polynomial);

    for (var t = 1; t < 4; ++t) {
      tables[t] = new T[256];
      for (var i = 0; i < 256; ++i)
        tables[t][i] = (tables[t - 1][i] >>> 8) ^ tables[0][int.CreateTruncating(tables[t - 1][i] & T.CreateTruncating(0xFF))];
    }

    return tables;
  }
}
