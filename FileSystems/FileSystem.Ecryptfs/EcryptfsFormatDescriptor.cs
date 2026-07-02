#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Ecryptfs;

/// <summary>
/// Read-only descriptor for eCryptfs per-file encryption containers.
/// eCryptfs (Linux) stacks on top of any underlying FS and stores each
/// encrypted file with a 4-byte big-endian marker <c>0x3C81B7F5</c> at
/// offset 0 followed by an 8-byte decrypted size, 4-byte flags, and
/// 4-byte extent-size hint. Decryption requires the user's passphrase +
/// EFEK packets — out of scope. The encrypted payload is surfaced as a
/// single opaque entry along with the parsed header metadata.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://docs.kernel.org/filesystems/ecryptfs.html</c> — Linux kernel eCryptfs documentation</description></item>
///   <item><description><c>https://github.com/torvalds/linux/tree/master/fs/ecryptfs</c> — mainline implementation (<c>ecryptfs_kernel.h</c> defines the file-header marker + packet layout)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/ECryptfs</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class EcryptfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveDefragmentable {
  public string Id => "Ecryptfs";
  public string DisplayName => "eCryptfs";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract;
  public string DefaultExtension => ".ecryptfs";
  public IReadOnlyList<string> Extensions => [".ecryptfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // eCryptfs marker: 0x3C81B7F5 big-endian at file offset 0.
    new([0x3C, 0x81, 0xB7, 0xF5], Offset: 0, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "eCryptfs file-level encryption container — header surface + opaque ciphertext.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new EcryptfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new EcryptfsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Defragment(Stream archive)
    => throw new NotSupportedException("Ecryptfs read-only — defragmentation requires a writer.");

  public void Defragment(Stream archive, DefragOptions options)
    => throw new NotSupportedException("Ecryptfs read-only — defragmentation requires a writer.");
}
