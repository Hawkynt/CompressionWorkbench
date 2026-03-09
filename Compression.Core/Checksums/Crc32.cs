namespace Compression.Core.Checksums;

/// <summary>
/// Table-driven CRC-32 implementation with configurable polynomial.
/// </summary>
public sealed class Crc32 : IChecksum {
  /// <summary>
  /// Standard IEEE 802.3 polynomial (used by ZIP, GZIP, PNG, etc.).
  /// </summary>
  public const uint Ieee = 0xEDB88320u;

  /// <summary>
  /// Castagnoli (CRC-32C) polynomial.
  /// </summary>
  public const uint Castagnoli = 0x82F63B78u;

  private readonly uint[] _table;
  private uint _crc;

  /// <summary>
  /// Initializes a new <see cref="Crc32"/> with the specified polynomial.
  /// </summary>
  /// <param name="polynomial">The reflected polynomial. Defaults to <see cref="Ieee"/>.</param>
  public Crc32(uint polynomial = Ieee) {
    this._table = GenerateTable(polynomial);
    this._crc = 0xFFFFFFFFu;
  }

  /// <inheritdoc />
  public uint Value => this._crc ^ 0xFFFFFFFFu;

  /// <inheritdoc />
  public void Reset() => this._crc = 0xFFFFFFFFu;

  /// <inheritdoc />
  public void Update(byte b) => this._crc = this._table[(this._crc ^ b) & 0xFF] ^ (this._crc >> 8);

  /// <inheritdoc />
  public void Update(ReadOnlySpan<byte> data) {
    uint crc = this._crc;
    for (int i = 0; i < data.Length; ++i)
      crc = this._table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);

    this._crc = crc;
  }

  /// <summary>
  /// Computes the CRC-32 of the given data in a single call using the IEEE polynomial.
  /// </summary>
  /// <param name="data">The data to checksum.</param>
  /// <returns>The CRC-32 value.</returns>
  public static uint Compute(ReadOnlySpan<byte> data) {
    var crc = new Crc32();
    crc.Update(data);
    return crc.Value;
  }

  /// <summary>
  /// Computes the CRC-32 of the given data with the specified polynomial.
  /// </summary>
  /// <param name="data">The data to checksum.</param>
  /// <param name="polynomial">The reflected polynomial.</param>
  /// <returns>The CRC-32 value.</returns>
  public static uint Compute(ReadOnlySpan<byte> data, uint polynomial) {
    var crc = new Crc32(polynomial);
    crc.Update(data);
    return crc.Value;
  }

  private static uint[] GenerateTable(uint polynomial) {
    var table = new uint[256];
    for (uint i = 0; i < 256; ++i) {
      uint crc = i;
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
