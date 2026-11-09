using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Compression.Core.Checksums;
using Compression.Registry;

namespace FileFormat.VeraCrypt;

/// <summary>Which header of a container to open.</summary>
public enum TcHeaderKind {
  /// <summary>The outer (standard) volume: header at 0, backup 128 KiB before the end.</summary>
  Standard,
  /// <summary>A hidden volume: header at 64 KiB, backup 64 KiB before the end (TrueCrypt &lt; 6: 1.5 KiB before the end).</summary>
  Hidden,
}

/// <summary>What opens a TrueCrypt/VeraCrypt volume, and which derivations and chains to try.</summary>
public sealed record TcUnlockOptions {
  /// <summary>Keyfile contents, mixed into the password pool.</summary>
  public IReadOnlyList<byte[]> Keyfiles { get; init; } = [];
  /// <summary>VeraCrypt PIM; 0 for the default iterations. A PIM restricts the search to VeraCrypt derivations.</summary>
  public int Pim { get; init; }
  /// <summary>Only try this PRF (<c>sha512</c>, <c>whirlpool</c>, …); null tries all.</summary>
  public string? Prf { get; init; }
  /// <summary>Only try chains whose cryptsetup name contains this (<c>aes</c>, <c>serpent-twofish-aes</c>, …); null tries all.</summary>
  public string? Cipher { get; init; }
  /// <summary>Which header to open; <see langword="null"/> tries the standard header, then the hidden one, as VeraCrypt does with a single password.</summary>
  public TcHeaderKind? Header { get; init; }
}

/// <summary>A decrypted TrueCrypt/VeraCrypt volume header and where its data lives.</summary>
public sealed class TcVolume {
  public const int HeaderSize = 512, SaltSize = 64, EncryptedSize = 448;
  public const long HiddenHeaderOffset = 65536, BackupHeaderFromEnd = 131072, HiddenBackupFromEnd = 65536, OldHiddenFromEnd = 1536;
  public const long DataAreaOffset = 131072;
  public const uint FlagSystem = 1;

  public required bool VeraCrypt { get; init; }
  public required TcKdf Kdf { get; init; }
  public required TcCipherChain Chain { get; init; }
  public required int Version { get; init; }
  public required int MinimumVersion { get; init; }
  public required ulong VolumeSize { get; init; }
  public required ulong HiddenVolumeSize { get; init; }
  public required ulong MasterKeyOffset { get; init; }
  public required ulong MasterKeySize { get; init; }
  public required uint Flags { get; init; }
  public required int SectorSize { get; init; }
  public required long HeaderOffset { get; init; }
  public required TcHeaderKind Kind { get; init; }
  /// <summary>Byte offset of the first data sector in the container.</summary>
  public required long DataOffset { get; init; }
  /// <summary>Data length in bytes.</summary>
  public required long DataLength { get; init; }
  /// <summary>IV sector number of the first data sector.</summary>
  public required long IvBase { get; init; }
  internal byte[] Keys { get; init; } = [];

  /// <summary>Whether <paramref name="area"/> (the decrypted 448 bytes) is a valid header.</summary>
  private static bool Valid(ReadOnlySpan<byte> area, bool allowVera) {
    var magic = area[..4];
    if (!magic.SequenceEqual("TRUE"u8) && !(allowVera && magic.SequenceEqual("VERA"u8))) return false;
    if (Crc32.Compute(area.Slice(192, 256)) != BinaryPrimitives.ReadUInt32BigEndian(area[8..])) return false;
    return BinaryPrimitives.ReadUInt16BigEndian(area[4..]) <= 3 || Crc32.Compute(area[..188]) == BinaryPrimitives.ReadUInt32BigEndian(area[188..]);
  }

