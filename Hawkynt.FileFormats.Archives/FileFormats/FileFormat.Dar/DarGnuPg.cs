using System.Security.Cryptography;
using System.Text;
using Compression.Core.Crypto.OpenPgp;
using Compression.Registry;

namespace FileFormat.Dar;

/// <summary>
/// dar's public-key encryption (<c>-K gnupg:[cipher]:recipient…</c>, <c>docs/DAR-ON-DISK.md</c>
/// section 12): the archive is encrypted with a random key used as the cipher key as it is (no key
/// derivation) — 56 bytes for Blowfish, 32 for the others — and that key, as the literal data of
/// an OpenPGP message to the recipients, is stored in the version header and trailer (flag 0x04).
/// With <c>--sign</c> (flag 0x0200) that message also carries the signers' signatures over the key,
/// and the catalogue is followed, inside its compression unit, by <c>00 00</c>, an infinint length
/// and a second signed message to the recipients holding the SHA-512 of the catalogue in hex,
/// two spaces, <c>x</c> and a newline.
/// </summary>
internal static class DarGnuPg {
  /// <summary>What opening an archive key gave: the key, the verified signers, and the keys and passphrase that did it.</summary>
  internal sealed record Opened(byte[] Key, IReadOnlyList<byte[]> Signers, List<OpenPgpKey> Keys, string? Passphrase) {
    /// <summary>The session key of the signed catalogue hash, so an edit reaches the same recipients.</summary>
    public OpenPgpSessionKey? CatalogueSession { get; init; }
  }

  /// <summary>
  /// The credentials of a GnuPG-encrypted archive, as the password parameter carries them: the
  /// path of an exported secret key (binary or armored, <c>gpg --export-secret-keys</c>), more key
  /// files after <c>;</c> (such as a signer's exported public key), and optionally <c>|</c> and
  /// the secret keys' passphrase.
  /// </summary>
  public static (List<OpenPgpKey> Keys, string? Passphrase) Credentials(string? password) {
    if (string.IsNullOrEmpty(password))
      throw new ArchivePasswordException("The DAR archive is encrypted for GnuPG recipients; give the path of an exported secret key (optionally followed by '|' and its passphrase) as the password.", false);
    var bar = password.IndexOf('|');
    var paths = (bar < 0 ? password : password[..bar]).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var passphrase = bar < 0 ? null : password[(bar + 1)..];
    var keys = new List<OpenPgpKey>();
    foreach (var path in paths) {
      if (!File.Exists(path))
        throw new ArchivePasswordException($"The DAR archive is encrypted for GnuPG recipients; the key file '{path}' does not exist.", true);
      keys.AddRange(OpenPgpKey.Load(File.ReadAllBytes(path)));
    }
    if (!keys.Any(k => k.HasSecret && k.CanEncrypt))
      throw new ArchivePasswordException($"'{string.Join(';', paths)}' holds no secret key that can decrypt (RSA or cv25519).", true);
    return (keys, passphrase);
  }

  /// <summary>
  /// The archive key inside <paramref name="cryptedKey"/>, the OpenPGP message the header carries,
  /// with the fingerprints of the keys whose signatures over it verified. A signed archive (or a
  /// message carrying signatures) is refused unless every signature verifies.
  /// </summary>
  /// <exception cref="ArchivePasswordException">No key given opens it, its passphrase is wrong, or a signer's key is not given.</exception>
  /// <exception cref="InvalidDataException">A signature does not verify, or the archive is flagged signed without one.</exception>
  public static Opened OpenKey(byte[] cryptedKey, char cipher, bool signed, string? password) {
    var (keys, passphrase) = Credentials(password);
    var (key, signers, _) = OpenSigned(cryptedKey, keys, passphrase, signed, "archive key");
    if (key.Length != DarCrypto.KeyLength(cipher))
      throw new InvalidDataException($"The DAR archive key is {key.Length} bytes; {DarCrypto.CipherName(cipher)} takes {DarCrypto.KeyLength(cipher)}.");
    return new Opened(key, signers, keys, passphrase);
  }

  private static (byte[] Literal, IReadOnlyList<byte[]> Signers, OpenPgpSessionKey Session) OpenSigned(byte[] message, List<OpenPgpKey> keys, string? passphrase, bool signed, string what) {
    byte[] literal;
    IReadOnlyList<OpenPgpSignature> signatures;
    OpenPgpSessionKey session;
    try {
      literal = OpenPgpMessage.Decrypt(message, keys, passphrase, out signatures, out session);
    } catch (CryptographicException e) {
      throw new ArchivePasswordException($"The DAR {what} does not decrypt with this secret key: {e.Message}", true);
    }
    if (signed && signatures.Count == 0)
      throw new InvalidDataException($"The DAR archive is flagged signed, but its {what} carries no signature.");
    foreach (var signature in signatures) {
      if (signature.Verified is null)
        throw new ArchivePasswordException($"The DAR {what} is signed by key {signature.IssuerKeyId:X16}, whose public key is not among the given keys; add its exported key file after ';'.", true);
      if (signature.Verified is false)
        throw new InvalidDataException($"The signature of key {signature.IssuerKeyId:X16} over the DAR {what} does not verify.");
    }
    return (literal, signatures.Select(s => s.Signer!.Fingerprint).ToList(), session);
  }

  /// <summary>The text dar signs for a catalogue: its SHA-512 in lowercase hex, two spaces, <c>x</c>, a newline.</summary>
  public static byte[] CatalogueHashText(ReadOnlySpan<byte> catalogue)
    => Encoding.ASCII.GetBytes(Convert.ToHexStringLower(SHA512.HashData(catalogue)) + "  x\n");

