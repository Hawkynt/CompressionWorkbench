using Compression.Lib;
using Compression.Registry;
using FileSystem.Xfs;

namespace Compression.Tests.FilesystemDrivers;

[TestFixture]
public sealed class XfsFilesystemDriverTests {
  [OneTimeSetUp]
  public void Init() => FormatRegistration.EnsureInitialized();

  [Test]
  public void RegistryUsesNativeXfsSidecar() {
    var coverage = FormatRegistry.GetFilesystemDriverCoverage("Xfs");
    Assert.That(coverage.Binding, Is.EqualTo(FilesystemDriverBindingKind.SidecarNative));
    Assert.That(coverage.HasExtentMap, Is.True);
    Assert.That(coverage.HasBlockMover, Is.True);
  }

  [Test]
  public void NativeSessionUsesInodeIdentityAndStreamsPositionalReads() {
    var payload = Enumerable.Range(0, 80_000).Select(i => (byte)(i * 17 + 11)).ToArray();
    var writer = new XfsWriter();
    writer.AddFile("a/b.bin", payload);
    var image = writer.Build();

    using var stream = new MemoryStream(image, writable: false);
    var profile = FormatRegistry.ProbeFilesystem("Xfs", stream);
    Assert.That(profile.CanMount, Is.True, string.Join("; ", profile.Limitations));
    Assert.That(profile.CanMountWritable, Is.False);

    stream.Position = 0;
    using var session = FormatRegistry.OpenFilesystem(
      "Xfs", stream, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    var a = session.Lookup(session.RootNodeId, "a");
    Assert.That(a, Is.Not.Null);
    var file = session.Lookup(a!.Value, "b.bin");
    Assert.That(file, Is.Not.Null);

    var stat = session.Stat(file!.Value);
    Assert.That(stat.NodeId.Value, Is.GreaterThan(0));
    Assert.That(stat.Size, Is.EqualTo(payload.Length));

    using var handle = session.OpenFile(file.Value, FileAccess.Read);
    var slice = new byte[1537];
    var read = handle.Read(31_337, slice);
    Assert.That(read, Is.EqualTo(slice.Length));
    Assert.That(slice, Is.EqualTo(payload.AsSpan(31_337, slice.Length).ToArray()));
  }

  [Test]
  public void WritableMountStaysFailClosed() {
    var writer = new XfsWriter();
    writer.AddFile("x", "x"u8.ToArray());
    using var stream = new MemoryStream(writer.Build(), writable: true);

    Assert.Throws<NotSupportedException>(() =>
      FormatRegistry.OpenFilesystem(
        "Xfs", stream, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true)));
  }
}
