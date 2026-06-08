#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Core.Streams;
using FileFormat.Zstd;

namespace FileFormat.Macrium;

/// <summary>
/// Reader for Macrium Reflect X image / backup files (<c>.mrimgx</c>,
/// <c>.mrbakx</c>) — and Stage-0 detection for the legacy <c>.mrimg</c>
/// container. Macrium Reflect is a Windows backup/disk-imaging product from
/// Paramount Software UK.
///
/// <para>
/// The <b>Reflect X</b> family (<c>.mrimgx</c> / <c>.mrbakx</c>, Reflect v9+)
/// is fully documented under MIT licence by the vendor at
/// <see href="https://github.com/macrium/mrimgx_file_layout"/>. Stage-1 R/O
/// metadata parsing is implemented here per that spec:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     Final 20 bytes of the file are the <b>footer</b>:
///     <c>uint64 first_metadata_block_offset</c> + 12-byte ASCII tag
///     <c>"MACRIUM_FILE"</c>.
///     </description>
///   </item>
///   <item>
///     <description>
///     The footer locates the first <b>metadata block</b>. Each metadata
///     block is preceded by a 32-byte header
///     (8-byte ASCII block name, 4-byte little-endian length, 16-byte MD5
///     hash, 1 flags byte = <c>last|encryption|compression|unused×5</c>,
///     3 padding bytes). Walking the chain stops on <c>last_block</c>.
///     </description>
///   </item>
///   <item>
///     <description>
///     The mandatory <c>$JSON</c> block (zstd-compressed when flagged)
///     carries human-readable navigational metadata (image id, backup GUID,
///     encryption descriptor, disk &amp; partition list, block sizes).
///     We expose it decompressed as <c>metadata.json</c> when possible.
///     </description>
///   </item>
///   <item>
///     <description>
///     Other documented block names (<c>$AUXDATA</c>, <c>$TRACK0</c>,
///     <c>$EPT</c>, <c>$BITMAP</c>, <c>$INDEX</c>) are surfaced as opaque
///     <c>block-NN.&lt;name&gt;.bin</c> entries carrying the still-compressed
///     /still-encrypted payload — sector-content reconstruction would
///     require AES-CBC, zstd, hash validation, and incremental chain
///     resolution, which is out of scope for this metadata-only reader.
///     </description>
///   </item>
/// </list>
///
/// <para>
/// The <b>legacy</b> <c>.mrimg</c> format (Reflect v8.x and earlier) is NOT
/// covered by the published spec. The only public reverse-engineering work
/// (<see href="https://github.com/ccooper21/mrimg-tools"/>) covers
/// decompression only and reports the algorithm as a custom LZ-family codec.
/// Macrium's legacy EULA also restricts reverse engineering of that
/// product. We therefore stay Stage-0 for <c>.mrimg</c> and surface a
/// <c>metadata.ini</c> documenting the blockers.
/// </para>
///
/// <para>
/// What this reader does NOT do (R/W promotion blockers):
/// </para>
/// <list type="number">
///   <item><description>Decrypt encrypted blocks (AES-128/192/256-CBC with
///     PBKDF2-SHA256/600k iterations; HMAC-SHA256 password validation; per-block
///     IV derived from imageid + disk + partition + block_index + key hash).</description></item>
///   <item><description>Reconstruct sector content from <c>$INDEX</c> mapping
///     (would need the data-block read pipeline + zstd + hash validation).</description></item>
///   <item><description>Walk delta / incremental / differential parent
///     chains across multiple files.</description></item>
///   <item><description>Produce a mountable VHDX (Macrium's own
///     <c>img_to_vhdx.exe</c> reference tool covers this).</description></item>
///   <item><description>Touch the legacy <c>.mrimg</c> body — proprietary
///     custom LZ codec, no published spec.</description></item>
/// </list>
/// </summary>
public sealed class MacriumReader : IDisposable {

