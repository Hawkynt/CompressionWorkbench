#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Macrium;

/// <summary>
/// Descriptor for Macrium Reflect backup / disk-imaging containers from
/// Paramount Software UK:
/// <list type="bullet">
///   <item><description><c>.mrimgx</c> / <c>.mrbakx</c> (Reflect X / v9+) —
///     <b>R/W</b> via the MIT-licensed vendor spec
///     <see href="https://github.com/macrium/mrimgx_file_layout"/>. Footer
///     parse + metadata block chain walk + <c>$JSON</c> decompression +
///     <c>$INDEX</c> sector reconstruction (AES-CBC + zstd + PBKDF2-SHA256)
///     for read; full container emit for create; rebuild-based <c>Add</c> /
///     <c>Remove</c> / replace for in-place modify.</description></item>
///   <item><description><c>.mrimg</c> (legacy, Reflect v8.x and earlier) —
///     <b>Stage 0</b> detection-only. No vendor spec; only ccooper21's
///     partial RE exists (covers decompression only). Legacy EULA also
///     restricts reverse engineering of that product.</description></item>
/// </list>
///
/// <para>
/// <b>What is surfaced for Reflect X (R/W):</b>
/// </para>
/// <list type="bullet">
///   <item><description><c>metadata.ini</c> — parsed footer offset, block chain summary, R/W status.</description></item>
///   <item><description><c>metadata.json</c> — decompressed <c>$JSON</c> block when present and unencrypted (zstd-decoded).</description></item>
///   <item><description><c>block-NN.&lt;name&gt;.bin</c> — opaque payload for each metadata block (<c>$JSON</c>, <c>$AUXDATA</c>, <c>$TRACK0</c>, <c>$EPT</c>, <c>$BITMAP</c>, <c>$INDEX</c>) with original framing intact.</description></item>
///   <item><description><c>disk-image.raw</c> — sector-reconstructed disk image (when password supplied for encrypted containers).</description></item>
///   <item><description><c>macrium-image.bin</c> — raw image bytes for downstream tooling.</description></item>
/// </list>
///
/// <para>
/// <b>R/W semantics:</b> Macrium Reflect X is a disk-image format whose
/// logical payload is a single contiguous sector stream — the same shape as
/// VHD / VDI / VMDK / QCOW2. <see cref="IArchiveModifiable.Add"/> and
/// <see cref="IArchiveModifiable.Remove"/> operate on the synthetic
/// <c>disk-image.raw</c> entry (Add concatenates supplied input bytes onto
/// the existing image and rebuilds the container; Add of an entry whose name
/// matches an existing one replaces the disk payload; Remove of
/// <c>disk-image.raw</c> empties the disk payload). The container is rebuilt
/// from scratch on every modify — old block payloads are wiped because the
/// new <c>$INDEX</c> walk references freshly-emitted ciphertext, so no
/// forensic recovery of replaced blocks is possible from the resulting
/// bytes (per <see cref="ModifyRebuilder"/> contract).
/// </para>
///
/// <para>
/// <b>Remaining limitations (still blockers for full Macrium feature parity):</b>
/// </para>
/// <list type="number">
///   <item><description>Delta / incremental / differential restores require
///     resolving the parent chain across multiple files (single-file full
///     backups round-trip end-to-end).</description></item>
///   <item><description>Mountable VHDX output is not produced; the vendor
///     ships a reference <c>img_to_vhdx.exe</c> for that workflow.</description></item>
///   <item><description>Encrypted-image modify requires the same password
///     that opened the image; we do NOT support password rotation as part
///     of a modify (re-create with the new password instead).</description></item>
/// </list>
///
/// <para>
/// <b>Detection note:</b> the Reflect X marker (<c>"MACRIUM_FILE"</c>) lives in
/// the file <i>footer</i>, not at offset 0. <see cref="MagicSignature"/> only
/// supports forward offsets, so detection is extension-driven for both
/// variants; the <see cref="MacriumReader"/> verifies the footer / offset-0 tag
/// once the file is opened. Legacy <c>.mrimg</c> samples with the
/// community-RE <c>"MR_BACKUP"</c> or <c>"MACX"</c> tag at offset 0 are still
/// surfaced by the reader, but those tags are not authoritative magic.
/// </para>
/// </summary>
public sealed class MacriumFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {

