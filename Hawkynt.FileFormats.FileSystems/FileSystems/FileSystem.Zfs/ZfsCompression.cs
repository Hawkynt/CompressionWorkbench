using System.Buffers.Binary;
using Compression.Core.Dictionary.Lz4;
using Compression.Core.Dictionary.Lzjb;
using Compression.Core.Streams;
using FileFormat.Zlib;
using Compression.Core.Transforms;
using FileFormat.Zstd;

namespace FileSystem.Zfs;

/// <summary>
/// Turns a block's physical bytes into its logical bytes for every compression id ZFS
/// writes (table in <c>docs/ZFS-ON-DISK.md</c>), through this repository's own codecs.
/// </summary>
internal static class ZfsCompression {
  public static string Name(byte id) => id switch {
    ZfsConstants.CompressOff => "off",
    ZfsConstants.CompressLzjb => "lzjb",
    ZfsConstants.CompressEmpty => "empty",
    >= ZfsConstants.CompressGzip1 and <= ZfsConstants.CompressGzip9 => $"gzip-{id - ZfsConstants.CompressGzip1 + 1}",
    ZfsConstants.CompressZle => "zle",
    ZfsConstants.CompressLz4 => "lz4",
    ZfsConstants.CompressZstd => "zstd",
    _ => $"compression {id}",
  };

  /// <summary>
  /// The compression id a <c>compression</c> property name stands for on a pool of version 28:
  /// <c>off</c>, <c>on</c> (lzjb there), <c>lzjb</c>, <c>gzip</c> (level 6), <c>gzip-1</c>…<c>gzip-9</c>, <c>zle</c>.
  /// </summary>
  /// <exception cref="ArgumentException">An unknown name.</exception>
  /// <exception cref="NotSupportedException">lz4 or zstd, which need feature flags.</exception>
  public static byte ParseForVersion28(string? name) => Parse(name, 28);

  /// <summary>
  /// The compression id a <c>compression</c> property name stands for on a pool of
  /// <paramref name="version"/>; with feature flags (5000) <c>lz4</c> is available and <c>on</c> means lz4.
  /// </summary>
  /// <exception cref="ArgumentException">An unknown name.</exception>
  /// <exception cref="NotSupportedException">An algorithm the pool version cannot hold.</exception>
  public static byte Parse(string? name, ulong version) => ParseWithLevel(name, version).Algorithm;

  /// <summary>As <see cref="Parse"/>, with the zstd level (<c>zstd</c> = 3, <c>zstd-1</c>…<c>zstd-19</c>; 0 for the others).</summary>
  public static (byte Algorithm, int Level) ParseWithLevel(string? name, ulong version) {
    var n = (name ?? "off").Trim().ToLowerInvariant();
    if (n == "zstd" || n.StartsWith("zstd-", StringComparison.Ordinal)) {
      if (version < 5000)
        throw new NotSupportedException($"ZFS: compression={n} needs a feature-flag pool; version 28 pools take off, lzjb, gzip-1..9 and zle.");
      if (n == "zstd") return (ZfsConstants.CompressZstd, 3);
      if (int.TryParse(n.AsSpan(5), out var z) && z is >= 1 and <= 19) return (ZfsConstants.CompressZstd, z);
      throw new ArgumentException($"ZFS: '{name}' is not a zstd level written here (zstd-1 to zstd-19).", nameof(name));
    }
    return (ParseClassic(n, name, version), 0);
  }

  private static byte ParseClassic(string n, string? name, ulong version) {
    var features = version >= 5000;
    return n switch {
      "off" or "" => ZfsConstants.CompressOff,
      "on" => features ? ZfsConstants.CompressLz4 : ZfsConstants.CompressLzjb,
      "lzjb" => ZfsConstants.CompressLzjb,
      "gzip" => ZfsConstants.CompressGzip1 + 5,
      "zle" => ZfsConstants.CompressZle,
      "lz4" when features => ZfsConstants.CompressLz4,
      "lz4" => throw new NotSupportedException($"ZFS: compression={n} needs a feature-flag pool; version 28 pools take off, lzjb, gzip-1..9 and zle."),
      _ when n.StartsWith("gzip-", StringComparison.Ordinal) && int.TryParse(n.AsSpan(5), out var level) && level is >= 1 and <= 9
        => (byte)(ZfsConstants.CompressGzip1 + level - 1),
      _ => throw new ArgumentException($"ZFS: '{name}' is not a compression ZFS knows.", nameof(name)),
    };
  }

