namespace FileFormat.AlZip;

/// <summary>
/// ALZip's encryption: PKWARE's traditional ZIP cipher (APPNOTE 6.1), password bytes in CP949, with
/// a 12-byte header whose last decrypted byte checks the password against the CRC's top byte (or the
/// DOS time's second byte when the entry has a data descriptor).
/// </summary>
internal sealed class AlZipCrypto {
  public const int HeaderLength = 12;
  private static readonly uint[] CrcTable = BuildTable();
  private uint _k0 = 305419896, _k1 = 591751049, _k2 = 878082192;

  private static uint[] BuildTable() {
    var t = new uint[256];
    for (var i = 0u; i < 256; ++i) {
      var c = i;
      for (var j = 0; j < 8; ++j)
        c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
      t[i] = c;
    }
    return t;
  }

  public AlZipCrypto(ReadOnlySpan<byte> password) {
    foreach (var b in password)
      this.Update(b);
  }

  private void Update(byte c) {
    this._k0 = CrcTable[(this._k0 ^ c) & 0xFF] ^ (this._k0 >> 8);
    this._k1 = (this._k1 + (this._k0 & 0xFF)) * 134775813 + 1;
    this._k2 = CrcTable[(this._k2 ^ (this._k1 >> 24)) & 0xFF] ^ (this._k2 >> 8);
  }

  private byte KeyStream() {
    var t = (ushort)(this._k2 | 2);
    return (byte)((t * (t ^ 1)) >> 8);
  }

  /// <summary>Decrypts the 12-byte header and reports whether its check byte matches.</summary>
  public bool CheckHeader(ReadOnlySpan<byte> header, byte expected) {
    byte last = 0;
    foreach (var b in header) {
      last = (byte)(b ^ this.KeyStream());
      this.Update(last);
    }
    return last == expected;
  }

  public void Decrypt(Span<byte> data) {
    for (var i = 0; i < data.Length; ++i) {
      var p = (byte)(data[i] ^ this.KeyStream());
      this.Update(p);
      data[i] = p;
    }
  }

  public void Encrypt(Span<byte> data) {
    for (var i = 0; i < data.Length; ++i) {
      var p = data[i];
      data[i] = (byte)(p ^ this.KeyStream());
      this.Update(p);
    }
  }

  /// <summary>An encryption header for a fresh cipher state: 11 random bytes and the check byte.</summary>
  public byte[] MakeHeader(byte check) {
    var header = new byte[HeaderLength];
    System.Security.Cryptography.RandomNumberGenerator.Fill(header.AsSpan(0, HeaderLength - 1));
    header[^1] = check;
    this.Encrypt(header);
    return header;
  }
}
