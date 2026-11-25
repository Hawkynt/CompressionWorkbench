using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Compression.Core.Crypto.OpenPgp;

/// <summary>
/// A version 4 OpenPGP public key or subkey (RFC 9580 section 5.5.2) that can take an encrypted
/// session key: RSA (algorithm 1 or 2) or ECDH over Curve25519 (algorithm 18, the legacy
/// <c>cv25519</c> key gpg makes). From a secret key packet (section 5.5.3) it also carries the
/// secret part, protected by a passphrase or not, until <see cref="Unlock"/> opens it.
/// </summary>
public sealed class OpenPgpKey {
  /// <summary>The OID of Curve25519 for ECDH (RFC 9580 section 9.2): 1.3.6.1.4.1.3029.1.5.1.</summary>
  public static ReadOnlySpan<byte> Curve25519Oid => [0x2B, 0x06, 0x01, 0x04, 0x01, 0x97, 0x55, 0x01, 0x05, 0x01];

  private readonly byte[] _publicBody;
  private readonly byte[]? _secretBody;
  private readonly int _secretAt;
  private byte[][]? _secret;

  private OpenPgpKey(byte[] publicBody, byte[]? secretBody, int secretAt) {
    this._publicBody = publicBody;
    this._secretBody = secretBody;
    this._secretAt = secretAt;
    var pos = 0;
    this.Version = publicBody[pos++];
    if (this.Version != 4)
      throw new NotSupportedException($"OpenPGP key version {this.Version} is not supported; version 4 is.");
    this.Created = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadUInt32BigEndian(publicBody.AsSpan(1)));
    pos = 5;
    this.Algorithm = publicBody[pos++];
    var fields = new List<byte[]>();
    switch (this.Algorithm) {
      case 1 or 2 or 3: // RSA: n, e
        fields.Add(OpenPgpPackets.ReadMpi(publicBody, ref pos));
        fields.Add(OpenPgpPackets.ReadMpi(publicBody, ref pos));
        break;
      case 18: { // ECDH: OID, point, KDF parameters
        var oidLength = publicBody[pos++];
        fields.Add(publicBody.AsSpan(pos, oidLength).ToArray());
        pos += oidLength;
        fields.Add(OpenPgpPackets.ReadMpi(publicBody, ref pos));
        var kdfLength = publicBody[pos];
        fields.Add(publicBody.AsSpan(pos, kdfLength + 1).ToArray());
        pos += kdfLength + 1;
        break;
      }
      case 19 or 22: { // ECDSA, EdDSA (signing only): OID, point
        var oidLength = publicBody[pos++];
        fields.Add(publicBody.AsSpan(pos, oidLength).ToArray());
        pos += oidLength;
        fields.Add(OpenPgpPackets.ReadMpi(publicBody, ref pos));
        break;
      }
      case 16 or 17: // Elgamal p, g, y; DSA p, q, g, y (never used to decrypt here)
        for (var i = 0; i < (this.Algorithm == 16 ? 3 : 4); ++i)
          fields.Add(OpenPgpPackets.ReadMpi(publicBody, ref pos));
        break;
      default:
        throw new NotSupportedException($"OpenPGP public-key algorithm {this.Algorithm} is not supported.");
    }
    this.PublicFields = fields;
    this.PublicLength = pos;
    using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
    sha1.AppendData([0x99, (byte)(pos >> 8), (byte)pos]);
    sha1.AppendData(publicBody.AsSpan(0, pos));
    this.Fingerprint = sha1.GetHashAndReset();
    this.KeyId = BinaryPrimitives.ReadUInt64BigEndian(this.Fingerprint.AsSpan(12));
  }

  /// <summary>The key packet version (4).</summary>
  public int Version { get; }

  /// <summary>When the key was made.</summary>
  public DateTimeOffset Created { get; }

  /// <summary>The public-key algorithm (1 RSA, 18 ECDH, …).</summary>
  public int Algorithm { get; }

  /// <summary>The v4 fingerprint: SHA-1 over the public key packet.</summary>
  public byte[] Fingerprint { get; }

  /// <summary>The key ID: the low 64 bits of the fingerprint.</summary>
  public ulong KeyId { get; }

  /// <summary>The algorithm's public fields: RSA n and e; ECDH curve OID, point and KDF parameters (with their length byte).</summary>
  public IReadOnlyList<byte[]> PublicFields { get; }

  private int PublicLength { get; }

  /// <summary>Whether a session key can be encrypted to this key and decrypted with it.</summary>
  public bool CanEncrypt => this.Algorithm is 1 or 2 || this.Algorithm == 18 && this.PublicFields[0].AsSpan().SequenceEqual(Curve25519Oid);

  /// <summary>Whether the key came with its secret part.</summary>
  public bool HasSecret => this._secretBody != null;

  /// <summary>Whether the secret part needs a passphrase.</summary>
  public bool IsProtected => this._secretBody != null && this._secretBody[this._secretAt] != 0;

  /// <summary>Whether the secret part has been opened.</summary>
  public bool IsUnlocked => this._secret != null;

  /// <summary>The secret fields once unlocked: RSA d, p, q, u; ECDH the scalar as stored (big-endian MPI).</summary>
  internal IReadOnlyList<byte[]> Secret => this._secret ?? throw new InvalidOperationException("The OpenPGP secret key is locked.");

  /// <summary>Every key (primary and subkeys, public or secret) in an exported key block (binary or armored).</summary>
  /// <exception cref="InvalidDataException">The data holds no OpenPGP packets or a key packet is damaged.</exception>
  public static List<OpenPgpKey> Load(ReadOnlySpan<byte> data) {
    var keys = new List<OpenPgpKey>();
    foreach (var packet in OpenPgpPackets.Read(data)) {
      try {
        switch (packet.Tag) {
          case 6 or 14: // public key, public subkey
            if (packet.Body is [4, ..])
              keys.Add(new OpenPgpKey(packet.Body, null, 0));
            break;
          case 5 or 7: { // secret key, secret subkey
            if (packet.Body is not [4, ..])
              break;
            var publicPart = new OpenPgpKey(packet.Body, null, 0);
            keys.Add(new OpenPgpKey(packet.Body.AsSpan(0, publicPart.PublicLength).ToArray(), packet.Body, publicPart.PublicLength));
            break;
          }
        }
      } catch (NotSupportedException) {
        // a key of another version or algorithm: not one this reader can use, skipped
      } catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException) {
        throw new InvalidDataException("An OpenPGP key packet is truncated.", e);
      }
    }
    return keys;
  }

  /// <summary>
  /// Opens the secret part (section 5.5.3): unprotected, or protected under S2K usage 254 (SHA-1
  /// check) or 255 (two-byte checksum) with a simple, salted or iterated and salted S2K and CFB.
  /// </summary>
  /// <exception cref="CryptographicException">The passphrase is wrong (the check fails).</exception>
  /// <exception cref="NotSupportedException">The protection uses a scheme not implemented here (AEAD, usage 253).</exception>
  public void Unlock(string? passphrase) {
    if (this._secretBody is not { } body)
      throw new InvalidOperationException("This OpenPGP key has no secret part.");
    if (this._secret != null)
      return;
    var pos = this._secretAt;
    var usage = body[pos++];
    byte[] clear;
    if (usage == 0) {
      clear = body.AsSpan(pos).ToArray();
      var sum = Checksum16(clear.AsSpan(0, clear.Length - 2));
      if (sum != BinaryPrimitives.ReadUInt16BigEndian(clear.AsSpan(clear.Length - 2)))
        throw new CryptographicException("The OpenPGP secret key fails its checksum.");
      clear = clear[..^2];
    } else if (usage is 254 or 255) {
      var algorithm = body[pos++];
      var s2kType = body[pos++];
      var hash = body[pos++];
      byte[] salt = [];
      var countByte = 0;
      if (s2kType is 1 or 3) {
        salt = body.AsSpan(pos, 8).ToArray();
        pos += 8;
      }
      if (s2kType == 3)
        countByte = body[pos++];
      if (s2kType is not (0 or 1 or 3))
        throw new NotSupportedException($"OpenPGP S2K type {s2kType} protects this secret key; types 0, 1 and 3 are supported.");
      var cipher = OpenPgpSymmetric.Create(algorithm,
        OpenPgpSymmetric.StringToKey(s2kType, hash, salt, countByte, Encoding.UTF8.GetBytes(passphrase ?? ""), OpenPgpSymmetric.KeyLength(algorithm)));
      var iv = body.AsSpan(pos, cipher.BlockSize).ToArray();
      pos += cipher.BlockSize;
      clear = body.AsSpan(pos).ToArray();
      OpenPgpSymmetric.CfbDecrypt(cipher, iv, clear);
      if (usage == 254) {
        if (clear.Length < 20 || !SHA1.HashData(clear.AsSpan(0, clear.Length - 20)).AsSpan().SequenceEqual(clear.AsSpan(clear.Length - 20)))
          throw new CryptographicException("The passphrase does not open the OpenPGP secret key.");
        clear = clear[..^20];
      } else {
        if (Checksum16(clear.AsSpan(0, clear.Length - 2)) != BinaryPrimitives.ReadUInt16BigEndian(clear.AsSpan(clear.Length - 2)))
          throw new CryptographicException("The passphrase does not open the OpenPGP secret key.");
        clear = clear[..^2];
      }
    } else
      throw new NotSupportedException($"OpenPGP secret key protection {usage} is not supported; 0, 254 and 255 are.");

    var fields = new List<byte[]>();
    var at = 0;
    var count = this.Algorithm is 1 or 2 or 3 ? 4 : 1;
    for (var i = 0; i < count; ++i)
      fields.Add(OpenPgpPackets.ReadMpi(clear, ref at));
    this._secret = [.. fields];
  }

  internal static ushort Checksum16(ReadOnlySpan<byte> data) {
    var sum = 0;
    foreach (var b in data)
      sum += b;
    return (ushort)sum;
  }
}
