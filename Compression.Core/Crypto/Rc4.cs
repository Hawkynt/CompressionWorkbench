namespace Compression.Core.Crypto;

/// <summary>
/// The RC4 (ARCFOUR) stream cipher: key-scheduling over a 256-byte permutation, then one keystream
/// byte XOR-ed onto each data byte. Encryption and decryption are the same operation.
/// </summary>
/// <remarks>
/// Written from the published algorithm (the 1994 "ARCFOUR" posting, as specified in RFC 6229's
/// test-vector document and ISO 32000-1 §7.6.2, which PDF's standard security handler uses).
/// RC4 is broken as a cipher; it exists here only to read data that was protected with it.
/// </remarks>
public sealed class Rc4 {

  private readonly byte[] _s = new byte[256];
  private int _i;
  private int _j;

  /// <summary>Keys the cipher; <paramref name="key"/> must be 1 to 256 bytes.</summary>
  public Rc4(ReadOnlySpan<byte> key) {
    if (key.Length is 0 or > 256)
      throw new ArgumentException("RC4 keys are 1 to 256 bytes long.", nameof(key));
    for (var i = 0; i < 256; ++i)
      this._s[i] = (byte)i;
    for (int i = 0, j = 0; i < 256; ++i) {
      j = (j + this._s[i] + key[i % key.Length]) & 0xFF;
      (this._s[i], this._s[j]) = (this._s[j], this._s[i]);
    }
  }

  /// <summary>XORs the next keystream bytes onto <paramref name="data"/> in place.</summary>
  public void Transform(Span<byte> data) {
    for (var k = 0; k < data.Length; ++k) {
      this._i = (this._i + 1) & 0xFF;
      this._j = (this._j + this._s[this._i]) & 0xFF;
      (this._s[this._i], this._s[this._j]) = (this._s[this._j], this._s[this._i]);
      data[k] ^= this._s[(this._s[this._i] + this._s[this._j]) & 0xFF];
    }
  }

  /// <summary>Encrypts or decrypts <paramref name="data"/> under <paramref name="key"/> and returns a new array.</summary>
  public static byte[] Apply(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data) {
    var result = data.ToArray();
    new Rc4(key).Transform(result);
    return result;
  }
}
