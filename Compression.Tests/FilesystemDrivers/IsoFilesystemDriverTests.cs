using Compression.Lib;
using Compression.Registry;
using FileSystem.Iso;

namespace Compression.Tests.FilesystemDrivers;

[TestFixture]
public sealed class IsoFilesystemDriverTests {
  [OneTimeSetUp]
  public void InitializeRegistry() => FormatRegistration.EnsureInitialized();

  [Test, Category("Driver")]
  public void GeneratedSidecar_IsRegisteredForIso() {
    var driver = FormatRegistry.GetFilesystemDriver("Iso");
    var coverage = FormatRegistry.GetFilesystemDriverCoverage("Iso");

    Assert.Multiple(() => {
      Assert.That(driver, Is.TypeOf<IsoFilesystemDriverAdapter>());
      Assert.That(coverage.Binding, Is.EqualTo(FilesystemDriverBindingKind.SidecarNative));
      Assert.That(coverage.HasExtentMap, Is.True);
      Assert.That(coverage.HasBlockMover, Is.True);
      Assert.That(coverage.HasNativeReadinessProvider, Is.True);
    });
  }

  [Test, Category("Driver")]
  public void NativeIsoSession_ReadsNestedArbitraryOffsetsWithoutExtraction() {
    var payload = Enumerable.Range(0, 9000).Select(static i => (byte)(i * 31 + 17)).ToArray();
    var writer = new IsoWriter();
    writer.AddFile("docs/api/reference.bin", payload);
    writer.AddFile("README.TXT", "root"u8.ToArray());
    using var image = new MemoryStream(writer.Build(), writable: false);

    var profile = FormatRegistry.ProbeFilesystem("Iso", image);
    Assert.Multiple(() => {
      Assert.That(profile.CanMount, Is.True);
      Assert.That(profile.CanMountWritable, Is.False);
      Assert.That(profile.Capabilities.HasFlag(FilesystemDriverCapabilities.RandomAccess), Is.True);
      Assert.That(profile.ProfileName, Does.Contain("ECMA-119"));
    });

    using var session = FormatRegistry.OpenFilesystem(
      "Iso",
      image,
      new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));

    var docs = session.Lookup(session.RootNodeId, "DOCS");
    Assert.That(docs, Is.Not.Null, "ISO lookup should fold case when the decoded namespace is unambiguous.");
    var api = session.Lookup(docs!.Value, "api");
    Assert.That(api, Is.Not.Null);
    var file = session.Lookup(api!.Value, "REFERENCE.BIN");
    Assert.That(file, Is.Not.Null);

    using var handle = session.OpenFile(file!.Value, FileAccess.Read);
    var middle = new byte[3077];
    Assert.That(handle.Read(1973, middle), Is.EqualTo(middle.Length));
    Assert.That(middle, Is.EqualTo(payload.AsSpan(1973, middle.Length).ToArray()));

    var tail = new byte[128];
    Assert.That(handle.Read(payload.Length - 29, tail), Is.EqualTo(29));
    Assert.That(tail.AsSpan(0, 29).ToArray(), Is.EqualTo(payload[^29..]));
    Assert.That(handle.Read(payload.Length, tail), Is.Zero);
  }

  [Test, Category("Driver"), Category("Contract")]
  public void ProbePreservesCallerStreamPosition() {
    var writer = new IsoWriter();
    writer.AddFile("A.TXT", "abc"u8.ToArray());
    using var image = new MemoryStream(writer.Build(), writable: false) { Position = 1234 };

    var profile = new IsoFilesystemDriverAdapter().ProbeFilesystem(image);

    Assert.Multiple(() => {
      Assert.That(profile.CanMount, Is.True);
      Assert.That(image.Position, Is.EqualTo(1234));
    });
  }

  [Test, Category("Driver"), Category("Corruption")]
  public void TruncatedIsoFailsClosed() {
    var writer = new IsoWriter();
    writer.AddFile("BROKEN.BIN", Enumerable.Repeat((byte)0xA5, 8192).ToArray());
    var bytes = writer.Build();
    Array.Resize(ref bytes, bytes.Length / 2);
    using var image = new MemoryStream(bytes, writable: false);

    var profile = new IsoFilesystemDriverAdapter().ProbeFilesystem(image);

    Assert.That(profile.CanMount, Is.False);
  }

  [Test, Category("Driver"), Category("Contract")]
  public void IsoReadinessSeparatesOfflineEditingFromMountedWrites() {
    var writer = new IsoWriter();
    writer.AddFile("A.TXT", "abc"u8.ToArray());
    using var image = new MemoryStream(writer.Build(), writable: true);

    var report = FormatRegistry.AssessFilesystemDriver("Iso", image, FilesystemDriverTarget.ReadWrite);

    Assert.Multiple(() => {
      Assert.That(report.UsesNativeProvider, Is.True);
      Assert.That(report.Derivable, Is.False);
      Assert.That(report.AvailableLayers.HasFlag(FilesystemDriverReadinessLayer.AllocationMap), Is.True);
      Assert.That(report.AvailableLayers.HasFlag(FilesystemDriverReadinessLayer.WriteData), Is.False);
      Assert.That(report.Blockers.Any(text => text.Contains("offline", StringComparison.OrdinalIgnoreCase)), Is.True);
    });
  }

  [Test, Category("Driver"), Category("Contract")]
  public void NativeIsoSessionRejectsMutation() {
    var writer = new IsoWriter();
    writer.AddFile("A.TXT", "abc"u8.ToArray());
    using var image = new MemoryStream(writer.Build(), writable: false);
    using var session = new IsoFilesystemDriverAdapter().OpenFilesystem(
      image,
      new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));

    Assert.Throws<NotSupportedException>(() => session.CreateFile(session.RootNodeId, "NEW.TXT"));
    Assert.Throws<NotSupportedException>(() => session.OpenFile(
      session.Lookup(session.RootNodeId, "A.TXT")!.Value,
      FileAccess.Write));
  }
}
