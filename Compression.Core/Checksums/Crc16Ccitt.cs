namespace Compression.Core.Checksums;

/// <summary>
/// Table-driven CRC-16/CCITT implementation (non-reflected form).
/// Polynomial 0x1021, MSB-first. Used by ECMA-167 / UDF descriptor tags,
/// XMODEM (init=0x0000) and CCITT-FALSE (init=0xFFFF) variants.
/// </summary>
/// <remarks>
/// Distinct from <see cref="Crc16"/>, which is the reflected CRC-16/ARC
/// variant with polynomial 0xA001.
/// </remarks>
public sealed class Crc16Ccitt : IChecksum {
  /// <summary>
  /// CRC-16/CCITT polynomial (non-reflected form).
  /// </summary>
  public const ushort Polynomial = 0x1021;

  private static readonly ushort[] _Table = GenerateTable();

  private readonly ushort _initialValue;
  private ushort _crc;

  /// <summary>
  /// Initializes a new <see cref="Crc16Ccitt"/> with the specified initial value.
  /// </summary>
  /// <param name="initialValue">The initial CRC value. Defaults to 0 (XMODEM / UDF).</param>
  public Crc16Ccitt(ushort initialValue = 0) {
    this._initialValue = initialValue;
    this._crc = initialValue;
  }

  /// <inheritdoc />
  public uint Value => this._crc;

  /// <inheritdoc />
  public void Reset() => this._crc = this._initialValue;

  /// <inheritdoc />
  public void Update(byte b) => this._crc = (ushort)((this._crc << 8) ^ _Table[(byte)((this._crc >> 8) ^ b)]);

  /// <inheritdoc />
  public void Update(ReadOnlySpan<byte> data) {
    var crc = this._crc;
    foreach (var value in data)
      crc = (ushort)((crc << 8) ^ _Table[(byte)((crc >> 8) ^ value)]);

    this._crc = crc;
  }

  /// <summary>
  /// Computes the CRC-16/CCITT of the given data.
  /// </summary>
  /// <param name="data">The data to checksum.</param>
  /// <param name="initial">The initial CRC value. Defaults to 0 (XMODEM / UDF).</param>
  /// <returns>The CRC-16 value.</returns>
  public static ushort Compute(ReadOnlySpan<byte> data, ushort initial = 0) {
    var crc = initial;
    foreach (var value in data)
      crc = (ushort)((crc << 8) ^ _Table[(byte)((crc >> 8) ^ value)]);

    return crc;
  }

  private static ushort[] GenerateTable() {
    var table = new ushort[256];
    for (var i = 0; i < 256; ++i) {
      var crc = (ushort)(i << 8);
      for (var j = 0; j < 8; ++j)
        crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ Polynomial) : (ushort)(crc << 1);

      table[i] = crc;
    }
    return table;
  }
}
