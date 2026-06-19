#pragma warning disable CS1591
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace FileFormat.EaseUs;

/// <summary>
/// Writer for the EaseUS Todo Backup container (<c>.pbd</c>). Emits a
/// container whose framing matches the on-disk shape recovered from the
/// EaseUS image engine (<c>ImgFile.dll</c>) and pinned in
/// <see cref="EaseUsContainerIndex"/>:
///
/// <list type="bullet">
///   <item><description>
///     A <see cref="EaseUsContainerIndex.HeaderBlockSize"/> (0x4E8 =
///     1256 byte) header block at file offset 0 carrying the
///     <c>{"IMGF", header_size=0x000004E8, version=0x00010001}</c> words
///     at offsets 0 / 4 / 8 (the constants the engine's header check
///     enforces), followed by the embedded UTF-16LE source-path string.
///   </description></item>
///   <item><description>
///     A body region consisting of one zlib (RFC 1950 + DEFLATE +
///     Adler-32) substream per stored file — the same
///     <c>0x78 {0x01|0x9C|0xDA}</c> framing the body uses, so our own
///     <see cref="EaseUsZlibScanner"/> trial-inflate recovers every
///     payload byte-identical.
///   </description></item>
///   <item><description>
///     A <see cref="EaseUsContainerIndex.TrailerBlockSize"/> (0xC0 = 192
///     byte) trailer block carrying the
///     <c>{version=0x00010001, size=0x000000C0, "IMGF"}</c> words at
///     trailer offsets 0xB4 / 0xB8 / 0xBC, followed by a 0xFF padding
///     run to the file's nominal end.
///   </description></item>
/// </list>
///
/// <para>
/// <b>A self-describing manifest</b> is stored as the first body
/// substream so the writer's output can be losslessly re-walked back into
/// the original file tree: a small UTF-8 table of
/// <c>relpath\tsize\tchunk_index</c> rows. Each subsequent substream is
/// the zlib-compressed bytes of one file in manifest order (empty files
/// emit a zero-length payload, still a real zlib stream). This makes the
/// writer's output round-trip byte-identical through our own reader and
/// gives the container a deterministic body layout.
/// </para>
///
/// <para>
/// <b>Validation status: writer implemented; pending vendor-restore
/// validation.</b> The container framing reproduces the header / trailer
/// constants the engine checks, and the body uses the same zlib-substream
/// envelope. Whether the EaseUS engine restores this exact byte layout
/// (its block-allocation INDX table and per-partition VOLM records map
/// logical sectors back to compressed chunks, and that mapping is not
/// reproduced here) can only be confirmed by feeding the output to the
/// vendor application. Until that restore-test passes the descriptor does
/// NOT advertise <c>CanCreate</c>.
/// </para>
/// </summary>
public static class EaseUsWriter {

  /// <summary>Zlib compression level used for every body substream (maps to the 0x78 0xDA FCHECK byte).</summary>
  public const CompressionLevel BodyCompressionLevel = CompressionLevel.Optimal;

  /// <summary>Number of 0xFF padding bytes appended after the trailer block (matches the observed tail convention).</summary>
  public const int DefaultTrailingFfPadding = 16;

  /// <summary>One file to store inside the container.</summary>
  public sealed record FileEntry(string RelativePath, byte[] Content);

