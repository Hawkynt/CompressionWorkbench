using System.Buffers.Binary;
using Compression.Registry;
using System.Security.Cryptography;
using System.Text;
using Compression.Core.Deflate;
using FileFormat.Zlib;
using Hawkynt.Algorithms.Hashing;

namespace FileFormat.Pea;

/// <summary>One object of a PEA archive.</summary>
public sealed class PeaEntry {
  /// <summary>Gets the path relative to the archive's common root, '/'-separated.</summary>
  public string Path { get; init; } = "";
  /// <summary>Gets the qualified name as stored (PEA stores absolute names).</summary>
  public string StoredName { get; init; } = "";
  /// <summary>Gets whether the object is a directory.</summary>
  public bool IsDirectory { get; init; }
  /// <summary>Gets the uncompressed size.</summary>
  public long Size { get; init; }
  /// <summary>Gets the file attributes as stored.</summary>
  public uint Attributes { get; init; }
  /// <summary>Gets the modification time, when the archive's time encoding is known.</summary>
  public DateTime? LastModified { get; init; }
  /// <summary>Gets the decoded content (empty for directories).</summary>
  public byte[] Data { get; init; } = [];
  /// <summary>Gets whether the archive's stream is encrypted.</summary>
  public bool IsEncrypted { get; init; }
  /// <summary>The qualified name's bytes as stored.</summary>
  internal byte[] RawName { get; init; } = [];
  /// <summary>The time as the archive encodes it.</summary>
  internal uint RawTime { get; init; }
  /// <summary>The directory every stored name of the archive lives under.</summary>
  internal string Root { get; init; } = "";
}

/// <summary>
/// PEA (PeaZip's own format, version 1 revision 6): a 10-byte archive header, one stream with a
/// 10-byte header naming the compression (stored or zlib at level 3/6/9 in independent 1 MiB
/// blocks) and three integrity checks — per object, for the stream and for the volume — then
/// objects (name, time, attributes, size, data) and an end-of-archive trigger. A volume set is
/// that byte stream cut into <c>name.000001.pea</c>, <c>name.000002.pea</c>, …, each volume
/// ending with the volume check over its own bytes. This covers single archives and volume sets
/// with any of the hash or checksum controls, and streams encrypted with one cipher (AES, Twofish
/// or Serpent in EAX mode, 128- or 256-bit, or AES-128 with HMAC) or with the triple cascades of
/// the three 256-bit EAX ciphers, which encrypt everything after salt-and-verifier subheaders and
/// end the stream with a tag in place of the stream check.
/// </summary>
/// <remarks>
/// Layout from the public-domain PEA file format specification and PeaZip's pea_utils.pas /
/// pea.pas (LGPL-3.0), measured against archives PeaZip 11.4 writes.
/// </remarks>
public static class PeaArchive {
  private const byte Magic = 0xEA;
  private const int WriteBlockSize = 1 << 20; // WBUFSIZE in pea.pas
  private const uint DirectoryAttribute = 0x10;

