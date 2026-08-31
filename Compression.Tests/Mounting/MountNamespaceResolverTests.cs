using Compression.Lib;
using Compression.Mounting;
using Compression.Registry;

namespace Compression.Tests.Mounting;

[TestFixture]
public sealed class MountNamespaceResolverTests {

  [OneTimeSetUp]
  public void RegisterFormats() => FormatRegistration.EnsureInitialized();

  [Test]
  public void Zip_ProjectsAsReadOnlyFilesystemNamespace() {
    var data = "mounted from zip"u8.ToArray();
    using var archive = Create("Zip", [ArchiveInputInfo.InMemory("nested/hello.txt", data)]);

    var probe = MountNamespaceResolver.Probe("Zip", archive);
    Assert.Multiple(() => {
      Assert.That(probe.Profile.CanMount, Is.True);
      Assert.That(probe.Profile.CanMountWritable, Is.False);
      Assert.That(probe.Layers, Does.Contain("archive:Zip"));
      Assert.That(probe.Layers, Does.Contain("namespace:derived-read-only"));
    });

    archive.Position = 0;
    using var session = MountNamespaceResolver.Open(
      "Zip",
      archive,
      new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));

    var nested = session.Lookup(session.RootNodeId, "nested");
    Assert.That(nested, Is.Not.Null);
    var file = session.Lookup(nested!.Value, "hello.txt");
    Assert.That(file, Is.Not.Null);
    Assert.That(ReadAll(session, file!.Value), Is.EqualTo(data));
    Assert.Throws<NotSupportedException>(() => session.CreateFile(session.RootNodeId, "nope.txt"));
  }

  [Test]
  public void Vhd_ResolvesGuestFatWithoutHostMounting() {
    var data = Enumerable.Range(0, 4096).Select(static i => (byte)(i * 37)).ToArray();
    using var image = Create("Vhd", [ArchiveInputInfo.InMemory("HELLO.TXT", data)]);

    var probe = MountNamespaceResolver.Probe("Vhd", image);
    Assert.Multiple(() => {
      Assert.That(probe.Profile.CanMount, Is.True);
      Assert.That(probe.Layers, Does.Contain("container:Vhd"));
      Assert.That(probe.Layers, Does.Contain("block-device"));
      Assert.That(probe.Layers, Does.Contain("filesystem:Fat"));
    });

    image.Position = 0;
    using var session = MountNamespaceResolver.Open(
      "Vhd",
      image,
      new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));

    Assert.That(session.Profile.FormatId, Is.EqualTo("Fat").IgnoreCase);
    var file = session.Lookup(session.RootNodeId, "HELLO.TXT");
    Assert.That(file, Is.Not.Null);
    Assert.That(ReadAll(session, file!.Value), Is.EqualTo(data));
  }

  [Test]
  public void PartitionBlockDevice_TranslatesBoundsAndWrites() {
    const int blockSize = 512;
    var bytes = new byte[8 * blockSize];
    for (var block = 0; block < 8; ++block)
      bytes.AsSpan(block * blockSize, blockSize).Fill((byte)block);

    using var stream = new MemoryStream(bytes, writable: true);
    using var disk = new StreamBlockDevice(stream, blockSize, writable: true, leaveOpen: true);
    using var partition = new PartitionBlockDevice(disk, firstBlock: 2, blockCount: 3, leaveOpen: true);

    var read = new byte[blockSize];
    Assert.That(partition.ReadBlocks(0, read), Is.EqualTo(1));
    Assert.That(read, Is.All.EqualTo((byte)2));

    var replacement = Enumerable.Repeat((byte)0xA5, blockSize).ToArray();
    partition.WriteBlocks(2, replacement);
    Assert.That(bytes.AsSpan(4 * blockSize, blockSize).SequenceEqual(replacement), Is.True);
    Assert.Throws<ArgumentOutOfRangeException>(() => partition.ReadBlocks(3, read));
  }

  private static MemoryStream Create(string formatId, IReadOnlyList<ArchiveInputInfo> inputs) {
    var ops = FormatRegistry.GetArchiveOps(formatId)
      ?? throw new AssertionException($"{formatId} has no archive operations.");
    if (ops is not IArchiveCreatable creator)
      throw new AssertionException($"{formatId} is not creatable.");

    var result = new MemoryStream();
    creator.Create(result, inputs, new FormatCreateOptions { ForceCompress = true });
    result.Position = 0;
    return result;
  }

  private static byte[] ReadAll(IFilesystemSession session, FilesystemNodeId nodeId) {
    using var handle = session.OpenFile(nodeId, FileAccess.Read);
    if (handle.Length > int.MaxValue)
      throw new AssertionException("Test fixture unexpectedly exceeds array limits.");
    var result = new byte[(int)handle.Length];
    var offset = 0;
    while (offset < result.Length) {
      var read = handle.Read(offset, result.AsSpan(offset));
      if (read == 0) break;
      offset += read;
    }
    Assert.That(offset, Is.EqualTo(result.Length));
    return result;
  }
}
