#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FileSystem.Ecryptfs;

internal static class EcryptfsCodec {
  internal const uint MarkerMagic = 0x3C81B7F5;
  internal const int FixedHeaderSize = 26;
  internal const int DefaultExtentSize = 4096;
  internal const int MinimumMetadataSize = 8192;
  internal const byte CurrentFileVersion = 3;
  internal const uint EncryptedFlag = 0x00000002;
  internal const uint MetadataInXattrFlag = 0x00000004;
  internal const int PassphraseHashIterations = 65536;

  private const byte Tag3 = 0x8C;
  private const byte Tag11 = 0xED;
  private const byte Tag1 = 0x01;
  private const byte Aes128 = 0x07;
  private const byte Aes192 = 0x08;
  private const byte Aes256 = 0x09;

  private static ReadOnlySpan<byte> DefaultSalt => [0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77];
  private static ReadOnlySpan<byte> ConsoleName => "_CONSOLE"u8;

  internal sealed record PassphrasePacket(
    byte CipherCode,
    byte[] Salt,
    byte[] EncryptedFileKey,
    byte[] Signature
  ) {
    public int KeySize => CipherCode switch {
      Aes128 => 16,
      Aes192 => 24,
      Aes256 => 32,
      _ => 0,
    };

    public string CipherDescription => CipherCode switch {
      Aes128 => "AES-128-CBC",
      Aes192 => "AES-192-CBC",
      Aes256 => "AES-256-CBC",
      _ => $"cipher-0x{CipherCode:X2}",
    };
  }

  internal sealed record Header(
    ulong DecryptedSize,
    uint MarkerWord,
    uint Flags,
    uint ExtentSize,
    ushort HeaderExtentCount,
    int MetadataSize,
    int PacketEndOffset,
    bool PaddingIsKnown,
    IReadOnlyList<PassphrasePacket> PassphrasePackets
  ) {
    public byte FileVersion => (byte)(Flags >> 24);

    public long CanonicalLength {
      get {
        var extents = DecryptedSize == 0 ? 0UL : checked((DecryptedSize + ExtentSize - 1) / ExtentSize);
        var dataBytes = checked(extents * ExtentSize);
        var total = checked((ulong)MetadataSize + dataBytes);
        return total <= long.MaxValue
          ? (long)total
          : throw new InvalidDataException("eCryptfs lower-file length exceeds the supported stream range.");
      }
    }

    public string CipherDescription
      => PassphrasePackets.Count == 0 ? "encrypted" : PassphrasePackets[0].CipherDescription;
  }

