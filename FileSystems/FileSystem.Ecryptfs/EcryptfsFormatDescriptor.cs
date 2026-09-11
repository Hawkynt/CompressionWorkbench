#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Ecryptfs;

/// <summary>
/// Descriptor for one eCryptfs lower file. eCryptfs is a stacked filesystem:
/// every lower file carries its own encrypted upper-file payload, size, extent
/// geometry and authentication-token packet set.
///
/// <para>
/// The implementation supports the ordinary passphrase path using the Linux
/// eCryptfs AES-128/192/256 packet conventions. Private-key authentication and
/// xattr-only metadata are deliberately rejected because their required key/xattr
/// material is not contained in a standalone stream.
/// </para>
/// </summary>
public sealed class EcryptfsFormatDescriptor :
  IFormatDescriptor,
  IArchiveFormatOperations,
  IArchiveCreatable,
  IArchiveShrinkable,
  IWipeEmpty,
  IArchivePurgeable {

  public string Id => "Ecryptfs";
  public string DisplayName => "eCryptfs";
  public FormatCategory Category => FormatCategory.Archive;

  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList
    | FormatCapabilities.CanExtract
    | FormatCapabilities.CanCreate
    | FormatCapabilities.SupportsPassword;

  public string DefaultExtension => ".ecryptfs";
  public IReadOnlyList<string> Extensions => [".ecryptfs"];
  public IReadOnlyList<string> CompoundExtensions => [];

  /// <summary>
  /// eCryptfs has no fixed byte magic: its marker is two random 32-bit words
  /// whose XOR equals 0x3C81B7F5. Static signature matching cannot express that
  /// relation, so detection falls back to the extension and the reader performs
  /// the authoritative relational check.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];

  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("aes128", "AES-128-CBC"),
    new("aes192", "AES-192-CBC"),
    new("aes256", "AES-256-CBC"),
  ];

  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Linux eCryptfs per-file lower-file encryption container.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var reader = new EcryptfsReader(stream);
    if (reader.DecryptedSize == 0) return [];
    return [new ArchiveEntryInfo(
      0,
      "content.bin",
      checked((long)reader.DecryptedSize),
      reader.CanonicalLength - reader.MetadataSize,
      reader.CipherDescription,
      IsDirectory: false,
      IsEncrypted: true,
      LastModified: null
    )];
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var reader = new EcryptfsReader(stream);
    if (reader.DecryptedSize == 0) return;
    if (files != null && !MatchesFilter("content.bin", files)) return;
    if (password is null)
      throw new ArgumentException("A passphrase is required to extract an eCryptfs lower file.", nameof(password));
    WriteFile(outputDir, "content.bin", reader.ExtractContent(password));
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    if (options.Password is null)
      throw new ArgumentException("A passphrase is required to create an eCryptfs lower file.", nameof(options));

    var files = inputs.Where(i => !i.IsDirectory).ToList();
    if (inputs.Any(i => i.IsDirectory))
      throw new NotSupportedException("An eCryptfs lower file represents one regular file; directories are not embedded in it.");
    if (files.Count > 1)
      throw new NotSupportedException("An eCryptfs lower file can contain exactly one logical file payload.");

    var content = files.Count == 0 ? Array.Empty<byte>() : files[0].ReadContent();
    var method = options.EncryptionMethod ?? options.MethodName;
    EcryptfsCodec.Create(output, content, options.Password, EcryptfsCodec.ResolveKeySize(method));
  }

  /// <summary>
  /// Clears metadata padding and bytes beyond the canonical encrypted extent set.
  /// Ciphertext inside the final live extent is never touched: plaintext tail
  /// zeroing would require the passphrase, which the wipe interface intentionally
  /// does not carry.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true)
    => EcryptfsCodec.WipeUnusedSpace(image);

  /// <summary>
  /// Drops any bytes after the encrypted extents implied by the header. This is
  /// the only meaningful compact/shrink operation for a single eCryptfs lower file.
  /// </summary>
  public void Shrink(Stream input, Stream output) => EcryptfsCodec.Shrink(input, output);

  /// <summary>
  /// Removes the logical payload without requiring the FEK: set plaintext length
  /// to zero and truncate all ciphertext extents while retaining valid key metadata.
  /// </summary>
  public void Purge(Stream archive) => EcryptfsCodec.Purge(archive);
}
