#pragma warning disable CS1591
using System.Text;

namespace FileFormat.Ghost;

/// <summary>
/// Ghost's per-block CRC-16 stream cipher (CRC-16/ARC polynomial 0xA001).
/// The password seeds a running 16-bit CRC state; each plaintext byte is
/// XOR'd with the low byte of the state, then the state is advanced by
/// feeding the plaintext through <see cref="Update"/>.
/// </summary>
/// <remarks>
/// Reverse-engineered from Norton Ghost 11.5.1 (via nyarime/gho). The
/// encryption indicator sits at byte 12, bit 1 of the file header (see
/// <see cref="GhostFileHeader.IsEncrypted"/>).
/// </remarks>
public sealed class GhostCrc16Cipher {

  private static readonly ushort[] Table = BuildTable();
  private ushort _state;

  private static ushort[] BuildTable() {
    var t = new ushort[256];
    for (var i = 0; i < 256; i++) {
      var crc = (ushort)i;
      for (var j = 0; j < 8; j++)
        crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
      t[i] = crc;
    }
    return t;
  }

  private static ushort Update(ushort crc, byte b)
    => (ushort)((crc >> 8) ^ Table[(crc ^ b) & 0xFF]);

  /// <summary>
  /// Initializes a new instance of <see cref="GhostCrc16Cipher"/>.
  /// </summary>
  public GhostCrc16Cipher(string password) {
    if (string.IsNullOrEmpty(password))
      throw new ArgumentException("Ghost: encrypted images require a non-empty password.", nameof(password));
    this._state = 0xFFFF;
    foreach (var b in Encoding.UTF8.GetBytes(password))
      this._state = Update(this._state, b);
  }

  /// <summary>Decrypts <paramref name="data"/> in place.</summary>
  public void Decrypt(Span<byte> data) {
    for (var i = 0; i < data.Length; i++) {
      var plain = (byte)(data[i] ^ (byte)this._state);
      this._state = Update(this._state, plain);
      data[i] = plain;
    }
  }

  /// <summary>Encrypts <paramref name="data"/> in place.</summary>
  public void Encrypt(Span<byte> data) {
    for (var i = 0; i < data.Length; i++) {
      var cipher = (byte)(data[i] ^ (byte)this._state);
      this._state = Update(this._state, data[i]);
      data[i] = cipher;
    }
  }
}