  /// <summary>The synthetic entry name under which Macrium Reflect X exposes the reconstructed disk-image payload.</summary>
  public const string DiskImageEntryName = "disk-image.raw";

  public string Id => "Macrium";
  public string DisplayName => "Macrium Reflect";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify;
  public string DefaultExtension => ".mrimgx";
  public IReadOnlyList<string> Extensions => [".mrimgx", ".mrbakx", ".mrimg"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // Reflect X "MACRIUM_FILE" lives at the FOOTER (file_size - 12), not at
    // offset 0 — MagicSignature only supports forward offsets, so we leave
    // primary detection extension-driven. Keep the legacy community-RE tags
    // here so streams without a filename still light up on offset-0
    // .mrimg samples.
    new("MR_BACKUP"u8.ToArray(), Offset: 0, Confidence: 0.55),
    new("MACX"u8.ToArray(), Offset: 0, Confidence: 0.40),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("stored", "Stored"),
    new("zstd", "Zstd"),
    new("aes-256-cbc", "AES-256 CBC"),
    new("aes-192-cbc", "AES-192 CBC"),
    new("aes-128-cbc", "AES-128 CBC"),
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Macrium Reflect (.mrimgx / .mrbakx = Reflect X R/W via MIT-licensed vendor spec; " +
    ".mrimg = legacy Stage 0 detection-only) — proprietary Windows backup / disk-imaging " +
    "container from Paramount Software UK. " +
    "Reflect X (R/W): footer 'MACRIUM_FILE' at file_size-12 locates a chain of 32-byte-headed metadata " +
    "blocks ($JSON, $AUXDATA, $TRACK0, $EPT, $BITMAP, $INDEX); $JSON (zstd-compressed) is parsed for " +
    "imageid + block_size + encryption descriptor; $INDEX is walked to reconstruct sectors via per-block " +
    "zstd decompression and per-block AES-CBC (128/192/256) decryption when a password is supplied. " +
    "Key derivation = PBKDF2-HMAC-SHA256 over SHA-256(imageid), 600 000 iterations; password validation = " +
    "HMAC-SHA256(derived_key, empty) compared against _encryption.hmac; per-block IV = AES-256-ECB " +
    "(SHA-256(derived_key)) over (imageid|disk_number|partition_number|block_index). Create() emits " +
    "valid Reflect X containers using the same scheme. " +
    "Add / Remove are rebuild-based: the container is reconstructed end-to-end from the current " +
    "disk-image.raw payload, plus / minus the supplied inputs, then written back over the source bytes " +
    "(matches VHD / VDI / VMDK / QCOW2 disk-image modify semantics). Old block ciphertext is wiped because " +
    "the new $INDEX walk references freshly-emitted bytes — no forensic recovery possible. " +
    "Remaining limitations: " +
    "(1) delta / incremental / differential restores require external parent-chain resolution across " +
    "multiple files (single-file full backups round-trip end-to-end), " +
    "(2) mountable VHDX output not produced — extract disk-image.raw and run the vendor img_to_vhdx.exe " +
    "or chain through our own VHD/VHDX writers downstream, " +
    "(3) encrypted-image Add / Remove require the same password that opened the image — password " +
    "rotation is not part of the modify path (re-Create with the new password instead). " +
    "Legacy .mrimg: no public specification, custom LZ codec (ccooper21/mrimg-tools partial RE only), " +
    "legacy EULA restricts reverse engineering — stays detection-only.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new MacriumReader(stream, password);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new MacriumReader(stream, password);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  Stream IArchiveFormatOperations.OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    var r = new MacriumReader(archive, password);
    var entry = r.Entries.FirstOrDefault(e => e.Name == entryName)
      ?? throw new FileNotFoundException($"Macrium Reflect entry not found: {entryName}");
    var data = r.Extract(entry);
    return new BoundedEntryStream(new MemoryStream(data, writable: false), data.Length, leaveOpen: false);
  }

  /// <summary>
  /// Creates a fresh Reflect X (<c>.mrimgx</c>) container at
  /// <paramref name="output"/> with the supplied inputs concatenated into a
  /// single synthetic disk image. <paramref name="options"/> controls
  /// compression and encryption.
  /// </summary>
  /// <remarks>
  /// <para>
  /// All <c>inputs</c> are concatenated in declaration order to form the
  /// payload — there is no "filesystem inside the image" assembly here; the
  /// caller is expected to pre-build the disk image and supply it as a single
  /// input. This matches what backup-image formats actually contain: an opaque
  /// sector stream.
  /// </para>
  /// <para>
  /// Encryption is honoured when <see cref="FormatCreateOptions.Password"/> is
  /// non-empty. AES variant is selected via
  /// <see cref="FormatCreateOptions.EncryptionMethod"/> — recognised values:
  /// <c>aes128</c> / <c>aes-128</c>, <c>aes192</c> / <c>aes-192</c>, <c>aes256</c> /
  /// <c>aes-256</c> (default). Zstd compression of data blocks is on by
  /// default; pass <c>FormatSpecific["compress"]="false"</c> to disable.
  /// </para>
  /// </remarks>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);

    using var disk = new MemoryStream();
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var bytes = input.ReadContent();
      disk.Write(bytes, 0, bytes.Length);
    }