  /// <summary>
  /// Compresses one logical block the way a ZFS write does (<c>docs/ZFS-ON-DISK.md</c>,
  /// "Compressed blocks"): a block of zeros becomes a hole (null), a result that does not save an
  /// eighth or does not shrink once rounded up to the sector stays uncompressed, and the result
  /// is zero-padded to whole <c>2^ashift</c> sectors.
  /// </summary>
  public static (byte Algorithm, byte[]? Physical) Compress(byte algorithm, byte[] logical, int ashift) {
    var (used, physical, _) = CompressBlock(algorithm, logical, ashift);
    return (used, physical);
  }

  /// <summary>
  /// As <see cref="Compress"/>, and also the compressed stream before padding whenever it saves an
  /// eighth (null otherwise, and for a hole) — what an embedded block pointer carries when it fits,
  /// even if rounding it up to a sector would not have saved a sector.
  /// </summary>
  public static (byte Algorithm, byte[]? Physical, byte[]? Stream) CompressBlock(byte algorithm, byte[] logical, int ashift, int zstdLevel = 3) {
    if (algorithm == ZfsConstants.CompressOff) return (ZfsConstants.CompressOff, logical, null);
    if (!logical.AsSpan().ContainsAnyExcept((byte)0)) return (algorithm, null, null);
    byte[] packed = algorithm switch {
      ZfsConstants.CompressLzjb => LzjbCompressor.Compress(logical),
      ZfsConstants.CompressZle => ZleEncoding.Encode(logical),
      ZfsConstants.CompressLz4 => Lz4(logical),
      ZfsConstants.CompressZstd => ZstdBlock(logical, zstdLevel),
      >= ZfsConstants.CompressGzip1 and <= ZfsConstants.CompressGzip9 =>
        ZlibStream.Compress(logical, (Compression.Core.Deflate.DeflateCompressionLevel)(algorithm - ZfsConstants.CompressGzip1 + 1)),
      _ => throw new NotSupportedException($"ZFS: blocks are not written with compression {Name(algorithm)}."),
    };
    if (packed.Length > logical.Length - (logical.Length >> 3)) return (ZfsConstants.CompressOff, logical, null);
    var sector = 1 << ashift;
    var rounded = (packed.Length + sector - 1) / sector * sector;
    // the stream is still offered: a write embeds it before rounding to the sector is considered
    if (rounded >= logical.Length) return (ZfsConstants.CompressOff, logical, packed);
    var physical = new byte[rounded];
    packed.CopyTo(physical, 0);
    return (algorithm, physical, packed);
  }

  /// <summary>ZFS's LZ4 block: the stream length as a big-endian <c>u32</c>, then one LZ4 block.</summary>
  private static byte[] Lz4(byte[] logical) {
    var block = Lz4BlockCompressor.Compress(logical);
    var packed = new byte[4 + block.Length];
    BinaryPrimitives.WriteUInt32BigEndian(packed, (uint)block.Length);
    block.CopyTo(packed, 4);
    return packed;
  }

  /// <summary>
  /// ZFS's zstd block: the frame length and then the zstd version (low 24 bits) and level (high 8
  /// bits) as big-endian <c>u32</c>s, then the frame without its 4-byte magic.
  /// </summary>
  private static byte[] ZstdBlock(byte[] logical, int level) {
    using var output = new MemoryStream();
    using (var zstd = new ZstdStream(output, CompressionStreamMode.Compress, Math.Clamp(level, 1, 9), leaveOpen: true))
      zstd.Write(logical);
    var frame = output.ToArray();
    if (frame.Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(frame) != 0xFD2FB528)
      throw new InvalidOperationException("ZFS: the zstd encoder produced no frame.");
    var packed = new byte[8 + frame.Length - 4];
    BinaryPrimitives.WriteUInt32BigEndian(packed, (uint)(frame.Length - 4));
    BinaryPrimitives.WriteUInt32BigEndian(packed.AsSpan(4), (uint)ZstdVersion | (uint)level << 24);
    frame.AsSpan(4).CopyTo(packed.AsSpan(8));
    return packed;
  }

