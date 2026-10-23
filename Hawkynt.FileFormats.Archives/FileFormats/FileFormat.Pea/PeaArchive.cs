using System.Buffers.Binary;
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
}

/// <summary>
/// PEA (PeaZip's own format, version 1 revision 6): a 10-byte archive header, one stream with a
/// 10-byte header naming the compression (stored or zlib at level 3/6/9 in independent 1 MiB
/// blocks) and three integrity checks — per object, for the stream and for the volume — then
/// objects (name, time, attributes, size, data) and an end-of-archive trigger. This covers
/// single-volume archives with any of the hash or checksum controls; encrypted streams (EAX,
/// HMAC, Twofish, Serpent and their cascades) are refused.
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
  }

  private static int TagSize(byte algo) => algo switch {
    0x00 => 0,
    0x01 or 0x02 => 4,
    0x03 => 8,
    0x10 => 16,
    0x11 or 0x12 => 20,
    0x13 or 0x16 or 0x18 => 32,
    0x14 or 0x15 or 0x17 or 0x19 => 64,
    >= 0x30 => throw new NotSupportedException($"PEA stream control algorithm 0x{algo:X2} is encryption, which is not supported."),
    _ => throw new InvalidDataException($"PEA check algorithm 0x{algo:X2} is unknown."),
  };

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

  /// <summary>Reads and verifies a single-volume PEA archive.</summary>
  /// <exception cref="InvalidDataException">Not a PEA archive, or a check fails.</exception>
  /// <exception cref="NotSupportedException">Encrypted stream, newer revision, or unsupported compression.</exception>
  public static List<PeaEntry> Read(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var b = ms.ToArray();
    if (b.Length < 20 || b[0] != Magic) throw new InvalidDataException("Not a PEA archive.");
    if (b[1] > 1 || b[1] == 1 && b[2] > 6) throw new NotSupportedException($"PEA format {b[1]}.{b[2]} is newer than this reader.");
    var volumeAlgo = b[3];
    var timeEncoding = b[6];
    if (!b.AsSpan(10, 6).SequenceEqual((ReadOnlySpan<byte>)[0, 0, (byte)'P', (byte)'O', (byte)'D', 0]))
      throw new InvalidDataException("PEA stream header is missing.");
    var compression = b[16];
    var streamAlgo = b[18];
    var objAlgo = b[19];
    if (compression > 3) throw new NotSupportedException($"PEA compression {compression} is not supported.");
    int volumeTag = TagSize(volumeAlgo), streamTag = TagSize(streamAlgo), objTag = TagSize(objAlgo);

    // A single-volume archive ends with the volume check; everything before it is checked by it.
    var end = b.Length - volumeTag;
    if (end < 20) throw new InvalidDataException("PEA archive is truncated.");
    if (volumeAlgo != 0 && !Tag(volumeAlgo, b.AsSpan(0, end)).AsSpan().SequenceEqual(b.AsSpan(end, volumeTag)))
      throw new InvalidDataException("PEA volume check fails (damaged, or not the only volume).");

    var p = 20;
    var blockSize = 0;
    if (compression != 0) {
      blockSize = (int)ReadU32(b, ref p, end);
      if (blockSize <= 0) throw new InvalidDataException("PEA compression block size is invalid.");
    }

    var raw = new List<(string Name, uint Time, uint Attr, bool Dir, long Size, byte[] Data)>();
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
      raw.Add((name, time, attr, isDir, size, data));
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
  /// Writes a single-volume PEA 1.6 archive the way PeaZip does on Unix: names are stored as
  /// absolute paths under <c>/</c>, times as Unix seconds, zlib blocks of 1 MiB (a block that
  /// does not shrink is stored), with the given object, stream and volume checks.
  /// </summary>
  /// <param name="compression">0 = stored, 1/2/3 = zlib level 3/6/9.</param>
  public static void Write(Stream output, IEnumerable<Input> inputs, int compression = 2,
      Check objectCheck = Check.Crc32, Check streamCheck = Check.Sha256, Check volumeCheck = Check.Crc32) {
    ArgumentNullException.ThrowIfNull(output);
    if (compression is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(compression));
    var ms = new MemoryStream();
    // archive header: magic, version 1, revision 6, volume check, ECC 0, OS (Linux), time encoding (Unix), ANSI, CPU, iterations
    ms.Write([Magic, 1, 6, (byte)volumeCheck, 0, 0x33, 0x30, 0x01, 0x21, 0x00]);
    ms.Write([0, 0, (byte)'P', (byte)'O', (byte)'D', 0, (byte)compression, 0, (byte)streamCheck, (byte)objectCheck]);
    if (compression != 0) ms.Write(Le32(WriteBlockSize));
    var level = compression switch { 1 => DeflateCompressionLevel.Fast, 3 => DeflateCompressionLevel.Maximum, _ => DeflateCompressionLevel.Default };

    // Directories first are not required; keep the caller's order, adding parent directories once.
    var seenDirs = new HashSet<string>(StringComparer.Ordinal);
    var objects = new List<Input>();
    foreach (var input in inputs) {
      var path = input.Path.Replace('\\', '/').Trim('/');
      if (path.Length == 0) continue;
      var parts = path.Split('/');
      for (var i = 1; i < parts.Length; ++i) {
        var dir = string.Join('/', parts[..i]);
        if (seenDirs.Add(dir)) objects.Add(new Input(dir, null, input.LastModified));
      }
      if (input.Data is null) { if (seenDirs.Add(path)) objects.Add(input with { Path = path }); }
      else objects.Add(input with { Path = path });
    }

    foreach (var obj in objects) {
      var isDir = obj.Data is null;
      var name = Ansi.Value.GetBytes("/" + obj.Path + (isDir ? "/" : ""));
      if (name.Length > ushort.MaxValue) throw new NotSupportedException($"PEA names are at most 65535 bytes ('{obj.Path}').");
      var header = new MemoryStream();
      header.Write(Le32((uint)name.Length).AsSpan(0, 2));
      header.Write(name);
      header.Write(Le32((uint)Math.Clamp(new DateTimeOffset(obj.LastModified.ToUniversalTime()).ToUnixTimeSeconds(), 0, uint.MaxValue)));
      header.Write(Le32(isDir ? 0x30u : 0x20u));
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
    ms.Write(Tag((byte)streamCheck, ms.GetBuffer().AsSpan(0, (int)ms.Length)));
    ms.Write(Tag((byte)volumeCheck, ms.GetBuffer().AsSpan(0, (int)ms.Length)));
    output.Write(ms.GetBuffer(), 0, (int)ms.Length);
  }
}