  /// <summary>Tries every derivation and chain on <paramref name="header"/>; null when none opens it.</summary>
  private static (TcKdf Kdf, TcCipherChain Chain, byte[] Area)? Decrypt(ReadOnlySpan<byte> header, ReadOnlySpan<byte> password, TcUnlockOptions o) {
    var salt = header[..SaltSize];
    var mixed = new Dictionary<bool, byte[]>();
    foreach (var kdf in TcKdf.All) {
      if (o.Prf is { } prf && !kdf.Prf.StartsWith(prf, StringComparison.OrdinalIgnoreCase)) continue;
      if (o.Pim > 0 && !kdf.VeraCrypt) continue;
      byte[] pool;
      try {
        if (!mixed.TryGetValue(kdf.VeraCrypt, out pool!)) mixed[kdf.VeraCrypt] = pool = TcKdf.MixPassword(password, o.Keyfiles, kdf.VeraCrypt);
      } catch (ArgumentException) {
        continue; // longer than this generation's pool
      }
      var key = kdf.Derive(pool, salt, o.Pim);
      try {
        foreach (var chain in TcCipherChain.All) {
          if (o.Cipher is { } c && !chain.Name.Contains(c, StringComparison.OrdinalIgnoreCase)) continue;
          var area = header.Slice(SaltSize, EncryptedSize).ToArray();
          using (var cipher = new TcSectorCipher(chain, key, headerKey: true))
            cipher.DecryptHeader(area);
          if (Valid(area, kdf.VeraCrypt))
            return (kdf, chain, area);
          CryptographicOperations.ZeroMemory(area);
        }
      } finally {
        CryptographicOperations.ZeroMemory(key);
      }
    }
    foreach (var p in mixed.Values) CryptographicOperations.ZeroMemory(p);
    return null;
  }

