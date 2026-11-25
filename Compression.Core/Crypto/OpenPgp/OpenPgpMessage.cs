using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Compression.Core.Deflate;
using FileFormat.Bzip2;

namespace Compression.Core.Crypto.OpenPgp;

/// <summary>
/// Public-key encrypted OpenPGP messages (RFC 9580; RFC 4880 and its LibrePGP successor for the
/// version 1 AEAD packet gpg 2.4 writes): decrypts a message of version 3 public-key encrypted
/// session key packets (RSA, or ECDH over Curve25519) followed by a symmetrically encrypted and
/// integrity protected data packet (tag 18, version 1: CFB with a SHA-1 modification detection
/// code) or an AEAD encrypted data packet (tag 20, version 1: OCB or EAX), down to its literal
/// data through any compressed data packet (uncompressed, ZIP, ZLIB, BZip2). Encrypts to one or
/// more recipients as PKESK v3 and a tag 18 packet.
/// </summary>
public static class OpenPgpMessage {
  private const int TagPkesk = 1;
  private const int TagCompressed = 8;
  private const int TagLiteral = 11;
  private const int TagSeipd = 18;
  private const int TagAead = 20;
  private const int TagMarker = 10;
  private const int MaxDepth = 8;

  /// <summary>
  /// Decrypts <paramref name="message"/> with the first of <paramref name="keys"/> a session key
  /// packet is addressed to (or, for a hidden recipient, that opens it), unlocking it with
  /// <paramref name="passphrase"/>, and returns the literal data.
  /// </summary>
  /// <exception cref="CryptographicException">No key opens the message, a passphrase is wrong, or the integrity check fails.</exception>
  /// <exception cref="InvalidDataException">The message is malformed.</exception>
  /// <exception cref="NotSupportedException">The message uses an algorithm not implemented here.</exception>
  public static byte[] Decrypt(ReadOnlySpan<byte> message, IEnumerable<OpenPgpKey> keys, string? passphrase) {
    var packets = OpenPgpPackets.Read(message);
    var candidates = keys.Where(k => k.HasSecret && k.CanEncrypt).ToList();
    (int Algorithm, byte[] Key)? session = null;
    var problems = new List<string>();
    foreach (var packet in packets.Where(p => p.Tag == TagPkesk)) {
      var body = packet.Body;
      if (body.Length < 10 || body[0] != 3) {
        problems.Add($"a session key packet of version {(body.Length > 0 ? body[0] : 0)}");
        continue;
      }
      var keyId = BinaryPrimitives.ReadUInt64BigEndian(body.AsSpan(1));
      foreach (var key in candidates.Where(k => (keyId == 0 || k.KeyId == keyId) && k.Algorithm == body[9]
                                              || keyId == 0 && k.Algorithm is 1 or 2 && body[9] is 1 or 2)) {
        try {
          key.Unlock(passphrase);
          session = OpenSessionKey(key, body.AsSpan(10));
          break;
        } catch (CryptographicException e) {
          problems.Add(e.Message);
        }
      }
      if (session != null)
        break;
    }
    if (session is not { } found)
      throw new CryptographicException(problems.Count > 0
        ? $"No key opens the OpenPGP message: {string.Join("; ", problems.Distinct())}"
        : "No key given is one the OpenPGP message is encrypted to.");
    var data = packets.FirstOrDefault(p => p.Tag is TagSeipd or TagAead)
      ?? throw new InvalidDataException("The OpenPGP message has no encrypted data packet.");
    var clear = data.Tag == TagSeipd ? DecryptSeipd(data.Body, found.Algorithm, found.Key) : DecryptAead(data.Body, found.Key);
    return LiteralData(clear, 0);
  }

