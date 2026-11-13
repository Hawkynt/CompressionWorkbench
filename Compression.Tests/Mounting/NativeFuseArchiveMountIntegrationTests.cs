using Compression.Lib;
using Compression.Mounting;
using Compression.Mounting.Fuse;
using Compression.Registry;

namespace Compression.Tests.Mounting;

/// <summary>
/// Genuine Linux FUSE3 tests: each File/Directory operation below enters the
/// host kernel VFS and comes back through the libfuse3 low-level callbacks.
/// An in-process IFilesystemSession round trip is not a substitute.
/// </summary>
[TestFixture]
[Category("OsIntegration")]
[Category("NativeFuse")]
public sealed class NativeFuseArchiveMountIntegrationTests {
  [OneTimeSetUp]
  public void RegisterFormats() => FormatRegistration.EnsureInitialized();

  [TestCase("Zip")]
  [TestCase("SevenZip")]
  [TestCase("Rar")]
  [TestCase("Wim")]
  [TestCase("TarGz")]
  [CancelAfter(120000)]
  public async Task ArchiveRoundTripsViaRealLinuxFuseKernelMount(string formatId) {
    if (!OperatingSystem.IsLinux())
      Assert.Ignore("Actual FUSE3 kernel integration requires Linux.");

    var runtime = FuseRuntimeProbe.Probe();
    if (!runtime.IsAvailable)
      Assert.Ignore(runtime.UnavailableReason ?? "FUSE3 and /dev/fuse are required.");

    var folder = Path.Combine(Path.GetTempPath(), $"cwb-fuse-archives-{Guid.NewGuid():N}");
    var mountpoint = Path.Combine(folder, "mounted");
    Directory.CreateDirectory(mountpoint);

    var descriptor = FormatRegistry.GetById(formatId)
      ?? throw new InvalidOperationException($"Missing archive descriptor {formatId}.");
    Assert.That(descriptor, Is.InstanceOf<IArchiveCreatable>());
    var imagePath = Path.Combine(folder, "source" + descriptor.DefaultExtension);
    FileStream? source = null;
    IFilesystemSession? filesystem = null;
    IMountSession? mount = null;

    try {
      using (var writer = new FileStream(imagePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) {
        ((IArchiveCreatable)descriptor).Create(writer, [
          ArchiveInputInfo.InMemory("keep.txt", "kept"u8.ToArray()),
          ArchiveInputInfo.InMemory("remove.txt", "delete-me"u8.ToArray()),
        ], new FormatCreateOptions());
        writer.Flush(flushToDisk: true);
      }

      source = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite,
        FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
      var probe = MountNamespaceResolver.Probe(formatId, source);
      Assert.That(probe.Profile.CanMountWritable, Is.True,
        $"{formatId} source should be writable: {string.Join("; ", probe.Profile.Limitations)}");
      source.Position = 0;
      filesystem = MountNamespaceResolver.Open(
        formatId, source, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true));

      var backend = new FuseFilesystemMountBackend(runtime);
      var plan = FilesystemMountCapabilityResolver.Resolve(
        filesystem.Profile, backend.GetProfile(), MountAccessMode.ReadWrite, sourceCanWrite: true);
      Assert.That(plan.IsSupported, Is.True,
        $"{formatId} FUSE plan refused: {string.Join("; ", plan.Reasons.Select(x => x.Message))}");

      mount = await backend.MountAsync(new(filesystem, mountpoint, plan));
      Assert.That(mount.IsMounted, Is.True);

      var added = Path.Combine(mountpoint, "new.bin");
      using (var file = new FileStream(added, FileMode.CreateNew, FileAccess.ReadWrite,
          FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.None)) {
        file.Write("abcdefghijkl"u8);
        file.SetLength(8);
        file.Flush(flushToDisk: true);
      }

      var renamed = Path.Combine(mountpoint, "renamed.bin");
      File.Move(added, renamed);
      File.Delete(Path.Combine(mountpoint, "remove.txt"));
      await mount.FlushAsync();

      Assert.Multiple(() => {
        Assert.That(File.Exists(added), Is.False);
        Assert.That(File.Exists(Path.Combine(mountpoint, "remove.txt")), Is.False);
        Assert.That(File.ReadAllBytes(renamed), Is.EqualTo("abcdefgh"u8.ToArray()));
        Assert.That(File.ReadAllBytes(Path.Combine(mountpoint, "keep.txt")), Is.EqualTo("kept"u8.ToArray()));
      });

      await mount.UnmountAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
      await mount.DisposeAsync();
      mount = null;
      filesystem.Dispose();
      filesystem = null;
      source.Dispose();
      source = null;

      using var reopenedSource = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
      using var reopened = MountNamespaceResolver.Open(
        formatId, reopenedSource, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
      var kept = reopened.Lookup(reopened.RootNodeId, "keep.txt");
      var replacement = reopened.Lookup(reopened.RootNodeId, "renamed.bin");
      Assert.Multiple(() => {
        Assert.That(kept, Is.Not.Null);
        Assert.That(replacement, Is.Not.Null);
        Assert.That(reopened.Lookup(reopened.RootNodeId, "remove.txt"), Is.Null);
        Assert.That(reopened.Lookup(reopened.RootNodeId, "new.bin"), Is.Null);
      });
      Assert.That(ReadAll(reopened, kept!.Value), Is.EqualTo("kept"u8.ToArray()));
      Assert.That(ReadAll(reopened, replacement!.Value), Is.EqualTo("abcdefgh"u8.ToArray()));
    } finally {
      if (mount is not null)
        await mount.DisposeAsync();
      filesystem?.Dispose();
      source?.Dispose();
      if (Directory.Exists(folder))
        Directory.Delete(folder, recursive: true);
    }
  }

  private static byte[] ReadAll(IFilesystemSession filesystem, FilesystemNodeId node) {
    using var handle = filesystem.OpenFile(node, FileAccess.Read);
    var result = new byte[checked((int)handle.Length)];
    for (var offset = 0; offset < result.Length;) {
      var read = handle.Read(offset, result.AsSpan(offset));
      if (read <= 0)
        throw new EndOfStreamException("FUSE archive reopen returned truncated content.");
      offset += read;
    }
    return result;
  }
}
