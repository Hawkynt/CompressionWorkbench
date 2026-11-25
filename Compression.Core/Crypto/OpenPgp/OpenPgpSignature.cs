using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Compression.Core.Crypto.OpenPgp;

/// <summary>
/// A version 4 OpenPGP signature over binary data (RFC 9580 sections 5.2.3 and 5.2.4, signature
/// type 0x00): RSA (PKCS#1 v1.5), ECDSA over NIST P-256/384/521 and the legacy EdDSA over Ed25519
/// that gpg makes with its default keys. <see cref="Verified"/> is null until checked, then whether
/// the key it names verifies it.
/// </summary>
public sealed class OpenPgpSignature {
  private readonly byte[] _hashedPart;
  private readonly byte[][] _values;

  private OpenPgpSignature(int type, int publicKeyAlgorithm, int hashAlgorithm, byte[] hashedPart, byte[] hashPrefix, byte[][] values,
      byte[]? issuerFingerprint, ulong? issuerKeyId, DateTimeOffset? created) {
    this.Type = type;
    this.PublicKeyAlgorithm = publicKeyAlgorithm;
    this.HashAlgorithm = hashAlgorithm;
    this._hashedPart = hashedPart;
    this.HashPrefix = hashPrefix;
    this._values = values;
    this.IssuerFingerprint = issuerFingerprint;
    this.IssuerKeyId = issuerKeyId;
    this.Created = created;
  }

  /// <summary>The signature type (0x00: binary document).</summary>
  public int Type { get; }

  /// <summary>The public-key algorithm (1 RSA, 19 ECDSA, 22 EdDSA).</summary>
  public int PublicKeyAlgorithm { get; }

  /// <summary>The hash algorithm (8 SHA-256, 10 SHA-512, …).</summary>
  public int HashAlgorithm { get; }

  /// <summary>The first two bytes of the hash, stored in the clear.</summary>
  public byte[] HashPrefix { get; }

  /// <summary>The issuer's v4 fingerprint (subpacket 33), when present.</summary>
  public byte[]? IssuerFingerprint { get; }

  /// <summary>The issuer's key ID (subpacket 16, or the fingerprint's low 64 bits).</summary>
  public ulong? IssuerKeyId { get; }

  /// <summary>The signature creation time (subpacket 2).</summary>
  public DateTimeOffset? Created { get; }

  /// <summary>Null until <see cref="Verify"/> ran with the issuer's key; then whether the signature holds.</summary>
  public bool? Verified { get; private set; }

  /// <summary>The key that verified it.</summary>
  public OpenPgpKey? Signer { get; private set; }

