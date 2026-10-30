using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Crypto;

namespace FileFormat.Zip;

/// <summary>
/// PKWARE Strong Encryption Specification (SES), password-based AES variant: general-purpose bit 6,
/// a 0x0017 Strong Encryption Header extra field, and a Decryption Header in front of each
/// member's data.
/// </summary>
/// <remarks>
/// <para>Layout per PKWARE APPNOTE.TXT §7 (Decryption Header: IV size, IV, remaining size, format 3,
/// algorithm id 0x660E/0x660F/0x6610 for AES-128/192/256, bit length, flags 1 = password, encrypted
/// random data, reserved, validation data ending in its CRC-32).</para>
/// <para>Keys: the master key derives from SHA-1(password) and the file key from SHA-1(IV ∥ random
/// data), each through the CryptDeriveKey construction (SHA-1 of the digest XOR-ed into 64 bytes of
/// 0x36, then of 0x5C, concatenated). The random data is AES-CBC under the master key with the IV
/// and PKCS#7 padding; the validation data and the member's (PKCS#7-padded) compressed bytes form
/// one AES-CBC stream under the file key, starting from the IV. Converted from 7-Zip's
/// <c>CPP/7zip/Crypto/ZipStrong.cpp</c> and the pad handling in <c>Archive/Zip/ZipHandler.cpp</c>
/// (Igor Pavlov, LGPL-2.1-or-later). Certificate-based and 3DES/RC2 variants are refused.</para>
/// </remarks>
internal static class ZipStrongEncryption {

  /// <summary>Strong Encryption Header extra-field tag.</summary>
  public const ushort ExtraFieldTag = 0x0017;

  /// <summary>General-purpose flag bit 6: strong encryption.</summary>
  public const ushort FlagStrongEncryption = 0x0040;

  /// <summary>AES-128 algorithm id; AES-192 and AES-256 follow it.</summary>
  public const ushort AlgAes128 = 0x660E;

  /// <summary>AES-256 algorithm id.</summary>
  public const ushort AlgAes256 = 0x6610;

  private const int BlockSize = 16;
  private const int RandomDataSize = 16;
  private const int ValidationSize = 16; // 12 random bytes + CRC-32

  /// <summary>The password bytes (7-Zip and PKZIP use the ANSI code page; Latin-1 here).</summary>
  private static byte[] PasswordBytes(string password) => Encoding.Latin1.GetBytes(password);

  /// <summary>Builds the 0x0017 extra field for a password-encrypted AES member.</summary>
  public static byte[] BuildExtraField(ushort algId) {
    var field = new byte[12];
    BinaryPrimitives.WriteUInt16LittleEndian(field, ExtraFieldTag);
    BinaryPrimitives.WriteUInt16LittleEndian(field.AsSpan(2), 8);
    BinaryPrimitives.WriteUInt16LittleEndian(field.AsSpan(4), 2); // format
    BinaryPrimitives.WriteUInt16LittleEndian(field.AsSpan(6), algId);
    BinaryPrimitives.WriteUInt16LittleEndian(field.AsSpan(8), BitLength(algId));
    BinaryPrimitives.WriteUInt16LittleEndian(field.AsSpan(10), 1); // password
    return field;
  }

  private static ushort BitLength(ushort algId) => algId switch {
    0x660E => 128, 0x660F => 192, 0x6610 => 256,
    _ => throw new NotSupportedException($"ZIP strong encryption algorithm 0x{algId:X4} is not supported (AES only)."),
  };

