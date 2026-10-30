#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Ace;

namespace FileFormat.Cba;

/// <summary>
/// Comic book archive: an ACE container of sequentially named page images, conventionally suffixed
/// .cba. It is byte-identical to an ACE archive, so it is identified by extension only and every
/// operation is the ACE one.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://en.wikipedia.org/wiki/Comic_book_archive</c> — the .cba/.cbz/.cbr naming convention</description></item>
///   <item><description><c>https://github.com/droe/acefile</c> — acefile, the ACE reference reader the container is verified against</description></item>
/// </list>
/// </summary>
public sealed class CbaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {

  private static readonly AceFormatDescriptor Inner = new();

  public string Id => "Cba";
  public string DisplayName => "CBA";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: ACE's writer (acefile-verified) and its rebuild-based edits.
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".cba";
  public IReadOnlyList<string> Extensions => [".cba"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // No magic: a .cba is a plain ACE archive, so a content scan resolves to Ace.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => Inner.Methods;
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Comic book ACE archive";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) => Inner.List(stream, password);

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => Inner.Extract(stream, outputDir, password, files);

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options)
    => Inner.Create(output, inputs, options);

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) => ((IArchiveModifiable)Inner).Add(archive, inputs);

  public void Remove(Stream archive, string[] entryNames) => ((IArchiveModifiable)Inner).Remove(archive, entryNames);
}
