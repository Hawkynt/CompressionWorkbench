#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.TahoeLafs;

/// <summary>
/// Tahoe-LAFS support has two deliberately different profiles:
/// <list type="bullet">
///   <item><description>storage-server share files: local opaque share bytes plus safe outer-container maintenance;</description></item>
///   <item><description><c>.tahoe-cap</c> connection documents: a live capability-backed namespace accessed through Tahoe's documented HTTP gateway.</description></item>
/// </list>
/// The latter is genuine R/W when its root is a directory write-capability; the
/// Tahoe node performs encryption, erasure coding, validation, mutable signatures
/// and share publication. A read-cap connection remains read-only at runtime.
/// </summary>
public sealed class TahoeLafsFormatDescriptor :
  IFormatDescriptor,
  IArchiveFormatOperations,
  IArchiveModifiable,
  IArchiveShrinkable,
  IArchiveDefragmentable,
  IArchiveLayoutMap {

  public string Id => "TahoeLafs";
  public string DisplayName => "Tahoe-LAFS share / capability namespace";
  public FormatCategory Category => FormatCategory.Archive;

  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList
    | FormatCapabilities.CanExtract
    | FormatCapabilities.CanModify
    | FormatCapabilities.SupportsMultipleEntries
    | FormatCapabilities.SupportsDirectories;

  public string DefaultExtension => ".tahoe-share";
  public IReadOnlyList<string> Extensions => [".tahoe-share", ".share", ".tahoe-cap"];
  public IReadOnlyList<string> CompoundExtensions => [];

  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x00, 0x00, 0x00, 0x01], Offset: 0, Confidence: 0.55),
    new([0x00, 0x00, 0x00, 0x02], Offset: 0, Confidence: 0.55),
    new(TahoeLafsContainer.MutableV1Magic.ToArray(), Offset: 0, Confidence: 0.98),
    new(TahoeLafsContainer.MutableV2Magic.ToArray(), Offset: 0, Confidence: 0.98),
    new(Encoding.ASCII.GetBytes(TahoeLafsConnection.Magic), Offset: 0, Confidence: 0.99),
  ];

  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored / Tahoe gateway")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description
    => "Tahoe-LAFS: opaque local storage-share parsing/maintenance plus capability-aware read/write directory access through the official Tahoe HTTP gateway.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    ArgumentNullException.ThrowIfNull(stream);
    if (TahoeLafsConnection.TryRead(stream, out var connection))
      return ListRemote(connection);

    using var reader = new TahoeLafsReader(stream);
    return reader.Entries.Select((entry, index) => new ArchiveEntryInfo(
      index, entry.Name, entry.Size, entry.Size, "Stored", entry.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    ArgumentNullException.ThrowIfNull(stream);
    if (TahoeLafsConnection.TryRead(stream, out var connection)) {
      using var client = new TahoeLafsClient(connection.NodeUri);
      foreach (var entry in client.ListDirectory(connection.RootCapability, recursive: true)) {
        if (entry.IsDirectory) continue;
        if (files != null && !MatchesFilter(entry.Path, files)) continue;
        var capability = entry.ReadCapability ?? entry.WriteCapability
          ?? throw new InvalidDataException("Tahoe-LAFS file entry has no readable capability.");
        WriteFile(outputDir, entry.Path, client.ReadFile(capability));
      }
      return;
    }

    using var reader = new TahoeLafsReader(stream);
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory) continue;
      if (files != null && !MatchesFilter(entry.Name, files)) continue;
      WriteFile(outputDir, entry.Name, reader.Extract(entry));
    }
  }

  /// <summary>
  /// Adds/replaces children in a live Tahoe directory. Raw storage-share files
  /// are not a namespace and therefore reject this operation.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    var connection = RequireWritableConnection(archive);
    using var client = new TahoeLafsClient(connection.NodeUri);
    foreach (var input in inputs) {
      if (string.IsNullOrWhiteSpace(input.ArchiveName)) continue;
      if (input.IsDirectory)
        client.CreateDirectory(connection.RootCapability, input.ArchiveName);
      else
        client.UploadFile(connection.RootCapability, input.ArchiveName, input.ReadContent());
    }
  }

  /// <summary>Unlinks named children from a live Tahoe directory.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    var connection = RequireWritableConnection(archive);
    using var client = new TahoeLafsClient(connection.NodeUri);
    foreach (var entryName in entryNames ?? [])
      if (!string.IsNullOrWhiteSpace(entryName))
        client.Remove(connection.RootCapability, entryName);
  }

  /// <summary>Empties the live root directory without destroying the root capability.</summary>
  public void Purge(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    var connection = RequireWritableConnection(archive);
    using var client = new TahoeLafsClient(connection.NodeUri);
    client.PurgeDirectory(connection.RootCapability);
  }

  public void Shrink(Stream input, Stream output)
    => TahoeLafsMaintenance.Shrink(input, output);

  public void Defragment(Stream archive)
    => TahoeLafsMaintenance.Defragment(archive);

  public void Defragment(Stream archive, DefragOptions options)
    => TahoeLafsMaintenance.Defragment(archive, options);

  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive)
    => TahoeLafsMaintenance.EnumerateLayout(archive);

  private static List<ArchiveEntryInfo> ListRemote(TahoeLafsConnection connection) {
    using var client = new TahoeLafsClient(connection.NodeUri);
    return client.ListDirectory(connection.RootCapability, recursive: true)
      .OrderBy(entry => entry.Path, StringComparer.Ordinal)
      .Select((entry, index) => new ArchiveEntryInfo(
        index,
        entry.Path,
        entry.Size,
        entry.Size,
        entry.Format ?? "Tahoe",
        entry.IsDirectory,
        false,
        null))
      .ToList();
  }

  private static TahoeLafsConnection RequireWritableConnection(Stream archive) {
    if (!TahoeLafsConnection.TryRead(archive, out var connection))
      throw new NotSupportedException(
        "Raw Tahoe-LAFS storage-share files are not an editable namespace; use a .tahoe-cap connection document backed by a Tahoe directory write-capability.");
    if (!connection.CanWrite)
      throw new UnauthorizedAccessException("Tahoe-LAFS root capability is read-only.");
    return connection;
  }
}
