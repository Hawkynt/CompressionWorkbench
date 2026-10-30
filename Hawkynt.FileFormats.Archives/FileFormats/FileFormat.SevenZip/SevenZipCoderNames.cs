using System.Security.Cryptography;
using System.Text;
using Compression.Core.Transforms;
using Compression.Registry;

namespace FileFormat.SevenZip;

/// <summary>Method ids, names and small property decoders shared by the 7z reader and writer.</summary>
internal static class SevenZipCoderNames {
  /// <summary>The 7-Zip method name for <paramref name="codecId"/>, or its hex id when unknown.</summary>
  public static string Describe(ReadOnlySpan<byte> codecId) {
    foreach (var (id, name) in Known)
      if (codecId.SequenceEqual(id))
        return name;
    return codecId.IsEmpty ? "(empty)" : Convert.ToHexString(codecId);
  }

  private static readonly (byte[] Id, string Name)[] Known = [
    (SevenZipConstants.CodecCopy.ToArray(), "Copy"),
    (SevenZipConstants.CodecLzma.ToArray(), "LZMA"),
    (SevenZipConstants.CodecLzma2.ToArray(), "LZMA2"),
    (SevenZipConstants.CodecPpmd.ToArray(), "PPMd"),
    (SevenZipConstants.CodecBzip2.ToArray(), "BZip2"),
    (SevenZipConstants.CodecDeflate.ToArray(), "Deflate"),
    (SevenZipConstants.CodecDeflate64.ToArray(), "Deflate64"),
    (SevenZipConstants.CodecBcj.ToArray(), "BCJ"),
    (SevenZipConstants.CodecBcj2.ToArray(), "BCJ2"),
    (SevenZipConstants.CodecBcjPpc.ToArray(), "PPC"),
    (SevenZipConstants.CodecBcjIa64.ToArray(), "IA64"),
    (SevenZipConstants.CodecBcjArm.ToArray(), "ARM"),
    (SevenZipConstants.CodecBcjArmThumb.ToArray(), "ARMT"),
    (SevenZipConstants.CodecBcjSparc.ToArray(), "SPARC"),
    (SevenZipConstants.CodecArm64.ToArray(), "ARM64"),
    (SevenZipConstants.CodecRiscV.ToArray(), "RISCV"),
    (SevenZipConstants.CodecDelta.ToArray(), "Delta"),
    (SevenZipConstants.CodecSwap2.ToArray(), "Swap2"),
    (SevenZipConstants.CodecSwap4.ToArray(), "Swap4"),
    (SevenZipConstants.CodecAes.ToArray(), "7zAES"),
    (SevenZipConstants.CodecZstd.ToArray(), "ZSTD"),
    (SevenZipConstants.CodecBrotli.ToArray(), "BROTLI"),
    (SevenZipConstants.CodecLz4.ToArray(), "LZ4"),
    ([0x03, 0x04, 0x01], "PPMD"),
  ];

  /// <summary>The branch converter a 7z method id names, or <see langword="null"/>.</summary>
  public static BranchArchitecture? BranchArchitectureOf(ReadOnlySpan<byte> codecId) {
    if (codecId.SequenceEqual(SevenZipConstants.CodecBcj)) return BranchArchitecture.X86;
    if (codecId.SequenceEqual(SevenZipConstants.CodecBcjPpc)) return BranchArchitecture.PowerPC;
    if (codecId.SequenceEqual(SevenZipConstants.CodecBcjIa64)) return BranchArchitecture.IA64;
    if (codecId.SequenceEqual(SevenZipConstants.CodecBcjArm)) return BranchArchitecture.Arm;
    if (codecId.SequenceEqual(SevenZipConstants.CodecBcjArmThumb)) return BranchArchitecture.ArmThumb;
    if (codecId.SequenceEqual(SevenZipConstants.CodecBcjSparc)) return BranchArchitecture.Sparc;
    if (codecId.SequenceEqual(SevenZipConstants.CodecArm64)) return BranchArchitecture.Arm64;
    if (codecId.SequenceEqual(SevenZipConstants.CodecRiscV)) return BranchArchitecture.RiscV;
    return null;
  }

  /// <summary>The LZMA2 dictionary size of property byte <paramref name="p"/> (0..39; 40 means 4 GiB - 1).</summary>
  public static int Lzma2DictionarySize(byte p)
    => p >= 40 ? int.MaxValue : (int)Math.Min(int.MaxValue, (2L | (p & 1)) << (p / 2 + 11));

  /// <summary>
  /// The name a folder's compressor goes by in listings: the first coder that is neither a
  /// filter nor the cipher ("Copy" when the folder only stores).
  /// </summary>
  public static string MethodOf(SevenZipFolder folder) {
    foreach (var coder in folder.Coders) {
      var name = PrimaryName(coder);
      if (name != null)
        return name;
    }
    return folder.Coders.Exists(static c => c.Is(SevenZipConstants.CodecCopy)) ? "Copy" : "7z";
  }