  /// <summary>A check algorithm by its PEA id.</summary>
  public enum Check : byte {
    /// <summary>No check.</summary>
    None = 0x00,
    /// <summary>Adler-32 (4 bytes, little-endian).</summary>
    Adler32 = 0x01,
    /// <summary>CRC-32 (4 bytes, little-endian).</summary>
    Crc32 = 0x02,
    /// <summary>CRC-64 ECMA-182, MSB-first, all-ones init and xor-out (8 bytes, little-endian).</summary>
    Crc64 = 0x03,
    /// <summary>MD5.</summary>
    Md5 = 0x10,
    /// <summary>RIPEMD-160.</summary>
    Ripemd160 = 0x11,
    /// <summary>SHA-1.</summary>
    Sha1 = 0x12,
    /// <summary>SHA-256.</summary>
    Sha256 = 0x13,
    /// <summary>SHA-512.</summary>
    Sha512 = 0x14,
    /// <summary>Whirlpool.</summary>
    Whirlpool = 0x15,
    /// <summary>SHA3-256.</summary>
    Sha3_256 = 0x16,
    /// <summary>SHA3-512.</summary>
    Sha3_512 = 0x17,
    /// <summary>BLAKE2s-256.</summary>
    Blake2s = 0x18,
    /// <summary>BLAKE2b-512.</summary>
    Blake2b = 0x19,
    /// <summary>Stream only: AES-128 CTR with HMAC-SHA-1, keys by PBKDF2-HMAC-SHA-1 (pea's HMAC).</summary>
    AesHmac = 0x30,
    /// <summary>Stream only: AES-128 EAX, keys by PBKDF2-HMAC-SHA-1 (pea's EAX).</summary>
    AesEax = 0x31,
    /// <summary>Stream only: Twofish-128 EAX, keys by PBKDF2-HMAC-SHA-1 (pea's TF).</summary>
    TwofishEax = 0x32,
    /// <summary>Stream only: Serpent-128 EAX, keys by PBKDF2-HMAC-SHA-1 (pea's SP).</summary>
    SerpentEax = 0x33,
    /// <summary>Stream only: AES-256 EAX, keys by PBKDF2-HMAC-Whirlpool (pea's EAX256, PeaZip's default).</summary>
    Aes256Eax = 0x41,
    /// <summary>Stream only: Twofish-256 EAX, keys by PBKDF2-HMAC-Whirlpool (pea's TF256).</summary>
    Twofish256Eax = 0x42,
    /// <summary>Stream only: Serpent-256 EAX, keys by PBKDF2-HMAC-Whirlpool (pea's SP256).</summary>
    Serpent256Eax = 0x43,
    /// <summary>Stream only: AES, then Twofish, then Serpent (256-bit EAX each), keys by PBKDF2 (pea's TRIATS).</summary>
    TriAts = 0x44,
    /// <summary>Stream only: Twofish, Serpent, AES; keys by PBKDF2 (pea's TRITSA).</summary>
    TriTsa = 0x45,
    /// <summary>Stream only: Serpent, AES, Twofish; keys by PBKDF2 (pea's TRISAT).</summary>
    TriSat = 0x46,
    /// <summary>Stream only: AES, Twofish, Serpent; keys by scrypt (pea's SRIATS).</summary>
    SriAts = 0x47,
    /// <summary>Stream only: Twofish, Serpent, AES; keys by scrypt (pea's SRITSA).</summary>
    SriTsa = 0x48,
    /// <summary>Stream only: Serpent, AES, Twofish; keys by scrypt (pea's SRISAT).</summary>
    SriSat = 0x49,
    /// <summary>Stream only: AES, Twofish, Serpent; keys by scrypt and PBKDF2 (pea's HRIATS).</summary>
    HriAts = 0x4A,
    /// <summary>Stream only: Twofish, Serpent, AES; keys by scrypt and PBKDF2 (pea's HRITSA).</summary>
    HriTsa = 0x4B,
    /// <summary>Stream only: Serpent, AES, Twofish; keys by scrypt and PBKDF2 (pea's HRISAT).</summary>
    HriSat = 0x4C,
  }

  private static int TagSize(byte algo) => algo switch {
    0x00 => 0,
    0x01 or 0x02 => 4,
    0x03 => 8,
    0x10 => 16,
    0x11 or 0x12 => 20,
    0x13 or 0x16 or 0x18 => 32,
    0x14 or 0x15 or 0x17 or 0x19 => 64,
    _ when PeaStreamCipher.Encrypts(algo) => PeaStreamCipher.TagSize(algo),
    _ => throw new InvalidDataException($"PEA check algorithm 0x{algo:X2} is unknown."),
  };

  /// <summary>The tag size of an object or volume check, which never encrypts.</summary>
  private static int CheckTagSize(byte algo) => PeaStreamCipher.Encrypts(algo)
    ? throw new InvalidDataException($"PEA check algorithm 0x{algo:X2} encrypts, which only the stream does.")
    : TagSize(algo);

  private static byte[] Tag(byte algo, ReadOnlySpan<byte> data) => algo switch {
    0x00 => [],
    0x01 => Le32(Adler32Of(data)),
    0x02 => Le32(Compression.Core.Checksums.Crc32.Compute(data)),
    0x03 => Le64(Crc64We(data)),
    0x10 => MD5.HashData(data),
    0x11 => Ripemd160.Compute(data),
    0x12 => SHA1.HashData(data),
    0x13 => SHA256.HashData(data),
    0x14 => SHA512.HashData(data),
    0x15 => Whirlpool.Compute(data),
    0x16 => Sha3.Compute256(data),
    0x17 => Sha3.Compute512(data),
    0x18 => Blake2s.Compute(data),
    0x19 => Compression.Core.Checksums.Blake2b.Compute(data, 64),
    _ => throw new NotSupportedException($"PEA check algorithm 0x{algo:X2} is not supported."),
  };