  /// <summary>The zstd version number the block header records (1.4.5, the library OpenZFS 2.x carries).</summary>
  private const int ZstdVersion = 10405;

  public static byte[] Decompress(byte id, byte[] physical, int logicalSize) {
    switch (id) {
      case ZfsConstants.CompressOff:
        return physical.Length == logicalSize ? physical : Fit(physical, logicalSize);
      case ZfsConstants.CompressEmpty:
        return new byte[logicalSize];
      case ZfsConstants.CompressLzjb:
        return LzjbDecompressor.Decompress(physical, logicalSize);
      case ZfsConstants.CompressZle: {
        var output = new byte[logicalSize];
        ZleEncoding.Decode(physical, output);
        return output;
      }
      case >= ZfsConstants.CompressGzip1 and <= ZfsConstants.CompressGzip9:
        return Gzip(physical, logicalSize);
      case ZfsConstants.CompressLz4: {
        var length = LengthPrefix(physical, 4, "LZ4");
        var output = new byte[logicalSize];
        var written = Lz4BlockDecompressor.Decompress(physical.AsSpan(4, length), output);
        if (written != logicalSize)
          throw new InvalidDataException($"ZFS: LZ4 block decoded to {written} of {logicalSize} bytes.");
        return output;
      }
      case ZfsConstants.CompressZstd:
        return Zstd(physical.AsSpan(8, LengthPrefix(physical, 8, "zstd")), logicalSize);
      default:
        throw new NotSupportedException($"ZFS: compression id {id} is not a ZFS algorithm.");
    }
  }

  private static int LengthPrefix(byte[] physical, int header, string what) {
    if (physical.Length < header)
      throw new InvalidDataException($"ZFS: {what} block shorter than its header.");
    var length = BinaryPrimitives.ReadUInt32BigEndian(physical);
    if (length > (uint)(physical.Length - header))
      throw new InvalidDataException($"ZFS: {what} block declares {length} bytes, holds {physical.Length - header}.");
    return (int)length;
  }

  private static byte[] Gzip(byte[] physical, int logicalSize) {
    using var input = new MemoryStream(physical, writable: false);
    using var output = new MemoryStream(logicalSize);
    // The zlib stream ends at its Adler-32; the zeros after it pad the block to a sector.
    ZlibStream.Decompress(input, output);
    return Fit(output.GetBuffer().AsSpan(0, (int)output.Length).ToArray(), logicalSize, "gzip");
  }

  private static byte[] Zstd(ReadOnlySpan<byte> frame, int logicalSize) {
    // OpenZFS writes magicless frames: the 4-byte magic is implied, so it is put back.
    var full = new byte[4 + frame.Length];
    BinaryPrimitives.WriteUInt32LittleEndian(full, 0xFD2FB528);
    frame.CopyTo(full.AsSpan(4));
    using var input = new MemoryStream(full, writable: false);
    using var zstd = new ZstdStream(input, CompressionStreamMode.Decompress);
    var output = new byte[logicalSize];
    var total = 0;
    while (total < logicalSize) {
      var n = zstd.Read(output, total, logicalSize - total);
      if (n == 0) break;
      total += n;
    }
    if (total != logicalSize)
      throw new InvalidDataException($"ZFS: zstd frame decoded to {total} of {logicalSize} bytes.");
    return output;
  }

  private static byte[] Fit(byte[] data, int logicalSize, string? what = null) {
    if (what != null && data.Length != logicalSize)
      throw new InvalidDataException($"ZFS: {what} block decoded to {data.Length} of {logicalSize} bytes.");
    if (data.Length == logicalSize) return data;
    var fitted = new byte[logicalSize];
    data.AsSpan(0, Math.Min(data.Length, logicalSize)).CopyTo(fitted);
    return fitted;
  }
}