  /// <summary>
  /// Builds a complete <c>.pbd</c> container holding <paramref name="files"/>.
  /// </summary>
  /// <param name="files">Files to store, in the order they should appear in the body.</param>
  /// <param name="sourcePath">Embedded UTF-16LE source-path string written into the header block.</param>
  /// <param name="trailingFfPadding">Number of trailing 0xFF padding bytes (default <see cref="DefaultTrailingFfPadding"/>).</param>
  public static byte[] Build(
    IReadOnlyList<FileEntry> files,
    string sourcePath = "",
    int trailingFfPadding = DefaultTrailingFfPadding
  ) {
    ArgumentNullException.ThrowIfNull(files);
    if (trailingFfPadding < 0) throw new ArgumentOutOfRangeException(nameof(trailingFfPadding));

    using var ms = new MemoryStream();

    // 1) Header block (0x4E8 bytes): magic + size + version + UTF-16LE source path.
    var header = new byte[EaseUsContainerIndex.HeaderBlockSize];
    "IMGF"u8.CopyTo(header.AsSpan(EaseUsContainerIndex.HeaderMagicFieldOffset, 4));
    BinaryPrimitives.WriteUInt32LittleEndian(
      header.AsSpan(EaseUsContainerIndex.HeaderSizeFieldOffset, 4),
      EaseUsContainerIndex.HeaderSizeFieldExpectedValue);
    BinaryPrimitives.WriteUInt32LittleEndian(
      header.AsSpan(EaseUsContainerIndex.HeaderVersionFieldOffset, 4),
      EaseUsContainerIndex.HeaderVersionFieldExpectedValue);
    if (!string.IsNullOrEmpty(sourcePath)) {
      var pathBytes = Encoding.Unicode.GetBytes(sourcePath);
      var room = EaseUsContainerIndex.HeaderBlockSize - EaseUsReader.HeaderSize - 2;
      if (pathBytes.Length <= room)
        pathBytes.CopyTo(header.AsSpan(EaseUsReader.HeaderSize));
    }
    ms.Write(header);

    // 2) Body: a manifest substream first, then one zlib substream per
    //    file. The manifest records every file's path, decompressed size,
    //    the compressed byte offset of its substream RELATIVE to the first
    //    payload substream, and its compressed length. Using a relative
    //    offset removes any circular dependency on the manifest's own
    //    length: the reader resolves the absolute base as
    //    (header_block_size + manifest_compressed_length), then adds each
    //    relative offset. Recovery is deterministic and does not rely on
    //    the reader's linear 0x78-scan heuristic (which a coincidental
    //    0x78 byte inside a compressed payload could fool).

    var payloadStreams = new byte[files.Count][];
    for (var i = 0; i < files.Count; i++)
      payloadStreams[i] = ZlibCompress(files[i].Content);

    var manifest = new StringBuilder();
    var relCursor = 0L;
    for (var i = 0; i < files.Count; i++) {
      manifest.Append(files[i].RelativePath.Replace('\\', '/'))
        .Append('\t').Append(files[i].Content.Length)
        .Append('\t').Append(relCursor)
        .Append('\t').Append(payloadStreams[i].Length)
        .Append('\n');
      relCursor += payloadStreams[i].Length;
    }
    var manifestStream = ZlibCompress(Encoding.UTF8.GetBytes(manifest.ToString()));

    ms.Write(manifestStream);
    foreach (var s in payloadStreams)
      ms.Write(s);

    // 3) Trailer block (0xC0 bytes): version + size + magic at the tail.
    var trailer = new byte[EaseUsContainerIndex.TrailerBlockSize];
    BinaryPrimitives.WriteUInt32LittleEndian(
      trailer.AsSpan(EaseUsContainerIndex.TrailerVersionFieldOffset, 4),
      EaseUsContainerIndex.TrailerVersionFieldExpectedValue);
    BinaryPrimitives.WriteUInt32LittleEndian(
      trailer.AsSpan(EaseUsContainerIndex.TrailerSizeFieldOffset, 4),
      EaseUsContainerIndex.TrailerSizeFieldExpectedValue);
    "IMGF"u8.CopyTo(trailer.AsSpan(EaseUsContainerIndex.TrailerMagicFieldOffset, 4));
    ms.Write(trailer);

    // 4) 0xFF padding tail.
    if (trailingFfPadding > 0) {
      var pad = new byte[trailingFfPadding];
      Array.Fill(pad, (byte)0xFF);
      ms.Write(pad);
    }

    return ms.ToArray();
  }

  /// <summary>
  /// Builds a container from every file under <paramref name="rootDirectory"/>
  /// (recursively), storing each with a forward-slash relative path.
  /// </summary>
  public static byte[] BuildFromDirectory(string rootDirectory, int trailingFfPadding = DefaultTrailingFfPadding) {
    ArgumentNullException.ThrowIfNull(rootDirectory);
    var root = Path.GetFullPath(rootDirectory);
    var files = new List<FileEntry>();
    foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                                  .OrderBy(p => p, StringComparer.Ordinal)) {
      var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
      files.Add(new FileEntry(rel, File.ReadAllBytes(path)));
    }
    return Build(files, sourcePath: root, trailingFfPadding: trailingFfPadding);
  }

  /// <summary>
  /// Canonical RFC-1950 zlib stream for a zero-length payload: header
  /// <c>0x78 0x9C</c>, a single empty stored/final DEFLATE block
  /// (<c>0x03 0x00</c>), and the Adler-32 of the empty input
  /// (<c>0x00000001</c>). .NET's <see cref="ZLibStream"/> emits nothing at
  /// all for a zero-byte write, so we substitute this fixed envelope to
  /// keep every body substream a real, scannable zlib stream.
  /// </summary>
  private static readonly byte[] EmptyZlibStream = [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01];

  private static byte[] ZlibCompress(byte[] payload) {
    if (payload.Length == 0) return (byte[])EmptyZlibStream.Clone();
    using var ms = new MemoryStream();
    using (var z = new ZLibStream(ms, BodyCompressionLevel, leaveOpen: true))
      z.Write(payload, 0, payload.Length);
    return ms.ToArray();
  }
}
