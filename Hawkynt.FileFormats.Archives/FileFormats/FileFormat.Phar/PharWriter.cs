#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Deflate;
using Compression.Core.Streams;
using FileFormat.Bzip2;
using Crc32 = Compression.Core.Checksums.Crc32;

namespace FileFormat.Phar;

/// <summary>
/// A member to write: a file, or a directory when <see cref="Name"/> ends in <c>/</c>. <see cref="Packed"/>,
/// when given, is the member's already-compressed bytes (an edit carries untouched members verbatim);
/// otherwise <see cref="Data"/> is compressed as <see cref="Compression"/> says.
/// </summary>
public sealed record PharWriteEntry(string Name, byte[] Data, uint Timestamp, uint Permissions,
  PharCompression Compression = PharCompression.None, byte[]? Metadata = null, byte[]? Packed = null);

/// <summary>
/// Writes a native-layout PHP archive the way PHP's <c>Phar</c> class flushes one: the stub cut after
/// <c>__HALT_COMPILER();</c> and closed with <c>" ?>\r\n"</c>, manifest API 1.1.1, members in the given
/// order, and a digest signature trailer.
/// </summary>
public static class PharWriter {

  /// <summary>The stub PHP writes for <c>setStub("&lt;?php __HALT_COMPILER();")</c>.</summary>
  public static ReadOnlySpan<byte> MinimalStub => "<?php __HALT_COMPILER(); ?>\r\n"u8;

  /// <summary>Default permission bits of a file member (PHP's 0666).</summary>
  public const uint DefaultFilePermissions = 0x1B6;

  /// <summary>Permission bits PHP gives an empty directory (0777).</summary>
  public const uint DefaultDirectoryPermissions = 0x1FF;

  private const ushort ApiVersion = 0x1110;

  public static void Write(Stream output, IReadOnlyList<PharWriteEntry> entries, byte[]? stub = null, string? alias = null,
      byte[]? metadata = null, PharSignatureType signature = PharSignatureType.Sha256, bool keepStubVerbatim = false) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(entries);
    if (signature is not (PharSignatureType.Md5 or PharSignatureType.Sha1 or PharSignatureType.Sha256 or PharSignatureType.Sha512))
      throw new NotSupportedException($"PHAR: cannot write a {signature} signature (it needs the publisher's private key).");

    using var image = new MemoryStream();
    // An edit keeps the stub byte for byte (it was valid as read); a new archive gets PHP's normalisation.
    image.Write(keepStubVerbatim && stub != null ? stub : NormalizeStub(stub));

    var stored = new List<byte[]>(entries.Count);
    using var manifest = new MemoryStream();
    WriteUInt32(manifest, checked((uint)entries.Count));
    manifest.WriteByte((byte)(ApiVersion >> 8));
    manifest.WriteByte((byte)(ApiVersion & 0xF0));
    WriteUInt32(manifest, PharReader.HeaderSignature);
    WriteBlock(manifest, Encoding.UTF8.GetBytes(alias ?? ""));
    WriteBlock(manifest, metadata ?? []);
    foreach (var e in entries) {
      if (string.IsNullOrEmpty(e.Name) || e.Name.StartsWith('/') || e.Name.Contains('\\') || e.Name.Contains('\0'))
        throw new ArgumentException($"PHAR: invalid member name '{e.Name}'.", nameof(entries));
      var isDirectory = e.Name.EndsWith('/');
      var data = isDirectory ? [] : e.Data;
      var compression = isDirectory ? PharCompression.None : e.Compression;
      var packed = e.Packed ?? compression switch {
        PharCompression.Gzip => DeflateCompressor.Compress(data),
        PharCompression.Bzip2 => CompressBzip2(data),
        _ => data,
      };
      stored.Add(packed);
      WriteBlock(manifest, Encoding.UTF8.GetBytes(e.Name));
      WriteUInt32(manifest, checked((uint)data.Length));
      WriteUInt32(manifest, e.Timestamp);
      WriteUInt32(manifest, checked((uint)packed.Length));
      WriteUInt32(manifest, isDirectory ? 0 : Crc32.Compute(data));
      WriteUInt32(manifest, (e.Permissions & PharReader.PermissionMask) | compression switch {
        PharCompression.Gzip => PharReader.EntryGzip,
        PharCompression.Bzip2 => PharReader.EntryBzip2,
        _ => 0u,
      });
      WriteBlock(manifest, e.Metadata ?? []);
    }

    WriteUInt32(image, checked((uint)manifest.Length));
    manifest.Position = 0;
    manifest.CopyTo(image);
    foreach (var s in stored) image.Write(s);

    var digest = PharReader.ComputeDigest(signature, image.GetBuffer().AsSpan(0, (int)image.Length));
    image.Write(digest);
    WriteUInt32(image, (uint)signature);
    image.Write(PharReader.SignatureMagic);

    image.Position = 0;
    image.CopyTo(output);
  }

  /// <summary>
  /// Cuts a stub after its <c>__HALT_COMPILER();</c> and closes it with <c>" ?>\r\n"</c>, as PHP's
  /// <c>Phar::setStub</c> does; a stub without the token is refused.
  /// </summary>
  public static byte[] NormalizeStub(byte[]? stub) {
    if (stub == null) return MinimalStub.ToArray();
    var text = Encoding.Latin1.GetString(stub);
    var halt = text.IndexOf("__HALT_COMPILER();", StringComparison.OrdinalIgnoreCase);
    if (halt < 0)
      throw new ArgumentException("PHAR: the stub must contain __HALT_COMPILER();.", nameof(stub));
    var end = halt + PharReader.HaltToken.Length;
    return [.. stub.AsSpan(0, end), .. " ?>\r\n"u8];
  }

  private static byte[] CompressBzip2(byte[] data) {
    using var output = new MemoryStream();
    using (var bz = new Bzip2Stream(output, CompressionStreamMode.Compress, leaveOpen: true))
      bz.Write(data);
    return output.ToArray();
  }

  private static void WriteBlock(Stream s, byte[] block) {
    WriteUInt32(s, checked((uint)block.Length));
    s.Write(block);
  }

  private static void WriteUInt32(Stream s, uint v) {
    Span<byte> b = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(b, v);
    s.Write(b);
  }
}
