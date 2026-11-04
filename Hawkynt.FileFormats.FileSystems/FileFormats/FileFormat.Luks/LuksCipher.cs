using System.Buffers.Binary;
using System.Security.Cryptography;

namespace FileFormat.Luks;

/// <summary>
/// The dm-crypt sector ciphers LUKS uses: AES in XTS or CBC mode with the <c>plain</c>,
/// <c>plain64</c> or <c>essiv:&lt;hash&gt;</c> IV generators. Every sector is processed on its own,
/// its IV derived from the sector number (plus the segment's IV tweak).
/// </summary>
public sealed class LuksCipher : IDisposable {
  private readonly Aes _aes;
  private readonly Aes? _tweakAes;   // XTS: second half of the key; ESSIV: hashed key
  private readonly bool _xts;
  private readonly bool _essiv;
  private readonly bool _plain32;

  /// <summary>The <c>cipher-mode</c> strings this class implements, as cryptsetup spells them.</summary>
  public static bool IsSupported(string cipher, string mode)
    => cipher.Equals("aes", StringComparison.OrdinalIgnoreCase) &&
       mode.ToLowerInvariant() is "xts-plain64" or "xts-plain" or "cbc-plain64" or "cbc-plain" or "cbc-essiv:sha256";

  public LuksCipher(string cipher, string mode, ReadOnlySpan<byte> key) {
    if (!IsSupported(cipher, mode))
      throw new NotSupportedException($"LUKS cipher '{cipher}-{mode}' is not supported (aes with xts-plain64/plain, cbc-plain64/plain or cbc-essiv:sha256 are).");
    mode = mode.ToLowerInvariant();
    this._xts = mode.StartsWith("xts", StringComparison.Ordinal);
    this._essiv = mode.EndsWith("essiv:sha256", StringComparison.Ordinal);
    this._plain32 = mode.EndsWith("-plain", StringComparison.Ordinal);
    this._aes = Aes.Create();
    if (this._xts) {
      if (key.Length is not (32 or 64))
        throw new ArgumentException("AES-XTS needs a 256- or 512-bit key.", nameof(key));
      this._aes.Key = key[..(key.Length / 2)].ToArray();
      this._tweakAes = Aes.Create();
      this._tweakAes.Key = key[(key.Length / 2)..].ToArray();
    } else {
      if (key.Length is not (16 or 24 or 32))
        throw new ArgumentException("AES-CBC needs a 128-, 192- or 256-bit key.", nameof(key));
      this._aes.Key = key.ToArray();
      if (this._essiv) {
        this._tweakAes = Aes.Create();
        this._tweakAes.Key = SHA256.HashData(key);
      }
    }
  }

  private void Iv(ulong sector, Span<byte> iv) {
    iv.Clear();
    if (this._plain32) BinaryPrimitives.WriteUInt32LittleEndian(iv, (uint)sector);
    else BinaryPrimitives.WriteUInt64LittleEndian(iv, sector);
    if (this._essiv) this._tweakAes!.EncryptEcb(iv, iv, PaddingMode.None);
  }

  /// <summary>Decrypts whole sectors in place; <paramref name="firstSector"/> is the IV sector of the first one.</summary>
  public void Decrypt(Span<byte> data, ulong firstSector, int sectorSize = 512) => this.Process(data, firstSector, sectorSize, encrypt: false);

  /// <summary>Encrypts whole sectors in place.</summary>
  public void Encrypt(Span<byte> data, ulong firstSector, int sectorSize = 512) => this.Process(data, firstSector, sectorSize, encrypt: true);

  private void Process(Span<byte> data, ulong firstSector, int sectorSize, bool encrypt) {
    if (data.Length % sectorSize != 0)
      throw new ArgumentException("LUKS data must be whole sectors.", nameof(data));
    Span<byte> iv = stackalloc byte[16];
    // dm-crypt derives the IV from 512-byte units even for larger sectors.
    var ivStep = (ulong)(sectorSize / 512);
    for (var offset = 0; offset < data.Length; offset += sectorSize) {
      var sector = data.Slice(offset, sectorSize);
      this.Iv(firstSector + (ulong)(offset / sectorSize) * ivStep, iv);
      if (this._xts) this.Xts(sector, iv, encrypt);
      else if (encrypt) this._aes.EncryptCbc(sector, iv, sector, PaddingMode.None);
      else this._aes.DecryptCbc(sector, iv, sector, PaddingMode.None);
    }
  }

  private void Xts(Span<byte> sector, ReadOnlySpan<byte> iv, bool encrypt) {
    Span<byte> tweak = stackalloc byte[16];
    this._tweakAes!.EncryptEcb(iv, tweak, PaddingMode.None);
    Span<byte> block = stackalloc byte[16];
    for (var i = 0; i < sector.Length; i += 16) {
      var b = sector.Slice(i, 16);
      for (var k = 0; k < 16; ++k) block[k] = (byte)(b[k] ^ tweak[k]);
      if (encrypt) this._aes.EncryptEcb(block, block, PaddingMode.None);
      else this._aes.DecryptEcb(block, block, PaddingMode.None);
      for (var k = 0; k < 16; ++k) b[k] = (byte)(block[k] ^ tweak[k]);
      // tweak *= alpha in GF(2^128), little-endian byte order (IEEE 1619).
      var carry = 0;
      for (var k = 0; k < 16; ++k) {
        var next = tweak[k] >> 7;
        tweak[k] = (byte)((tweak[k] << 1) | carry);
        carry = next;
      }
      if (carry != 0) tweak[0] ^= 0x87;
    }
  }

  public void Dispose() {
    this._aes.Dispose();
    this._tweakAes?.Dispose();
  }
}
