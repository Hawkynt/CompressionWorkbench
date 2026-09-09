using System.Text;
using Compression.Mounting;
using Compression.Mounting.Dokan;
using Compression.Mounting.Fuse;
using Compression.Registry;

namespace Compression.Tests.Mounting;

[TestFixture]
public sealed class FilesystemMountBackendIntegrationTests {
  private static readonly byte[] Payload = Encoding.UTF8.GetBytes("CompressionWorkbench mount transport\n");

  [Test]
  [Category("OsIntegration")]
  [CancelAfter(30000)]
  public async Task FuseServesNamespaceReadsAndUnmountsWithAnOpenFile() {
    if (!OperatingSystem.IsLinux())
      Assert.Ignore("FUSE integration requires Linux.");

    var runtime = FuseRuntimeProbe.Probe();
    if (!runtime.IsAvailable)
      Assert.Ignore(runtime.UnavailableReason ?? "FUSE3 is unavailable on this host.");

    var mountPoint = Path.Combine(Path.GetTempPath(), $"cwb-fuse-{Guid.NewGuid():N}");
    Directory.CreateDirectory(mountPoint);

    using var filesystem = CreateFilesystem();
    var backend = new FuseFilesystemMountBackend(runtime);
    var plan = FilesystemMountCapabilityResolver.Resolve(
      filesystem.Profile,
      backend.GetProfile(),
      MountAccessMode.ReadOnly,
      sourceCanWrite: false
    );
    Assert.That(plan.IsSupported, Is.True, string.Join(Environment.NewLine, plan.Reasons.Select(static reason => reason.Message)));

    IMountSession? mount = null;
    try {
      mount = await backend.MountAsync(new(filesystem, mountPoint, plan));
      Assert.That(mount.IsMounted, Is.True);

      var docs = Path.Combine(mountPoint, "docs");
      var readme = Path.Combine(docs, "readme.txt");
      Assert.That(Directory.GetFileSystemEntries(mountPoint).Select(Path.GetFileName), Is.EqualTo(new[] { "docs" }));
      Assert.That(Directory.GetFileSystemEntries(docs).Select(Path.GetFileName), Is.EqualTo(new[] { "readme.txt" }));

      using var heldOpenFile = File.OpenRead(readme);
      var actual = new byte[Payload.Length];
      heldOpenFile.ReadExactly(actual);
      Assert.That(actual, Is.EqualTo(Payload));

      await mount.UnmountAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
      Assert.That(mount.IsMounted, Is.False);
    } finally {
      if (mount is not null)
        await mount.DisposeAsync();
      Directory.Delete(mountPoint, recursive: true);
    }
  }

  [Test]
  [Category("OsIntegration")]
  [CancelAfter(30000)]
  public async Task DokanServesNamespaceReadsAndUnmountsWithAnOpenFile() {
    if (!OperatingSystem.IsWindows())
      Assert.Ignore("Dokan integration requires Windows.");

    var runtime = DokanRuntimeProbe.Probe();
    if (!runtime.IsAvailable)
      Assert.Ignore(runtime.UnavailableReason ?? "Dokan is unavailable on this host.");

    using var filesystem = CreateFilesystem();
    var backend = new DokanFilesystemMountBackend(runtime);
    var plan = FilesystemMountCapabilityResolver.Resolve(
      filesystem.Profile,
      backend.GetProfile(),
      MountAccessMode.ReadOnly,
      sourceCanWrite: false
    );
    Assert.That(plan.IsSupported, Is.True, string.Join(Environment.NewLine, plan.Reasons.Select(static reason => reason.Message)));

    var mountPoint = FindFreeDriveMountPoint();
    IMountSession? mount = null;
    try {
      mount = await backend.MountAsync(new(filesystem, mountPoint, plan));
      Assert.That(mount.IsMounted, Is.True);

      var docs = Path.Combine(mountPoint, "docs");
      var readme = Path.Combine(docs, "readme.txt");
      Assert.That(Directory.GetFileSystemEntries(mountPoint).Select(Path.GetFileName), Is.EqualTo(new[] { "docs" }));
      Assert.That(Directory.GetFileSystemEntries(docs).Select(Path.GetFileName), Is.EqualTo(new[] { "readme.txt" }));

      using var heldOpenFile = File.OpenRead(readme);
      var actual = new byte[Payload.Length];
      heldOpenFile.ReadExactly(actual);
      Assert.That(actual, Is.EqualTo(Payload));

      await mount.UnmountAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
      Assert.That(mount.IsMounted, Is.False);
    } finally {
      if (mount is not null)
        await mount.DisposeAsync();
    }
  }

  [TestCase(0U, false)]
  [TestCase(1000U, true)]
  public void FuseMountHelperIsRequiredOnlyForUnprivilegedUsers(uint effectiveUserId, bool expected)
    => Assert.That(FuseRuntimeProbe.RequiresFusermount(effectiveUserId), Is.EqualTo(expected));

  private static ReadOnlyFilesystemSnapshotSession CreateFilesystem() {
    var root = new FilesystemNodeId(1, 1);
    var docs = new FilesystemNodeId(2, 1);
    var readme = new FilesystemNodeId(3, 1);
    var profile = new FilesystemDriverProfile(
      "mount-test",
      "synthetic read-only mount smoke test",
      FilesystemMountCapabilityResolver.CoreReadCapabilities |
      FilesystemDriverCapabilities.CasePreservingNames,
      FilesystemMutationModel.None,
      CanMount: true,
      CanMountWritable: false,
      Array.Empty<string>()
    );

    return new(
      profile,
      root,
      [
        new(root, root, "/", FilesystemNodeKind.Directory, 0, 0, LinkCount: 2),
        new(docs, root, "docs", FilesystemNodeKind.Directory, 0, 0, LinkCount: 2),
        new(
          readme,
          docs,
          "readme.txt",
          FilesystemNodeKind.RegularFile,
          Payload.Length,
          Payload.Length,
          OpenReadHandle: () => new MemoryFileHandle(readme, Payload)
        ),
      ]
    );
  }

  private static string FindFreeDriveMountPoint() {
    var used = DriveInfo
      .GetDrives()
      .Select(static drive => char.ToUpperInvariant(drive.Name[0]))
      .ToHashSet();

    for (var drive = 'Z'; drive >= 'D'; --drive)
      if (!used.Contains(drive))
        return $"{drive}:\\";

    throw new IOException("No free drive letter is available for the Dokan integration test.");
  }

  private sealed class MemoryFileHandle(FilesystemNodeId nodeId, byte[] data) : IFilesystemFileHandle {
    private readonly byte[] _data = data;

    public FilesystemNodeId NodeId { get; } = nodeId;
    public long Length => this._data.Length;

    public int Read(long offset, Span<byte> destination) {
      if (offset < 0)
        throw new ArgumentOutOfRangeException(nameof(offset));
      if (offset >= this._data.Length)
        return 0;

      var start = checked((int)offset);
      var count = Math.Min(destination.Length, this._data.Length - start);
      this._data.AsSpan(start, count).CopyTo(destination);
      return count;
    }

    public void Write(long offset, ReadOnlySpan<byte> source) => throw new NotSupportedException();
    public void SetLength(long length) => throw new NotSupportedException();
    public void Flush() { }
    public void Dispose() { }
  }
}
