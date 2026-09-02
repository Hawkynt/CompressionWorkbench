using Compression.Registry;
using FileSystem.D64;

namespace Compression.Tests.D64;

[TestFixture]
public sealed class D64FilesystemDriverTests {
  [Test, Category("Driver")]
  public void WritableSession_PreservesNodeIdentityAndPersistsNamespaceAndData() {
    var original = Enumerable.Range(0, 700).Select(i => (byte)(i * 17)).ToArray();
    var writer = new D64Writer();
    writer.AddFile("HELLO", original);
    using var image = new MemoryStream();
    image.Write(writer.Build("DRIVER", "42"));
    image.Position = 0;

    var descriptor = new D64FormatDescriptor();
    var profile = descriptor.ProbeFilesystem(image);
    Assert.Multiple(() => {
      Assert.That(profile.CanMount, Is.True);
      Assert.That(profile.CanMountWritable, Is.True, string.Join("; ", profile.Limitations));
      Assert.That(profile.MutationModel, Is.EqualTo(FilesystemMutationModel.Direct));
      Assert.That(profile.Capabilities.HasFlag(FilesystemDriverCapabilities.RandomAccess), Is.True);
      Assert.That(profile.Capabilities.HasFlag(FilesystemDriverCapabilities.Transactions), Is.False);
    });

    FilesystemNodeId createdId;
    using (var fs = descriptor.OpenFilesystem(image, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true))) {
      var root = fs.RootNodeId;
      var helloId = fs.Lookup(root, "hello");
      Assert.That(helloId.HasValue, Is.True);
      using (var hello = fs.OpenFile(helloId!.Value, FileAccess.Read)) {
        var slice = new byte[97];
        Assert.That(hello.Read(123, slice), Is.EqualTo(slice.Length));
        Assert.That(slice, Is.EqualTo(original.AsSpan(123, slice.Length).ToArray()));
      }

      createdId = fs.CreateFile(root, "newfile");
      using (var handleA = fs.OpenFile(createdId, FileAccess.ReadWrite))
      using (var handleB = fs.OpenFile(createdId, FileAccess.ReadWrite)) {
        handleA.Write(0, "0123456789"u8);
        var observed = new byte[4];
        Assert.That(handleB.Read(3, observed), Is.EqualTo(4));
        Assert.That(observed, Is.EqualTo("3456"u8.ToArray()));
        handleB.Write(4, "ABCD"u8);
        handleA.SetLength(12);
        handleA.Flush();
      }

      fs.Rename(root, "NEWFILE", root, "RENAMED", replace: false);
      Assert.That(fs.Lookup(root, "RENAMED"), Is.EqualTo(createdId), "rename must not change node identity");
      fs.DeleteFile(root, "HELLO");
      fs.Flush();
    }

    image.Position = 0;
    using var reopened = descriptor.OpenFilesystem(image, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    var reopenedRoot = reopened.RootNodeId;
    Assert.Multiple(() => {
      Assert.That(reopened.Lookup(reopenedRoot, "HELLO"), Is.Null);
      Assert.That(reopened.Lookup(reopenedRoot, "RENAMED").HasValue, Is.True);
    });
    var renamedId = reopened.Lookup(reopenedRoot, "RENAMED")!.Value;
    using var renamed = reopened.OpenFile(renamedId, FileAccess.Read);
    var payload = new byte[12];
    Assert.That(renamed.Read(0, payload), Is.EqualTo(payload.Length));
    Assert.That(payload, Is.EqualTo(new byte[] {
      (byte)'0', (byte)'1', (byte)'2', (byte)'3',
      (byte)'A', (byte)'B', (byte)'C', (byte)'D',
      (byte)'8', (byte)'9', 0, 0,
    }));
  }

  [Test, Category("Driver"), Category("EdgeCase")]
  public void Probe_RefusesWritableMountWhenBamOwnershipIsInconsistent() {
    var writer = new D64Writer();
    writer.AddFile("HELLO", [1, 2, 3]);
    var bytes = writer.Build();

    // Track 1 sector 0 is allocated to HELLO by the writer. Lie in the BAM and
    // mark it free while retaining a matching free-count byte.
    const int bamOffset = 17 * 21 * 256; // start of track 18
    bytes[bamOffset + 4]++;
    bytes[bamOffset + 5] |= 0x01;

    using var image = new MemoryStream(bytes, writable: true);
    var profile = new D64FormatDescriptor().ProbeFilesystem(image);
    Assert.Multiple(() => {
      Assert.That(profile.CanMount, Is.True);
      Assert.That(profile.CanMountWritable, Is.False);
      Assert.That(profile.Limitations.Any(x => x.Contains("BAM", StringComparison.OrdinalIgnoreCase)), Is.True);
    });
  }
}