  /// <summary>Footer magic of Reflect X files: 12-byte ASCII <c>"MACRIUM_FILE"</c> at <c>file_size − 12</c>.</summary>
  public static readonly byte[] FooterMagic = "MACRIUM_FILE"u8.ToArray();

  /// <summary>Legacy <c>.mrimg</c> ASCII tag occasionally observed at offset 0 in community-RE samples. Detection-only; not authoritative.</summary>
  public static readonly byte[] LegacyAsciiTag = "MR_BACKUP"u8.ToArray();

  /// <summary>Legacy <c>.mrimg</c> binary tag occasionally observed at offset 0 in community-RE samples. Detection-only; not authoritative.</summary>
  public static readonly byte[] LegacyBinaryTag = "MACX"u8.ToArray();

  private const int FooterSize = 20;
  private const int MetadataBlockHeaderSize = 32;

  private readonly byte[] _data;
  private readonly List<MacriumEntry> _entries = [];
  private readonly List<MacriumBlock> _blocks = [];
  private readonly string? _password;

  /// <summary>Parsed metadata blocks (Reflect X only). Empty for legacy <c>.mrimg</c>.</summary>
  public IReadOnlyList<MacriumBlock> Blocks => _blocks;

  /// <summary>All surfaced entries — <c>metadata.ini</c>, <c>metadata.json</c> (if a <c>$JSON</c> block was decoded), per-block opaque entries, and the raw image.</summary>
  public IReadOnlyList<MacriumEntry> Entries => _entries;

  /// <summary>Family of the parsed file: <c>"mrimgx"</c> (footer-tagged) or <c>"mrimg-legacy"</c> (offset-0 community RE).</summary>
  public string Variant { get; private set; } = "";

  /// <summary>Header tag actually observed (e.g. <c>"MACRIUM_FILE"</c>, <c>"MR_BACKUP"</c>, <c>"MACX"</c>).</summary>
  public string Tag { get; private set; } = "";

  /// <summary>Offset (from start of file) of the first metadata block. Reflect X only; 0 when not applicable.</summary>
  public long FirstMetadataBlockOffset { get; private set; }

  /// <summary>True once <see cref="Parse"/> has confirmed at least one known structural marker.</summary>
  public bool ValidHeader { get; private set; }

  /// <summary>True when the parsed image had an <c>_encryption.enable=true</c> JSON field. Sector reconstruction needs the matching password.</summary>
  public bool IsEncrypted { get; private set; }

  /// <summary>True when sector reconstruction succeeded and a <c>disk-image.raw</c> entry is surfaced.</summary>
  public bool SectorReconstructionAvailable { get; private set; }

  /// <summary>Diagnostic reason why sector reconstruction was skipped, or empty when it succeeded / wasn't attempted.</summary>
  public string SectorReconstructionStatus { get; private set; } = "not-attempted";

  public MacriumReader(Stream stream) : this(stream, password: null) { }

