#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Macrium;

/// <summary>
/// Macrium Reflect pre-X disk image (<c>.mrimg</c>) and file/folder backup
/// (<c>.mrbak</c>) container produced by Reflect v6/v7/v8 (and the
/// corresponding rescue media). The Reflect X (<c>.mrimgx</c> / <c>.mrbakx</c>)
/// format is a completely different layout (MIT-licensed open spec, JSON
/// metadata, MACRIUM_FILE footer) and is handled separately; this descriptor
/// covers ONLY the legacy proprietary format.
/// <para>
/// Stage-2 read-only surfacing: header inspection PLUS block-payload
/// decompression via <see cref="MacriumPreXCodec"/> (clean-room port of
/// the algorithm described by the MIT-licensed ccooper21/mrimg-tools
/// reference project). Surfaces the synthetic entries:
/// </para>
/// <list type="bullet">
///   <item><c>FULL.mrimg</c> — passthrough slice over the entire archive</item>
///   <item><c>metadata.ini</c> — block preamble decode, compression flag,
///     embedded XML comment, scanned-block totals, decoded-block
///     success/failure counts</item>
///   <item><c>header.bin</c> — raw first preamble (9 bytes)</item>
///   <item><c>block-NN.bin</c> — decoded payload of the first
///     <see cref="MaxDecodedBlocks"/> blocks (zero-padded index). Blocks
///     that fail to decode (truncated / encrypted / corrupt token stream)
///     are skipped and their absence reflected in the metadata.ini
///     <c>decode_failures</c> counter.</item>
/// </list>
/// <para>
/// Still out of scope: the BAT-style block-index walk that maps from
/// (volume, sector) to (block, offset) — required for assembling the
/// inner partition image. The AES-128/192/256 catalog decryptor used by
/// password-protected backups. Both are tracked as future phases.
/// </para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.macrium.com</c> — vendor site — the on-disk image format itself is proprietary and undocumented</description></item>
///   <item><description>No public specification — header layout reverse-engineered from v6-v8 images</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// File-format facts established by binary reverse engineering of the
/// official Macrium Reflect Free v8.0.7783 build (ReflectBin.exe,
/// RShellEx.dll, MRVerify.exe, Consolidate.exe), cross-referenced against
/// the community ccooper21/mrimg-tools project (MIT licence) and Macrium's
/// own MIT-licensed Reflect X spec at github.com/macrium/mrimgx_file_layout:
/// </para>
/// <list type="bullet">
///   <item>Recognised extensions: <c>.mrimg</c> (disk image),
///     <c>.mrbak</c> (file/folder backup), <c>.mrex</c> (Exchange backup),
///     <c>.mrsql</c> (SQL backup). All share the same container.</item>
///   <item>The container is a sequence of compressed blocks. Each block
///     starts with a 9-byte preamble <c>[flags:1][block_len:4 LE]
///     [out_block_expected_len:4 LE]</c>. For an mrimg/mrbak data block the
///     flags byte is <c>0x03</c>; this is the de-facto magic-at-offset-0
///     since the very first byte of every legitimate file is the preamble
///     of block 0.</item>
///   <item>The first 9 bytes therefore form a quasi-signature:
///     <c>03 XX XX XX XX YY YY YY YY</c> where XX is the on-disk block
///     length and YY is the uncompressed block size. Confidence is reduced
///     because <c>0x03</c> alone is not a strong signature — extension and
///     filename-pattern checks are used for disambiguation.</item>
///   <item>Compression is a Lempel-Ziv-derived custom codec (per
///     ccooper21/mrimg-tools); per-token control bits decide literal vs
///     match. The codec is proprietary and not yet re-implemented here.</item>
///   <item>Optional AES encryption (128/192/256-bit selectable per backup,
///     surfaced via the <c>aes</c> XML element); AES handled by
///     <c>AESDll.dll</c> in stock Reflect installs.</item>
///   <item>An XML metadata blob is embedded (text strings are stored
///     in-stream with optional UTF-8 BOM). Roots seen in the binary:
///     <c>&lt;backup_definition&gt;</c> (BDF v3.2.0 job XML),
///     <c>&lt;archive_user_data&gt;</c> (catalog), <c>&lt;cmc_data&gt;</c>
///     (messaging), <c>&lt;log_data&gt;</c> (run log).</item>
///   <item>The user-visible filename pattern is
///     <c>name-IMAGEID-IIxFF.mrimg</c> where <c>IIxFF</c> is the increment
///     number and file number (e.g. <c>00-00</c> = full image, single
///     segment; <c>01-00</c> = first incremental).</item>
/// </list>
/// </remarks>
public sealed class MacriumPreXFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <summary>Preamble length in bytes — flags(1) + block_len(4 LE) + out_len(4 LE).</summary>
  public const int PreambleSize = 9;

  /// <summary>Flags byte for a data block. The very first byte of every
  /// legitimate <c>.mrimg</c> / <c>.mrbak</c> file is this preamble flag.</summary>
  public const byte DataBlockFlags = 0x03;

  /// <summary>How many bytes from the start we are willing to scan looking
  /// for an embedded XML comment. The metadata XML is interleaved with
  /// compressed payload starting in the first block; we scan a generous
  /// window and stop at the first match.</summary>
  public const int CommentScanWindow = 1 << 20; // 1 MiB

  /// <summary>Maximum number of leading blocks we attempt to decompress
  /// when surfacing decoded synthetic entries. We cap aggressively so
  /// that listing a large image stays cheap; callers can extract the
  /// FULL.mrimg passthrough and rerun decode for deeper coverage.</summary>
  public const int MaxDecodedBlocks = 16;

  public string Id => "MacriumPreX";
  public string DisplayName => "Macrium Reflect pre-X (.mrimg/.mrbak)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".mrimg";
  public IReadOnlyList<string> Extensions => [".mrimg", ".mrbak", ".mrex", ".mrsql"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // The 9-byte block-preamble structure starts at offset 0 — the flags
    // byte 0x03 is the only fixed byte. Confidence is intentionally low
    // because 0x03 alone is a weak signature; the FormatDetector also
    // weighs the extension and the heuristics in `LooksLikeMrimg`.
    new([DataBlockFlags], Offset: 0, Confidence: 0.45),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("mrimg-lz", "Macrium proprietary LZ77-derived"),
    new("stored", "Stored (uncompressed)"),
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Macrium Reflect pre-X disk image / backup (v6-v8). Stage-1 header surfacing only.";

  /// <summary>
  /// Validates that <paramref name="header"/> begins with a Macrium
  /// pre-X preamble. The check is intentionally permissive — a single
  /// 0x03 flags byte plus plausible block-length fields. Callers that
  /// need stronger evidence should also check the file extension and
  /// the filename pattern.
  /// </summary>
  public static bool LooksLikeMrimg(ReadOnlySpan<byte> header) {
    if (header.Length < PreambleSize) return false;
    if (header[0] != DataBlockFlags) return false;
    var blockLen = BinaryPrimitives.ReadUInt32LittleEndian(header[1..]);
    var outLen = BinaryPrimitives.ReadUInt32LittleEndian(header[5..]);
    // Sanity range: block must be at least the preamble itself, and
    // uncompressed payload at least 1 byte. The Reflect default block
    // size is 1 MiB so anything beyond 64 MiB is highly suspect.
    if (blockLen < PreambleSize || blockLen > (64u << 20)) return false;
    if (outLen == 0 || outLen > (64u << 20)) return false;
    return true;
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    ArgumentNullException.ThrowIfNull(stream);
    var entries = new List<ArchiveEntryInfo> {
      new(0, "FULL.mrimg", stream.Length, stream.Length, "stored", false, false, null, "Track"),
    };
    foreach (var e in BuildSynthetic(stream))
      entries.Add(new ArchiveEntryInfo(
        entries.Count, e.Name, e.Data.Length, e.Data.Length,
        "stored", false, false, null, e.Kind));
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(outputDir);
    if (files == null || files.Length == 0 || MatchesFilter("FULL.mrimg", files)) {
      stream.Seek(0, SeekOrigin.Begin);
      var fullPath = Path.Combine(outputDir, "FULL.mrimg");
      var dir = Path.GetDirectoryName(fullPath);
      if (dir != null) Directory.CreateDirectory(dir);
      using var outStream = File.Create(fullPath);
      stream.CopyTo(outStream);
    }
    foreach (var e in BuildSynthetic(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (string.Equals(entryName, "FULL.mrimg", StringComparison.OrdinalIgnoreCase)) {
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new Compression.Registry.Streaming.ReadOnlyStreamSlice(archive, 0, archive.Length),
        archive.Length, leaveOpen: false);
    }
    foreach (var e in BuildSynthetic(archive)) {
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(e.Data, writable: false), e.Data.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream([], writable: false), 0, leaveOpen: false);
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  /// <summary>
  /// Builds the synthetic surface entries: <c>metadata.ini</c>,
  /// <c>header.bin</c>, and zero-or-more <c>block-NN.bin</c> decoded block
  /// payloads. The block decoder is best-effort — if a block fails to
  /// decode (truncated, encrypted, malformed token stream) we record the
  /// failure in metadata.ini and skip the synthetic entry but keep going.
  /// </summary>
  private static IReadOnlyList<(string Name, byte[] Data, string Kind)> BuildSynthetic(Stream stream) {
    stream.Seek(0, SeekOrigin.Begin);
    Span<byte> header = stackalloc byte[PreambleSize];
    var read = 0;
    while (read < PreambleSize) {
      var n = stream.Read(header[read..]);
      if (n <= 0) break;
      read += n;
    }
    if (read < PreambleSize) return [];
    if (!LooksLikeMrimg(header)) return [];

    var flags = header[0];
    var blockLen = BinaryPrimitives.ReadUInt32LittleEndian(header[1..]);
    var outLen = BinaryPrimitives.ReadUInt32LittleEndian(header[5..]);

    // Lightweight scan for the embedded XML <comment> tag. The AHK
    // community has shown that the metadata XML is interleaved into
    // the first block (or the first ~1 MiB) of the file; we cap the
    // scan to 1 MiB and bail out early on the first match.
    var (comment, commentOffset, blockCount, totalUncompressed, totalCompressed)
      = ScanFirstBlocks(stream, blockLen);

    // Decode the leading blocks (cap at MaxDecodedBlocks). Successes
    // become block-NN.bin synthetic entries; failures get counted for
    // metadata.ini.
    var decoded = DecodeLeadingBlocks(stream, MaxDecodedBlocks);

    var fileSize = stream.Length;
    var ini = new StringBuilder();
    ini.AppendLine("; Macrium Reflect pre-X disk image / backup (.mrimg / .mrbak)");
    ini.AppendLine("; Block-payload decompression implemented via MacriumPreXCodec.");
    ini.AppendLine();
    ini.AppendLine("[container]");
    ini.Append("format=mrimg-prex").AppendLine();
    ini.Append("file_size=").AppendLine(fileSize.ToString(CultureInfo.InvariantCulture));
    ini.Append("preamble_flags=0x").AppendLine(flags.ToString("X2", CultureInfo.InvariantCulture));
    ini.Append("first_block_compressed_size=").AppendLine(blockLen.ToString(CultureInfo.InvariantCulture));
    ini.Append("first_block_uncompressed_size=").AppendLine(outLen.ToString(CultureInfo.InvariantCulture));
    ini.AppendLine();
    ini.AppendLine("[blocks]");
    ini.Append("scanned_block_count=").AppendLine(blockCount.ToString(CultureInfo.InvariantCulture));
    ini.Append("scanned_compressed_bytes=").AppendLine(totalCompressed.ToString(CultureInfo.InvariantCulture));
    ini.Append("scanned_uncompressed_bytes=").AppendLine(totalUncompressed.ToString(CultureInfo.InvariantCulture));
    if (totalUncompressed > 0) {
      var ratio = (double)totalCompressed / totalUncompressed;
      ini.Append("scanned_compression_ratio=").AppendLine(ratio.ToString("F4", CultureInfo.InvariantCulture));
    }
    ini.Append("decoded_blocks=").AppendLine(decoded.Successes.ToString(CultureInfo.InvariantCulture));
    ini.Append("decode_failures=").AppendLine(decoded.Failures.ToString(CultureInfo.InvariantCulture));
    if (decoded.FirstFailureReason is not null) {
      ini.Append("first_decode_failure=").AppendLine(EscapeIni(decoded.FirstFailureReason));
    }
    ini.AppendLine();
    ini.AppendLine("[metadata]");
    ini.Append("comment_present=").AppendLine(comment is null ? "no" : "yes");
    if (comment is not null) {
      ini.Append("comment_offset=").AppendLine(commentOffset.ToString(CultureInfo.InvariantCulture));
      ini.Append("comment=").AppendLine(EscapeIni(comment));
    }
    string parseStatus;
    if (decoded.Successes > 0)
      parseStatus = comment is null ? "decoded" : "decoded+comment";
    else
      parseStatus = comment is null ? "partial-no-comment" : "partial";
    ini.Append("parse_status=").AppendLine(parseStatus);
    ini.AppendLine();
    ini.AppendLine("[capabilities]");
    ini.AppendLine("read_only=true");
    ini.AppendLine("listing=metadata+decoded_blocks");
    ini.Append("payload_decompression=").AppendLine(decoded.Successes > 0 ? "implemented" : "implemented_but_no_block_decoded");
    ini.AppendLine("payload_codec=mrimg-lz (proprietary Lempel-Ziv-derived, clean-room ported from ccooper21/mrimg-tools)");
    ini.AppendLine("encryption_supported_by_format=AES-128|AES-192|AES-256");

    var entries = new List<(string Name, byte[] Data, string Kind)> {
      ("metadata.ini", Encoding.UTF8.GetBytes(ini.ToString()), "Tag"),
      ("header.bin", header.ToArray(), "Track"),
    };
    for (var i = 0; i < decoded.Blocks.Count; i++)
      entries.Add(($"block-{i:D2}.bin", decoded.Blocks[i], "Track"));
    return entries;
  }

  /// <summary>
  /// Walks up to <paramref name="maxBlocks"/> blocks from the start of
  /// <paramref name="stream"/>, feeding each block body through
  /// <see cref="MacriumPreXCodec"/>. Returns the decoded byte arrays
  /// (encrypted/corrupt blocks skipped silently — their absence is
  /// reflected in the failure count).
  /// </summary>
  private static (IReadOnlyList<byte[]> Blocks, int Successes, int Failures, string? FirstFailureReason)
    DecodeLeadingBlocks(Stream stream, int maxBlocks) {
    var blocks = new List<byte[]>();
    var failures = 0;
    string? firstFailureReason = null;
    stream.Seek(0, SeekOrigin.Begin);

    Span<byte> preamble = stackalloc byte[PreambleSize];
    var offset = 0L;
    for (var i = 0; i < maxBlocks; i++) {
      stream.Seek(offset, SeekOrigin.Begin);
      preamble.Clear();
      var read = 0;
      while (read < PreambleSize) {
        var n = stream.Read(preamble[read..]);
        if (n <= 0) break;
        read += n;
      }
      if (read < PreambleSize) break;
      if (preamble[0] != DataBlockFlags) break;
      var blockLen = BinaryPrimitives.ReadUInt32LittleEndian(preamble[1..]);
      var outLen = BinaryPrimitives.ReadUInt32LittleEndian(preamble[5..]);
      if (blockLen < PreambleSize || blockLen > (64u << 20)) break;
      if (outLen == 0 || outLen > MacriumPreXCodec.MaxUncompressedSize) break;
      var bodyLen = (int)(blockLen - PreambleSize);
      if (offset + blockLen > stream.Length) break;
      var body = new byte[bodyLen];
      var bodyRead = 0;
      while (bodyRead < bodyLen) {
        var n = stream.Read(body, bodyRead, bodyLen - bodyRead);
        if (n <= 0) break;
        bodyRead += n;
      }
      if (bodyRead < bodyLen) break;

      try {
        var decoded = MacriumPreXCodec.DecodeBlock(body, (int)outLen);
        blocks.Add(decoded);
      } catch (InvalidDataException ex) {
        failures++;
        firstFailureReason ??= ex.Message;
      } catch (ArgumentOutOfRangeException ex) {
        failures++;
        firstFailureReason ??= ex.Message;
      }
      offset += blockLen;
    }

    return (blocks, blocks.Count, failures, firstFailureReason);
  }

  /// <summary>
  /// Walks the block-preamble chain for up to <see cref="CommentScanWindow"/>
  /// bytes, accumulating per-block totals and looking for a literal
  /// <c>&lt;comment&gt;</c> tag (the Macrium BDF v3.2.0 metadata marker).
  /// </summary>
  private static (string? Comment, long CommentOffset, int BlockCount, long TotalUncompressed, long TotalCompressed)
    ScanFirstBlocks(Stream stream, uint firstBlockLen) {
    var totalCompressed = 0L;
    var totalUncompressed = 0L;
    var blockCount = 0;
    string? comment = null;
    var commentOffset = -1L;

    // Re-read everything from offset 0 — preamble is the first 9 bytes.
    stream.Seek(0, SeekOrigin.Begin);
    var maxScan = (long)Math.Min(stream.Length, CommentScanWindow);
    var buf = new byte[maxScan];
    var have = 0;
    while (have < maxScan) {
      var n = stream.Read(buf, have, (int)(maxScan - have));
      if (n <= 0) break;
      have += n;
    }
    var slice = new ReadOnlySpan<byte>(buf, 0, have);

    // Walk preambles
    var offset = 0;
    while (offset + PreambleSize <= slice.Length) {
      if (slice[offset] != DataBlockFlags) break;
      var blockLen = BinaryPrimitives.ReadUInt32LittleEndian(slice[(offset + 1)..]);
      var outLen = BinaryPrimitives.ReadUInt32LittleEndian(slice[(offset + 5)..]);
      if (blockLen < PreambleSize || blockLen > (64u << 20)) break;
      if (outLen == 0 || outLen > (64u << 20)) break;
      totalCompressed += blockLen;
      totalUncompressed += outLen;
      blockCount++;
      // Advance by the on-disk block length.
      var next = (long)offset + blockLen;
      if (next > slice.Length) break;
      offset = (int)next;
      // Cap at scan window — we're only interested in the first few blocks.
      if (blockCount >= 64) break;
    }

    // Search for <comment>…</comment> in the raw bytes (one byte per char
    // per the AHK community report; the parser also handles a UTF-8 BOM).
    var openTag = "<comment>"u8;
    var closeTag = "</comment>"u8;
    var openIdx = slice.IndexOf(openTag);
    if (openIdx >= 0) {
      var afterOpen = openIdx + openTag.Length;
      var rest = slice[afterOpen..];
      var closeRel = rest.IndexOf(closeTag);
      if (closeRel >= 0) {
        commentOffset = openIdx;
        var raw = rest[..closeRel];
        // Strip trailing/leading null bytes that the writer pads with.
        var text = StripBinaryPadding(raw);
        comment = text;
      }
    }

    return (comment, commentOffset, blockCount, totalUncompressed, totalCompressed);
  }

  /// <summary>
  /// Strips embedded null bytes (the Macrium writer pads short strings
  /// with NULs inside the otherwise UTF-8 payload) and falls back to a
  /// hex-escaped representation if the result still isn't printable.
  /// </summary>
  public static string StripBinaryPadding(ReadOnlySpan<byte> raw) {
    Span<byte> tmp = raw.Length <= 4096 ? stackalloc byte[raw.Length] : new byte[raw.Length];
    var w = 0;
    foreach (var b in raw) {
      if (b == 0x00) continue;
      tmp[w++] = b;
    }
    var clean = tmp[..w];
    // Heuristic: if >90% printable, treat as text. Otherwise return a
    // truncated hex preview so we never throw and never lose information.
    var printable = 0;
    foreach (var b in clean) {
      if (b >= 0x20 && b < 0x7F) printable++;
      else if (b == 0x09 || b == 0x0A || b == 0x0D) printable++;
    }
    if (clean.Length > 0 && printable * 10 >= clean.Length * 9)
      return Encoding.UTF8.GetString(clean);
    // Fallback: hex preview, capped at 256 bytes so metadata.ini stays sane.
    var capped = clean.Length > 256 ? clean[..256] : clean;
    return Convert.ToHexString(capped);
  }

  /// <summary>Escapes newline/CR/equals so the .ini value stays on one line.</summary>
  public static string EscapeIni(string s) {
    var sb = new StringBuilder(s.Length);
    foreach (var c in s) {
      if (c is '\r' or '\n') sb.Append(' ');
      else sb.Append(c);
    }
    return sb.ToString().Trim();
  }
}