  /// <summary>Encrypts a member's compressed bytes: returns Decryption Header + ciphertext.</summary>
  public static byte[] Encrypt(ReadOnlySpan<byte> compressed, string password, ushort algId = AlgAes256) {
    ArgumentNullException.ThrowIfNull(password);
    var keySize = BitLength(algId) / 8;
    var iv = RandomNumberGenerator.GetBytes(BlockSize);
    var masterKey = DeriveKey(SHA1Of(PasswordBytes(password)), keySize);

    var random = RandomNumberGenerator.GetBytes(RandomDataSize);
    var erd = AesCryptor.EncryptCbcNoPaddingAny(Pad(random), masterKey, iv);
    var fileKey = DeriveKey(SHA1Of([.. iv, .. random]), keySize);

    var validation = new byte[ValidationSize];
    RandomNumberGenerator.Fill(validation.AsSpan(0, ValidationSize - 4));
    BinaryPrimitives.WriteUInt32LittleEndian(validation.AsSpan(ValidationSize - 4), Crc32.Compute(validation.AsSpan(0, ValidationSize - 4)));
    var stream = AesCryptor.EncryptCbcNoPaddingAny([.. validation, .. Pad(compressed)], fileKey, iv);

    using var ms = new MemoryStream();
    Span<byte> b = stackalloc byte[4];
    BinaryPrimitives.WriteUInt16LittleEndian(b, BlockSize); ms.Write(b[..2]);
    ms.Write(iv);
    var remaining = 10 + erd.Length + 4 + 2 + ValidationSize;
    BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)remaining); ms.Write(b);
    BinaryPrimitives.WriteUInt16LittleEndian(b, 3); ms.Write(b[..2]);
    BinaryPrimitives.WriteUInt16LittleEndian(b, algId); ms.Write(b[..2]);
    BinaryPrimitives.WriteUInt16LittleEndian(b, BitLength(algId)); ms.Write(b[..2]);
    BinaryPrimitives.WriteUInt16LittleEndian(b, 1); ms.Write(b[..2]);
    BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)erd.Length); ms.Write(b[..2]);
    ms.Write(erd);
    BinaryPrimitives.WriteUInt32LittleEndian(b, 0); ms.Write(b);
    BinaryPrimitives.WriteUInt16LittleEndian(b, ValidationSize); ms.Write(b[..2]);
    ms.Write(stream);
    return ms.ToArray();
  }

  /// <summary>Decrypts a member's stored bytes (Decryption Header + ciphertext) to its compressed bytes.</summary>
  /// <exception cref="NotSupportedException">A certificate-based, 3DES or otherwise unsupported variant.</exception>
  /// <exception cref="InvalidDataException">Wrong password, malformed header or bad padding.</exception>
  public static byte[] Decrypt(ReadOnlySpan<byte> payload, string password, uint crc, long uncompressedSize) {
    ArgumentNullException.ThrowIfNull(password);
    if (payload.Length < 6)
      throw new InvalidDataException("ZIP strong-encryption header is truncated.");
    var ivSize = BinaryPrimitives.ReadUInt16LittleEndian(payload);
    var pos = 2;
    var iv = new byte[BlockSize];
    if (ivSize == 0) {
      BinaryPrimitives.WriteUInt32LittleEndian(iv, crc);
      BinaryPrimitives.WriteUInt64LittleEndian(iv.AsSpan(4), (ulong)uncompressedSize);
      ivSize = 12;
    } else if (ivSize == BlockSize) {
      payload.Slice(pos, BlockSize).CopyTo(iv);
      pos += BlockSize;
    } else
      throw new NotSupportedException($"ZIP strong-encryption IV size {ivSize} is not supported.");
    if (payload.Length < pos + 4)
      throw new InvalidDataException("ZIP strong-encryption header is truncated.");
    var remSize = BinaryPrimitives.ReadUInt32LittleEndian(payload[pos..]);
    pos += 4;
    if (remSize < 16 || remSize > 1 << 18 || pos + remSize > payload.Length)
      throw new InvalidDataException("ZIP strong-encryption header size is out of range.");
    var header = payload.Slice(pos, (int)remSize);
    var body = payload[(pos + (int)remSize)..];

    var format = BinaryPrimitives.ReadUInt16LittleEndian(header);
    var algId = BinaryPrimitives.ReadUInt16LittleEndian(header[2..]);
    var bitLen = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
    var flags = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
    if (format != 3 || algId is < AlgAes128 or > AlgAes256 || BitLength(algId) != bitLen)
      throw new NotSupportedException($"ZIP strong encryption format {format}, algorithm 0x{algId:X4} is not supported (AES only).");
    if ((flags & 0x4000) != 0 || (flags & 2) != 0 || (flags & 1) == 0)
      throw new NotSupportedException("ZIP strong encryption with certificates or 3DES random data is not supported.");
    var rdSize = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
    if (rdSize + 16 > remSize || rdSize < BlockSize || rdSize % BlockSize != 0)
      throw new InvalidDataException("ZIP strong-encryption random-data size is invalid.");
    var p2 = 10 + rdSize;
    if (BinaryPrimitives.ReadUInt32LittleEndian(header[p2..]) != 0)
      throw new NotSupportedException("ZIP strong encryption with recipients is not supported.");
    p2 += 4;
    var validSize = BinaryPrimitives.ReadUInt16LittleEndian(header[p2..]);
    p2 += 2;
    if (validSize % BlockSize != 0 || validSize < 4 || p2 + validSize != remSize)
      throw new InvalidDataException("ZIP strong-encryption validation-data size is invalid.");

    var keySize = bitLen / 8;
    var masterKey = DeriveKey(SHA1Of(PasswordBytes(password)), keySize);
    var rd = AesCryptor.DecryptCbcNoPaddingAny(header.Slice(10, rdSize), masterKey, iv);
    if (!HasPkcs7Block(rd))
      throw new InvalidDataException("Wrong password for ZIP strong-encrypted entry.");
    var random = rd.AsSpan(0, rd.Length - BlockSize);
    var fileKey = DeriveKey(SHA1Of([.. iv.AsSpan(0, ivSize), .. random]), keySize);

    if (body.Length % BlockSize != 0)
      throw new InvalidDataException("ZIP strong-encrypted data is not a whole number of AES blocks.");
    var plain = AesCryptor.DecryptCbcNoPaddingAny([.. header.Slice(p2, validSize), .. body], fileKey, iv);
    var check = plain.AsSpan(0, validSize);
    if (BinaryPrimitives.ReadUInt32LittleEndian(check[^4..]) != Crc32.Compute(check[..^4]))
      throw new InvalidDataException("Wrong password for ZIP strong-encrypted entry.");
    var data = plain.AsSpan(validSize);
    if (data.Length == 0)
      throw new InvalidDataException("ZIP strong-encrypted data lacks its padding block.");
    var pad = data[^1];
    if (pad is 0 or > BlockSize || data.Length < pad || data[^pad..].ContainsAnyExcept(pad))
      throw new InvalidDataException("ZIP strong-encrypted data has bad PKCS#7 padding.");
    return data[..^pad].ToArray();
  }

  private static bool HasPkcs7Block(ReadOnlySpan<byte> rd) {
    if (rd.Length < BlockSize) return false;
    var tail = rd[^BlockSize..];
    return !tail.ContainsAnyExcept((byte)BlockSize);
  }

  /// <summary>PKCS#7 padding to the AES block size (a whole block when already aligned).</summary>
  private static byte[] Pad(ReadOnlySpan<byte> data) {
    var pad = BlockSize - data.Length % BlockSize;
    var result = new byte[data.Length + pad];
    data.CopyTo(result);
    result.AsSpan(data.Length).Fill((byte)pad);
    return result;
  }

  private static byte[] SHA1Of(ReadOnlySpan<byte> data) {
    var sha = new Sha1();
    sha.Update(data);
    sha.Finish();
    return sha.Hash;
  }

  /// <summary>CryptDeriveKey with SHA-1: two SHA-1s of the digest XOR-ed into 0x36 and 0x5C blocks.</summary>
  private static byte[] DeriveKey(byte[] digest, int keySize) {
    Span<byte> block = stackalloc byte[64];
    var result = new byte[40];
    for (var half = 0; half < 2; ++half) {
      block.Fill(half == 0 ? (byte)0x36 : (byte)0x5C);
      for (var i = 0; i < digest.Length; ++i)
        block[i] ^= digest[i];
      SHA1Of(block).CopyTo(result, half * 20);
    }
    return result[..keySize];
  }
}