  /// <summary>Opens the volume's header with <paramref name="password"/>; null when it does not open (wrong password, wrong header kind, not a volume).</summary>
  public static TcVolume? Open(Stream stream, string password, TcUnlockOptions? options = null) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(password);
    var o = options ?? new();
    var pw = Encoding.UTF8.GetBytes(password);
    var length = stream.Length;
    TcHeaderKind[] kinds = o.Header is { } only ? [only] : [TcHeaderKind.Standard, TcHeaderKind.Hidden];
    var header = new byte[HeaderSize];
    try {
      foreach (var kind in kinds) {
        long[] offsets = kind == TcHeaderKind.Standard
          ? [0, length - BackupHeaderFromEnd]
          : [HiddenHeaderOffset, length - OldHiddenFromEnd, length - HiddenBackupFromEnd];
        foreach (var offset in offsets.Where(x => x >= 0 && x + HeaderSize <= length).Distinct()) {
          stream.Position = offset;
          stream.ReadExactly(header);
          if (Decrypt(header, pw, o) is not { } found) continue;
          return FromArea(found.Kdf, found.Chain, found.Area, offset, kind, length);
        }
      }
      return null;
    } finally {
      CryptographicOperations.ZeroMemory(pw);
    }
  }

  private static TcVolume FromArea(TcKdf kdf, TcCipherChain chain, byte[] a, long headerOffset, TcHeaderKind kind, long deviceLength) {
    var version = BinaryPrimitives.ReadUInt16BigEndian(a.AsSpan(4));
    var hiddenSize = BinaryPrimitives.ReadUInt64BigEndian(a.AsSpan(28));
    var volumeSize = BinaryPrimitives.ReadUInt64BigEndian(a.AsSpan(36));
    var mkOffset = BinaryPrimitives.ReadUInt64BigEndian(a.AsSpan(44));
    if (mkOffset == 0) mkOffset = 512;
    var sectorSize = (int)BinaryPrimitives.ReadUInt32BigEndian(a.AsSpan(64));
    if (sectorSize == 0) sectorSize = 512;
    if (sectorSize != 512)
      throw new NotSupportedException($"TrueCrypt/VeraCrypt: {sectorSize}-byte sectors are not supported.");

    // Data offset and IV base as cryptsetup computes them (TCRYPT_get_data_offset / _get_iv_offset).
    var hidden = kind == TcHeaderKind.Hidden;
    var oldHiddenStart = deviceLength - (long)hiddenSize - OldHiddenFromEnd;
    long dataSector = chain.Mode == TcMode.Xts
      ? version < 3 ? 1 : hidden && version <= 3 ? oldHiddenStart / 512 : (long)(mkOffset / 512)
      : hidden ? oldHiddenStart / 512 : (long)(mkOffset / 512);
    var ivBase = chain.Mode switch {
      TcMode.Xts => dataSector,
      TcMode.Lrw => 0,
      _ => (long)(mkOffset / 512),
    };
    // TrueCrypt before 5.0 stored no volume size: the volume runs to the end of the container.
    var dataLength = (long)(hidden ? hiddenSize : volumeSize);
    if (dataLength == 0)
      dataLength = (deviceLength - dataSector * 512) / 512 * 512;
    if (dataSector * 512 + dataLength > deviceLength)
      throw new InvalidDataException("TrueCrypt/VeraCrypt: the data area runs past the end of the container.");
    return new TcVolume {
      VeraCrypt = a.AsSpan(0, 4).SequenceEqual("VERA"u8), Kdf = kdf, Chain = chain, Version = version,
      MinimumVersion = BinaryPrimitives.ReadUInt16BigEndian(a.AsSpan(6)),
      VolumeSize = volumeSize, HiddenVolumeSize = hiddenSize, MasterKeyOffset = mkOffset,
      MasterKeySize = BinaryPrimitives.ReadUInt64BigEndian(a.AsSpan(52)),
      Flags = BinaryPrimitives.ReadUInt32BigEndian(a.AsSpan(60)), SectorSize = sectorSize,
      HeaderOffset = headerOffset, Kind = kind, DataOffset = dataSector * 512, DataLength = dataLength, IvBase = ivBase,
      Keys = a.AsSpan(192, 256).ToArray(),
    };
  }

  /// <summary>The decrypted data area as a block device of 512-byte sectors.</summary>
  public SectorCipherBlockDevice OpenBlockDevice(Stream stream, bool writable, bool leaveOpen = true)
    => new(stream, this.DataOffset, TcSectorCipher.SectorSize, this.DataLength / TcSectorCipher.SectorSize,
      new TcSectorCipher(this.Chain, this.Keys, this.IvBase), writable, leaveOpen);

  // ── Writing ──────────────────────────────────────────────────────────────

  /// <summary>Builds an encrypted 512-byte header.</summary>
  internal static byte[] BuildHeader(bool veraCrypt, TcKdf kdf, int pim, TcCipherChain chain, ReadOnlySpan<byte> password,
      IReadOnlyList<byte[]> keyfiles, ReadOnlySpan<byte> keys, ulong volumeSize, ulong hiddenVolumeSize, ulong mkOffset, ulong mkSize) {
    var header = new byte[HeaderSize];
    RandomNumberGenerator.Fill(header.AsSpan(0, SaltSize));
    var a = header.AsSpan(SaltSize, EncryptedSize);
    (veraCrypt ? "VERA"u8 : "TRUE"u8).CopyTo(a);
    BinaryPrimitives.WriteUInt16BigEndian(a[4..], 5);
    BinaryPrimitives.WriteUInt16BigEndian(a[6..], (ushort)(veraCrypt ? 0x010B : 0x0700));
    BinaryPrimitives.WriteUInt64BigEndian(a[28..], hiddenVolumeSize);
    BinaryPrimitives.WriteUInt64BigEndian(a[36..], volumeSize);
    BinaryPrimitives.WriteUInt64BigEndian(a[44..], mkOffset);
    BinaryPrimitives.WriteUInt64BigEndian(a[52..], mkSize);
    BinaryPrimitives.WriteUInt32BigEndian(a[64..], 512);
    keys.CopyTo(a[192..]);
    BinaryPrimitives.WriteUInt32BigEndian(a[8..], Crc32.Compute(a.Slice(192, 256)));
    BinaryPrimitives.WriteUInt32BigEndian(a[188..], Crc32.Compute(a[..188]));
    var pool = TcKdf.MixPassword(password, keyfiles, veraCrypt);
    var headerKey = kdf.Derive(pool, header.AsSpan(0, SaltSize), pim);
    using (var c = new TcSectorCipher(chain, headerKey, headerKey: true))
      c.EncryptHeader(a);
    CryptographicOperations.ZeroMemory(headerKey);
    CryptographicOperations.ZeroMemory(pool);
    return header;
  }
}
