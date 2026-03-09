namespace Compression.Core.Checksums;

/// <summary>
/// Table-driven CRC-64 implementation with configurable polynomial.
/// </summary>
public sealed class Crc64 : IChecksum {
  /// <summary>
  /// ECMA-182 polynomial used by the XZ format.
  /// </summary>
  public const ulong Ecma182 = 0xC96C5795D7870F42UL;

  private readonly ulong[] _table;
  private ulong _crc;

  /// <summary>
  /// Initializes a new <see cref="Crc64"/> with the specified polynomial.
  /// </summary>
  /// <param name="polynomial">The reflected polynomial. Defaults to <see cref="Ecma182"/>.</param>
  public Crc64(ulong polynomial = Ecma182) {
    this._table = GenerateTable(polynomial);
    this._crc = 0xFFFFFFFFFFFFFFFFUL;
  }

  /// <summary>
  /// Gets the full 64-bit CRC value.
  /// </summary>
  public ulong Value64 => this._crc ^ 0xFFFFFFFFFFFFFFFFUL;

  /// <inheritdoc />
  /// <remarks>Returns the lower 32 bits of the CRC-64 for interface compatibility.</remarks>
  uint IChecksum.Value => (uint)Value64;

  /// <inheritdoc />
  public void Reset() => this._crc = 0xFFFFFFFFFFFFFFFFUL;

  /// <inheritdoc />
  public void Update(byte b) => this._crc = this._table[(this._crc ^ b) & 0xFF] ^ (this._crc >> 8);

  /// <inheritdoc />
  public void Update(ReadOnlySpan<byte> data) {
    ulong crc = this._crc;
    for (int i = 0; i < data.Length; ++i)
      crc = this._table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);

    this._crc = crc;
  }

  /// <summary>
  /// Computes the CRC-64 of the given data in a single call using the ECMA-182 polynomial.
  /// </summary>
  /// <param name="data">The data to checksum.</param>
  /// <returns>The CRC-64 value.</returns>
  public static ulong Compute(ReadOnlySpan<byte> data) {
    var crc = new Crc64();
    crc.Update(data);
    return crc.Value64;
  }

  private static ulong[] GenerateTable(ulong polynomial) {
    var table = new ulong[256];
    for (uint i = 0; i < 256; ++i) {
      ulong crc = i;
      for (int j = 0; j < 8; ++j) {
        if ((crc & 1) != 0)
          crc = (crc >> 1) ^ polynomial;
        else
          crc >>= 1;
      }
      table[i] = crc;
    }
    return table;
  }
}
