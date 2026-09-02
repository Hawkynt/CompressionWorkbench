#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.ResourceDll;

/// <summary>
/// Format descriptor for resource-DLL archives — PE32+ DLLs whose only payload is
/// a populated <c>.rsrc</c> section holding files as <c>RT_RCDATA</c> resources.
/// Detection by magic alone matches every PE; <c>List</c>/<c>Extract</c> validate
/// the structure (a PE without an <c>RT_RCDATA</c> tree yields zero entries rather
/// than throwing). The <c>.resource.dll</c> compound extension routes file-by-name
/// dispatch here without claiming all <c>.dll</c> files.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://learn.microsoft.com/en-us/windows/win32/debug/pe-format</c> — PE/COFF specification — defines the .rsrc resource section</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Portable_Executable</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class ResourceDllFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable {

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive)
    => throw new NotSupportedException(
      "ResourceDll is a PE32+ DLL with RVAs, section alignment, and import tables — " +
      "rebuilding from RT_RCDATA blobs alone would destroy the PE structure.");
  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive, DefragOptions options) => this.Defragment(archive);

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "ResourceDll";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Resource DLL (Win32 RT_RCDATA archive)";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".resource.dll";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [".resource.dll"];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    // Lower confidence than PeResources (0.25). ResourceDll is the narrow view over
    // *our* writer's compound .resource.dll files (RT_RCDATA by name only); a random
    // Windows PE should surface through PeResources instead.
    new([(byte)'M', (byte)'Z'], Confidence: 0.15),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("rcdata", "Stored as RT_RCDATA")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description =>
    "Win32 PE DLL with embedded files as RT_RCDATA resources; readable by " +
    "LoadLibraryEx+FindResource (native) or any PE resource parser (cross-platform).";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new ResourceDllReader().Read(stream);
    return entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Data.Length, e.Data.Length, "Stored", false, false, null
    )).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in new ResourceDllReader().Read(stream)) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new ResourceDllWriter();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddFile(i.ArchiveName, i.ReadContent());
    }
    w.WriteTo(output);
  }
}