  private static byte[] Le32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return b; }
  private static byte[] Le64(ulong v) { var b = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); return b; }

  private static uint Adler32Of(ReadOnlySpan<byte> data) {
    uint a = 1, b = 0;
    foreach (var x in data) { a = (a + x) % 65521; b = (b + a) % 65521; }
    return (b << 16) | a;
  }

  private static readonly ulong[] Crc64Table = BuildCrc64();

  private static ulong[] BuildCrc64() {
    const ulong poly = 0x42F0E1EBA9EA3693;
    var t = new ulong[256];
    for (var i = 0; i < 256; ++i) {
      var c = (ulong)i << 56;
      for (var k = 0; k < 8; ++k) c = (c & 0x8000000000000000) != 0 ? (c << 1) ^ poly : c << 1;
      t[i] = c;
    }
    return t;
  }

  /// <summary>CRC-64 as PeaZip computes it (W. Ehrhardt's crc64: ECMA-182 polynomial, not reflected, init and xor-out all ones).</summary>
  internal static ulong Crc64We(ReadOnlySpan<byte> data) {
    var crc = ulong.MaxValue;
    foreach (var b in data) crc = (crc << 8) ^ Crc64Table[(byte)(crc >> 56) ^ b];
    return ~crc;
  }

  private static readonly Lazy<Encoding> Ansi = new(() => {
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    return Encoding.GetEncoding(1252); // the format's "ANSI" character encoding (byte 7 = 1)
  });

  private static readonly System.Text.RegularExpressions.Regex VolumeName =
    new(@"^(?<base>.+)\.(?<n>[0-9]{6,})\.pea$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

  /// <summary>
  /// The file name of volume <paramref name="number"/> (1-based) of the set named <paramref name="baseName"/>:
  /// <c>base.000001.pea</c>, <c>base.000002.pea</c>, … (at least six digits).
  /// </summary>
  public static string VolumePath(string baseName, int number) {
    ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
    return $"{baseName}.{number.ToString("D6", System.Globalization.CultureInfo.InvariantCulture)}.pea";
  }

  /// <summary>
  /// The set name of a volume path (<c>dir/base.000003.pea</c> gives <c>dir/base</c>), or null when
  /// <paramref name="path"/> is not named like a volume.
  /// </summary>
  public static string? VolumeSetBase(string path) {
    var m = VolumeName.Match(path);
    return m.Success ? m.Groups["base"].Value : null;
  }

  /// <summary>
  /// Every volume of the set <paramref name="path"/> belongs to, in order from volume 1 while the
  /// next file exists; just <paramref name="path"/> when it is not named like a volume.
  /// </summary>
  public static List<string> VolumePaths(string path) {
    ArgumentNullException.ThrowIfNull(path);
    var setBase = VolumeSetBase(path);
    if (setBase is null) return [path];
    var paths = new List<string>();
    for (var n = 1; ; ++n) {
      var candidate = n == 1 && Path.GetFileName(path).Equals(Path.GetFileName(VolumePath(setBase, 1)), StringComparison.OrdinalIgnoreCase)
        ? path : VolumePath(setBase, n);
      if (!File.Exists(candidate)) break;
      paths.Add(candidate);
    }
    if (paths.Count == 0) throw new FileNotFoundException($"PEA volume set '{Path.GetFileName(setBase)}' has no first volume.", VolumePath(setBase, 1));
    return paths;
  }

  /// <summary>
  /// Reads and verifies a PEA archive. A file stream named like a volume (<c>name.000001.pea</c>)
  /// is read together with the volumes beside it, starting from volume 1.
  /// </summary>
  /// <param name="stream">The archive, or one of its volumes.</param>
  /// <param name="password">The password of an encrypted stream; ignored otherwise.</param>
  /// <exception cref="InvalidDataException">Not a PEA archive, a volume is missing, or a check fails.</exception>
  /// <exception cref="ArchivePasswordException">The stream is encrypted and the password is missing or wrong.</exception>
  /// <exception cref="NotSupportedException">Newer revision, or unsupported compression.</exception>
  public static List<PeaEntry> Read(Stream stream, string? password = null) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream is FileStream { Name: { Length: > 0 } name } && VolumeSetBase(name) is not null) {
      var paths = VolumePaths(name);
      return ReadVolumes(paths.Select(p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(name), StringComparison.Ordinal) ? ReadAll(stream) : File.ReadAllBytes(p)), password);
    }
    return ReadVolumes([ReadAll(stream)], password);
  }

  private static byte[] ReadAll(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  /// <summary>
  /// Reads and verifies a PEA archive from its volumes in order. Each volume ends with its own
  /// volume check over that volume's bytes; the archive is the volumes' contents, checks removed,
  /// joined.
  /// </summary>
  /// <param name="volumes">The volumes, from volume 1 on.</param>
  /// <param name="password">The password of an encrypted stream; ignored otherwise.</param>
  /// <exception cref="InvalidDataException">Not a PEA archive, a volume is missing or damaged, or a check fails.</exception>
  /// <exception cref="ArchivePasswordException">The stream is encrypted and the password is missing or wrong.</exception>
  /// <exception cref="NotSupportedException">Newer revision, or unsupported compression.</exception>
  public static List<PeaEntry> ReadVolumes(IEnumerable<byte[]> volumes, string? password = null) {
    ArgumentNullException.ThrowIfNull(volumes);
    var joined = new MemoryStream();
    var volumeAlgo = (byte)0;
    var volumeTag = 0;
    var count = 0;
    var failedVolume = 0;
    foreach (var v in volumes) {
      if (count++ == 0) {
        if (v.Length < 10 || v[0] != Magic) throw new InvalidDataException("Not a PEA archive.");
        if (v[1] > 1 || v[1] == 1 && v[2] > 6) throw new NotSupportedException($"PEA format {v[1]}.{v[2]} is newer than this reader.");
        volumeAlgo = v[3];
        volumeTag = CheckTagSize(volumeAlgo);
      }
      var content = v.Length - volumeTag;
      if (content <= 0) throw new InvalidDataException($"PEA volume {count} is truncated.");
      if (failedVolume == 0 && volumeAlgo != 0 && !Tag(volumeAlgo, v.AsSpan(0, content)).AsSpan().SequenceEqual(v.AsSpan(content, volumeTag)))
        failedVolume = count;
      joined.Write(v, 0, content);
    }
    if (count == 0) throw new InvalidDataException("Not a PEA archive.");
    var b = joined.ToArray();
    if (b.Length >= 20) {
      // an encrypted or unknown stream is refused as such, not as the damage its unknown layout looks like
      _ = TagSize(b[18]);
      _ = CheckTagSize(b[19]);
    }
    if (failedVolume != 0)
      throw new InvalidDataException($"PEA volume check fails for volume {failedVolume} (damaged, or a volume of the set is missing).");
    return ReadJoined(b, password);
  }

  private static List<PeaEntry> ReadJoined(byte[] b, string? password) {
    if (b.Length < 20 || !b.AsSpan(10, 6).SequenceEqual((ReadOnlySpan<byte>)[0, 0, (byte)'P', (byte)'O', (byte)'D', 0]))
      throw new InvalidDataException("PEA stream header is missing.");
    var timeEncoding = b[6];
    var compression = b[16];
    var streamAlgo = b[18];
    var objAlgo = b[19];
    if (compression > 3) throw new NotSupportedException($"PEA compression {compression} is not supported.");
    int streamTag = TagSize(streamAlgo), objTag = CheckTagSize(objAlgo);
    var end = b.Length;

    var p = 20;
    var encrypted = PeaStreamCipher.Encrypts(streamAlgo);
    if (encrypted) {
      // salt-and-verifier subheader, then everything up to the tag is one authenticated ciphertext
      int subheaderSize = PeaStreamCipher.SubheaderSize(streamAlgo), tagSize = PeaStreamCipher.TagSize(streamAlgo);
      if (b.Length < 20 + subheaderSize + tagSize) throw new InvalidDataException("PEA archive is truncated.");
      var cipher = PeaStreamCipher.Open(streamAlgo, b[9], password, b.AsSpan(0, 20), b.AsSpan(20, subheaderSize));
      p += subheaderSize;
      end -= tagSize;
      cipher.Decrypt(b.AsSpan(p, end - p));
      if (!CryptographicOperations.FixedTimeEquals(cipher.Tag(), b.AsSpan(end, tagSize)))
        throw new InvalidDataException("PEA stream authentication fails (the archive is damaged).");
      if (PeaStreamCipher.IsCascade(streamAlgo)) {
        // a cascade hides the exact size behind 1-128 bytes of padding: a count byte, then that many
        if (p >= end || p + 1 + b[p] > end) throw new InvalidDataException("PEA archive is truncated.");
        p += 1 + b[p];
      }
      streamAlgo = 0; // authenticated: no stream check follows the end-of-archive trigger
      streamTag = 0;
    }
    var blockSize = 0;
    if (compression != 0) {
      blockSize = (int)ReadU32(b, ref p, end);
      if (blockSize <= 0) throw new InvalidDataException("PEA compression block size is invalid.");
    }

    var raw = new List<(string Name, byte[] RawName, uint Time, uint Attr, bool Dir, long Size, byte[] Data)>();
    while (true) {
      var objStart = p;
      var nameLength = ReadU16(b, ref p, end);
      if (nameLength == 0) {
        Need(b, p, 4, end);
        var trigger = b.AsSpan(p, 4);
        p += 4;
        if (trigger.SequenceEqual("EOA\0"u8)) break;
        throw new NotSupportedException($"PEA trigger '{Encoding.ASCII.GetString(trigger).TrimEnd('\0')}' (multiple streams) is not supported.");
      }
      Need(b, p, nameLength + 8, end);
      var name = Ansi.Value.GetString(b, p, nameLength);
      var rawName = b.AsSpan(p, nameLength).ToArray();
      p += nameLength;
      var time = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p));
      var attr = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 4));
      p += 8;
      var objCheck = new MemoryStream();
      objCheck.Write(b, objStart, p - objStart);
      var isDir = (attr & DirectoryAttribute) != 0;
      long size = 0;
      byte[] data = [];
      if (!isDir) {
        Need(b, p, 8, end);
        size = BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(p));
        if (size < 0 || size > int.MaxValue) throw new NotSupportedException("PEA object exceeds the 2 GiB in-memory limit.");
        objCheck.Write(b, p, 8);
        p += 8;
        data = new byte[size];
        if (size > 0) {
          if (compression == 0) {
            Need(b, p, (int)size, end);
            b.AsSpan(p, (int)size).CopyTo(data);
            objCheck.Write(b, p, (int)size);
            p += (int)size;
          } else {
            var blocks = (size + blockSize - 1) / blockSize;
            var o = 0;
            for (var i = 0; i < blocks; ++i) {
              var rawSize = i < blocks - 1 ? blockSize : (int)(size - (long)blockSize * (blocks - 1));
              var compSize = (int)ReadU32(b, ref p, end);
              Need(b, p, compSize, end);
              byte[] block;
              if (compSize == rawSize) block = b.AsSpan(p, compSize).ToArray(); // stored: compression did not help
              else block = ZlibStream.Decompress(b.AsSpan(p, compSize));
              if (block.Length != rawSize) throw new InvalidDataException($"PEA block of '{name}' has the wrong size.");
              p += compSize;
              objCheck.Write(Le32((uint)compSize));
              objCheck.Write(block);
              block.CopyTo(data, o);
              o += rawSize;
            }
            var last = ReadU32(b, ref p, end);
            objCheck.Write(Le32(last));
          }
        }
      }
      Need(b, p, objTag, end);
      if (objAlgo != 0 && !Tag(objAlgo, objCheck.ToArray()).AsSpan().SequenceEqual(b.AsSpan(p, objTag)))
        throw new InvalidDataException($"PEA object check fails for '{name}'.");
      p += objTag;
      raw.Add((name, rawName, time, attr, isDir, size, data));
    }
    if (p + streamTag != end) throw new InvalidDataException("PEA archive has unexpected data after the end of the stream.");
    if (streamAlgo != 0 && !Tag(streamAlgo, b.AsSpan(0, p)).AsSpan().SequenceEqual(b.AsSpan(p, streamTag)))
      throw new InvalidDataException("PEA stream check fails.");

    var root = CommonRoot(raw.Select(r => r.Name));
    return raw.Select(r => new PeaEntry {
      StoredName = r.Name,
      Path = Relative(r.Name, root),
      IsDirectory = r.Dir,
      Size = r.Size,
      Attributes = r.Attr,
      LastModified = Time(r.Time, timeEncoding),
      Data = r.Data,
      IsEncrypted = encrypted,
      RawName = r.RawName,
      RawTime = r.Time,
      Root = root,
    }).ToList();
  }

  private static void Need(byte[] b, int p, int n, int end) {
    if (n < 0 || p + n > end) throw new InvalidDataException("PEA archive is truncated.");
  }

  private static uint ReadU32(byte[] b, ref int p, int end) {
    Need(b, p, 4, end);
    var v = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p));
    p += 4;
    return v;
  }

  private static int ReadU16(byte[] b, ref int p, int end) {
    Need(b, p, 2, end);
    var v = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p));
    p += 2;
    return v;
  }

  private static string Normalize(string name) => name.Replace('\\', '/');

  /// <summary>The directory every stored name lives under; unpea extracts relative to it.</summary>
  private static string CommonRoot(IEnumerable<string> names) {
    string? root = null;
    foreach (var n in names.Select(Normalize)) {
      var trimmed = n.TrimEnd('/');
      var slash = trimmed.LastIndexOf('/');
      var dir = slash < 0 ? "" : trimmed[..(slash + 1)];
      if (root is null) { root = dir; continue; }
      while (root.Length > 0 && !dir.StartsWith(root, StringComparison.Ordinal)) {
        var cut = root.TrimEnd('/').LastIndexOf('/');
        root = cut < 0 ? "" : root[..(cut + 1)];
      }
    }
    return root ?? "";
  }

  private static string Relative(string name, string root) {
    var n = Normalize(name);
    var rel = n.StartsWith(root, StringComparison.Ordinal) ? n[root.Length..] : n;
    rel = rel.Trim('/');
    if (rel.Length >= 2 && rel[1] == ':') rel = rel[2..].TrimStart('/');
    var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s is not "." and not "..");
    var joined = string.Join('/', parts);
    return joined.Length == 0 ? "_" : joined;
  }

  private static DateTime? Time(uint value, byte encoding) {
    try {
      return encoding switch {
        0x30 => DateTimeOffset.FromUnixTimeSeconds(value).UtcDateTime,
        0x10 => DosTime(value),
        _ => null,
      };
    } catch (ArgumentOutOfRangeException) {
      return null;
    }
  }

  private static DateTime? DosTime(uint v) {
    var date = (int)(v >> 16);
    var time = (int)(v & 0xFFFF);
    if (date == 0) return null;
    return new DateTime(1980 + (date >> 9), (date >> 5) & 15, date & 31, time >> 11, (time >> 5) & 63, (time & 31) * 2, DateTimeKind.Local);
  }

  // ── writing ─────────────────────────────────────────────────────────

  /// <summary>An object to write.</summary>
  public readonly record struct Input(string Path, byte[]? Data, DateTime LastModified);

  /// <summary>
  /// Writes a PEA 1.6 archive the way PeaZip does on Unix: names are stored as absolute paths
  /// under <c>/</c>, times as Unix seconds, zlib blocks of 1 MiB (a block that does not shrink is
  /// stored), with the given object, stream and volume checks.
  /// </summary>
  /// <param name="output">Receives the archive, or its first volume.</param>
  /// <param name="inputs">The objects to store.</param>
  /// <param name="compression">0 = stored, 1/2/3 = zlib level 3/6/9.</param>
  /// <param name="objectCheck">The check after every object.</param>
  /// <param name="streamCheck">The check over the whole stream.</param>
  /// <param name="volumeCheck">The check at the end of every volume.</param>
  /// <param name="volumeSize">The size of every volume but the last, check included; 0 writes one volume.
  /// Volumes are named <c>name.000001.pea</c>, <c>name.000002.pea</c>, … (see <see cref="VolumePath"/>).</param>
  /// <param name="openVolume">Opens volume <c>n</c> (2, 3, …) for writing; the stream is disposed after it.</param>
  /// <param name="password">The password, required exactly when <paramref name="streamCheck"/> encrypts
  /// (<see cref="Check.Aes256Eax"/> is what PeaZip picks by default).</param>
  /// <param name="cascadeStrength">For the triple cascades: pea's key-derivation level 0-7 (the digit
  /// after the name, as in <c>SRIATS7</c>), stored in archive header byte 9; 0 for everything else.</param>
  public static void Write(Stream output, IEnumerable<Input> inputs, int compression = 2,
      Check objectCheck = Check.Crc32, Check streamCheck = Check.Sha256, Check volumeCheck = Check.Crc32,
      long volumeSize = 0, Func<int, Stream>? openVolume = null, string? password = null, int cascadeStrength = 0) {
    ArgumentNullException.ThrowIfNull(output);
    if (compression is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(compression));
    ArgumentOutOfRangeException.ThrowIfNegative(volumeSize);
    ArgumentOutOfRangeException.ThrowIfNegative(cascadeStrength);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(cascadeStrength, 7);
    if (cascadeStrength != 0 && !PeaStreamCipher.IsCascade((byte)streamCheck))
      throw new ArgumentException("A cascade strength needs a triple-cascade stream encryption.", nameof(cascadeStrength));
    if (PeaStreamCipher.Encrypts((byte)objectCheck) || PeaStreamCipher.Encrypts((byte)volumeCheck))
      throw new ArgumentException("Only the stream check encrypts; object and volume checks are hashes or checksums.");
    var encrypted = PeaStreamCipher.Encrypts((byte)streamCheck);
    if (encrypted != !string.IsNullOrEmpty(password))
      throw new ArgumentException(encrypted
        ? $"PEA stream encryption {streamCheck} needs a password."
        : $"A password needs an encrypting stream check ({Check.Aes256Eax} and the like), not {streamCheck}.", nameof(password));
    // Directories first are not required; keep the caller's order, adding parent directories once.
    var seenDirs = new HashSet<string>(StringComparer.Ordinal);
    var objects = new List<RawObject>();
    foreach (var input in inputs) {
      var path = input.Path.Replace('\\', '/').Trim('/');
      if (path.Length == 0) continue;
      var parts = path.Split('/');
      for (var i = 1; i < parts.Length; ++i) {
        var dir = string.Join('/', parts[..i]);
        if (seenDirs.Add(dir)) objects.Add(NewObject("/", dir, null, input.LastModified, UnixTime));
      }
      if (input.Data is null) { if (seenDirs.Add(path)) objects.Add(NewObject("/", path, null, input.LastModified, UnixTime)); }
      else objects.Add(NewObject("/", path, input.Data, input.LastModified, UnixTime));
    }
    // OS (Linux), time encoding (Unix), ANSI, CPU: what PeaZip on Linux writes
    WriteObjects(output, [0x33, UnixTime, 0x01, 0x21], objects, compression, objectCheck, streamCheck, volumeCheck,
      volumeSize, openVolume, password, cascadeStrength);
  }

  /// <summary>
  /// Edits a single-file PEA archive in place: drops the <paramref name="remove"/> paths (a folder
  /// with everything beneath it), then adds <paramref name="add"/>, replacing files of the same
  /// path. Everything else stays as it was: the archive's compression, object, stream and volume
  /// checks, encryption kind and strength (under a fresh salt), system bytes, and every kept
  /// object's stored name, time and attributes. New objects go under the archive's common folder,
  /// their times in its encoding.
  /// </summary>
  /// <param name="archive">The archive; read, then rewritten from the start.</param>
  /// <param name="add">Objects to add or replace (null data: a folder).</param>
  /// <param name="remove">Paths to remove, as <see cref="PeaEntry.Path"/> gives them.</param>
  /// <param name="password">The password of an encrypted archive.</param>
  /// <exception cref="NotSupportedException">A volume set: its volumes would all change size.</exception>
  /// <exception cref="ArchivePasswordException">The archive is encrypted and the password is missing or wrong.</exception>
  public static void Modify(Stream archive, IEnumerable<Input> add, IEnumerable<string> remove, string? password = null) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(add);
    ArgumentNullException.ThrowIfNull(remove);
    if (archive is FileStream { Name: { Length: > 0 } name } && VolumeSetBase(name) is not null)
      throw new NotSupportedException("A PEA volume set is not edited in place: every volume after the change would move.");
    var bytes = ReadAll(archive);
    var entries = ReadVolumes([bytes], password); // verifies every check (and the password) first
    var compression = bytes[16];
    var volumeCheck = (Check)bytes[3];
    var streamCheck = (Check)bytes[18];
    var objectCheck = (Check)bytes[19];
    var timeEncoding = bytes[6];
    var root = entries.Count > 0 ? entries[0].Root : "/";

    static string Clean(string path) => path.Replace('\\', '/').Trim('/');
    var adds = add.Select(i => i with { Path = Clean(i.Path) }).Where(i => i.Path.Length > 0).ToList();
    var gone = remove.Select(Clean).Where(p => p.Length > 0).ToList();
    var replaced = adds.Where(i => i.Data != null).Select(i => i.Path).ToHashSet(StringComparer.Ordinal);
    bool Dropped(PeaEntry e) => gone.Any(r => e.Path == r || e.Path.StartsWith(r + "/", StringComparison.Ordinal))
      || !e.IsDirectory && replaced.Contains(e.Path);

    var objects = new List<RawObject>();
    var dirs = new HashSet<string>(StringComparer.Ordinal);
    foreach (var e in entries.Where(e => !Dropped(e))) {
      objects.Add(new RawObject(e.RawName, e.RawTime, e.Attributes, e.IsDirectory ? null : e.Data));
      if (e.IsDirectory) dirs.Add(e.Path);
    }
    foreach (var input in adds) {
      var parts = input.Path.Split('/');
      for (var i = 1; i < parts.Length; ++i) {
        var dir = string.Join('/', parts[..i]);
        if (dirs.Add(dir)) objects.Add(NewObject(root, dir, null, input.LastModified, timeEncoding));
      }
      if (input.Data is null) {
        if (dirs.Add(input.Path)) objects.Add(NewObject(root, input.Path, null, input.LastModified, timeEncoding));
      } else objects.Add(NewObject(root, input.Path, input.Data, input.LastModified, timeEncoding));
    }

    using var rebuilt = new MemoryStream();
    WriteObjects(rebuilt, bytes.AsSpan(5, 4), objects, compression, objectCheck, streamCheck, volumeCheck,
      0, null, PeaStreamCipher.Encrypts((byte)streamCheck) ? password : null, PeaStreamCipher.IsCascade((byte)streamCheck) ? bytes[9] : 0);
    archive.Position = 0;
    archive.SetLength(0);
    rebuilt.Position = 0;
    rebuilt.CopyTo(archive);
    archive.Flush();
  }

  private const byte UnixTime = 0x30, DosTimeEncoding = 0x10;
  private const uint FileAttribute = 0x20, DirectoryAttributes = 0x30;

  /// <summary>One object as stored: its qualified name bytes, time and attributes as the archive encodes them, and data (null for a directory).</summary>
  private readonly record struct RawObject(byte[] Name, uint Time, uint Attributes, byte[]? Data);

  /// <summary>A new object stored as <paramref name="root"/> + <paramref name="path"/>, its time in the archive's encoding.</summary>
  private static RawObject NewObject(string root, string path, byte[]? data, DateTime time, byte timeEncoding) {
    var name = Ansi.Value.GetBytes(root + path + (data is null ? "/" : ""));
    if (name.Length > ushort.MaxValue) throw new NotSupportedException($"PEA names are at most 65535 bytes ('{path}').");
    return new RawObject(name, EncodeTime(time, timeEncoding), data is null ? DirectoryAttributes : FileAttribute, data);
  }

  private static uint EncodeTime(DateTime time, byte encoding) {
    if (encoding == DosTimeEncoding) {
      var t = time.Kind == DateTimeKind.Utc ? time.ToLocalTime() : time;
      if (t.Year < 1980) return 0;
      return (uint)(((t.Year - 1980) << 25) | (t.Month << 21) | (t.Day << 16) | (t.Hour << 11) | (t.Minute << 5) | (t.Second / 2));
    }
    return (uint)Math.Clamp(new DateTimeOffset(time.ToUniversalTime()).ToUnixTimeSeconds(), 0, uint.MaxValue);
  }

  /// <summary>Writes the archive: headers (with <paramref name="system"/> as header bytes 5-8), objects, trigger, check or tag, volumes.</summary>
  private static void WriteObjects(Stream output, ReadOnlySpan<byte> system, List<RawObject> objects, int compression,
      Check objectCheck, Check streamCheck, Check volumeCheck, long volumeSize, Func<int, Stream>? openVolume, string? password, int cascadeStrength) {
    var encrypted = PeaStreamCipher.Encrypts((byte)streamCheck);
    // archive header: magic, version 1, revision 6, volume check, ECC 0, OS, time encoding, character set, CPU, iterations
    byte[] headers = [
      Magic, 1, 6, (byte)volumeCheck, 0, system[0], system[1], system[2], system[3], (byte)cascadeStrength,
      0, 0, (byte)'P', (byte)'O', (byte)'D', 0, (byte)compression, 0, (byte)streamCheck, (byte)objectCheck,
    ];
    var ms = new MemoryStream(); // everything after the headers (and the crypto subheader)
    if (PeaStreamCipher.IsCascade((byte)streamCheck)) {
      // 1-128 bytes of padding, as pea masks a cascade archive's exact size: a count byte, then that many random bytes
      var padding = RandomNumberGenerator.GetBytes(128);
      padding[0] >>= 1;
      ms.Write(padding, 0, padding[0] + 1);
    }
    if (compression != 0) ms.Write(Le32(WriteBlockSize));
    var level = compression switch { 1 => DeflateCompressionLevel.Fast, 3 => DeflateCompressionLevel.Maximum, _ => DeflateCompressionLevel.Default };

    foreach (var obj in objects) {
      var isDir = obj.Data is null;
      var name = obj.Name;
      var header = new MemoryStream();
      header.Write(Le32((uint)name.Length).AsSpan(0, 2));
      header.Write(name);
      header.Write(Le32(obj.Time));
      header.Write(Le32(obj.Attributes));
      var objCheck = new MemoryStream();
      if (!isDir) header.Write(Le64((ulong)obj.Data!.Length));
      var headerBytes = header.ToArray();
      ms.Write(headerBytes);
      objCheck.Write(headerBytes);
      if (!isDir && obj.Data!.Length > 0) {
        if (compression == 0) {
          ms.Write(obj.Data);
          objCheck.Write(obj.Data);
        } else {
          var lastSize = 0;
          for (var offset = 0; offset < obj.Data.Length; offset += WriteBlockSize) {
            var block = obj.Data.AsSpan(offset, Math.Min(WriteBlockSize, obj.Data.Length - offset));
            var packed = ZlibStream.Compress(block, level);
            var stored = packed.Length >= block.Length;
            var payload = stored ? block.ToArray() : packed;
            ms.Write(Le32((uint)payload.Length));
            ms.Write(payload);
            objCheck.Write(Le32((uint)payload.Length));
            objCheck.Write(block);
            lastSize = block.Length;
          }
          ms.Write(Le32((uint)lastSize));
          objCheck.Write(Le32((uint)lastSize));
        }
      }
      ms.Write(Tag((byte)objectCheck, objCheck.ToArray()));
    }
    ms.Write([0, 0, (byte)'E', (byte)'O', (byte)'A', 0]);
    var body = ms.GetBuffer().AsSpan(0, (int)ms.Length);
    var archive = new MemoryStream();
    archive.Write(headers);
    if (encrypted) {
      // the headers are bound into the key; the stream check becomes the cipher's tag
      var cipher = PeaStreamCipher.Create((byte)streamCheck, cascadeStrength, password!, headers, out var subheader);
      archive.Write(subheader);
      cipher.Encrypt(body);
      archive.Write(body);
      archive.Write(cipher.Tag());
    } else {
      archive.Write(body);
      archive.Write(Tag((byte)streamCheck, archive.GetBuffer().AsSpan(0, (int)archive.Length)));
    }
    WriteVolumes(output, archive.GetBuffer().AsSpan(0, (int)archive.Length), volumeCheck, volumeSize, openVolume);
  }

  /// <summary>
  /// Cuts the archive into volumes of <paramref name="volumeSize"/> bytes, each ending with the
  /// volume check over its own bytes (the way pea fills a volume to its size, check included, before
  /// it opens the next). Volume 1 goes to <paramref name="output"/>, the others to
  /// <paramref name="openVolume"/>; a volume size of 0 writes one volume.
  /// </summary>
  private static void WriteVolumes(Stream output, ReadOnlySpan<byte> archive, Check volumeCheck, long volumeSize, Func<int, Stream>? openVolume) {
    var tagSize = TagSize((byte)volumeCheck);
    if (volumeSize <= 0) {
      output.Write(archive);
      output.Write(Tag((byte)volumeCheck, archive));
      return;
    }
    // pea raises a volume size below the check size + 10 to that, so volume 1 holds the whole archive header.
    var payload = (int)Math.Min(Math.Max(volumeSize, tagSize + 10) - tagSize, int.MaxValue);
    var number = 1;
    for (var offset = 0; offset < archive.Length; offset += payload, ++number) {
      var chunk = archive.Slice(offset, Math.Min(payload, archive.Length - offset));
      if (number == 1) {
        output.Write(chunk);
        output.Write(Tag((byte)volumeCheck, chunk));
        continue;
      }
      if (openVolume is null) throw new ArgumentException("A PEA archive larger than one volume needs a way to open its other volumes.", nameof(openVolume));
      using var volume = openVolume(number);
      volume.Write(chunk);
      volume.Write(Tag((byte)volumeCheck, chunk));
    }
  }
}