  /// <summary>
  /// Constructs a reader; the optional <paramref name="password"/> unlocks
  /// encrypted Reflect X images and triggers sector reconstruction via the
  /// <c>$INDEX</c> walk.
  /// </summary>
  public MacriumReader(Stream stream, string? password) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    _password = password;
    Parse();
  }

  private void Parse() {
    if (_data.Length < 16)
      throw new InvalidDataException("Macrium Reflect: file too small for any known container header.");

    if (TryParseReflectXFooter())
      return;

    if (TryParseLegacyMrimgTag())
      return;

    throw new InvalidDataException(
      "Macrium Reflect: no recognized container marker — neither 'MACRIUM_FILE' footer (Reflect X) "
      + "nor legacy 'MR_BACKUP' / 'MACX' offset-0 tag was found.");
  }

  // ---- Reflect X: footer-driven metadata block walk -----------------------

  private bool TryParseReflectXFooter() {
    if (_data.Length < FooterSize)
      return false;

    var footer = _data.AsSpan(_data.Length - FooterSize, FooterSize);
    var firstOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(footer[..8]);
    var magic = footer[8..];
    if (!magic.SequenceEqual(FooterMagic))
      return false;

    this.Variant = "mrimgx";
    this.Tag = "MACRIUM_FILE";
    this.FirstMetadataBlockOffset = firstOffset;
    this.ValidHeader = true;

    // Walk the ROOT chain at the footer offset first ($JSON + $AUXDATA per the
    // vendor spec). The $JSON block, once decoded, optionally points at a
    // separate DISK metadata chain at _header.index_file_position — we walk
    // that second chain after the JSON is parsed.
    WalkMetadataBlocks(firstOffset);
    SurfaceReflectXEntries();
    return true;
  }

  private void WalkMetadataBlocks(long firstOffset) {
    // Sanity gate: offset must point inside the file before the 20-byte
    // footer. A bogus offset means the file is corrupt or this isn't really
    // a Reflect X image after all — surface what we have without crashing.
    var cursor = firstOffset;
    var fileEnd = _data.Length - FooterSize;
    var safety = 0;

    while (cursor >= 0 && cursor + MetadataBlockHeaderSize <= fileEnd) {
      ++safety;
      if (safety > 1024)
        break; // Defensive: spec doesn't bound block count, but 1024 is far above realistic.

      // Multi-chain heuristic may walk into an offset we've already recorded.
      // Stop honestly when that happens to avoid double-counting.
      if (_blocks.Any(b => b.HeaderOffset == cursor))
        break;

      var headerSpan = _data.AsSpan((int)cursor, MetadataBlockHeaderSize);
      var name = Encoding.ASCII.GetString(headerSpan[..8]).TrimEnd();
      var length = BinaryPrimitives.ReadUInt32LittleEndian(headerSpan.Slice(8, 4));
      var hash = headerSpan.Slice(12, 16).ToArray();
      var flags = headerSpan[28];
      var lastBlock = (flags & 0x01) != 0;
      var compressed = (flags & 0x02) != 0;
      var encrypted = (flags & 0x04) != 0;

      var payloadOffset = cursor + MetadataBlockHeaderSize;
      if (payloadOffset + length > fileEnd)
        break; // Truncated / wrong cursor — stop honestly.

      _blocks.Add(new MacriumBlock {
        Name = name,
        HeaderOffset = cursor,
        PayloadOffset = payloadOffset,
        PayloadLength = length,
        Md5Hash = hash,
        Flags = flags,
        IsLast = lastBlock,
        IsCompressed = compressed,
        IsEncrypted = encrypted,
      });

      var nextCursor = payloadOffset + length;
      if (lastBlock) {
        // The vendor's actual files chain MULTIPLE last_block-terminated
        // sub-chains contiguously: the disk chain ends with $TRACK0(last=1),
        // immediately followed by the per-partition chain ($BITMAP, $INDEX,
        // ...). Peek at the next bytes — if they parse as a valid Macrium
        // block header (name starts with '$' and 7 more ASCII chars), keep
        // walking; otherwise we've hit the real end of the metadata region.
        if (nextCursor + MetadataBlockHeaderSize > fileEnd) break;
        if (!LooksLikeBlockHeader(_data.AsSpan((int)nextCursor, 8))) break;
      }

      cursor = nextCursor;
    }
  }

  /// <summary>Heuristic: a Macrium metadata block name is 8 ASCII bytes starting with '$' (e.g. <c>"$JSON   "</c>, <c>"$BITMAP "</c>). Anything else is post-metadata data / EOF.</summary>
  private static bool LooksLikeBlockHeader(ReadOnlySpan<byte> name8) {
    if (name8.Length < 8) return false;
    if (name8[0] != (byte)'$') return false;
    // Remaining 7 bytes must be ASCII printable (letters, digits, or space pads).
    for (var i = 1; i < 8; ++i) {
      var c = name8[i];
      var isOk = c is (>= (byte)'A' and <= (byte)'Z')
        or (>= (byte)'a' and <= (byte)'z')
        or (>= (byte)'0' and <= (byte)'9')
        or (byte)' ' or (byte)'_';
      if (!isOk) return false;
    }
    return true;
  }

  private void SurfaceReflectXEntries() {
    // Try to decompress the $JSON block into a readable metadata.json first
    // so the later metadata.ini build can summarize its contents and so the
    // sector-reconstruction step can use it.
    byte[]? jsonBytes = null;
    var jsonBlock = _blocks.FirstOrDefault(b => b.Name == "$JSON");
    if (jsonBlock is not null && !jsonBlock.IsEncrypted) {
      try {
        jsonBytes = ReadBlockPayload(jsonBlock);
      } catch {
        // Surface the raw payload below; metadata.ini already names the block.
      }
    }

    // Parse the JSON layout if available — this gives us imageid, block sizes,
    // encryption descriptor, and the offsets we need for sector walk.
    if (jsonBytes is not null) {
      try { _layout = MacriumLayout.Parse(jsonBytes); } catch { _layout = null; }
    }

    this.IsEncrypted = _layout?.IsEncrypted ?? false;

    // If the JSON's _header.index_file_position points at a DISK metadata
    // chain distinct from the ROOT chain (which is what the vendor's actual
    // .mrimgx files do), walk it now so $TRACK0 + $INDEX become visible.
    if (_layout is not null
        && _layout.IndexFilePosition > 0
        && _layout.IndexFilePosition < _data.Length - FooterSize
        && !_blocks.Any(b => b.HeaderOffset == _layout.IndexFilePosition))
      WalkMetadataBlocks(_layout.IndexFilePosition);

    TryReconstructSector();

    // metadata.ini — now reflects sector-reconstruction status too.
    var meta = BuildReflectXMetadata();
    _entries.Add(new MacriumEntry {
      Name = "metadata.ini",
      Size = meta.Length,
      IsDirectory = false,
      Offset = 0,
      Data = meta,
    });

    if (jsonBytes is not null) {
      _entries.Add(new MacriumEntry {
        Name = "metadata.json",
        Size = jsonBytes.Length,
        IsDirectory = false,
        Offset = jsonBlock!.PayloadOffset,
        Data = jsonBytes,
      });
    }

    // Surface reconstructed disk image when available.
    if (_reconstructedDisk is not null) {
      _entries.Add(new MacriumEntry {
        Name = "disk-image.raw",
        Size = _reconstructedDisk.Length,
        IsDirectory = false,
        Offset = 0,
        Data = _reconstructedDisk,
      });
    }

    // Surface every metadata block as an opaque payload — the user still
    // gets the same bytes the on-disk format stored.
    var ix = 0;
    foreach (var block in _blocks) {
      var raw = _data.AsSpan((int)block.PayloadOffset, (int)block.PayloadLength).ToArray();
      _entries.Add(new MacriumEntry {
        Name = $"block-{ix:D2}.{SanitizeBlockName(block.Name)}.bin",
        Size = raw.Length,
        IsDirectory = false,
        Offset = block.PayloadOffset,
        Data = raw,
      });
      ++ix;
    }

    // Finally the raw image, for downstream tooling (e.g. Macrium's own
    // img_to_vhdx.exe) that can do the full restore we honestly can't.
    _entries.Add(new MacriumEntry {
      Name = "macrium-image.bin",
      Size = _data.Length,
      IsDirectory = false,
      Offset = 0,
      Data = _data,
    });
  }

  /// <summary>Reconstructs the original disk image from the $INDEX walk + per-block decompress / decrypt, when possible.</summary>
  private void TryReconstructSector() {
    if (_layout is null) {
      this.SectorReconstructionStatus = "no-json-layout";
      return;
    }

    var indexBlock = _blocks.FirstOrDefault(b => b.Name == "$INDEX");
    if (indexBlock is null) {
      this.SectorReconstructionStatus = "no-$INDEX-block";
      return;
    }

    // Encryption gate: when the layout says encrypted, require a matching password.
    byte[]? aesKey = null;
    byte[]? derivedKey = null;
    if (_layout.IsEncrypted) {
      if (_password is null) {
        this.SectorReconstructionStatus = "encrypted-no-password";
        return;
      }
      try {
        derivedKey = MacriumCrypto.DeriveKey(_password, _layout.ImageId, _layout.KeyIterations);
        if (_layout.ExpectedHmac is not null && !MacriumCrypto.ValidateHmac(derivedKey, _layout.ExpectedHmac)) {
          this.SectorReconstructionStatus = "encrypted-wrong-password";
          return;
        }
        aesKey = derivedKey[..(int)_layout.AesType];
      } catch {
        this.SectorReconstructionStatus = "encrypted-key-derivation-failed";
        return;
      }
    }

    // Walk $INDEX.
    List<MacriumWriter.DataBlockIndexElement> elements;
    try {
      var indexPayload = ReadBlockPayload(indexBlock); // never encrypted in our writer.
      elements = DeserializeIndex(indexPayload);
    } catch {
      this.SectorReconstructionStatus = "$INDEX-parse-failed";
      return;
    }

    // Find $TRACK0 (uncompressed, never encrypted in our writer per spec convention).
    byte[] track0 = [];
    var t0 = _blocks.FirstOrDefault(b => b.Name == "$TRACK0");
    if (t0 is not null) {
      try { track0 = ReadBlockPayload(t0); } catch { /* tolerate missing */ }
    }

    // Reconstruct partition payload.
    using var partitionMs = new MemoryStream();
    for (var i = 0; i < elements.Count; ++i) {
      var element = elements[i];
      if (element.FilePosition < 0
          || element.BlockLength == 0
          || element.FilePosition + element.BlockLength > _data.Length) {
        this.SectorReconstructionStatus = $"index-element-{i}-out-of-range";
        return;
      }
      var raw = _data.AsSpan((int)element.FilePosition, (int)element.BlockLength).ToArray();
      try {
        var working = raw;
        if (_layout.IsEncrypted) {
          var iv = MacriumCrypto.DeriveBlockIv(
            derivedKey!, _layout.ImageId,
            _layout.DiskNumber, _layout.PartitionNumber, i);
          working = MacriumCrypto.DecryptBlock(working, aesKey!, iv);
        }
        if (_layout.IsZstd)
          working = DecompressZstd(working);
        partitionMs.Write(working, 0, working.Length);
      } catch {
        this.SectorReconstructionStatus = $"block-{i}-decode-failed";
        return;
      }
    }

    // Truncate partition payload to its exact byte size per JSON
    // _cw_extra.partition_byte_size — vendor doesn't expose this; we
    // emit and consume it as a private extension. When absent (foreign
    // image) we surface the block-aligned payload as-is.
    var partition = partitionMs.ToArray();
    if (_layout.PartitionByteSize > 0 && _layout.PartitionByteSize < partition.Length)
      partition = partition[.._layout.PartitionByteSize];

    // Final disk = track0 prefix + partition payload.
    var disk = new byte[track0.Length + partition.Length];
    Buffer.BlockCopy(track0, 0, disk, 0, track0.Length);
    Buffer.BlockCopy(partition, 0, disk, track0.Length, partition.Length);

    _reconstructedDisk = disk;
    this.SectorReconstructionAvailable = true;
    this.SectorReconstructionStatus = "ok";
  }

  private static List<MacriumWriter.DataBlockIndexElement> DeserializeIndex(byte[] indexBytes) {
    if (indexBytes.Length < 4)
      throw new InvalidDataException("$INDEX block too small.");
    var span = indexBytes.AsSpan();
    var count = BinaryPrimitives.ReadUInt32LittleEndian(span[..4]);
    var required = 4 + (long)count * 30;
    if (required > indexBytes.Length)
      throw new InvalidDataException("$INDEX block truncated.");
    var list = new List<MacriumWriter.DataBlockIndexElement>((int)count);
    var offset = 4;
    for (var i = 0; i < count; ++i) {
      var filePos = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(offset, 8));
      offset += 8;
      var md5 = span.Slice(offset, 16).ToArray();
      offset += 16;
      var blockLen = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));
      offset += 4;
      var fileNum = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, 2));
      offset += 2;
      list.Add(new MacriumWriter.DataBlockIndexElement {
        FilePosition = filePos,
        Md5Hash = md5,
        BlockLength = blockLen,
        FileNumber = fileNum,
      });
    }
    return list;
  }

  private static byte[] DecompressZstd(byte[] raw) {
    using var input = new MemoryStream(raw, writable: false);
    using var zs = new ZstdStream(input, CompressionStreamMode.Decompress, leaveOpen: false);
    using var output = new MemoryStream();
    zs.CopyTo(output);
    return output.ToArray();
  }

  private MacriumLayout? _layout;
  private byte[]? _reconstructedDisk;

  private byte[] ReadBlockPayload(MacriumBlock block) {
    var raw = _data.AsSpan((int)block.PayloadOffset, (int)block.PayloadLength).ToArray();
    if (!block.IsCompressed)
      return raw;

    using var input = new MemoryStream(raw, writable: false);
    using var zs = new ZstdStream(input, CompressionStreamMode.Decompress, leaveOpen: false);
    using var output = new MemoryStream();
    zs.CopyTo(output);
    return output.ToArray();
  }

  private static string SanitizeBlockName(string name) {
    var sb = new StringBuilder(name.Length);
    foreach (var c in name) {
      if (c is '$' or '_' or '-' or '.' || char.IsLetterOrDigit(c))
        sb.Append(c);
    }
    return sb.Length == 0 ? "block" : sb.ToString();
  }

  // ---- Legacy .mrimg: Stage-0 offset-0 tag detection ----------------------

  private bool TryParseLegacyMrimgTag() {
    if (_data.Length >= LegacyAsciiTag.Length
        && _data.AsSpan(0, LegacyAsciiTag.Length).SequenceEqual(LegacyAsciiTag)) {
      this.Variant = "mrimg-legacy";
      this.Tag = "MR_BACKUP";
      this.ValidHeader = true;
      SurfaceLegacyEntries();
      return true;
    }

    if (_data.Length >= LegacyBinaryTag.Length
        && _data.AsSpan(0, LegacyBinaryTag.Length).SequenceEqual(LegacyBinaryTag)) {
      this.Variant = "mrimg-legacy";
      this.Tag = "MACX";
      this.ValidHeader = true;
      SurfaceLegacyEntries();
      return true;
    }

    return false;
  }

  private void SurfaceLegacyEntries() {
    var meta = BuildLegacyMrimgMetadata();
    _entries.Add(new MacriumEntry {
      Name = "metadata.ini",
      Size = meta.Length,
      IsDirectory = false,
      Offset = 0,
      Data = meta,
    });
    _entries.Add(new MacriumEntry {
      Name = "macrium-image.bin",
      Size = _data.Length,
      IsDirectory = false,
      Offset = 0,
      Data = _data,
    });
  }

  // ---- metadata.ini builders ---------------------------------------------

  private byte[] BuildReflectXMetadata() {
    var bldr = new StringBuilder();
    bldr.Append("parse_status=ro-metadata\n");
    bldr.Append("stage=1\n");
    bldr.Append("variant=mrimgx\n");
    bldr.Append("format=Macrium Reflect X image / backup (.mrimgx / .mrbakx)\n");
    bldr.Append("vendor=Paramount Software UK (Macrium)\n");
    bldr.Append(CultureInfo.InvariantCulture, $"footer_magic={this.Tag}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"first_metadata_block_offset={this.FirstMetadataBlockOffset}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"file_size={_data.Length}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"metadata_block_count={_blocks.Count}\n");
    for (var i = 0; i < _blocks.Count; ++i) {
      var b = _blocks[i];
      bldr.Append(CultureInfo.InvariantCulture,
        $"block_{i:D2}={b.Name};offset={b.PayloadOffset};length={b.PayloadLength};"
        + $"compressed={(b.IsCompressed ? 1 : 0)};encrypted={(b.IsEncrypted ? 1 : 0)};"
        + $"last={(b.IsLast ? 1 : 0)}\n");
    }
    bldr.Append("spec=https://github.com/macrium/mrimgx_file_layout (MIT)\n");
    bldr.Append("encrypted=").Append(this.IsEncrypted ? 1 : 0).Append('\n');
    bldr.Append("sector_reconstruction=").Append(this.SectorReconstructionStatus).Append('\n');
    bldr.Append("rw_promotion=");
    if (this.SectorReconstructionAvailable)
      bldr.Append("rw\n");
    else
      bldr.Append(this.IsEncrypted ? "blocked-encrypted\n" : "blocked\n");
    bldr.Append("rw_capability_1=Reflect X sector reconstruction via $INDEX walk + zstd decompression + AES-CBC decryption (when password supplied) + PBKDF2-SHA256/600k\n");
    bldr.Append("rw_capability_2=writer emits valid Reflect X containers (footer + metadata chain + $TRACK0/$INDEX/$JSON + per-block zstd + per-block AES-CBC with ESSIV IV)\n");
    bldr.Append("rw_limitation_1=delta / incremental / differential restores still require walking the parent chain across multiple files (single-file full backups only)\n");
    bldr.Append("rw_limitation_2=mountable VHDX output not produced — extract the raw image and use the vendor img_to_vhdx.exe or our VHD/VHDX writers downstream\n");
    bldr.Append("note=Reflect X R/W via vendor MIT spec. Reconstructed disk surfaced as disk-image.raw when $INDEX walk + decryption succeed. ");
    bldr.Append("All other blocks are surfaced as opaque block-NN.<name>.bin payloads with original framing intact.\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  private byte[] BuildLegacyMrimgMetadata() {
    var bldr = new StringBuilder();
    bldr.Append("parse_status=detection-only\n");
    bldr.Append("stage=0\n");
    bldr.Append("variant=mrimg-legacy\n");
    bldr.Append("format=Macrium Reflect legacy sector image (.mrimg, Reflect v8.x and earlier)\n");
    bldr.Append("vendor=Paramount Software UK (Macrium)\n");
    bldr.Append(CultureInfo.InvariantCulture, $"detected_tag={this.Tag}\n");
    bldr.Append("detected_tag_offset=0\n");
    bldr.Append(CultureInfo.InvariantCulture, $"image_size={_data.Length}\n");
    bldr.Append("spec=none (vendor never published; ccooper21/mrimg-tools partial RE covers decompression only)\n");
    bldr.Append("ro_promotion=blocked\n");
    bldr.Append("ro_promotion_reason_1=on-disk container format is proprietary; Macrium has not published a spec for v8.x and earlier\n");
    bldr.Append("ro_promotion_reason_2=internal LZ-family block compression is a custom Reflect codec; the only public partial RE (ccooper21/mrimg-tools) implements decompression only and is not a verified spec\n");
    bldr.Append("ro_promotion_reason_3=block / partition metadata index layout is opaque\n");
    bldr.Append("ro_promotion_reason_4=incremental / differential backups form parent chains that require external chain resolution\n");
    bldr.Append("ro_promotion_reason_5=Macrium legacy EULA explicitly restricts reverse engineering of the legacy Reflect product\n");
    bldr.Append("note=Stage 0 — detection only. Legacy .mrimg image surfaced as opaque blob plus this metadata.ini. ");
    bldr.Append("Reflect X (.mrimgx / .mrbakx) IS R/O-promoted in this reader via the MIT-licensed vendor spec; ");
    bldr.Append("upgrading legacy backup sets through Macrium Reflect X is the supported migration path.\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  public byte[] Extract(MacriumEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return entry.Data;
  }

  public void Dispose() { }
}
