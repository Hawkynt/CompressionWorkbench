using Compression.Lib;
using Compression.Registry;
using FileSystem.Apfs;

namespace Compression.Tests.FilesystemDrivers;

[TestFixture]
public sealed class ApfsFilesystemDriverTests {
  [OneTimeSetUp]
  public void Init() => FormatRegistration.EnsureInitialized();

  [Test]
  public void RegistryUsesNativeApfsSidecar() {
    var coverage = FormatRegistry.GetFilesystemDriverCoverage("Apfs");
    Assert.That(coverage.Binding, Is.EqualTo(FilesystemDriverBindingKind.SidecarNative));
    Assert.That(coverage.HasExtentMap, Is.True);
    Assert.That(coverage.HasBlockMover, Is.True);
  }

  [Test]
  public void NativeWriterProfileUsesObjectIdentityAndDirectPositionalReads() {
    var payload = Enumerable.Range(0, 20_000).Select(i => (byte)(i * 13 + 5)).ToArray();
    var writer = new ApfsWriter();
    writer.SetMinImageSize(4 * 1024 * 1024);
    writer.AddFile("dir/data.bin", payload);
    var image = writer.Build();

    using var stream = new MemoryStream(image, writable: false);
    var profile = FormatRegistry.ProbeFilesystem("Apfs", stream);
    Assert.That(profile.CanMount, Is.True, string.Join("; ", profile.Limitations));
    Assert.That(profile.CanMountWritable, Is.False);

    stream.Position = 0;
    using var session = FormatRegistry.OpenFilesystem(
      "Apfs", stream, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    var dir = session.Lookup(session.RootNodeId, "dir");
    Assert.That(dir, Is.Not.Null);
    var file = session.Lookup(dir!.Value, "data.bin");
    Assert.That(file, Is.Not.Null);

    var stat = session.Stat(file!.Value);
    Assert.That(stat.NodeId.Value, Is.GreaterThanOrEqualTo((ulong)ApfsConstants.APFS_MIN_USER_INO_NUM));
    Assert.That(stat.Size, Is.EqualTo(payload.Length));

    using var handle = session.OpenFile(file.Value, FileAccess.Read);
    var slice = new byte[1025];
    var read = handle.Read(12_345, slice);
    Assert.That(read, Is.EqualTo(slice.Length));
    Assert.That(slice, Is.EqualTo(payload.AsSpan(12_345, slice.Length).ToArray()));
  }

  [Test]
  public void WritableMountStaysFailClosed() {
    var writer = new ApfsWriter();
    writer.SetMinImageSize(4 * 1024 * 1024);
    writer.AddFile("x", "x"u8.ToArray());
    using var stream = new MemoryStream(writer.Build(), writable: true);

    Assert.Throws<NotSupportedException>(() =>
      FormatRegistry.OpenFilesystem(
        "Apfs", stream, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true)));
  }
}
