namespace Compression.Core.Checksums;

/// <summary>
/// Table-driven CRC-32 implementation with configurable polynomial and slicing-by-4 acceleration.
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

  private readonly uint[][] _tables;
  private uint _crc;

  /// <summary>
  /// Initializes a new <see cref="Crc32"/> with the specified polynomial.
  /// </summary>
  /// <param name="polynomial">The reflected polynomial. Defaults to <see cref="Ieee"/>.</param>
  public Crc32(uint polynomial = Crc32.Ieee) {
    this._tables = CrcTableGenerator.GenerateSlicingTables(polynomial);
    this._crc = 0xFFFFFFFFu;
  }

  /// <inheritdoc />
  public uint Value => this._crc ^ 0xFFFFFFFFu;

  /// <inheritdoc />
  public void Reset() => this._crc = 0xFFFFFFFFu;

  /// <inheritdoc />
  public void Update(byte b) => this._crc = this._tables[0][(this._crc ^ b) & 0xFF] ^ (this._crc >> 8);

  /// <inheritdoc />
  public void Update(ReadOnlySpan<byte> data) {
    var crc = this._crc;
    var t0 = this._tables[0];
    var t1 = this._tables[1];
    var t2 = this._tables[2];
    var t3 = this._tables[3];
    var i = 0;

    // Slicing-by-4: process 4 bytes per iteration
    var end4 = data.Length - 3;
    while (i < end4) {
      crc ^= (uint)(data[i] | (data[i + 1] << 8) | (data[i + 2] << 16) | (data[i + 3] << 24));
      crc = t3[crc & 0xFF] ^ t2[(crc >> 8) & 0xFF] ^ t1[(crc >> 16) & 0xFF] ^ t0[(crc >> 24) & 0xFF];
      i += 4;
    }

    // Scalar tail
    for (; i < data.Length; ++i)
      crc = t0[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);

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
}
