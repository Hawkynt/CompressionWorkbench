namespace Compression.Core.Checksums;

/// <summary>
/// Table-driven CRC-16 implementation with configurable polynomial.
/// </summary>
public sealed class Crc16 : IChecksum {
  /// <summary>
  /// CRC-16/ARC polynomial (reflected form).
  /// </summary>
  public const ushort Arc = 0xA001;
  
  private readonly ushort[] _table;
  private readonly ushort _initialValue;
  private ushort _crc;

  /// <summary>
  /// Initializes a new <see cref="Crc16"/> with the specified polynomial.
  /// </summary>
  /// <param name="polynomial">The reflected polynomial. Defaults to <see cref="Arc"/>.</param>
  /// <param name="initialValue">The initial CRC value. Defaults to 0.</param>
  public Crc16(ushort polynomial = Arc, ushort initialValue = 0) {
    this._table = GenerateTable(polynomial);
    this._initialValue = initialValue;
    this._crc = initialValue;
  }

  /// <inheritdoc />
  public uint Value => this._crc;

  /// <inheritdoc />
  public void Reset() => this._crc = this._initialValue;

  /// <inheritdoc />
  public void Update(byte b) => this._crc = (ushort)((this._crc >> 8) ^ this._table[(byte)(this._crc ^ b)]);

  /// <inheritdoc />
  public void Update(ReadOnlySpan<byte> data) {
    var crc = this._crc;
    foreach (var value in data)
      crc = (ushort)((crc >> 8) ^ this._table[(byte)(crc ^ value)]);

    this._crc = crc;
  }

  /// <summary>
  /// Computes the CRC-16 of the given data using the ARC polynomial.
  /// </summary>
  /// <param name="data">The data to checksum.</param>
  /// <returns>The CRC-16 value.</returns>
  public static ushort Compute(ReadOnlySpan<byte> data) {
    var crc = new Crc16();
    crc.Update(data);
    return (ushort)crc.Value;
  }

  private static ushort[] GenerateTable(ushort polynomial) => CrcTableGenerator.Generate(polynomial);
}
