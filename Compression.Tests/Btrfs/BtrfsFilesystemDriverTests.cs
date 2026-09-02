using Compression.Registry;
using FileSystem.Btrfs;

namespace Compression.Tests.Btrfs;

[TestFixture]
public sealed class BtrfsFilesystemDriverTests {
  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void NativeSession_UsesInodeIdentityAndDirectPositionalReads() {
    var payload = new byte[32 * 1024 + 503];
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)((i * 41 + 13) & 0xFF);

    var writer = new BtrfsWriter();
    writer.AddFile("dir/data.bin", payload);
    writer.AddFile("dir/tiny.txt", "tiny"u8.ToArray()); // exercises an inline extent too
    using var image = new MemoryStream();
    writer.WriteTo(image);

    var adapter = new BtrfsFilesystemDriverAdapter();
    image.Position = 0;
    var profile = adapter.ProbeFilesystem(image);
    Assert.That(profile.CanMount, Is.True, string.Join("; ", profile.Limitations));
    Assert.That(profile.CanMountWritable, Is.False);
    Assert.That(profile.Capabilities.HasFlag(FilesystemDriverCapabilities.StableNodeIds), Is.True);
    Assert.That(profile.Capabilities.HasFlag(FilesystemDriverCapabilities.SparseFiles), Is.True);

    image.Position = 0;
    using var session = adapter.OpenFilesystem(image, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    Assert.That(session.RootNodeId.Value, Is.EqualTo(256UL));
    var dir = session.Lookup(session.RootNodeId, "dir");
    Assert.That(dir, Is.Not.Null);
    var file = session.Lookup(dir!.Value, "data.bin");
    Assert.That(file, Is.Not.Null);
    Assert.That(file!.Value.Value, Is.GreaterThan(256UL), "file identity must be the native Btrfs inode object id");

    using var handle = session.OpenFile(file.Value, FileAccess.Read);
    var slice = new byte[911];
    var read = handle.Read(7_777, slice);
    Assert.That(read, Is.EqualTo(slice.Length));
    Assert.That(slice, Is.EqualTo(payload.AsSpan(7_777, slice.Length).ToArray()));

    var tiny = session.Lookup(dir.Value, "tiny.txt");
    Assert.That(tiny, Is.Not.Null);
    using var tinyHandle = session.OpenFile(tiny!.Value, FileAccess.Read);
    var tinyBytes = new byte[4];
    Assert.That(tinyHandle.Read(0, tinyBytes), Is.EqualTo(4));
    Assert.That(tinyBytes, Is.EqualTo("tiny"u8.ToArray()));
  }

  [Test, Category("ErrorHandling")]
  public void Probe_RejectsCompressedExtentInsteadOfSilentlyShorteningFile() {
    var inline = Enumerable.Range(0, 31).Select(i => (byte)(0xA1 + i)).ToArray();
    var writer = new BtrfsWriter();
    writer.AddFile("compressed-marker.bin", inline);
    using var built = new MemoryStream();
    writer.WriteTo(built);
    var bytes = built.ToArray();

    var payloadAt = bytes.AsSpan().IndexOf(inline);
    Assert.That(payloadAt, Is.GreaterThanOrEqualTo(21), "test payload must be present as one inline EXTENT_DATA value");
    Assert.That(bytes.AsSpan(payloadAt + inline.Length).IndexOf(inline), Is.EqualTo(-1), "payload marker must be unique in the image");

    // Inline payload starts at file_extent_item + 21; compression is byte 16.
    bytes[payloadAt - 5] = 1; // non-zero compression id: current native profile must reject it
    using var image = new MemoryStream(bytes, writable: false);
    var profile = new BtrfsFilesystemDriverAdapter().ProbeFilesystem(image);

    Assert.That(profile.CanMount, Is.False);
    Assert.That(string.Join("; ", profile.Limitations), Does.Contain("compression="));
  }

  [Test, Category("ErrorHandling")]
  public void NativeSession_RefusesWritableMount() {
    var writer = new BtrfsWriter();
    writer.AddFile("a.bin", "abc"u8.ToArray());
    using var image = new MemoryStream();
    writer.WriteTo(image);

    var adapter = new BtrfsFilesystemDriverAdapter();
    image.Position = 0;
    Assert.Throws<NotSupportedException>(() =>
      adapter.OpenFilesystem(image, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true)));
  }
}