  internal static Header ReadHeader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead || !stream.CanSeek)
      throw new ArgumentException("eCryptfs parsing requires a readable, seekable stream.", nameof(stream));

    var originalPosition = stream.Position;
    try {
      Span<byte> fixedHeader = stackalloc byte[FixedHeaderSize];
      stream.Position = 0;
      ReadExactly(stream, fixedHeader);

      var extentSize = BinaryPrimitives.ReadUInt32BigEndian(fixedHeader[20..24]);
      var headerExtentCount = BinaryPrimitives.ReadUInt16BigEndian(fixedHeader[24..26]);
      ValidateGeometry(extentSize, headerExtentCount);

      var metadataSize64 = checked((ulong)extentSize * headerExtentCount);
      if (metadataSize64 > int.MaxValue)
        throw new InvalidDataException("eCryptfs metadata region is too large to parse.");
      var metadataSize = (int)metadataSize64;
      if (stream.Length < metadataSize)
        throw new InvalidDataException("Truncated eCryptfs metadata region.");

      var metadata = new byte[metadataSize];
      stream.Position = 0;
      ReadExactly(stream, metadata);
      var header = ParseHeader(metadata);
      if (stream.Length < header.CanonicalLength)
        throw new InvalidDataException("Truncated eCryptfs ciphertext extents.");
      return header;
    } finally {
      stream.Position = originalPosition;
    }
  }

  internal static void DecryptTo(Stream input, Stream output, Header header, string password) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(password);
    if (!input.CanRead || !input.CanSeek)
      throw new ArgumentException("eCryptfs decryption requires a readable, seekable input.", nameof(input));
    if (!output.CanWrite)
      throw new ArgumentException("eCryptfs decryption requires a writable output.", nameof(output));

    var packet = FindMatchingPassphrasePacket(header, password, out var fekek);
    try {
      if (packet.KeySize == 0)
        throw new NotSupportedException($"eCryptfs cipher code 0x{packet.CipherCode:X2} is not supported; AES-128/192/256 are supported.");

      var fileKey = DecryptFileKey(packet, fekek);
      try {
        var rootIv = MD5.HashData(fileKey);
        var encrypted = new byte[header.ExtentSize];
        var remaining = header.DecryptedSize;
        var extentIndex = 0L;
        input.Position = header.MetadataSize;

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = fileKey;

        while (remaining > 0) {
          ReadExactly(input, encrypted);
          var iv = DeriveExtentIv(rootIv, extentIndex++);
          using var transform = aes.CreateDecryptor(aes.Key, iv);
          var plaintext = transform.TransformFinalBlock(encrypted, 0, encrypted.Length);
          var toWrite = (int)Math.Min((ulong)plaintext.Length, remaining);
          output.Write(plaintext, 0, toWrite);
          remaining -= (ulong)toWrite;
          CryptographicOperations.ZeroMemory(plaintext);
        }
      } finally {
        CryptographicOperations.ZeroMemory(fileKey);
      }
    } finally {
      CryptographicOperations.ZeroMemory(fekek);
    }
  }

  internal static void Create(Stream output, ReadOnlySpan<byte> plaintext, string password, int keySize = 16) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(password);
    if (!output.CanWrite || !output.CanSeek)
      throw new ArgumentException("eCryptfs creation requires a writable, seekable output.", nameof(output));
    if (keySize is not (16 or 24 or 32))
      throw new ArgumentOutOfRangeException(nameof(keySize), "AES key size must be 16, 24, or 32 bytes.");

    var passwordBytes = Encoding.UTF8.GetBytes(password);
    if (passwordBytes.Length > 64)
      throw new ArgumentException("eCryptfs passphrases are limited to 64 encoded bytes.", nameof(password));

    var metadata = new byte[MinimumMetadataSize];
    BinaryPrimitives.WriteUInt64BigEndian(metadata.AsSpan(0, 8), (ulong)plaintext.Length);

    Span<byte> markerBytes = stackalloc byte[4];
    RandomNumberGenerator.Fill(markerBytes);
    var markerWord = BinaryPrimitives.ReadUInt32BigEndian(markerBytes);
    BinaryPrimitives.WriteUInt32BigEndian(metadata.AsSpan(8, 4), markerWord);
    BinaryPrimitives.WriteUInt32BigEndian(metadata.AsSpan(12, 4), markerWord ^ MarkerMagic);
    BinaryPrimitives.WriteUInt32BigEndian(metadata.AsSpan(16, 4), ((uint)CurrentFileVersion << 24) | EncryptedFlag);
    BinaryPrimitives.WriteUInt32BigEndian(metadata.AsSpan(20, 4), DefaultExtentSize);
    BinaryPrimitives.WriteUInt16BigEndian(metadata.AsSpan(24, 2), MinimumMetadataSize / DefaultExtentSize);

    var fileKey = RandomNumberGenerator.GetBytes(keySize);
    var (fekek, signature) = DerivePassphraseMaterial(passwordBytes, DefaultSalt);
    try {
      var encryptedFileKey = EncryptFileKey(fileKey, fekek, keySize);
      try {
        var offset = FixedHeaderSize;
        offset = WriteTag3(metadata, offset, keySize, encryptedFileKey, DefaultSalt);
        offset = WriteTag11(metadata, offset, signature);

        output.Position = 0;
        output.SetLength(0);
        output.Write(metadata);

        if (!plaintext.IsEmpty) {
          var rootIv = MD5.HashData(fileKey);
          using var aes = Aes.Create();
          aes.Mode = CipherMode.CBC;
          aes.Padding = PaddingMode.None;
          aes.Key = fileKey;

          var plainExtent = new byte[DefaultExtentSize];
          var consumed = 0;
          var extentIndex = 0L;
          while (consumed < plaintext.Length) {
            Array.Clear(plainExtent);
            var count = Math.Min(DefaultExtentSize, plaintext.Length - consumed);
            plaintext.Slice(consumed, count).CopyTo(plainExtent);
            var iv = DeriveExtentIv(rootIv, extentIndex++);
            using var transform = aes.CreateEncryptor(aes.Key, iv);
            var encrypted = transform.TransformFinalBlock(plainExtent, 0, plainExtent.Length);
            output.Write(encrypted);
            CryptographicOperations.ZeroMemory(encrypted);
            consumed += count;
          }
          CryptographicOperations.ZeroMemory(plainExtent);
        }
        output.Position = 0;
      } finally {
        CryptographicOperations.ZeroMemory(encryptedFileKey);
      }
    } finally {
      CryptographicOperations.ZeroMemory(fileKey);
      CryptographicOperations.ZeroMemory(fekek);
      CryptographicOperations.ZeroMemory(signature);
      CryptographicOperations.ZeroMemory(passwordBytes);
    }
  }

  internal static int ResolveKeySize(string? encryptionMethod) {
    if (string.IsNullOrWhiteSpace(encryptionMethod)) return 16;
    return encryptionMethod.Trim().ToLowerInvariant() switch {
      "aes" or "aes128" or "aes-128" or "aes-128-cbc" => 16,
      "aes192" or "aes-192" or "aes-192-cbc" => 24,
      "aes256" or "aes-256" or "aes-256-cbc" => 32,
      _ => throw new NotSupportedException($"Unsupported eCryptfs encryption method '{encryptionMethod}'. Use AES-128, AES-192, or AES-256."),
    };
  }

  internal static long WipeUnusedSpace(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("eCryptfs wipe requires a readable, writable, seekable stream.", nameof(image));

    var header = ReadHeader(image);
    long wiped = 0;
    if (header.PaddingIsKnown && header.PacketEndOffset < header.MetadataSize)
      wiped += ZeroRange(image, header.PacketEndOffset, header.MetadataSize - header.PacketEndOffset);
    if (image.Length > header.CanonicalLength)
      wiped += ZeroRange(image, header.CanonicalLength, image.Length - header.CanonicalLength);
    image.Position = 0;
    return wiped;
  }

  internal static void Shrink(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    if (!input.CanRead || !input.CanSeek)
      throw new ArgumentException("eCryptfs shrink requires a readable, seekable input.", nameof(input));
    if (!output.CanWrite || !output.CanSeek)
      throw new ArgumentException("eCryptfs shrink requires a writable, seekable output.", nameof(output));

    var header = ReadHeader(input);
    input.Position = 0;
    output.Position = 0;
    output.SetLength(0);
    CopyExactly(input, output, header.CanonicalLength);
    output.Position = 0;
  }

  internal static void Purge(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("eCryptfs purge requires a readable, writable, seekable stream.", nameof(archive));

    var header = ReadHeader(archive);
    Span<byte> zeroSize = stackalloc byte[8];
    archive.Position = 0;
    archive.Write(zeroSize);
    archive.SetLength(header.MetadataSize);
    archive.Position = 0;
  }

  private static Header ParseHeader(ReadOnlySpan<byte> metadata) {
    if (metadata.Length < FixedHeaderSize)
      throw new InvalidDataException("Truncated eCryptfs fixed header.");

    var decryptedSize = BinaryPrimitives.ReadUInt64BigEndian(metadata[..8]);
    var markerWord = BinaryPrimitives.ReadUInt32BigEndian(metadata[8..12]);
    var markerCheck = BinaryPrimitives.ReadUInt32BigEndian(metadata[12..16]);
    if ((markerWord ^ MarkerMagic) != markerCheck)
      throw new InvalidDataException("Invalid eCryptfs marker pair.");

    var flags = BinaryPrimitives.ReadUInt32BigEndian(metadata[16..20]);
    var version = (byte)(flags >> 24);
    if (version is 0 or > CurrentFileVersion)
      throw new InvalidDataException($"Unsupported eCryptfs file version {version}.");
    if ((flags & EncryptedFlag) == 0)
      throw new InvalidDataException("The eCryptfs header does not mark the lower file as encrypted.");
    if ((flags & MetadataInXattrFlag) != 0)
      throw new InvalidDataException("This standalone eCryptfs stream declares xattr metadata; the required xattr is not part of the stream.");

    var extentSize = BinaryPrimitives.ReadUInt32BigEndian(metadata[20..24]);
    var headerExtentCount = BinaryPrimitives.ReadUInt16BigEndian(metadata[24..26]);
    ValidateGeometry(extentSize, headerExtentCount);
    var metadataSize64 = checked((ulong)extentSize * headerExtentCount);
    if (metadataSize64 > (ulong)metadata.Length)
      throw new InvalidDataException("Truncated eCryptfs metadata region.");
    var metadataSize = (int)metadataSize64;

    var packets = new List<PassphrasePacket>();
    var offset = FixedHeaderSize;
    var paddingKnown = true;
    while (offset < metadataSize) {
      var tag = metadata[offset];
      if (tag == 0) break;

      if (tag == Tag3) {
        var tag3 = ParsePacket(metadata[..metadataSize], ref offset, Tag3);
        var packet = ParseTag3(tag3);
        if (offset >= metadataSize || metadata[offset] != Tag11)
          throw new InvalidDataException("eCryptfs tag-3 packet is not followed by its tag-11 signature packet.");
        var tag11 = ParsePacket(metadata[..metadataSize], ref offset, Tag11);
        packet = packet with { Signature = ParseTag11Signature(tag11) };
        packets.Add(packet);
        continue;
      }

      if (tag == Tag1) {
        _ = ParsePacket(metadata[..metadataSize], ref offset, Tag1);
        continue;
      }

      // Unknown non-zero metadata is potentially live. Stop parsing and refuse to
      // call the remainder free space; this keeps wipe fail-closed.
      paddingKnown = false;
      offset = metadataSize;
      break;
    }

    return new Header(decryptedSize, markerWord, flags, extentSize, headerExtentCount,
      metadataSize, offset, paddingKnown, packets);
  }

  private static PassphrasePacket ParseTag3(ReadOnlySpan<byte> body) {
    if (body.Length < 14)
      throw new InvalidDataException("Truncated eCryptfs tag-3 packet.");
    if (body[0] != 0x04)
      throw new InvalidDataException($"Unsupported eCryptfs tag-3 version {body[0]}.");
    if (body[2] != 0x03)
      throw new InvalidDataException($"Unsupported eCryptfs S2K specifier {body[2]}.");
    if (body[3] != 0x01)
      throw new InvalidDataException($"Unsupported eCryptfs tag-3 hash identifier {body[3]}.");
    if (body[12] != 0x60)
      throw new InvalidDataException($"Unsupported eCryptfs S2K iteration code 0x{body[12]:X2}.");

    var encryptedKey = body[13..].ToArray();
    if (encryptedKey.Length == 0 || encryptedKey.Length > 64 || (encryptedKey.Length & 15) != 0)
      throw new InvalidDataException("Invalid eCryptfs encrypted file-key length.");
    return new PassphrasePacket(body[1], body[4..12].ToArray(), encryptedKey, []);
  }

  private static byte[] ParseTag11Signature(ReadOnlySpan<byte> body) {
    if (body.Length < 14 || body[0] != 0x62)
      throw new InvalidDataException("Invalid eCryptfs tag-11 literal packet.");
    var nameLength = body[1];
    var signatureOffset = 2 + nameLength + 4;
    if (signatureOffset > body.Length || body.Length - signatureOffset != 8)
      throw new InvalidDataException("Invalid eCryptfs tag-11 signature length.");
    return body[signatureOffset..].ToArray();
  }

  private static ReadOnlySpan<byte> ParsePacket(ReadOnlySpan<byte> data, ref int offset, byte expectedTag) {
    if (offset >= data.Length || data[offset++] != expectedTag)
      throw new InvalidDataException($"Expected eCryptfs packet tag 0x{expectedTag:X2}.");
    var bodyLength = ReadPacketLength(data, ref offset);
    if (bodyLength < 0 || bodyLength > data.Length - offset)
      throw new InvalidDataException("Truncated eCryptfs packet body.");
    var body = data.Slice(offset, bodyLength);
    offset += bodyLength;
    return body;
  }

  private static int ReadPacketLength(ReadOnlySpan<byte> data, ref int offset) {
    if (offset >= data.Length)
      throw new InvalidDataException("Truncated eCryptfs packet length.");
    var first = data[offset++];
    if (first < 192) return first;
    if (first < 224) {
      if (offset >= data.Length)
        throw new InvalidDataException("Truncated two-byte eCryptfs packet length.");
      return ((first - 192) << 8) + data[offset++] + 192;
    }
    throw new InvalidDataException("Unsupported eCryptfs partial/five-byte packet length.");
  }

  private static int WriteTag3(Span<byte> metadata, int offset, int keySize, ReadOnlySpan<byte> encryptedFileKey, ReadOnlySpan<byte> salt) {
    var bodyLength = 13 + encryptedFileKey.Length;
    metadata[offset++] = Tag3;
    metadata[offset++] = checked((byte)bodyLength);
    metadata[offset++] = 0x04;
    metadata[offset++] = keySize switch { 16 => Aes128, 24 => Aes192, 32 => Aes256, _ => throw new ArgumentOutOfRangeException(nameof(keySize)) };
    metadata[offset++] = 0x03;
    metadata[offset++] = 0x01;
    salt.CopyTo(metadata[offset..]);
    offset += salt.Length;
    metadata[offset++] = 0x60;
    encryptedFileKey.CopyTo(metadata[offset..]);
    return offset + encryptedFileKey.Length;
  }

  private static int WriteTag11(Span<byte> metadata, int offset, ReadOnlySpan<byte> signature) {
    const int bodyLength = 22;
    metadata[offset++] = Tag11;
    metadata[offset++] = bodyLength;
    metadata[offset++] = 0x62;
    metadata[offset++] = 8;
    ConsoleName.CopyTo(metadata[offset..]);
    offset += ConsoleName.Length;
    metadata.Slice(offset, 4).Clear();
    offset += 4;
    signature.CopyTo(metadata[offset..]);
    return offset + signature.Length;
  }

  private static PassphrasePacket FindMatchingPassphrasePacket(Header header, string password, out byte[] fekek) {
    var passwordBytes = Encoding.UTF8.GetBytes(password);
    if (passwordBytes.Length > 64)
      throw new CryptographicException("eCryptfs passphrase exceeds 64 encoded bytes.");
    try {
      foreach (var packet in header.PassphrasePackets) {
        var material = DerivePassphraseMaterial(passwordBytes, packet.Salt);
        if (CryptographicOperations.FixedTimeEquals(material.Signature, packet.Signature)) {
          CryptographicOperations.ZeroMemory(material.Signature);
          fekek = material.Fekek;
          return packet;
        }
        CryptographicOperations.ZeroMemory(material.Fekek);
        CryptographicOperations.ZeroMemory(material.Signature);
      }
    } finally {
      CryptographicOperations.ZeroMemory(passwordBytes);
    }
    if (header.PassphrasePackets.Count == 0)
      throw new NotSupportedException("This eCryptfs file has no passphrase tag-3 packet; private-key authentication is not supported.");
    throw new CryptographicException("The supplied eCryptfs passphrase does not match any authentication-token signature.");
  }

  private static (byte[] Fekek, byte[] Signature) DerivePassphraseMaterial(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt) {
    var seed = new byte[salt.Length + password.Length];
    salt.CopyTo(seed);
    password.CopyTo(seed.AsSpan(salt.Length));

    var digest = SHA512.HashData(seed);
    CryptographicOperations.ZeroMemory(seed);
    for (var i = 1; i < PassphraseHashIterations; ++i) {
      var next = SHA512.HashData(digest);
      CryptographicOperations.ZeroMemory(digest);
      digest = next;
    }

    var sigHash = SHA512.HashData(digest);
    var signature = sigHash.AsSpan(0, 8).ToArray();
    CryptographicOperations.ZeroMemory(sigHash);
    return (digest, signature);
  }

  private static byte[] EncryptFileKey(ReadOnlySpan<byte> fileKey, ReadOnlySpan<byte> fekek, int keySize) {
    var padded = new byte[keySize == 24 ? 32 : keySize];
    fileKey.CopyTo(padded);
    try {
      using var aes = Aes.Create();
      aes.Mode = CipherMode.ECB;
      aes.Padding = PaddingMode.None;
      aes.Key = fekek[..keySize].ToArray();
      using var transform = aes.CreateEncryptor();
      return transform.TransformFinalBlock(padded, 0, padded.Length);
    } finally {
      CryptographicOperations.ZeroMemory(padded);
    }
  }

  private static byte[] DecryptFileKey(PassphrasePacket packet, ReadOnlySpan<byte> fekek) {
    if (packet.KeySize == 0)
      throw new NotSupportedException($"Unsupported eCryptfs cipher code 0x{packet.CipherCode:X2}.");
    using var aes = Aes.Create();
    aes.Mode = CipherMode.ECB;
    aes.Padding = PaddingMode.None;
    aes.Key = fekek[..packet.KeySize].ToArray();
    using var transform = aes.CreateDecryptor();
    var decrypted = transform.TransformFinalBlock(packet.EncryptedFileKey, 0, packet.EncryptedFileKey.Length);
    if (decrypted.Length < packet.KeySize) {
      CryptographicOperations.ZeroMemory(decrypted);
      throw new InvalidDataException("eCryptfs encrypted file key is shorter than the selected cipher key size.");
    }
    var fileKey = decrypted.AsSpan(0, packet.KeySize).ToArray();
    CryptographicOperations.ZeroMemory(decrypted);
    return fileKey;
  }

  private static byte[] DeriveExtentIv(ReadOnlySpan<byte> rootIv, long extentIndex) {
    Span<byte> source = stackalloc byte[32];
    source.Clear();
    rootIv[..16].CopyTo(source);
    var text = extentIndex.ToString(CultureInfo.InvariantCulture);
    if (!Encoding.ASCII.TryGetBytes(text, source[16..31], out _))
      throw new InvalidDataException("eCryptfs extent index is too large for IV derivation.");
    return MD5.HashData(source);
  }

  private static void ValidateGeometry(uint extentSize, ushort headerExtentCount) {
    if (extentSize < 16 || (extentSize & (extentSize - 1)) != 0 || (extentSize & 15) != 0)
      throw new InvalidDataException($"Invalid eCryptfs extent size {extentSize}.");
    if (headerExtentCount == 0)
      throw new InvalidDataException("eCryptfs header extent count is zero.");
    var metadataSize = checked((ulong)extentSize * headerExtentCount);
    if (metadataSize < FixedHeaderSize)
      throw new InvalidDataException("eCryptfs metadata region is smaller than the fixed header.");
  }

  private static long ZeroRange(Stream stream, long offset, long count) {
    if (count <= 0) return 0;
    var zeroes = new byte[64 * 1024];
    stream.Position = offset;
    var remaining = count;
    while (remaining > 0) {
      var chunk = (int)Math.Min(zeroes.Length, remaining);
      stream.Write(zeroes, 0, chunk);
      remaining -= chunk;
    }
    return count;
  }

  private static void CopyExactly(Stream input, Stream output, long count) {
    var buffer = new byte[64 * 1024];
    var remaining = count;
    while (remaining > 0) {
      var requested = (int)Math.Min(buffer.Length, remaining);
      var read = input.Read(buffer, 0, requested);
      if (read <= 0) throw new EndOfStreamException();
      output.Write(buffer, 0, read);
      remaining -= read;
    }
  }

  private static void ReadExactly(Stream stream, Span<byte> buffer) {
    var total = 0;
    while (total < buffer.Length) {
      var read = stream.Read(buffer[total..]);
      if (read <= 0) throw new EndOfStreamException("Unexpected end of eCryptfs stream.");
      total += read;
    }
  }

  private static void ReadExactly(Stream stream, byte[] buffer) => ReadExactly(stream, buffer.AsSpan());
}
