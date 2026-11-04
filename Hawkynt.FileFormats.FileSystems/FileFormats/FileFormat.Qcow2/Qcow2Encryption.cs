using System.Text;
using Compression.Core.Crypto;

namespace FileFormat.Qcow2;

/// <summary>
/// The payload encryption of a QCOW2 image (qcow2.txt, "Data encryption"): guest data clusters
/// are encrypted 512-byte sector by sector, metadata never.
/// </summary>
/// <remarks>
/// <para><b>AES</b> (crypt_method 1): AES-CBC with the plain64 IV of the <i>guest</i> sector. The key
/// is the password's bytes, cut to 16 or padded with zeros to 16: AES-128, whatever qcow2.txt says
/// about 256-bit keys. That was measured: <c>qemu-img</c> 8.2 accepts no other reading of the images
/// it writes.</para>
/// <para><b>LUKS</b> (crypt_method 2): a LUKS1 header in the clusters the full-disk-encryption header
/// extension points at (key material offsets relative to it); the payload cipher the header names,
/// with the IV of the <i>host</i> sector.</para>
/// </remarks>
internal sealed class Qcow2Encryption : IDisposable {
  private const int SectorSize = 512;
  private readonly Luks1SectorCipher _cipher;
  private readonly bool _hostSectorIv;

  private Qcow2Encryption(Luks1SectorCipher cipher, bool hostSectorIv) {
    this._cipher = cipher;
    this._hostSectorIv = hostSectorIv;
  }

  /// <summary>The cipher of an encrypted image, unlocked with <paramref name="password"/>.</summary>
  /// <exception cref="UnauthorizedAccessException">No password was given, or it is wrong.</exception>
  public static Qcow2Encryption Open(Stream image, in Qcow2Header header, string? password) {
    if (password is null)
      throw new UnauthorizedAccessException("QCOW2: the image is encrypted; a password is required.");
    var passphrase = Encoding.UTF8.GetBytes(password);
    if (header.CryptMethod == Qcow2Structures.CryptAes) {
      var key = new byte[16];
      passphrase.AsSpan(0, Math.Min(16, passphrase.Length)).CopyTo(key);
      return new Qcow2Encryption(Luks1SectorCipher.Create("aes", "cbc-plain64", key), hostSectorIv: false);
    }

    var luks = ReadLuksHeader(image, header);
    var fdeOffset = header.FdeOffset;
    var fdeLength = header.FdeLength;
    byte[] masterKey;
    try {
      masterKey = luks.Unlock(passphrase, (offset, length) => {
        if (offset < 0 || offset > fdeLength - length)
          throw new InvalidDataException("QCOW2: LUKS key material lies outside the encryption header.");
        var material = new byte[length];
        Qcow2Structures.ReadExactlyAt(image, checked(fdeOffset + offset), material);
        return material;
      });
    } catch (System.Security.Cryptography.CryptographicException) {
      throw new UnauthorizedAccessException("QCOW2: the password opens none of the image's LUKS key slots.");
    }
    return new Qcow2Encryption(luks.CreateCipher(masterKey), hostSectorIv: true);
  }

  /// <summary>The LUKS header behind the full-disk-encryption header extension.</summary>
  internal static Luks1Header ReadLuksHeader(Stream image, in Qcow2Header header) {
    var bytes = new byte[Luks1Header.HeaderSize];
    Qcow2Structures.ReadExactlyAt(image, header.FdeOffset, bytes);
    return Luks1Header.Parse(bytes);
  }

  /// <summary>
  /// Decrypts <paramref name="data"/>, whole sectors stored at <paramref name="hostOffset"/> that
  /// hold the guest bytes from <paramref name="guestOffset"/>.
  /// </summary>
  public void Decrypt(long hostOffset, long guestOffset, Span<byte> data)
    => this._cipher.Decrypt(this.Sector(hostOffset, guestOffset), data);

  /// <summary>Encrypts <paramref name="data"/> for storage at <paramref name="hostOffset"/>.</summary>
  public void Encrypt(long hostOffset, long guestOffset, Span<byte> data)
    => this._cipher.Encrypt(this.Sector(hostOffset, guestOffset), data);

  private ulong Sector(long hostOffset, long guestOffset) {
    var offset = this._hostSectorIv ? hostOffset : guestOffset;
    if ((offset & (SectorSize - 1)) != 0)
      throw new InvalidOperationException("QCOW2: encrypted data is addressed in whole sectors.");
    return (ulong)(offset / SectorSize);
  }

  /// <summary>An encryptor for a new LUKS image, from the master key and header it was built with.</summary>
  internal static Qcow2Encryption ForLuks(Luks1SectorCipher cipher) => new(cipher, hostSectorIv: true);

  /// <inheritdoc />
  public void Dispose() => this._cipher.Dispose();
}
