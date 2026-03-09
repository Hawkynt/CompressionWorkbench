namespace Compression.Core.Checksums;

/// <summary>
/// Adler-32 checksum as used by zlib, with Nmax optimization.
/// </summary>
public sealed class Adler32 : IChecksum {
  private const uint Mod = 65521; // largest prime less than 2^16
  private const int Nmax = 5552;  // max bytes before modulus is needed

  private uint _a = 1;
  private uint _b;

  /// <inheritdoc />
  public uint Value => (this._b << 16) | this._a;

  /// <inheritdoc />
  public void Reset() {
    this._a = 1;
    this._b = 0;
  }

  /// <inheritdoc />
  public void Update(byte b) {
    this._a = (this._a + b) % Mod;
    this._b = (this._b + this._a) % Mod;
  }

  /// <inheritdoc />
  public void Update(ReadOnlySpan<byte> data) {
    uint a = this._a;
    uint b = this._b;
    int offset = 0;
    int remaining = data.Length;

    while (remaining > 0) {
      int blockLen = Math.Min(remaining, Nmax);
      for (int i = 0; i < blockLen; ++i) {
        a += data[offset + i];
        b += a;
      }
      a %= Mod;
      b %= Mod;
      offset += blockLen;
      remaining -= blockLen;
    }

    this._a = a;
    this._b = b;
  }

  /// <summary>
  /// Computes the Adler-32 of the given data in a single call.
  /// </summary>
  /// <param name="data">The data to checksum.</param>
  /// <returns>The Adler-32 value.</returns>
  public static uint Compute(ReadOnlySpan<byte> data) {
    var adler = new Adler32();
    adler.Update(data);
    return adler.Value;
  }
}
