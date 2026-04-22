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
/// </summary>
public sealed class ResourceDllFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "ResourceDll";
  public string DisplayName => "Resource DLL (Win32 RT_RCDATA archive)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".resource.dll";
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [".resource.dll"];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // Lower confidence than PeResources (0.25). ResourceDll is the narrow view over
    // *our* writer's compound .resource.dll files (RT_RCDATA by name only); a random
    // Windows PE should surface through PeResources instead.
    new([(byte)'M', (byte)'Z'], Confidence: 0.15),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("rcdata", "Stored as RT_RCDATA")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Win32 PE DLL with embedded files as RT_RCDATA resources; readable by " +
    "LoadLibraryEx+FindResource (native) or any PE resource parser (cross-platform).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new ResourceDllReader().Read(stream);
    return entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Data.Length, e.Data.Length, "Stored", false, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in new ResourceDllReader().Read(stream)) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new ResourceDllWriter();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddFile(i.ArchiveName, File.ReadAllBytes(i.FullPath));
    }
    w.WriteTo(output);
  }
}
