using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.FilesystemDrivers;

[TestFixture]
public sealed class FilesystemDriverDerivationTests {
  [OneTimeSetUp]
  public void InitializeRegistry() => FormatRegistration.EnsureInitialized();

  [Test, Category("Driver")]
  public void DerivedReadOnlySession_ReconstructsHierarchyAndProvidesPositionalHandles() {
    var descriptor = new ProjectionDescriptor();
    using var image = new MemoryStream(new byte[] { 0x42 }, writable: false);
    using var session = FilesystemDriverDerivation.Open(
      descriptor, image, new FilesystemOpenOptions(ReadOnly: true));

    Assert.That(session.Profile.CanMount, Is.True);
    Assert.That(session.Profile.CanMountWritable, Is.False);
    var root = session.RootNodeId;
    var rootEntries = session.Enumerate(root);
    Assert.That(rootEntries.Select(e => e.Name), Is.EqualTo(new[] { "folder", "link" }));

    var folder = session.Lookup(root, "folder");
    Assert.That(folder, Is.Not.Null);
    Assert.That(session.Lookup(root, "FOLDER"), Is.EqualTo(folder),
      "fallback lookup may case-fold only when the result is unambiguous");
    var dataNode = session.Lookup(folder!.Value, "data.bin");
    Assert.That(dataNode, Is.Not.Null);
    Assert.That(session.Lookup(folder.Value, "data.bin"), Is.EqualTo(dataNode),
      "node ids must remain stable for the session lifetime");

    using var handle = session.OpenFile(dataNode!.Value, FileAccess.Read);
    Span<byte> slice = stackalloc byte[3];
    Assert.That(handle.Read(2, slice), Is.EqualTo(3));
    Assert.That(slice.ToArray(), Is.EqualTo(new byte[] { 3, 4, 5 }));
    Assert.That(handle.Read(99, slice), Is.Zero);

    var link = session.Lookup(root, "link");
    Assert.That(link, Is.Not.Null);
    Assert.That(session.Stat(link!.Value).Kind, Is.EqualTo(FilesystemNodeKind.SymbolicLink));
    Assert.That(session.ReadSymbolicLink(link.Value), Is.EqualTo("folder/data.bin"));

    Assert.That(() => session.CreateFile(root, "new.bin"), Throws.InstanceOf<NotSupportedException>());
    Assert.That(() => session.BeginTransaction(), Throws.InstanceOf<NotSupportedException>());
  }

  [Test, Category("Driver"), Category("Contract")]
  public void EveryRegisteredFilesystemDescriptor_HasAtLeastReadOnlyDriverDerivationSurface() {
    var filesystemDescriptors = FormatRegistry.All
      .Where(descriptor => descriptor.GetType().Assembly.GetName().Name
        ?.StartsWith("FileSystem.", StringComparison.Ordinal) == true)
      .ToArray();

    Assert.That(filesystemDescriptors, Is.Not.Empty, "No FileSystem.* descriptors were registered.");
    var missing = filesystemDescriptors
      .Where(descriptor => descriptor is not IFilesystemDriverProvider && descriptor is not IArchiveFormatOperations)
      .Select(descriptor => $"{descriptor.Id} ({descriptor.GetType().FullName})")
      .OrderBy(name => name, StringComparer.Ordinal)
      .ToArray();

    Assert.That(missing, Is.Empty,
      "Every FileSystem.* descriptor must expose either a native filesystem provider or List/OpenEntry so the common read-only driver can be derived. Missing: " +
      string.Join(", ", missing));
  }

  [Test, Category("Driver"), Category("Contract")]
  public void ArchiveModifyCapability_IsNeverPromotedToMountedWriteSupport() {
    var descriptor = new ProjectionDescriptor();
    using var image = new MemoryStream(new byte[] { 0x42 }, writable: true);

    var profile = FilesystemDriverDerivation.Probe(descriptor, image);
    Assert.That(profile.CanMount, Is.True);
    Assert.That(profile.CanMountWritable, Is.False);
    Assert.That(() => FilesystemDriverDerivation.Open(
      descriptor, image, new FilesystemOpenOptions(ReadOnly: false)),
      Throws.InstanceOf<NotSupportedException>());
  }

  private sealed class ProjectionDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveModifiable {
    private static readonly byte[] Data = [1, 2, 3, 4, 5, 6];

    public string Id => "DriverProjectionTest";
    public string DisplayName => "driver projection test";
    public FormatCategory Category => FormatCategory.Archive;
    public FormatCapabilities Capabilities =>
      FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanModify |
      FormatCapabilities.SupportsMultipleEntries;
    public string DefaultExtension => ".drvtest";
    public IReadOnlyList<string> Extensions => [".drvtest"];
    public IReadOnlyList<string> CompoundExtensions => [];
    public IReadOnlyList<MagicSignature> MagicSignatures => [];
    public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
    public string? TarCompressionFormatId => null;

    public List<ArchiveEntryInfo> List(Stream stream, string? password) => [
      new(0, "folder", 0, 0, "directory", true, false, null),
      new(1, "folder/data.bin", Data.Length, Data.Length, "stored", false, false, null),
      new(2, "link", "folder/data.bin".Length, "folder/data.bin".Length,
        "symlink", false, false, null, IsSymlink: true, LinkTarget: "folder/data.bin"),
    ];

    public void Extract(Stream stream, string outputDir, string? password, string[]? files)
      => throw new NotSupportedException("The derivation test uses OpenEntry directly.");

    public Stream OpenEntry(Stream archive, string entryName, string? password) {
      var bytes = string.Equals(entryName, "folder/data.bin", StringComparison.Ordinal)
        ? Data
        : [];
      return new MemoryStream(bytes, writable: false);
    }

    public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)
      => throw new NotSupportedException();
    public void Remove(Stream archive, string[] entryNames)
      => throw new NotSupportedException();
  }
}