  /// <summary>
  /// Checks the signed catalogue hash that follows the catalogue of a signed archive: present, opened
  /// by the keys given, signed by the same keys as the archive key, and equal to the catalogue's hash.
  /// </summary>
  /// <exception cref="InvalidDataException">It is missing, malformed, signed by other keys, or does not match.</exception>
  public static Opened CheckCatalogue(Opened opened, byte[] catalogue, byte[] tail) {
    var pos = 0;
    if (tail.Length < 3 || tail[0] != 0 || tail[1] != 0)
      throw new InvalidDataException("The signed DAR archive has no signed catalogue hash after its catalogue.");
    pos = 2;
    var length = DarPrimitives.ReadInfinint(tail, ref pos);
    if (length == 0 || length > (ulong)(tail.Length - pos))
      throw new InvalidDataException("The signed DAR catalogue hash runs past its catalogue unit.");
    var message = tail.AsSpan(pos, (int)length).ToArray();
    var (literal, signers, session) = OpenSigned(message, opened.Keys, opened.Passphrase, signed: true, "catalogue hash");
    if (!signers.Select(Convert.ToHexString).Order().SequenceEqual(opened.Signers.Select(Convert.ToHexString).Order()))
      throw new InvalidDataException("The DAR catalogue hash is signed by other keys than the archive key.");
    if (!literal.AsSpan().SequenceEqual(CatalogueHashText(catalogue)))
      throw new InvalidDataException("The DAR catalogue does not match the signed hash of it.");
    return opened with { CatalogueSession = session };
  }

  /// <summary>The bytes that follow a signed catalogue in its unit: <c>00 00</c>, the message length and the message.</summary>
  public static byte[] CatalogueTailFor(byte[] message) {
    using var ms = new MemoryStream();
    ms.Write([0, 0]);
    DarPrimitives.WriteInfinint(ms, (ulong)message.Length);
    ms.Write(message);
    return ms.ToArray();
  }

  /// <summary>
  /// For an edit of a signed archive: signs the new catalogue's hash with the secret keys of the
  /// original signers (among the keys given) and sends it under the original catalogue message's
  /// session key, so the same recipients read it.
  /// </summary>
  /// <exception cref="NotSupportedException">A signer's secret key is not among the keys given.</exception>
  public static Func<byte[], byte[]> ResignCatalogue(Opened opened) {
    var session = opened.CatalogueSession ?? throw new InvalidOperationException("The catalogue hash of the signed archive was not opened.");
    var signers = new List<OpenPgpKey>();
    foreach (var fingerprint in opened.Signers) {
      var key = opened.Keys.FirstOrDefault(k => k.HasSecret && k.Fingerprint.AsSpan().SequenceEqual(fingerprint))
        ?? throw new NotSupportedException($"The signed DAR archive is not edited: its new catalogue must be signed again, and the secret key of signer {Convert.ToHexString(fingerprint)} is not among the keys given.");
      try {
        key.Unlock(opened.Passphrase);
      } catch (CryptographicException e) {
        throw new NotSupportedException($"The signed DAR archive is not edited: the secret key of signer {Convert.ToHexString(fingerprint)} does not unlock: {e.Message}", e);
      }
      signers.Add(key);
    }
    return catalogue => CatalogueTailFor(OpenPgpMessage.Encrypt(CatalogueHashText(catalogue), session, signers));
  }

  /// <summary>
  /// A new random archive key for <paramref name="cipher"/> and its OpenPGP message to every key in
  /// <paramref name="recipientFiles"/>, signed by <paramref name="signer"/> (<c>path|passphrase</c>)
  /// when given; then also the function that makes the signed catalogue hash.
  /// </summary>
  public static (byte[] Key, byte[] CryptedKey, Func<byte[], byte[]>? SignCatalogue) NewKey(char cipher, IEnumerable<string> recipientFiles, string? signer = null) {
    var recipients = new List<OpenPgpKey>();
    foreach (var file in recipientFiles) {
      var keys = OpenPgpKey.Load(File.ReadAllBytes(file)).Where(k => k.CanEncrypt).ToList();
      if (keys.Count == 0)
        throw new ArgumentException($"'{file}' holds no OpenPGP key that can encrypt (RSA or cv25519).");
      recipients.Add(keys[^1]); // the newest encryption subkey, as gpg picks
    }
    if (recipients.Count == 0)
      throw new ArgumentException("DAR GnuPG encryption needs at least one recipient key file.");
    List<OpenPgpKey>? signers = null;
    if (!string.IsNullOrEmpty(signer)) {
      var bar = signer.IndexOf('|');
      var path = bar < 0 ? signer : signer[..bar];
      var signing = OpenPgpKey.Load(File.ReadAllBytes(path)).FirstOrDefault(k => k.HasSecret && k.Algorithm is 1 or 3 or 19 or 22)
        ?? throw new ArgumentException($"'{path}' holds no secret signing key (RSA, ECDSA or EdDSA).");
      try {
        signing.Unlock(bar < 0 ? null : signer[(bar + 1)..]);
      } catch (CryptographicException e) {
        throw new ArgumentException($"The signing key in '{path}' does not unlock: {e.Message}", e);
      }
      signers = [signing];
    }
    var key = RandomNumberGenerator.GetBytes(DarCrypto.KeyLength(cipher));
    Func<byte[], byte[]>? signCatalogue = signers is null
      ? null
      : catalogue => CatalogueTailFor(OpenPgpMessage.Encrypt(CatalogueHashText(catalogue), recipients, signers: signers));
    return (key, OpenPgpMessage.Encrypt(key, recipients, signers: signers), signCatalogue);
  }
}
