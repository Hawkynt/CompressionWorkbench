namespace Compression.Core.Checksums;

/// <summary>
/// Table-driven CRC-64 implementation with configurable polynomial and slicing-by-4 acceleration.
/// </summary>
public sealed class Crc64 : IChecksum {
  /// <summary>
  /// ECMA-182 polynomial used by the XZ format.
  /// </summary>
  public const ulong Ecma182 = 0xC96C5795D7870F42UL;

  private readonly ulong[][] _tables;
  private ulong _crc;

  /// <summary>
  /// Initializes a new <see cref="Crc64"/> with the specified polynomial.
  /// </summary>
  /// <param name="polynomial">The reflected polynomial. Defaults to <see cref="Ecma182"/>.</param>
  public Crc64(ulong polynomial = Crc64.Ecma182) {
    this._tables = CrcTableGenerator.GenerateSlicingTables(polynomial);
    this._crc = 0xFFFFFFFFFFFFFFFFUL;
  }

  /// <summary>
  /// Gets the full 64-bit CRC value.
  /// </summary>
  public ulong Value64 => this._crc ^ 0xFFFFFFFFFFFFFFFFUL;

  /// <inheritdoc />
  /// <remarks>Returns the lower 32 bits of the CRC-64 for interface compatibility.</remarks>
  uint IChecksum.Value => (uint)this.Value64;

  /// <inheritdoc />
  public void Reset() => this._crc = 0xFFFFFFFFFFFFFFFFUL;

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
      var lo = (uint)(crc & 0xFFFFFFFF);
      lo ^= (uint)(data[i] | (data[i + 1] << 8) | (data[i + 2] << 16) | (data[i + 3] << 24));
      crc = t3[lo & 0xFF] ^ t2[(lo >> 8) & 0xFF] ^ (crc >> 32) ^ t1[(lo >> 16) & 0xFF] ^ t0[(lo >> 24) & 0xFF];
      i += 4;
    }

    // Scalar tail
    for (; i < data.Length; ++i)
      crc = t0[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);

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
}