  /// <summary>Parses a signature packet body.</summary>
  /// <exception cref="InvalidDataException">The packet is malformed.</exception>
  /// <exception cref="NotSupportedException">A signature version other than 4.</exception>
  public static OpenPgpSignature Parse(byte[] body) {
    try {
      if (body.Length < 6 || body[0] != 4)
        throw new NotSupportedException($"OpenPGP signature version {(body.Length > 0 ? body[0] : 0)} is not supported; version 4 is.");
      var type = body[1];
      var pk = body[2];
      var hash = body[3];
      var hashedLength = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(4));
      var hashedEnd = 6 + hashedLength;
      var hashedPart = body.AsSpan(0, hashedEnd).ToArray();
      byte[]? fingerprint = null;
      ulong? keyId = null;
      DateTimeOffset? created = null;
      void Subpackets(ReadOnlySpan<byte> area) {
        var at = 0;
        while (at < area.Length) {
          var first = area[at];
          int length;
          if (first < 192) { length = first; at += 1; }
          else if (first < 255) { length = ((first - 192) << 8) + area[at + 1] + 192; at += 2; }
          else { length = (int)BinaryPrimitives.ReadUInt32BigEndian(area[(at + 1)..]); at += 5; }
          if (length < 1 || at + length > area.Length)
            throw new InvalidDataException("An OpenPGP signature subpacket runs past its area.");
          var data = area.Slice(at + 1, length - 1);
          switch (area[at] & 0x7F) {
            case 2 when data.Length == 4:
              created = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadUInt32BigEndian(data));
              break;
            case 16 when data.Length == 8:
              keyId ??= BinaryPrimitives.ReadUInt64BigEndian(data);
              break;
            case 33 when data.Length == 21 && data[0] == 4:
              fingerprint = data[1..].ToArray();
              break;
          }
          at += length;
        }
      }
      Subpackets(body.AsSpan(6, hashedLength));
      var unhashedLength = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(hashedEnd));
      Subpackets(body.AsSpan(hashedEnd + 2, unhashedLength));
      var pos = hashedEnd + 2 + unhashedLength;
      var prefix = body.AsSpan(pos, 2).ToArray();
      pos += 2;
      var values = new List<byte[]>();
      var count = pk is 1 or 3 ? 1 : 2;
      for (var i = 0; i < count; ++i)
        values.Add(OpenPgpPackets.ReadMpi(body, ref pos));
      if (fingerprint != null)
        keyId ??= BinaryPrimitives.ReadUInt64BigEndian(fingerprint.AsSpan(12));
      return new OpenPgpSignature(type, pk, hash, hashedPart, prefix, [.. values], fingerprint, keyId, created);
    } catch (ArgumentOutOfRangeException e) {
      throw new InvalidDataException("An OpenPGP signature packet is truncated.", e);
    }
  }

  /// <summary>Whether <paramref name="key"/> is the issuer this signature names.</summary>
  public bool IsIssuedBy(OpenPgpKey key) => this.IssuerFingerprint is { } f
    ? f.AsSpan().SequenceEqual(key.Fingerprint)
    : this.IssuerKeyId == key.KeyId;

  /// <summary>
  /// Checks the signature over <paramref name="data"/> with the first of <paramref name="keys"/>
  /// it names, and records the result in <see cref="Verified"/> (left null when no such key is given).
  /// </summary>
  public bool? Verify(ReadOnlySpan<byte> data, IEnumerable<OpenPgpKey> keys) {
    var key = keys.FirstOrDefault(k => this.IsIssuedBy(k) && k.Algorithm == this.PublicKeyAlgorithm);
    if (key == null)
      return this.Verified = null;
    this.Signer = key;
    if (this.Type is not (0x00))
      return this.Verified = false; // text and other signature types are not what dar makes
    var digest = this.Digest(data);
    if (digest[0] != this.HashPrefix[0] || digest[1] != this.HashPrefix[1])
      return this.Verified = false;
    try {
      return this.Verified = this.PublicKeyAlgorithm switch {
        1 or 3 => VerifyRsa(key, digest, this._values[0], this.HashAlgorithm),
        19 => VerifyEcdsa(key, digest, this._values[0], this._values[1]),
        22 => VerifyEdDsa(key, digest, this._values[0], this._values[1]),
        _ => false,
      };
    } catch (CryptographicException) {
      return this.Verified = false;
    }
  }

  private byte[] Digest(ReadOnlySpan<byte> data) {
    using var hash = OpenPgpSymmetric.Hash(this.HashAlgorithm);
    hash.AppendData(data);
    hash.AppendData(this._hashedPart);
    Span<byte> trailer = [4, 0xFF, 0, 0, 0, 0];
    BinaryPrimitives.WriteUInt32BigEndian(trailer[2..], (uint)this._hashedPart.Length);
    hash.AppendData(trailer);
    return hash.GetHashAndReset();
  }

  internal static HashAlgorithmName HashName(int algorithm) => algorithm switch {
    1 => HashAlgorithmName.MD5,
    2 => HashAlgorithmName.SHA1,
    8 => HashAlgorithmName.SHA256,
    9 => HashAlgorithmName.SHA384,
    10 => HashAlgorithmName.SHA512,
    _ => throw new NotSupportedException($"OpenPGP hash algorithm {algorithm} is not supported."),
  };

  private static bool VerifyRsa(OpenPgpKey key, byte[] digest, byte[] signature, int hash) {
    using var rsa = RSA.Create(new RSAParameters { Modulus = key.PublicFields[0], Exponent = key.PublicFields[1] });
    var s = new byte[key.PublicFields[0].Length];
    if (signature.Length > s.Length)
      return false;
    signature.CopyTo(s, s.Length - signature.Length);
    return rsa.VerifyHash(digest, s, HashName(hash), RSASignaturePadding.Pkcs1);
  }

  internal static (ECCurve Curve, int Size)? NistCurve(ReadOnlySpan<byte> oid) => Convert.ToHexString(oid) switch {
    "2A8648CE3D030107" => (ECCurve.NamedCurves.nistP256, 32),
    "2B81040022" => (ECCurve.NamedCurves.nistP384, 48),
    "2B81040023" => (ECCurve.NamedCurves.nistP521, 66),
    _ => null,
  };

  private static bool VerifyEcdsa(OpenPgpKey key, byte[] digest, byte[] r, byte[] s) {
    if (NistCurve(key.PublicFields[0]) is not { } curve)
      return false;
    var point = key.PublicFields[1];
    if (point.Length != 1 + 2 * curve.Size || point[0] != 4)
      return false;
    using var ecdsa = ECDsa.Create(new ECParameters {
      Curve = curve.Curve,
      Q = new ECPoint { X = point[1..(1 + curve.Size)], Y = point[(1 + curve.Size)..] },
    });
    if (r.Length > curve.Size || s.Length > curve.Size)
      return false;
    var rs = new byte[2 * curve.Size];
    r.CopyTo(rs, curve.Size - r.Length);
    s.CopyTo(rs, 2 * curve.Size - s.Length);
    return ecdsa.VerifyHash(digest, rs);
  }

  /// <summary>The Ed25519 OID of legacy EdDSA keys: 1.3.6.1.4.1.11591.15.1.</summary>
  internal static ReadOnlySpan<byte> Ed25519Oid => [0x2B, 0x06, 0x01, 0x04, 0x01, 0xDA, 0x47, 0x0F, 0x01];

  private static bool VerifyEdDsa(OpenPgpKey key, byte[] digest, byte[] r, byte[] s) {
    // legacy EdDSA (RFC 9580 section 5.2.3.3): the hash digest is the Ed25519 message; R and S are
    // MPIs of the native 32-byte strings; the key point is 0x40 then A
    if (!key.PublicFields[0].AsSpan().SequenceEqual(Ed25519Oid) || key.PublicFields[1] is not [0x40, ..] point || point.Length != 33
        || r.Length > 32 || s.Length > 32)
      return false;
    var signature = new byte[64];
    r.CopyTo(signature, 32 - r.Length);
    s.CopyTo(signature, 64 - s.Length);
    return Ed25519.Verify(point.AsSpan(1), digest, signature);
  }

  // ── signing ────────────────────────────────────────────────────────────────

  /// <summary>
  /// A one-pass signature packet body (section 5.4) and the signature packet body of
  /// <paramref name="signer"/> (unlocked, RSA, ECDSA or Ed25519 EdDSA) over binary
  /// <paramref name="data"/>, with SHA-512 as gpg uses for these keys.
  /// </summary>
  public static (byte[] OnePass, byte[] Signature) Create(OpenPgpKey signer, ReadOnlySpan<byte> data, DateTimeOffset? created = null) {
    if (!signer.IsUnlocked)
      throw new InvalidOperationException("The signing key is locked; unlock it with its passphrase first.");
    const int hash = 10; // SHA-512
    using var hashed = new MemoryStream();
    hashed.Write([4, 0x00, (byte)signer.Algorithm, hash]);
    using var subpackets = new MemoryStream();
    subpackets.Write([22, 33, 4]);
    subpackets.Write(signer.Fingerprint);
    Span<byte> time = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(time, (uint)(created ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds());
    subpackets.Write([5, 2]);
    subpackets.Write(time);
    var area = subpackets.ToArray();
    hashed.Write([(byte)(area.Length >> 8), (byte)area.Length]);
    hashed.Write(area);
    var hashedPart = hashed.ToArray();

    using var h = OpenPgpSymmetric.Hash(hash);
    h.AppendData(data);
    h.AppendData(hashedPart);
    Span<byte> trailer = [4, 0xFF, 0, 0, 0, 0];
    BinaryPrimitives.WriteUInt32BigEndian(trailer[2..], (uint)hashedPart.Length);
    h.AppendData(trailer);
    var digest = h.GetHashAndReset();

    using var body = new MemoryStream();
    body.Write(hashedPart);
    Span<byte> issuer = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64BigEndian(issuer, signer.KeyId);
    body.Write([0, 10, 9, 16]);
    body.Write(issuer);
    body.Write(digest.AsSpan(0, 2));
    switch (signer.Algorithm) {
      case 1 or 3:
        OpenPgpPackets.WriteMpi(body, SignRsa(signer, digest));
        break;
      case 19: {
        var (r, s) = SignEcdsa(signer, digest);
        OpenPgpPackets.WriteMpi(body, r);
        OpenPgpPackets.WriteMpi(body, s);
        break;
      }
      case 22: {
        var seed = new byte[32];
        var secret = signer.Secret[0];
        if (secret.Length > 32)
          throw new CryptographicException("The EdDSA secret key is longer than 32 bytes.");
        secret.CopyTo(seed, 32 - secret.Length);
        var signature = Ed25519.Sign(seed, digest);
        CryptographicOperations.ZeroMemory(seed);
        OpenPgpPackets.WriteMpi(body, signature.AsSpan(0, 32));
        OpenPgpPackets.WriteMpi(body, signature.AsSpan(32));
        break;
      }
      default:
        throw new NotSupportedException($"Signing with OpenPGP public-key algorithm {signer.Algorithm} is not supported.");
    }

    var onePass = new byte[13];
    onePass[0] = 3;
    onePass[1] = 0x00;
    onePass[2] = hash;
    onePass[3] = (byte)signer.Algorithm;
    BinaryPrimitives.WriteUInt64BigEndian(onePass.AsSpan(4), signer.KeyId);
    onePass[12] = 1;
    return (onePass, body.ToArray());
  }

  private static byte[] SignRsa(OpenPgpKey key, byte[] digest) {
    using var rsa = RSA.Create(OpenPgpMessage.RsaPrivateParameters(key));
    return rsa.SignHash(digest, HashName(10), RSASignaturePadding.Pkcs1);
  }

  private static (byte[] R, byte[] S) SignEcdsa(OpenPgpKey key, byte[] digest) {
    if (NistCurve(key.PublicFields[0]) is not { } curve)
      throw new NotSupportedException("ECDSA signing is supported over NIST P-256, P-384 and P-521.");
    var point = key.PublicFields[1];
    var d = new byte[curve.Size];
    key.Secret[0].CopyTo(d, curve.Size - key.Secret[0].Length);
    using var ecdsa = ECDsa.Create(new ECParameters {
      Curve = curve.Curve, D = d,
      Q = new ECPoint { X = point[1..(1 + curve.Size)], Y = point[(1 + curve.Size)..] },
    });
    CryptographicOperations.ZeroMemory(d);
    var rs = ecdsa.SignHash(digest);
    return (rs[..curve.Size], rs[curve.Size..]);
  }
}
