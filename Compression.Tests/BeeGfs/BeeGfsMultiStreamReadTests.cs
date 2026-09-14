using System.Text;
using Compression.Registry;
using FileSystem.BeeGfs;

namespace Compression.Tests.BeeGfs;

[TestFixture]
public sealed class BeeGfsMultiStreamReadTests {
  private const uint ChunkSize = 64 * 1024;
  private const string EntryId = "1-6705972F-1";
  private const string ChunkPath = "u0/0/r/root/1-6705972F-1";

  [Test, Category("RoundTrip")]
  public void Registry_MountsLogicalNamespaceAndReassemblesRaid0File() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var payload = Enumerable.Range(0, checked((int)(ChunkSize * 2 + 12_345)))
      .Select(i => (byte)((i * 37 + 11) & 0xFF))
      .ToArray();
    var (chunk101, chunk201) = Stripe(payload);

    using var metadata = BuildMetadataSnapshot(payload.LongLength);
    using var storage101 = BuildStorageSnapshot(101, chunk101);
    using var storage201 = BuildStorageSnapshot(201, chunk201);
    var sources = new FilesystemStreamSet([
      new FilesystemStreamSource("meta-7", metadata, FilesystemSourceRole.Unknown, "Zip", "/targets/meta"),
      new FilesystemStreamSource("storage-101", storage101, FilesystemSourceRole.Unknown, "Zip", "/targets/storage-101"),
      new FilesystemStreamSource("storage-201", storage201, FilesystemSourceRole.Unknown, "Zip", "/targets/storage-201"),
    ]);

    var profile = FormatRegistry.ProbeFilesystem("BeeGfs", sources);
    var readiness = FormatRegistry.AssessFilesystemDriver("BeeGfs", sources, FilesystemDriverTarget.ReadOnly);

    Assert.Multiple(() => {
      Assert.That(profile.CanMount, Is.True);
      Assert.That(profile.CanMountWritable, Is.False);
      Assert.That(profile.Capabilities.HasFlag(FilesystemDriverCapabilities.EnumerateDirectories), Is.True);
      Assert.That(profile.Capabilities.HasFlag(FilesystemDriverCapabilities.ReadData), Is.True);
      Assert.That(profile.Capabilities.HasFlag(FilesystemDriverCapabilities.RandomAccess), Is.True);
      Assert.That(readiness.Derivable, Is.True);
      Assert.That(readiness.AvailableLayers.HasFlag(FilesystemDriverReadinessLayer.NativeStableNodeIds), Is.True);
    });

    using var session = FormatRegistry.OpenFilesystem(
      "BeeGfs", sources, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    var entries = session.Enumerate(session.RootNodeId);
    var hello = entries.Single(entry => entry.Name == "hello.bin");
    Assert.Multiple(() => {
      Assert.That(hello.Kind, Is.EqualTo(FilesystemNodeKind.RegularFile));
      Assert.That(session.Stat(hello.NodeId).Size, Is.EqualTo(payload.LongLength));
    });

    using var handle = session.OpenFile(hello.NodeId, FileAccess.Read);
    var decoded = new byte[payload.Length];
    Assert.That(handle.Read(0, decoded), Is.EqualTo(decoded.Length));
    Assert.That(decoded, Is.EqualTo(payload));

    var crossing = new byte[512];
    var crossingOffset = checked((long)ChunkSize - 123);
    Assert.That(handle.Read(crossingOffset, crossing), Is.EqualTo(crossing.Length));
    Assert.That(crossing, Is.EqualTo(payload.AsSpan(checked((int)crossingOffset), crossing.Length).ToArray()));
  }

  [Test, Category("Exception")]
  public void Probe_FailsClosedWhenReferencedStorageTargetIsMissing() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var payload = new byte[checked((int)(ChunkSize + 1))];
    var (chunk101, _) = Stripe(payload);
    using var metadata = BuildMetadataSnapshot(payload.LongLength);
    using var storage101 = BuildStorageSnapshot(101, chunk101);
    var sources = new FilesystemStreamSet([
      new FilesystemStreamSource("meta-7", metadata, FilesystemSourceRole.Metadata, "Zip", "/targets/meta"),
      new FilesystemStreamSource("storage-101", storage101, FilesystemSourceRole.Data, "Zip", "/targets/storage-101"),
    ]);

    var profile = FormatRegistry.ProbeFilesystem("BeeGfs", sources);

