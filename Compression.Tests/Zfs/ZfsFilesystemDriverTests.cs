using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.Zfs;

namespace Compression.Tests.Zfs;

[TestFixture]
public sealed class ZfsFilesystemDriverTests {
  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void NativeSession_UsesDnodeIdentityAndSupportsPositionalReads() {
    var payload = new byte[48 * 1024 + 137];
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)((i * 29 + 7) & 0xFF);

    var writer = new ZfsWriter();
    writer.AddFile("dir/data.bin", payload);
    using var image = new MemoryStream();
    writer.WriteTo(image, 8L * 1024 * 1024);

    var adapter = new ZfsFilesystemDriverAdapter();
    image.Position = 0;
    var profile = adapter.ProbeFilesystem(image);
    Assert.That(profile.CanMount, Is.True, string.Join("; ", profile.Limitations));
    Assert.That(profile.CanMountWritable, Is.False);
    Assert.That(profile.Capabilities.HasFlag(FilesystemDriverCapabilities.StableNodeIds), Is.True);
    Assert.That(profile.Capabilities.HasFlag(FilesystemDriverCapabilities.RandomAccess), Is.True);

    image.Position = 0;
    using var session = adapter.OpenFilesystem(image, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    var dirId = session.Lookup(session.RootNodeId, "dir");
    Assert.That(dirId, Is.Not.Null);
    var fileId = session.Lookup(dirId!.Value, "data.bin");
    Assert.That(fileId, Is.Not.Null);
    Assert.That(fileId!.Value.Value, Is.GreaterThan(0UL), "regular-file node must carry the native dataset dnode object id");

    using var handle = session.OpenFile(fileId.Value, FileAccess.Read);
    Assert.That(handle.Length, Is.EqualTo(payload.Length));
    var slice = new byte[733];
    var read = handle.Read(12_345, slice);
    Assert.That(read, Is.EqualTo(slice.Length));
    Assert.That(slice, Is.EqualTo(payload.AsSpan(12_345, slice.Length).ToArray()));
  }

  [Test, Category("ErrorHandling")]
  public void Probe_RejectsUnsupportedPoolVersion() {
    var writer = new ZfsWriter();
    writer.AddFile("a.bin", "abc"u8.ToArray());
    using var built = new MemoryStream();
    writer.WriteTo(built, 8L * 1024 * 1024);
    var bytes = built.ToArray();

    var changed = 0;
    for (var slot = ZfsConstants.UberblockArrayOffset;
         slot + ZfsConstants.UberblockSize <= ZfsConstants.LabelSize;
         slot += ZfsConstants.UberblockSize) {
      if (BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(slot, 8)) != ZfsConstants.UberblockMagic)
        continue;
      BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(slot + 8, 8), ZfsConstants.PoolVersion + 1);
      changed++;
    }
    Assert.That(changed, Is.GreaterThan(0), "writer must emit at least one valid L0 uberblock");

    using var image = new MemoryStream(bytes, writable: false);
    var profile = new ZfsFilesystemDriverAdapter().ProbeFilesystem(image);
    Assert.That(profile.CanMount, Is.False);
    Assert.That(string.Join("; ", profile.Limitations), Does.Contain("pool version"));
  }

  [Test, Category("ErrorHandling")]
  public void NativeSession_RefusesWritableMount() {
    var writer = new ZfsWriter();
    writer.AddFile("a.bin", "abc"u8.ToArray());
    using var image = new MemoryStream();
    writer.WriteTo(image, 8L * 1024 * 1024);

    var adapter = new ZfsFilesystemDriverAdapter();
    image.Position = 0;
    Assert.Throws<NotSupportedException>(() =>
      adapter.OpenFilesystem(image, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true)));
  }
}
