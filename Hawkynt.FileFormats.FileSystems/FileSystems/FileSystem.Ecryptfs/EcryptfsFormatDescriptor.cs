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
  IArchiveModifiable,
  IArchiveShrinkable,
  IWipeEmpty {

  public string Id => "Ecryptfs";
  public string DisplayName => "eCryptfs";
  public FormatCategory Category => FormatCategory.Archive;

  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList
    | FormatCapabilities.CanExtract
    | FormatCapabilities.CanCreate
    | FormatCapabilities.CanModify
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
  public string Description => "Linux eCryptfs per-file lower-file encryption container — passphrase AES read/create/replace/remove.";

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
  /// Credential-free mutation cannot safely replace encrypted plaintext. Callers
  /// must use the <see cref="IArchiveModifiable.Add(Stream,IReadOnlyList{ArchiveInputInfo},ArchiveMutationOptions)"/>
  /// overload instead of relying on ambient or format-specific password state.
  /// </summary>
  void IArchiveModifiable.Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)
    => throw new InvalidOperationException(
      "eCryptfs replacement requires a passphrase; use the credential-aware IArchiveModifiable.Add overload.");

  /// <summary>
  /// Replaces the one logical upper-file payload after validating the existing
  /// lower file with the supplied passphrase. A complete replacement lower file
  /// is staged before the caller's stream is changed, so a bad passphrase or
  /// writer failure leaves the original bytes untouched.
  /// </summary>
  void IArchiveModifiable.Add(
    Stream archive,
    IReadOnlyList<ArchiveInputInfo> inputs,
    ArchiveMutationOptions options
  ) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("eCryptfs mutation requires a readable, writable, seekable stream.", nameof(archive));
    if (options.Password is null)
      throw new ArgumentException("A passphrase is required to modify an eCryptfs lower file.", nameof(options));
    if (inputs.Any(i => i.IsDirectory))
      throw new NotSupportedException("An eCryptfs lower file represents one regular file; directories cannot be added.");

    var files = inputs.Where(i => !i.IsDirectory).ToList();
    if (files.Count != 1)
      throw new NotSupportedException("eCryptfs replacement requires exactly one regular-file payload.");

    archive.Position = 0;
    int keySize;
    using (var reader = new EcryptfsReader(archive)) {
      reader.ValidatePassword(options.Password);
      keySize = reader.CipherDescription switch {
        "AES-128-CBC" => 16,
        "AES-192-CBC" => 24,
        "AES-256-CBC" => 32,
        _ => throw new NotSupportedException($"Cannot rewrite eCryptfs cipher '{reader.CipherDescription}'."),
      };
    }

    var content = files[0].ReadContent();
    using var staged = new MemoryStream();
    EcryptfsCodec.Create(staged, content, options.Password, keySize);

    staged.Position = 0;
    archive.Position = 0;
    archive.SetLength(0);
    staged.CopyTo(archive);
    archive.Position = 0;
  }

  /// <summary>
  /// Removes the single logical payload. This does not require the FEK: the
  /// plaintext length can be set to zero and ciphertext extents discarded while
  /// retaining valid authentication-token metadata.
  /// </summary>
  void IArchiveModifiable.Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(entryNames);
    if (entryNames.Any(IsContentName))
      EcryptfsCodec.Purge(archive);
  }

  void IArchiveModifiable.Remove(Stream archive, string[] entryNames, ArchiveMutationOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    ((IArchiveModifiable)this).Remove(archive, entryNames);
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

  private static bool IsContentName(string name)
    => Path.GetFileName(name.Replace('\\', '/')).Equals("content.bin", StringComparison.OrdinalIgnoreCase);
}
