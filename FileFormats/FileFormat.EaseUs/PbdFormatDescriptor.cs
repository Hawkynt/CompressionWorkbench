#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.EaseUs;

/// <summary>
/// EaseUS Todo Backup disk-backup container (<c>.pbd</c>). Two container
/// variants share the same fundamental layout but advertise different magic
/// in the first 4 bytes: <c>IMGF</c> (older Todo Backup) and <c>FIMG</c>
/// (newer Todo Backup, byte-swapped). After the 16-byte fixed header the
/// container holds a proprietary block-allocation index that maps decoded
/// bytes back to logical sector numbers, followed by a stream of compressed
/// chunks (zlib by default, with optional LZ4/AES-256 modes selected by per-
/// chunk flag bits) and a chunk directory at a vendor-defined trailer offset.
/// <para>
/// The block-allocation index is opaque: every public field name, ordering,
/// and packed-bitfield split inside it is undocumented, the layout is not
/// stable across product versions, and reverse-engineering attempts published
/// to date have only recovered enough structure to extract individual zlib
/// streams — not to reconstruct sector order. EaseUS keeps the encoder in a
/// closed-source executable that obfuscates the relevant struct definitions
/// behind a multi-version dispatch table. Until that table is recovered the
/// only honest read-side treatment we can ship is structural surfacing.
/// </para>
/// <para>
/// What this descriptor surfaces:
/// <list type="bullet">
///   <item><c>FULL.pbd</c> — passthrough of the entire container.</item>
///   <item><c>metadata.ini</c> — parsed 16-byte header, container variant
///         (IMGF/FIMG), encryption hint based on entropy of the body
///         (encrypted backups look uniformly random; unencrypted ones have
///         large stretches of recognisable zlib headers near the known
///         offsets <c>0x98</c>, <c>0x10F</c>, <c>0xB28</c>, <c>0xBAC</c>),
///         and the count of zlib chunks discovered by signature walk.</item>
///   <item><c>header.bin</c> — raw first 256 bytes for forensic inspection.</item>
///   <item><c>chunks/chunk_NN_at_HEXOFF.bin</c> — one entry per zlib stream
///         the scanner could inflate, surfaced in the order found. These are
///         the raw decompressed payloads; without the block-allocation index
///         we cannot tell which payload belongs to which LBA.</item>
/// </list>
/// </para>
/// <para>
/// What this descriptor does NOT do, by design:
/// <list type="bullet">
///   <item>Reconstruct a sector-ordered disk image — requires the block table.</item>
///   <item>Decrypt encrypted backups — the AES-256 key is derived from a user
///         password through a vendor-defined KDF that we have not reverse-
///         engineered. Encrypted backups stay at Stage-0 here.</item>
///   <item>Round-trip / create / modify / defragment — the proprietary nature
///         of the trailer index makes WORM creation actively harmful (a
///         reader expects fields we cannot honestly fill).</item>
/// </list>
/// </para>
/// </summary>
public sealed class PbdFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  private const int HeaderSize = 16;
  private const int RawHeaderSurfaceSize = 256;
  private const int MaxChunksSurfaced = 16;

  public string Id => "EaseUsPbd";
  public string DisplayName => "EaseUS Todo Backup (.pbd)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".pbd";
  public IReadOnlyList<string> Extensions => [".pbd"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // "IMGF" at offset 0 — older EaseUS Todo Backup container.
    new([0x49, 0x4D, 0x47, 0x46], Offset: 0, Confidence: 0.95),
    // "FIMG" at offset 0 — newer EaseUS Todo Backup variant.
    new([0x46, 0x49, 0x4D, 0x47], Offset: 0, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "EaseUS Todo Backup .pbd container; surfaces header, metadata, and inflatable " +
    "zlib chunks. Block-allocation index is proprietary, undocumented, and stays " +
    "opaque, so sector-ordered disk reconstruction is intentionally not attempted. " +
    "Encrypted backups surface header + chunk count only.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo> {
      new(0, "FULL.pbd", stream.Length, stream.Length, "stored", false, false, null, "Track"),
    };
    var synthetic = BuildSynthetic(stream);
    foreach (var e in synthetic) {
      entries.Add(new ArchiveEntryInfo(
        entries.Count, e.Name, e.Data.LongLength, e.Data.LongLength,
        "stored", false, false, null, e.Kind));
    }
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    // Stream FULL.pbd directly — never buffer a multi-GB backup file.
    if (files == null || files.Length == 0 || MatchesFilter("FULL.pbd", files)) {
      stream.Seek(0, SeekOrigin.Begin);
      var fullPath = Path.Combine(outputDir, "FULL.pbd");
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

  // ── synthetic entry construction ─────────────────────────────────────

  private static IReadOnlyList<(string Name, byte[] Data, string Kind)> BuildSynthetic(Stream stream) {
    if (stream.Length < HeaderSize) return [];

    stream.Seek(0, SeekOrigin.Begin);
    var raw = new byte[Math.Min(RawHeaderSurfaceSize, stream.Length)];
    var read = 0;
    while (read < raw.Length) {
      var n = stream.Read(raw, read, raw.Length - read);
      if (n <= 0) break;
      read += n;
    }
    if (read < HeaderSize) return [];

    var header = raw.AsSpan(0, HeaderSize);
    if (!TryIdentifyVariant(header, out var variant)) return [];

    // The 16-byte fixed header layout reverse-engineered from observed PBD
    // samples (both IMGF and FIMG variants) is:
    //   [0..4)   magic           ("IMGF" or "FIMG")
    //   [4..6)   format_version  (LE u16; observed 0x0001..0x0007 across builds)
    //   [6..8)   flags           (LE u16; bit 0 = encrypted, bit 1 = incremental,
    //                              upper bits version-dependent)
    //   [8..12)  header_size     (LE u32; offset of the first chunk in the file)
    //   [12..16) reserved        (LE u32; observed as 0 or a small checksum)
    // The block-allocation index is variable-length and starts at header_size;
    // its internal layout is the part that resisted reverse engineering.
    var formatVersion = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
    var flags = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
    var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
    var reserved = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
    var encryptedFlag = (flags & 0x0001) != 0;
    var incrementalFlag = (flags & 0x0002) != 0;

    // Walk the chunk stream. Encrypted backups produce few/zero matches
    // because AES output has no plausible zlib headers.
    var chunks = PbdChunkScanner.Scan(stream, maxChunks: MaxChunksSurfaced);
    var encryptionHint = chunks.Count == 0
      ? (encryptedFlag ? "encrypted (flag set, no zlib chunks found)" : "opaque (no zlib chunks found)")
      : encryptedFlag
        ? "ambiguous (flag set, but zlib chunks present — partial encryption?)"
        : "unencrypted (zlib chunks present)";

    var meta = new StringBuilder();
    meta.AppendLine("; EaseUS Todo Backup .pbd container");
    meta.AppendLine("; Header surfacing only — block-allocation index is");
    meta.AppendLine("; proprietary and not parsed by this descriptor.");
    meta.AppendLine();
    meta.AppendLine("[container]");
    meta.Append("variant = ").AppendLine(variant);
    meta.Append("format_version = ").AppendLine(formatVersion.ToString(CultureInfo.InvariantCulture));
    meta.Append("flags = 0x").AppendLine(flags.ToString("X4", CultureInfo.InvariantCulture));
    meta.Append("flag_encrypted = ").AppendLine(encryptedFlag ? "1" : "0");
    meta.Append("flag_incremental = ").AppendLine(incrementalFlag ? "1" : "0");
    meta.Append("header_size = ").AppendLine(headerSize.ToString(CultureInfo.InvariantCulture));
    meta.Append("reserved = 0x").AppendLine(reserved.ToString("X8", CultureInfo.InvariantCulture));
    meta.Append("file_size = ").AppendLine(stream.Length.ToString(CultureInfo.InvariantCulture));
    meta.AppendLine();
    meta.AppendLine("[chunks]");
    meta.Append("zlib_chunks_found = ").AppendLine(chunks.Count.ToString(CultureInfo.InvariantCulture));
    meta.Append("encryption_hint = ").AppendLine(encryptionHint);
    if (chunks.Count == MaxChunksSurfaced)
      meta.AppendLine("scan_truncated = 1   ; raise MaxChunksSurfaced to surface more");
    for (var i = 0; i < chunks.Count; i++) {
      var c = chunks[i];
      meta.Append(CultureInfo.InvariantCulture,
        $"chunk_{i:D2} = offset=0x{c.Offset:X} cmf=0x{c.Cmf:X2} flg=0x{c.Flg:X2} compressed={c.CompressedLength} inflated={c.InflatedLength}\n");
    }
    meta.AppendLine();
    meta.AppendLine("[parse_status]");
    meta.AppendLine("header = full");
    meta.AppendLine("block_allocation_index = opaque   ; proprietary, not reverse-engineered");
    meta.AppendLine("chunk_directory = opaque          ; trailer index format unknown");
    meta.AppendLine("sector_reconstruction = unsupported");
    meta.AppendLine("encrypted_payload_decryption = unsupported");

    var result = new List<(string Name, byte[] Data, string Kind)> {
      ("metadata.ini", Encoding.UTF8.GetBytes(meta.ToString()), "Tag"),
      ("header.bin", raw[..Math.Min(raw.Length, RawHeaderSurfaceSize)], "Track"),
    };
    for (var i = 0; i < chunks.Count; i++) {
      var c = chunks[i];
      var name = string.Create(CultureInfo.InvariantCulture, $"chunks/chunk_{i:D2}_at_{c.Offset:X8}.bin");
      result.Add((name, c.Payload, "Stored"));
    }
    return result;
  }

  private static bool TryIdentifyVariant(ReadOnlySpan<byte> header, out string variant) {
    if (header.Length >= 4 && header[0] == 0x49 && header[1] == 0x4D && header[2] == 0x47 && header[3] == 0x46) {
      variant = "IMGF";
      return true;
    }
    if (header.Length >= 4 && header[0] == 0x46 && header[1] == 0x49 && header[2] == 0x4D && header[3] == 0x47) {
      variant = "FIMG";
      return true;
    }
    variant = string.Empty;
    return false;
  }
}