  /// <summary>The session key a v3 PKESK body (after version, key ID and algorithm) carries for <paramref name="key"/>.</summary>
  private static (int Algorithm, byte[] Key) OpenSessionKey(OpenPgpKey key, ReadOnlySpan<byte> fields) {
    byte[] m;
    var pos = 0;
    if (key.Algorithm is 1 or 2) {
      var c = OpenPgpPackets.ReadMpi(fields, ref pos);
      m = RsaDecrypt(key, c);
    } else {
      var ephemeral = OpenPgpPackets.ReadMpi(fields, ref pos);
      if (ephemeral.Length != 33 || ephemeral[0] != 0x40)
        throw new InvalidDataException("The ECDH session key packet does not carry a Curve25519 point.");
      var wrappedLength = fields[pos++];
      var wrapped = fields.Slice(pos, wrappedLength);
      var kek = EcdhKek(key, X25519.ScalarMultiply(SecretScalar(key.Secret[0]), ephemeral.AsSpan(1)));
      var padded = AesKeyWrap.Unwrap(kek, wrapped);
      var pad = padded[^1];
      if (pad is 0 or > 8 || padded.AsSpan(padded.Length - pad).ContainsAnyExcept(pad))
        throw new CryptographicException("The ECDH session key has a damaged padding.");
      m = padded[..^pad];
    }
    if (m.Length < 4)
      throw new CryptographicException("The session key is too short.");
    var sessionKey = m.AsSpan(1, m.Length - 3).ToArray();
    if (OpenPgpKey.Checksum16(sessionKey) != BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(m.Length - 2)))
      throw new CryptographicException("The session key fails its checksum (wrong key).");
    return (m[0], sessionKey);
  }

  private static BigInteger Unsigned(ReadOnlySpan<byte> bigEndian) => new(bigEndian, isUnsigned: true, isBigEndian: true);

  /// <summary>
  /// RSAES-PKCS1-v1_5 decryption with the platform's RSA (CRT, blinded, constant-time padding
  /// check) rather than a plain modular exponentiation of the secret exponent. OpenPGP stores
  /// p &lt; q and u = p⁻¹ mod q; the platform wants InverseQ = Q⁻¹ mod P, which is u with p and q
  /// swapped.
  /// </summary>
  private static byte[] RsaDecrypt(OpenPgpKey key, byte[] c) {
    var n = key.PublicFields[0];
    var (d, p, q, u) = (key.Secret[0], key.Secret[1], key.Secret[2], key.Secret[3]);
    var half = (n.Length + 1) / 2;
    var dValue = Unsigned(d);
    var parameters = new RSAParameters {
      Modulus = n,
      Exponent = TrimLeadingZeros(key.PublicFields[1]),
      D = Pad(d, n.Length),
      P = Pad(q, half),
      Q = Pad(p, half),
      DP = Pad((dValue % (Unsigned(q) - 1)).ToByteArray(isUnsigned: true, isBigEndian: true), half),
      DQ = Pad((dValue % (Unsigned(p) - 1)).ToByteArray(isUnsigned: true, isBigEndian: true), half),
      InverseQ = Pad(u, half),
    };
    try {
      using var rsa = RSA.Create(parameters);
      var m = rsa.Decrypt(Pad(c, n.Length), RSAEncryptionPadding.Pkcs1);
      if (m.Length < 4)
        throw new CryptographicException("The RSA session key does not decrypt (wrong key).");
      return m;
    } catch (CryptographicException e) {
      throw new CryptographicException("The RSA session key does not decrypt (wrong key).", e);
    } finally {
      CryptographicOperations.ZeroMemory(parameters.D);
      CryptographicOperations.ZeroMemory(parameters.DP);
      CryptographicOperations.ZeroMemory(parameters.DQ);
    }
  }

  private static byte[] Pad(byte[] value, int length) {
    var trimmed = TrimLeadingZeros(value);
    if (trimmed.Length > length)
      throw new CryptographicException("An RSA key field is longer than its modulus allows.");
    var output = new byte[length];
    trimmed.CopyTo(output, length - trimmed.Length);
    return output;
  }

  private static byte[] TrimLeadingZeros(byte[] value) {
    var start = 0;
    while (start < value.Length - 1 && value[start] == 0)
      ++start;
    return value[start..];
  }

  /// <summary>The native (little-endian) X25519 scalar from the secret MPI, which stores it reversed (RFC 9580 section 5.5.5.6).</summary>
  private static byte[] SecretScalar(ReadOnlySpan<byte> mpi) {
    if (mpi.Length > X25519.KeySize)
      throw new CryptographicException("The Curve25519 secret key is longer than 32 bytes.");
    var scalar = new byte[X25519.KeySize];
    for (var i = 0; i < mpi.Length; ++i)
      scalar[i] = mpi[mpi.Length - 1 - i];
    return scalar;
  }

  /// <summary>The key-encryption key of RFC 6637 section 7/8 (RFC 9580 section 11.5): hash(00000001 ‖ Z ‖ param), cut to the KEK length.</summary>
  private static byte[] EcdhKek(OpenPgpKey key, ReadOnlySpan<byte> shared) {
    var kdf = key.PublicFields[2]; // length 03, reserved 01, hash, cipher
    if (kdf.Length != 4 || kdf[1] != 1)
      throw new NotSupportedException("The ECDH key has KDF parameters of an unknown form.");
    using var param = new MemoryStream();
    var oid = key.PublicFields[0];
    param.WriteByte((byte)oid.Length);
    param.Write(oid);
    param.WriteByte(18);
    param.Write(kdf);
    param.Write("Anonymous Sender    "u8);
    param.Write(key.Fingerprint);
    using var hash = OpenPgpSymmetric.Hash(kdf[2]);
    hash.AppendData([0, 0, 0, 1]);
    hash.AppendData(shared);
    hash.AppendData(param.ToArray());
    var digest = hash.GetHashAndReset();
    var length = OpenPgpSymmetric.KeyLength(kdf[3]);
    if (kdf[3] is not (7 or 8 or 9))
      throw new NotSupportedException($"ECDH key wrap with symmetric algorithm {kdf[3]} is not supported; AES is.");
    return digest[..length];
  }

  /// <summary>Tag 18 version 1 (RFC 9580 section 5.13.1): CFB from a zero IV over random prefix, repeat bytes, data and the MDC packet.</summary>
  private static byte[] DecryptSeipd(byte[] body, int algorithm, byte[] key) {
    if (body.Length == 0 || body[0] != 1)
      throw new NotSupportedException($"OpenPGP encrypted data packet version {(body.Length > 0 ? body[0] : 0)} is not supported; version 1 is.");
    var cipher = OpenPgpSymmetric.Create(algorithm, key);
    var bs = cipher.BlockSize;
    var data = body.AsSpan(1).ToArray();
    if (data.Length < bs + 2 + 22)
      throw new InvalidDataException("The OpenPGP encrypted data packet is too short.");
    OpenPgpSymmetric.CfbDecrypt(cipher, new byte[bs], data);
    if (data[bs] != data[bs - 2] || data[bs + 1] != data[bs - 1])
      throw new CryptographicException("The OpenPGP encrypted data does not decrypt (its check bytes differ).");
    if (data[^22] != 0xD3 || data[^21] != 0x14
        || !SHA1.HashData(data.AsSpan(0, data.Length - 20)).AsSpan().SequenceEqual(data.AsSpan(data.Length - 20)))
      throw new CryptographicException("The OpenPGP encrypted data fails its modification detection code.");
    return data[(bs + 2)..^22];
  }

  /// <summary>Tag 20 version 1 (LibrePGP; draft-ietf-openpgp-rfc4880bis-10 section 5.16): chunks of 2^(c+6) bytes, each with its tag, then a final tag over the total length.</summary>
  internal static byte[] DecryptAead(byte[] body, byte[] key) {
    if (body.Length < 4 || body[0] != 1)
      throw new NotSupportedException("Only version 1 AEAD encrypted data packets are supported.");
    var algorithm = body[1];
    var mode = body[2];
    var chunkBits = body[3];
    if (key.Length != OpenPgpSymmetric.KeyLength(algorithm))
      throw new CryptographicException($"The session key is {key.Length} bytes; symmetric algorithm {algorithm} takes {OpenPgpSymmetric.KeyLength(algorithm)}.");
    var cipher = OpenPgpSymmetric.Create(algorithm, key);
    if (cipher.BlockSize != 16)
      throw new NotSupportedException($"AEAD needs a 128-bit block cipher; symmetric algorithm {algorithm} is not one.");
    if (chunkBits > 56)
      throw new InvalidDataException($"AEAD chunk size octet {chunkBits} is reserved.");
    var ivLength = mode switch {
      1 => 16, // EAX
      2 => 15, // OCB
      _ => throw new NotSupportedException($"AEAD mode {mode} is not supported; EAX (1) and OCB (2) are."),
    };
    const int tagLength = 16;
    if (body.Length < 4 + ivLength + tagLength)
      throw new InvalidDataException("The AEAD encrypted data packet is too short.");
    var iv = body.AsSpan(4, ivLength).ToArray();
    var payload = body.AsSpan(4 + ivLength, body.Length - 4 - ivLength - tagLength);
    var finalTag = body.AsSpan(body.Length - tagLength);
    var chunkSize = chunkBits >= 31 ? int.MaxValue : 1 << (chunkBits + 6);
    var ocb = mode == 2 ? new Ocb(cipher.Encrypt, cipher.Decrypt) : null;
    Span<byte> ad = stackalloc byte[13 + 8];
    ad[0] = 0xD4;
    body.AsSpan(0, 4).CopyTo(ad[1..]);
    var output = new MemoryStream();
    long index = 0, total = 0;
    var pos = 0;
    while (pos < payload.Length) {
      var take = Math.Min(payload.Length - pos, chunkSize + tagLength);
      if (take <= tagLength)
        throw new InvalidDataException("An AEAD chunk is shorter than its tag.");
      var chunk = payload.Slice(pos, take - tagLength);
      var tag = payload.Slice(pos + take - tagLength, tagLength);
      BinaryPrimitives.WriteInt64BigEndian(ad[5..], index);
      var clear = new byte[chunk.Length];
      Open(cipher, ocb, Nonce(iv, index), ad[..13], chunk, tag, clear);
      output.Write(clear);
      total += clear.Length;
      pos += take;
      ++index;
    }
    BinaryPrimitives.WriteInt64BigEndian(ad[5..], index);
    BinaryPrimitives.WriteInt64BigEndian(ad[13..], total);
    Open(cipher, ocb, Nonce(iv, index), ad, [], finalTag, []);
    return output.ToArray();
  }

  private static byte[] Nonce(byte[] iv, long index) {
    var nonce = (byte[])iv.Clone();
    for (var i = 0; i < 8; ++i)
      nonce[^(i + 1)] ^= (byte)(index >> (8 * i));
    return nonce;
  }

  private static void Open(OpenPgpBlockCipher cipher, Ocb? ocb, byte[] nonce, ReadOnlySpan<byte> ad, ReadOnlySpan<byte> chunk, ReadOnlySpan<byte> tag, Span<byte> clear) {
    if (ocb != null) {
      ocb.Decrypt(nonce, ad, chunk, tag, clear);
      return;
    }
    var eax = new Eax(Eax.InPlace(b => cipher.Encrypt(b)), nonce, ad);
    var copy = chunk.ToArray();
    eax.Decrypt(copy);
    if (!CryptographicOperations.FixedTimeEquals(eax.Tag(), tag))
      throw new CryptographicException("The EAX authentication tag does not match (wrong key or damaged data).");
    copy.CopyTo(clear);
  }

  /// <summary>The literal data inside decrypted packets, through compressed data packets.</summary>
  internal static byte[] LiteralData(byte[] data, int depth) {
    if (depth > MaxDepth)
      throw new InvalidDataException("OpenPGP compressed packets nest too deep.");
    foreach (var packet in OpenPgpPackets.Read(data)) {
      switch (packet.Tag) {
        case TagMarker:
          continue;
        case TagLiteral: {
          var body = packet.Body;
          if (body.Length < 6)
            throw new InvalidDataException("The OpenPGP literal data packet is too short.");
          var at = 2 + body[1] + 4; // format, name length, name, date
          if (at > body.Length)
            throw new InvalidDataException("The OpenPGP literal data packet is too short.");
          return body[at..];
        }
        case TagCompressed:
          return LiteralData(Decompress(packet.Body), depth + 1);
        default:
          continue; // signatures, one-pass signatures: not checked here
      }
    }
    throw new InvalidDataException("The OpenPGP message holds no literal data.");
  }

  private static byte[] Decompress(byte[] body) {
    if (body.Length == 0)
      throw new InvalidDataException("The OpenPGP compressed packet is empty.");
    var data = body.AsSpan(1);
    try {
      switch (body[0]) {
        case 0:
          return data.ToArray();
        case 1:
          return DeflateDecompressor.Decompress(data);
        case 2:
          if (data.Length < 2)
            throw new InvalidDataException("The ZLIB stream is too short.");
          return DeflateDecompressor.Decompress(data[2..]); // zlib header; the Adler-32 trails unread
        case 3: {
          using var input = new MemoryStream(body, 1, body.Length - 1);
          using var bz = new Bzip2Stream(input, Streams.CompressionStreamMode.Decompress);
          using var output = new MemoryStream();
          bz.CopyTo(output);
          return output.ToArray();
        }
        default:
          throw new NotSupportedException($"OpenPGP compression algorithm {body[0]} is not supported.");
      }
    } catch (Exception e) when (e is not (InvalidDataException or NotSupportedException)) {
      throw new InvalidDataException($"The OpenPGP compressed data does not decode: {e.Message}", e);
    }
  }

  // ── encryption ─────────────────────────────────────────────────────────────

  /// <summary>
  /// Encrypts <paramref name="data"/> as binary literal data to every recipient in
  /// <paramref name="recipients"/> under symmetric algorithm <paramref name="algorithm"/>: one
  /// PKESK v3 per recipient and a tag 18 version 1 packet.
  /// </summary>
  /// <exception cref="ArgumentException">A recipient key cannot take a session key.</exception>
  public static byte[] Encrypt(ReadOnlySpan<byte> data, IReadOnlyList<OpenPgpKey> recipients, int algorithm = 9) {
    if (recipients.Count == 0)
      throw new ArgumentException("An OpenPGP message needs at least one recipient.", nameof(recipients));
    var sessionKey = RandomNumberGenerator.GetBytes(OpenPgpSymmetric.KeyLength(algorithm));
    var m = new byte[sessionKey.Length + 3];
    m[0] = (byte)algorithm;
    sessionKey.CopyTo(m, 1);
    BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(m.Length - 2), OpenPgpKey.Checksum16(sessionKey));
    using var output = new MemoryStream();
    foreach (var recipient in recipients) {
      if (!recipient.CanEncrypt)
        throw new ArgumentException($"OpenPGP key {recipient.KeyId:X16} cannot take a session key.", nameof(recipients));
      using var pkesk = new MemoryStream();
      pkesk.WriteByte(3);
      var id = new byte[8];
      BinaryPrimitives.WriteUInt64BigEndian(id, recipient.KeyId);
      pkesk.Write(id);
      pkesk.WriteByte((byte)recipient.Algorithm);
      if (recipient.Algorithm is 1 or 2)
        OpenPgpPackets.WriteMpi(pkesk, RsaEncrypt(recipient, m));
      else {
        var ephemeralSecret = RandomNumberGenerator.GetBytes(X25519.KeySize);
        var ephemeralPublic = X25519.PublicKey(ephemeralSecret);
        var shared = X25519.ScalarMultiply(ephemeralSecret, recipient.PublicFields[1].AsSpan(1));
        var pad = 8 - m.Length % 8;
        var padded = new byte[m.Length + pad];
        m.CopyTo(padded, 0);
        padded.AsSpan(m.Length).Fill((byte)pad);
        var wrapped = AesKeyWrap.Wrap(EcdhKek(recipient, shared), padded);
        OpenPgpPackets.WriteMpi(pkesk, [0x40, .. ephemeralPublic]);
        pkesk.WriteByte((byte)wrapped.Length);
        pkesk.Write(wrapped);
      }
      OpenPgpPackets.Write(output, TagPkesk, pkesk.ToArray());
    }

    using var literal = new MemoryStream();
    literal.WriteByte((byte)'b');
    literal.WriteByte(0); // no file name
    literal.Write([0, 0, 0, 0]); // no date
    literal.Write(data);
    using var inner = new MemoryStream();
    OpenPgpPackets.Write(inner, TagLiteral, literal.ToArray());

    var cipher = OpenPgpSymmetric.Create(algorithm, sessionKey);
    var bs = cipher.BlockSize;
    var prefix = RandomNumberGenerator.GetBytes(bs);
    var plain = new byte[bs + 2 + inner.Length + 22];
    prefix.CopyTo(plain, 0);
    plain[bs] = prefix[bs - 2];
    plain[bs + 1] = prefix[bs - 1];
    inner.ToArray().CopyTo(plain, bs + 2);
    plain[^22] = 0xD3;
    plain[^21] = 0x14;
    SHA1.HashData(plain.AsSpan(0, plain.Length - 20)).CopyTo(plain, plain.Length - 20);
    OpenPgpSymmetric.CfbEncrypt(cipher, new byte[bs], plain);
    var seipd = new byte[plain.Length + 1];
    seipd[0] = 1;
    plain.CopyTo(seipd, 1);
    OpenPgpPackets.Write(output, TagSeipd, seipd);
    return output.ToArray();
  }

  /// <summary>RSAES-PKCS1-v1_5 encryption of <paramref name="m"/> to the recipient's n and e.</summary>
  private static byte[] RsaEncrypt(OpenPgpKey recipient, ReadOnlySpan<byte> m) {
    var n = recipient.PublicFields[0];
    var k = n.Length;
    if (m.Length > k - 11)
      throw new ArgumentException("The RSA key is too small for the session key.");
    var em = new byte[k];
    em[1] = 2;
    var padding = em.AsSpan(2, k - m.Length - 3);
    RandomNumberGenerator.Fill(padding);
    for (var i = 0; i < padding.Length; ++i)
      while (padding[i] == 0)
        padding[i] = (byte)RandomNumberGenerator.GetInt32(1, 256);
    m.CopyTo(em.AsSpan(k - m.Length));
    var c = BigInteger.ModPow(Unsigned(em), Unsigned(recipient.PublicFields[1]), Unsigned(n));
    return c.ToByteArray(isUnsigned: true, isBigEndian: true);
  }
}
