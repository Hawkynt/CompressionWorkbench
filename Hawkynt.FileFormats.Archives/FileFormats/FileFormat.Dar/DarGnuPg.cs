using System.Security.Cryptography;
using Compression.Core.Crypto.OpenPgp;
using Compression.Registry;

namespace FileFormat.Dar;

/// <summary>
/// dar's public-key encryption (<c>-K gnupg:[cipher]:recipient…</c>, <c>docs/DAR-ON-DISK.md</c>
/// section 12): the archive is encrypted with a random key used as the cipher key as it is (no key
/// derivation) — 56 bytes for Blowfish, 32 for the others — and that key, as the literal data of
/// an OpenPGP message to the recipients, is stored in the version header and trailer (flag 0x04).
/// </summary>
internal static class DarGnuPg {
  /// <summary>
  /// The credentials of a GnuPG-encrypted archive, as the password parameter carries them: the
  /// path of an exported secret key (binary or armored, <c>gpg --export-secret-keys</c>),
  /// optionally followed by <c>|</c> and the key's passphrase.
  /// </summary>
  public static (List<OpenPgpKey> Keys, string? Passphrase) Credentials(string? password) {
    if (string.IsNullOrEmpty(password))
      throw new ArchivePasswordException("The DAR archive is encrypted for GnuPG recipients; give the path of an exported secret key (optionally followed by '|' and its passphrase) as the password.", false);
    var bar = password.IndexOf('|');
    var path = bar < 0 ? password : password[..bar];
    var passphrase = bar < 0 ? null : password[(bar + 1)..];
    if (!File.Exists(path))
      throw new ArchivePasswordException($"The DAR archive is encrypted for GnuPG recipients; the secret key file '{path}' does not exist.", true);
    var keys = OpenPgpKey.Load(File.ReadAllBytes(path));
    if (!keys.Any(k => k.HasSecret && k.CanEncrypt))
      throw new ArchivePasswordException($"'{path}' holds no secret key that can decrypt (RSA or cv25519).", true);
    return (keys, passphrase);
  }

  /// <summary>The archive key inside <paramref name="cryptedKey"/>, the OpenPGP message the header carries.</summary>
  /// <exception cref="ArchivePasswordException">No key given opens it, or its passphrase is wrong.</exception>
  public static byte[] OpenKey(byte[] cryptedKey, char cipher, string? password) {
    var (keys, passphrase) = Credentials(password);
    byte[] key;
    try {
      key = OpenPgpMessage.Decrypt(cryptedKey, keys, passphrase);
    } catch (CryptographicException e) {
      throw new ArchivePasswordException($"The DAR archive key does not decrypt with this secret key: {e.Message}", true);
    }
    if (key.Length != DarCrypto.KeyLength(cipher))
      throw new InvalidDataException($"The DAR archive key is {key.Length} bytes; {DarCrypto.CipherName(cipher)} takes {DarCrypto.KeyLength(cipher)}.");
    return key;
  }

  /// <summary>A new random archive key for <paramref name="cipher"/> and its OpenPGP message to every key in <paramref name="recipientFiles"/>.</summary>
  public static (byte[] Key, byte[] CryptedKey) NewKey(char cipher, IEnumerable<string> recipientFiles) {
    var recipients = new List<OpenPgpKey>();
    foreach (var file in recipientFiles) {
      var keys = OpenPgpKey.Load(File.ReadAllBytes(file)).Where(k => k.CanEncrypt).ToList();
      if (keys.Count == 0)
        throw new ArgumentException($"'{file}' holds no OpenPGP key that can encrypt (RSA or cv25519).");
      recipients.Add(keys[^1]); // the newest encryption subkey, as gpg picks
    }
    if (recipients.Count == 0)
      throw new ArgumentException("DAR GnuPG encryption needs at least one recipient key file.");
    var key = RandomNumberGenerator.GetBytes(DarCrypto.KeyLength(cipher));
    return (key, OpenPgpMessage.Encrypt(key, recipients));
  }
}
