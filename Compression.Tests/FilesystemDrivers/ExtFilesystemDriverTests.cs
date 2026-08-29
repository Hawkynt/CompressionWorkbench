using Compression.Lib;
using Compression.Registry;
using FileSystem.Ext;

namespace Compression.Tests.FilesystemDrivers;

[TestFixture]
public sealed class ExtFilesystemDriverTests {
  [OneTimeSetUp]
  public void Init() => FormatRegistration.EnsureInitialized();

  [Test]
  public void RegistryUsesNativeExtSidecar() {
    var coverage = FormatRegistry.GetFilesystemDriverCoverage("Ext");
    Assert.That(coverage.Binding, Is.EqualTo(FilesystemDriverBindingKind.SidecarNative));
    Assert.That(coverage.HasNativeReadinessProvider, Is.True);
  }

  [Test]
  public void NativeSessionProvidesStableInodeIdentityAndPositionalReads() {
    var payload = Enumerable.Range(0, 20_000).Select(i => (byte)(i * 31)).ToArray();
    var writer = new ExtWriter();
    writer.AddFile("dir/file.bin", payload);
    var image = writer.Build();

    using var stream = new MemoryStream(image, writable: false);
    var profile = FormatRegistry.ProbeFilesystem("Ext", stream);
    Assert.That(profile.CanMount, Is.True, string.Join("; ", profile.Limitations));
    Assert.That(profile.CanMountWritable, Is.False);

    stream.Position = 0;
    using var session = FormatRegistry.OpenFilesystem(
      "Ext", stream, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));

    var dir = session.Lookup(session.RootNodeId, "dir");
    Assert.That(dir, Is.Not.Null);
    var file = session.Lookup(dir!.Value, "file.bin");
    Assert.That(file, Is.Not.Null);

    var firstStat = session.Stat(file!.Value);
    var secondStat = session.Stat(file.Value);
    Assert.That(secondStat.NodeId, Is.EqualTo(firstStat.NodeId));
    Assert.That(firstStat.NodeId.Value, Is.GreaterThan(0));
    Assert.That(firstStat.Size, Is.EqualTo(payload.Length));

    using var handle = session.OpenFile(file.Value, FileAccess.Read);
    var slice = new byte[777];
    var read = handle.Read(12_345, slice);
    Assert.That(read, Is.EqualTo(slice.Length));
    Assert.That(slice, Is.EqualTo(payload.AsSpan(12_345, slice.Length).ToArray()));
  }

  [Test]
  public void WritableMountStaysFailClosed() {
    var writer = new ExtWriter();
    writer.AddFile("x", "data"u8.ToArray());
    using var stream = new MemoryStream(writer.Build(), writable: true);

    Assert.Throws<NotSupportedException>(() =>
      FormatRegistry.OpenFilesystem(
        "Ext", stream, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true)));
  }
}
