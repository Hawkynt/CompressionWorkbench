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
  public void TruncatedFileExtentFailsClosed() {
    var payload = Enumerable.Range(0, 8192).Select(static i => (byte)(i * 7 + 3)).ToArray();
    var writer = new IsoWriter();
    writer.AddFile("BROKEN.BIN", payload);
    var bytes = writer.Build();

    // The written volume ends in trailing pad sectors, so a fraction of the
    // total length says nothing about whether any extent was actually lost.
    // Cut one byte short of the payload instead.
    var dataStart = IndexOf(bytes, payload.AsSpan(0, 64));
    Assert.That(dataStart, Is.GreaterThan(0), "the built image should carry the payload verbatim.");
    Array.Resize(ref bytes, dataStart + payload.Length - 1);
    using var image = new MemoryStream(bytes, writable: false);

    var profile = new IsoFilesystemDriverAdapter().ProbeFilesystem(image);

    Assert.Multiple(() => {
      Assert.That(profile.CanMount, Is.False);
      Assert.That(profile.Limitations[0], Does.Contain("outside the image"));
    });
  }

  [Test, Category("Driver"), Category("Corruption")]
  public void TruncatedDirectoryExtentFailsClosed() {
    var writer = new IsoWriter();
    writer.AddFile("A.TXT", "abc"u8.ToArray());
    var bytes = writer.Build();

    // Keep the volume descriptors, drop every directory extent behind them.
    // The reader decodes such an image as an empty namespace, which must not
    // be published as a mountable filesystem.
    Array.Resize(ref bytes, 17 * 2048);
    using var image = new MemoryStream(bytes, writable: false);

    var profile = new IsoFilesystemDriverAdapter().ProbeFilesystem(image);

    Assert.Multiple(() => {
      Assert.That(profile.CanMount, Is.False);
      Assert.That(profile.Limitations[0], Does.Contain("outside the image"));
    });
  }

  [Test, Category("Driver"), Category("Corruption")]
  public void ImageMissingOnlyTrailingPadStaysMountable() {
    var payload = Enumerable.Range(0, 8192).Select(static i => (byte)(i * 7 + 3)).ToArray();
    var writer = new IsoWriter();
    writer.AddFile("KEPT.BIN", payload);
    var bytes = writer.Build();

    var dataStart = IndexOf(bytes, payload.AsSpan(0, 64));
    Assert.That(dataStart, Is.GreaterThan(0));
    Array.Resize(ref bytes, dataStart + payload.Length);
    using var image = new MemoryStream(bytes, writable: false);

    var profile = new IsoFilesystemDriverAdapter().ProbeFilesystem(image);
    Assert.That(profile.CanMount, Is.True, "dropping pad sectors loses no addressed extent.");

    using var session = new IsoFilesystemDriverAdapter().OpenFilesystem(
      image,
      new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    using var handle = session.OpenFile(session.Lookup(session.RootNodeId, "KEPT.BIN")!.Value, FileAccess.Read);
    var tail = new byte[64];
    Assert.That(handle.Read(payload.Length - tail.Length, tail), Is.EqualTo(tail.Length));
    Assert.That(tail, Is.EqualTo(payload[^tail.Length..]));
  }

  private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle) {
    for (var i = 0; i + needle.Length <= haystack.Length; ++i)
      if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
        return i;

    return -1;
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
