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
///     <b>R/O metadata</b> via the MIT-licensed vendor spec
///     <see href="https://github.com/macrium/mrimgx_file_layout"/>. Footer
///     parse + metadata block chain walk + <c>$JSON</c> decompression.</description></item>
///   <item><description><c>.mrimg</c> (legacy, Reflect v8.x and earlier) —
///     <b>Stage 0</b> detection-only. No vendor spec; only ccooper21's
///     partial RE exists (covers decompression only). Legacy EULA also
///     restricts reverse engineering of that product.</description></item>
/// </list>
///
/// <para>
/// <b>What is surfaced for Reflect X (R/O metadata):</b>
/// </para>
/// <list type="bullet">
///   <item><description><c>metadata.ini</c> — parsed footer offset, block chain summary, blockers.</description></item>
///   <item><description><c>metadata.json</c> — decompressed <c>$JSON</c> block when present and unencrypted (zstd-decoded).</description></item>
///   <item><description><c>block-NN.&lt;name&gt;.bin</c> — opaque payload for each metadata block (<c>$JSON</c>, <c>$AUXDATA</c>, <c>$TRACK0</c>, <c>$EPT</c>, <c>$BITMAP</c>, <c>$INDEX</c>) with original framing intact.</description></item>
///   <item><description><c>macrium-image.bin</c> — raw image bytes for downstream tooling.</description></item>
/// </list>
///
/// <para>
/// <b>R/W promotion blockers (Reflect X):</b>
/// </para>
/// <list type="number">
///   <item><description>Disk content reconstruction needs AES-CBC (128/192/256) +
///     PBKDF2-SHA256 / 600 000 iterations + HMAC-SHA256 password validation; per-block
///     IV combines image id, disk #, partition #, block index, and an AES-256-ECB-encrypted
///     SHA-256 of the derived key.</description></item>
///   <item><description>Sector content needs an <c>$INDEX</c> walk + per-block zstd
///     decompression + MD5 validation; full implementation is non-trivial.</description></item>
///   <item><description>Delta / incremental / differential restores require resolving
///     the parent chain across multiple files.</description></item>
///   <item><description>Mountable VHDX output is non-trivial; the vendor ships a
///     reference <c>img_to_vhdx.exe</c> for that workflow.</description></item>
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
public sealed class MacriumFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {

  public string Id => "Macrium";
  public string DisplayName => "Macrium Reflect";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.CanCreate;
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
    "Remaining limitations: " +
    "(1) delta / incremental / differential restores require external parent-chain resolution across " +
    "multiple files (single-file full backups round-trip end-to-end), " +
    "(2) mountable VHDX output not produced — extract disk-image.raw and run the vendor img_to_vhdx.exe " +
    "or chain through our own VHD/VHDX writers downstream. " +
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

    var writer = new MacriumWriter {
      CompressDataBlocks = compress,
      EncryptDataBlocks = encrypt,
      Password = options.Password,
      AesType = aesType,
      Pbkdf2Iterations = options.GetOptionInt("pbkdf2_iterations", MacriumCrypto.DefaultPbkdf2Iterations),
    };
    var bytes2 = writer.Build(disk.ToArray());
    output.Write(bytes2, 0, bytes2.Length);
  }
}