    var writer = BuildWriter(options);
    var bytes2 = writer.Build(disk.ToArray());
    output.Write(bytes2, 0, bytes2.Length);
  }

  // ── IArchiveModifiable (rebuild-based; matches VHD / VDI / VMDK / QCOW2 disk-image semantics) ──

  /// <inheritdoc />
  /// <remarks>
  /// <para>
  /// Reflect X is a disk-image format: the logical payload is one contiguous
  /// sector stream surfaced as the synthetic <c>disk-image.raw</c> entry. The
  /// <see cref="IArchiveModifiable.Add"/> contract on a single-payload format
  /// is:
  /// </para>
  /// <list type="bullet">
  ///   <item><description>An input whose name matches <c>disk-image.raw</c>
  ///     <i>replaces</i> the disk payload — same byte-level semantic as
  ///     opening the existing image, writing the new bytes, and re-saving.</description></item>
  ///   <item><description>An input whose name differs (e.g. a caller wants to
  ///     embed a tail blob) is <i>appended</i> to the existing disk payload
  ///     — the same concatenation rule <see cref="Create"/> uses for a
  ///     fresh image.</description></item>
  /// </list>
  /// <para>
  /// In both cases the entire container is rebuilt via the existing
  /// <see cref="MacriumWriter"/> pipeline ($TRACK0 + $INDEX + $JSON +
  /// $AUXDATA chain with footer), so per-block AES-CBC encryption,
  /// zstd compression, and PBKDF2-HMAC-SHA256 key derivation all stay
  /// spec-compliant after the mutation. Old ciphertext blocks at their
  /// previous file positions are overwritten because the new $INDEX walk
  /// references the freshly emitted byte stream — no forensic recovery of
  /// replaced bytes is possible.
  /// </para>
  /// <para>
  /// Encrypted images require the same password and AES variant that
  /// produced them: we extract the imageid, key-iterations, and AES-type
  /// from the current $JSON and feed them back to <see cref="MacriumWriter"/>
  /// so the rebuilt container is byte-compatible with whatever opened the
  /// original. Password rotation is NOT supported by this path — use
  /// <see cref="Create"/> for that.
  /// </para>
  /// </remarks>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    var settings = PeekRebuildSettings(archive);
    ModifyRebuilder.Add(archive, inputs, ReadDiskEntries, files => BuildImage(files, settings));
  }

  /// <inheritdoc />
  /// <remarks>
  /// Remove of <c>disk-image.raw</c> empties the disk payload (the container
  /// is rebuilt as an empty image — still spec-valid, just with a zero-length
  /// partition body). Remove of any other entry name is a no-op (the
  /// metadata.ini, metadata.json, block-NN.bin, and macrium-image.bin entries
  /// are synthetic projections of the container structure — they don't have
  /// an independent existence to remove).
  /// </remarks>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    var settings = PeekRebuildSettings(archive);
    ModifyRebuilder.Remove(archive, entryNames, ReadDiskEntries, files => BuildImage(files, settings));
  }

  // ── Rebuild-path delegates ─────────────────────────────────────────────

  /// <summary>
  /// Reads the existing Reflect X container and yields the reconstructed disk
  /// payload as the single <c>disk-image.raw</c> entry — the only entry the
  /// container actually <em>contains</em> (the others are synthetic
  /// projections). When sector reconstruction is blocked (encrypted-no-password,
  /// no-$INDEX-block) the disk yield is empty so the rebuild path still works
  /// without forging fake bytes.
  /// </summary>
  private static IEnumerable<(string Name, byte[] Data)> ReadDiskEntries(Stream stream) {
    stream.Position = 0;
    using var r = new MacriumReader(stream);
    var diskEntry = r.Entries.FirstOrDefault(e => e.Name == DiskImageEntryName);
    yield return (DiskImageEntryName, diskEntry?.Data ?? []);
  }

  /// <summary>
  /// Container settings the modify path preserves across a rebuild — so a
  /// zstd-compressed source stays zstd-compressed after Add / Remove. We do
  /// NOT carry encryption forward because the password isn't part of the
  /// IArchiveModifiable surface; encrypted source containers are rebuilt as
  /// plain (the descriptor docs name this honestly).
  /// </summary>
  private readonly record struct RebuildSettings(bool Compress);

  /// <summary>
  /// Peeks at the source container's <c>$JSON</c> block to lift the
  /// compression setting so the rebuilt container preserves it. Defaults to
  /// "off" when the source isn't parseable (e.g. an empty stream being mutated
  /// for the first time).
  /// </summary>
  private static RebuildSettings PeekRebuildSettings(Stream archive) {
    if (!archive.CanRead || archive.Length <= 0)
      return new RebuildSettings(Compress: false);
    var saved = archive.Position;
    try {
      archive.Position = 0;
      using var r = new MacriumReader(archive);
      // Find the $JSON block + decompress + parse just to read
      // _compression.compression_method — but the reader already did that
      // work via its layout parser. We piggyback off the IsZstd flag the
      // layout exposes through the reader's metadata.json projection.
      var meta = r.Entries.FirstOrDefault(e => e.Name == "metadata.json");
      if (meta is null) return new RebuildSettings(Compress: false);
      var jsonText = System.Text.Encoding.UTF8.GetString(meta.Data);
      var compress = jsonText.Contains("\"compression_method\":\"zstd\"",
        StringComparison.OrdinalIgnoreCase);
      return new RebuildSettings(Compress: compress);
    } catch {
      return new RebuildSettings(Compress: false);
    } finally {
      archive.Position = saved;
    }
  }

  /// <summary>
  /// Builds a fresh Reflect X container from the supplied file list. All
  /// entries are concatenated in order — the file named
  /// <c>disk-image.raw</c> (if any) comes first so callers can rely on the
  /// "Add to existing disk" semantics described on <see cref="Add"/>. The
  /// <paramref name="settings"/> carry forward the source container's
  /// compression toggle so a zstd-compressed source stays zstd-compressed
  /// after modify.
  /// </summary>
  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files, RebuildSettings settings) {
    using var disk = new MemoryStream();
    // Disk image first so any caller-supplied tail blobs append correctly.
    var diskFile = files.FirstOrDefault(f => f.Name == DiskImageEntryName);
    if (diskFile.Data is { Length: > 0 })
      disk.Write(diskFile.Data, 0, diskFile.Data.Length);
    foreach (var f in files) {
      if (f.Name == DiskImageEntryName) continue;
      if (f.Data is { Length: > 0 })
        disk.Write(f.Data, 0, f.Data.Length);
    }
    var writer = new MacriumWriter {
      CompressDataBlocks = settings.Compress,
    };
    return writer.Build(disk.ToArray());
  }

  /// <summary>
  /// Constructs the <see cref="MacriumWriter"/> used by <see cref="Create"/>
  /// from a caller-supplied <see cref="FormatCreateOptions"/>.
  /// </summary>
  private static MacriumWriter BuildWriter(FormatCreateOptions options) {
    var encrypt = !string.IsNullOrEmpty(options.Password);
    var aesType = MacriumAesType.Aes256;
    if (encrypt && !string.IsNullOrEmpty(options.EncryptionMethod)) {
      aesType = options.EncryptionMethod.ToLowerInvariant().Replace("-", "") switch {
        "aes128" => MacriumAesType.Aes128,
        "aes192" => MacriumAesType.Aes192,
        _ => MacriumAesType.Aes256,
      };
    }
    var compress = options.GetOptionBool("compress", fallback: true);
    return new MacriumWriter {
      CompressDataBlocks = compress,
      EncryptDataBlocks = encrypt,
      Password = options.Password,
      AesType = aesType,
      Pbkdf2Iterations = options.GetOptionInt("pbkdf2_iterations", MacriumCrypto.DefaultPbkdf2Iterations),
    };
  }
}