    Assert.Multiple(() => {
      Assert.That(profile.CanMount, Is.False);
      Assert.That(profile.ProfileName, Does.Contain("validated BeeGFS topology"));
      Assert.That(string.Join('\n', profile.Limitations), Does.Contain("storage target 201"));
    });
  }

  private static MemoryStream BuildMetadataSnapshot(long fileSize) {
    var metadata = BuildV6InlineRaid0File(fileSize);
    return BuildZip([
      ArchiveInputInfo.InMemory("targets/meta/format.conf", "version=4\n"u8),
      ArchiveInputInfo.InMemory("targets/meta/nodeNumID", "7"u8),
      ArchiveInputInfo.InMemory("targets/meta/inodes/.keep", []),
      ArchiveInputInfo.InMemory("targets/meta/dentries/00/00/root/#fSiDs#/.keep", []),
      ArchiveInputInfo.InMemory("targets/meta/dentries/00/00/root/hello.bin", metadata),
    ]);
  }

  private static MemoryStream BuildStorageSnapshot(ushort targetId, byte[] localChunk) => BuildZip([
    ArchiveInputInfo.InMemory($"targets/storage-{targetId}/format.conf", "version=3\n"u8),
    ArchiveInputInfo.InMemory($"targets/storage-{targetId}/targetNumID", Encoding.ASCII.GetBytes(targetId.ToString())),
    ArchiveInputInfo.InMemory($"targets/storage-{targetId}/chunks/{ChunkPath}", localChunk),
  ]);

  private static MemoryStream BuildZip(IReadOnlyList<ArchiveInputInfo> inputs) {
    var creator = FormatRegistry.GetById("Zip") as IArchiveCreatable
      ?? throw new InvalidOperationException("ZIP creator is not registered.");
    var image = new MemoryStream();
    creator.Create(image, inputs, new FormatCreateOptions());
    image.Position = 0;
    return image;
  }

  private static (byte[] First, byte[] Second) Stripe(ReadOnlySpan<byte> logical) {
    using var first = new MemoryStream();
    using var second = new MemoryStream();
    var offset = 0;
    while (offset < logical.Length) {
      var index = (offset / checked((int)ChunkSize)) & 1;
      var length = Math.Min(checked((int)ChunkSize), logical.Length - offset);
      var target = index == 0 ? first : second;
      target.Write(logical.Slice(offset, length));
      offset += length;
    }
    return (first.ToArray(), second.ToArray());
  }

  private static byte[] BuildV6InlineRaid0File(long fileSize) {
    using var output = new MemoryStream();
    using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
    writer.Write((byte)1); // DiskMetaDataType_FILEDENTRY
    writer.Write((byte)6);
    writer.Write((ushort)(1 | 2 | 16)); // inline inode | file inode | 32-bit IDs
    writer.Write((byte)2); // regular file
    writer.Write(new byte[3]);

    const uint inodeFeatures = 64 | 128; // stat flags + versions; orig uid/parent fall back to stat/current parent
    writer.Write(inodeFeatures);
    writer.Write(0u); // no state flags => four alignment bytes
    writer.Write(0u); // stat flags: non-sparse
    writer.Write(0x81A4u); // S_IFREG | 0644
    writer.Write(1_700_000_000L);
    writer.Write(1_700_000_001L);
    writer.Write(1_700_000_002L);
    writer.Write(1_700_000_003L);
    writer.Write(fileSize);
    writer.Write(1u);
    writer.Write(1u);
    writer.Write(0u); // uid => chunk path u0
    writer.Write(0u); // gid
    WriteAlignedString4(writer, EntryId);

    var pattern = BuildRaid0Pattern([101, 201]);
    writer.Write(pattern);
    writer.Write(1u); // file version
    writer.Write(1u); // metadata version
    return output.ToArray();
  }

  private static byte[] BuildRaid0Pattern(ushort[] targetIds) {
    using var body = new MemoryStream();
    using (var writer = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true)) {
      writer.Write(1u); // RAID0
      writer.Write(ChunkSize);
      writer.Write((ushort)1); // default storage pool
      writer.Write((uint)targetIds.Length);
      writer.Write(checked(8u + (uint)targetIds.Length * 2u));
      writer.Write((uint)targetIds.Length);
      foreach (var id in targetIds) writer.Write(id);
    }

    using var result = new MemoryStream();
    using (var writer = new BinaryWriter(result, Encoding.UTF8, leaveOpen: true)) {
      writer.Write(checked((uint)(4 + body.Length)));
      writer.Write(body.ToArray());
    }
    return result.ToArray();
  }

  private static void WriteAlignedString4(BinaryWriter writer, string value) {
    var start = writer.BaseStream.Position;
    var bytes = Encoding.UTF8.GetBytes(value);
    writer.Write((uint)bytes.Length);
    writer.Write(bytes);
    writer.Write((byte)0);
    while (((writer.BaseStream.Position - start) & 3) != 0)
      writer.Write((byte)0);
  }
}