  private static string? PrimaryName(SevenZipCoder c) {
    if (c.Is(SevenZipConstants.CodecLzma2)) return "LZMA2";
    if (c.Is(SevenZipConstants.CodecLzma)) return "LZMA";
    if (c.Is(SevenZipConstants.CodecDeflate)) return "Deflate";
    if (c.Is(SevenZipConstants.CodecDeflate64)) return "Deflate64";
    if (c.Is(SevenZipConstants.CodecBzip2)) return "BZip2";
    if (c.Is(SevenZipConstants.CodecPpmd) || c.Is([0x03, 0x04, 0x01])) return "PPMd";
    if (c.Is(SevenZipConstants.CodecZstd)) return "Zstd";
    if (c.Is(SevenZipConstants.CodecBrotli)) return "Brotli";
    if (c.Is(SevenZipConstants.CodecLz4)) return "LZ4";
    return null;
  }
}

/// <summary>
/// Derives and caches 7z AES-256 keys: SHA-256 over 2^NumCyclesPower rounds of salt, the
/// password in UTF-16LE and a 64-bit little-endian round counter (7-Zip 7zAes.cpp), or — for
/// NumCyclesPower 0x3F — salt and password copied into the key unhashed. One archive usually
/// shares one salt and cycle count across all folders, so the expensive derivation runs once.
/// </summary>
internal sealed class SevenZipKeyCache(string? password) {
  private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);

  /// <summary>Whether a password was supplied.</summary>
  public bool HasPassword => password != null;

  /// <summary>Parses 7z AES properties and builds the decrypting filter for them.</summary>
  public SevenZipAesDecryptFilter CreateDecryptor(byte[]? properties) {
    var (cycles, salt, iv) = ParseProperties(properties);
    if (password == null)
      throw new ArchivePasswordException("7z: the archive is encrypted; a password is required.", passwordWasSupplied: false);
    return new SevenZipAesDecryptFilter(this.KeyFor(cycles, salt), iv);
  }

  /// <summary>
  /// The salt, IV and cycle count of 7z AES properties: byte 0 holds NumCyclesPower in its low
  /// six bits, bit 7 flags a salt and bit 6 an IV; byte 1 holds the extra salt length in its high
  /// nibble and the extra IV length in its low one (7-Zip 7zAes.cpp SetDecoderProperties2).
  /// </summary>
  public static (int Cycles, byte[] Salt, byte[] Iv) ParseProperties(byte[]? props) {
    if (props is not { Length: > 0 })
      throw new InvalidDataException("7z: AES coder without properties.");
    var b0 = props[0];
    var cycles = b0 & 0x3F;
    var iv = new byte[16];
    if ((b0 & 0xC0) == 0) {
      if (props.Length != 1)
        throw new InvalidDataException("7z: malformed AES properties.");
      return (cycles, [], iv);
    }
    if (props.Length < 2)
      throw new InvalidDataException("7z: malformed AES properties.");
    var b1 = props[1];
    var saltSize = ((b0 >> 7) & 1) + (b1 >> 4);
    var ivSize = ((b0 >> 6) & 1) + (b1 & 0x0F);
    if (props.Length != 2 + saltSize + ivSize)
      throw new InvalidDataException("7z: malformed AES properties.");
    var salt = props.AsSpan(2, saltSize).ToArray();
    props.AsSpan(2 + saltSize, ivSize).CopyTo(iv);
    if (cycles > 24 && cycles != 0x3F)
      throw new NotSupportedException($"7z: AES key derivation with 2^{cycles} rounds is not supported (7-Zip stops at 2^24).");
    return (cycles, salt, iv);
  }

  private byte[] KeyFor(int cycles, byte[] salt) {
    var cacheKey = $"{cycles}:{Convert.ToHexString(salt)}";
    lock (this._keys) {
      if (this._keys.TryGetValue(cacheKey, out var cached))
        return cached;
    }
    var key = DeriveKey(password!, salt, cycles);
    lock (this._keys)
      this._keys[cacheKey] = key;
    return key;
  }

  /// <summary>The 7z AES-256 key for <paramref name="password"/>.</summary>
  public static byte[] DeriveKey(string password, ReadOnlySpan<byte> salt, int cycles) {
    var passwordBytes = Encoding.Unicode.GetBytes(password);
    if (cycles == 0x3F) {
      var raw = new byte[32];
      var n = Math.Min(salt.Length, 32);
      salt[..n].CopyTo(raw);
      passwordBytes.AsSpan(0, Math.Min(passwordBytes.Length, 32 - n)).CopyTo(raw.AsSpan(n));
      return raw;
    }

    var block = new byte[salt.Length + passwordBytes.Length + 8];
    salt.CopyTo(block);
    passwordBytes.CopyTo(block, salt.Length);
    var counterAt = salt.Length + passwordBytes.Length;
    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    var rounds = 1L << cycles;
    for (long round = 0; round < rounds; ++round) {
      System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(block.AsSpan(counterAt), round);
      sha.AppendData(block);
    }
    return sha.GetHashAndReset();
  }
}
