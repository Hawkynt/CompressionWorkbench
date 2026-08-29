using Compression.Lib;
using Compression.Registry;
using FileSystem.Ntfs;

namespace Compression.Tests.FilesystemDrivers;

[TestFixture]
public sealed class NtfsFilesystemDriverTests {
  [OneTimeSetUp]
  public void Init() => FormatRegistration.EnsureInitialized();

  [Test]
  public void RegistryUsesNativeNtfsSidecar() {
    var coverage = FormatRegistry.GetFilesystemDriverCoverage("Ntfs");
    Assert.That(coverage.Binding, Is.EqualTo(FilesystemDriverBindingKind.SidecarNative));
    Assert.That(coverage.HasExtentMap, Is.True);
    Assert.That(coverage.HasBlockMover, Is.True);
  }

  [Test]
  public void NativeSessionUsesMftIdentityAndSupportsPositionalReads() {
    var payload = Enumerable.Range(0, 200 * 1024).Select(i => (byte)(i * 29 + 7)).ToArray();
    var writer = new NtfsWriter();
    writer.AddFile("dir/data.bin", payload);
    var image = writer.Build(8 * 1024 * 1024);

    using var stream = new MemoryStream(image, writable: false);
    var profile = FormatRegistry.ProbeFilesystem("Ntfs", stream);
    Assert.That(profile.CanMount, Is.True, string.Join("; ", profile.Limitations));
    Assert.That(profile.CanMountWritable, Is.False);

    stream.Position = 0;
    using var session = FormatRegistry.OpenFilesystem(
      "Ntfs", stream, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));

    var dir = session.Lookup(session.RootNodeId, "dir");
    Assert.That(dir, Is.Not.Null);
    var file = session.Lookup(dir!.Value, "data.bin");
    Assert.That(file, Is.Not.Null);
    Assert.That(file!.Value.Value, Is.GreaterThan(15), "user object id should be its non-reserved MFT record");

    using var handle = session.OpenFile(file.Value, FileAccess.Read);
    var slice = new byte[2049];
    var read = handle.Read(73_333, slice);
    Assert.That(read, Is.EqualTo(slice.Length));
    Assert.That(slice, Is.EqualTo(payload.AsSpan(73_333, slice.Length).ToArray()));
  }

  [Test]
  public void WritableMountStaysFailClosed() {
    var writer = new NtfsWriter();
    writer.AddFile("x.txt", "x"u8.ToArray());
    using var stream = new MemoryStream(writer.Build(), writable: true);

    Assert.Throws<NotSupportedException>(() =>
      FormatRegistry.OpenFilesystem(
        "Ntfs", stream, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true)));
  }
}
