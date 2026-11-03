#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Tar;

namespace FileFormat.Cbt;

/// <summary>
/// Comic book archive — a POSIX tar container of sequentially named page images, conventionally suffixed .cbt.
/// Byte-identical to a tar archive, so it is identified by extension only and every operation is the tar one.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://en.wikipedia.org/wiki/Comic_book_archive</c> — the .cbt/.cbz/.cbr naming convention</description></item>
///   <item><description><c>https://pubs.opengroup.org/onlinepubs/9799919799/utilities/pax.html</c> — POSIX ustar/pax interchange format — the underlying container</description></item>
/// </list>
/// </summary>
public sealed class CbtFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {

  private static readonly TarFormatDescriptor Inner = new();

  public string Id => "Cbt";
  public string DisplayName => "CBT";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: tar's own native append/remove editors.
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".cbt";
  public IReadOnlyList<string> Extensions => [".cbt"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // No magic: a .cbt is a plain tar, so a content scan resolves to Tar.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("tar", "tar")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Comic book tar archive";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) => Inner.List(stream, password);

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => Inner.Extract(stream, outputDir, password, files);

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options)
    => Inner.Create(output, inputs, options);

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) => ((IArchiveModifiable)Inner).Add(archive, inputs);

  public void Remove(Stream archive, string[] entryNames) => ((IArchiveModifiable)Inner).Remove(archive, entryNames);
}
