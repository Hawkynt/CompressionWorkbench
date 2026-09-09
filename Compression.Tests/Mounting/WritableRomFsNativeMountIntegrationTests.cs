#pragma warning disable CS1591
using Compression.Lib;
using Compression.Mounting;
using Compression.Mounting.Dokan;
using Compression.Mounting.Fuse;
using Compression.Registry;

namespace Compression.Tests.Mounting;

[TestFixture]
public sealed class WritableRomFsNativeMountIntegrationTests {
  private const string KeptName = "KEEP.BIN";
  private const string DeletedName = "DELETE.BIN";
  private const string CreatedName = "CREATED.BIN";
  private const string RenamedName = "RENAMED.BIN";

  private static readonly byte[] KeptPayload = BuildPayload(0x1177, 211);
  private static readonly byte[] DeletedPayload = BuildPayload(0x2288, 149);
  private static readonly byte[] CreatedPayload = BuildPayload(0x3399, 1536);

  [Test]
  [Category("OsIntegration")]
  [CancelAfter(90000)]
  public async Task RomFsNativeWritableMountPersistsCreateWriteTruncateRenameDeleteFlushAndReopen() {
    var backend = RequireHostBackend();
    using var image = CreateRomFsImage();

    var target = CreateMountTarget();
    try {
      image.Position = 0;
      var profile = FormatRegistry.ProbeFilesystem("RomFs", image);
      Assert.That(profile.CanMountWritable, Is.True,
        "The real ROMFS writer fixture must qualify for the explicit flat writable ROMFS profile.");

      image.Position = 0;
      using (var filesystem = FormatRegistry.OpenFilesystem("RomFs", image, new(ReadOnly: false, LeaveOpen: true))) {
        var plan = FilesystemMountCapabilityResolver.Resolve(
          filesystem.Profile,
          backend.GetProfile(),
          MountAccessMode.ReadWrite,
          sourceCanWrite: true);
        Assert.That(plan.IsSupported, Is.True,
          "Writable ROMFS mount was rejected: " + string.Join("; ", plan.Reasons.Select(static reason => reason.Message)));

        await using var mount = await backend.MountAsync(new(filesystem, target, plan));
        Assert.Multiple(() => {
          Assert.That(mount.IsMounted, Is.True);
          Assert.That(mount.AccessMode, Is.EqualTo(MountAccessMode.ReadWrite));
        });

        var createdPath = Path.Combine(target, CreatedName);
        using (var created = new FileStream(
          createdPath,
          FileMode.CreateNew,
          FileAccess.ReadWrite,
          FileShare.ReadWrite | FileShare.Delete,
          bufferSize: 4096,
          FileOptions.None)) {
          created.Write(CreatedPayload);
          created.Flush(flushToDisk: true);
          created.SetLength(769);
          created.Flush(flushToDisk: true);
        }

        var renamedPath = Path.Combine(target, RenamedName);
        File.Move(createdPath, renamedPath);
        File.Delete(Path.Combine(target, DeletedName));

        await mount.FlushAsync();

        Assert.Multiple(() => {
          Assert.That(File.Exists(createdPath), Is.False);
          Assert.That(File.Exists(renamedPath), Is.True);
          Assert.That(File.Exists(Path.Combine(target, DeletedName)), Is.False);
          Assert.That(File.ReadAllBytes(renamedPath), Is.EqualTo(CreatedPayload[..769]));
          Assert.That(File.ReadAllBytes(Path.Combine(target, KeptName)), Is.EqualTo(KeptPayload));
        });

        await mount.UnmountAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(mount.IsMounted, Is.False);
      }

      image.Position = 0;
      using var reopened = FormatRegistry.OpenFilesystem("RomFs", image, new(ReadOnly: true, LeaveOpen: true));
      var renamed = reopened.Lookup(reopened.RootNodeId, RenamedName);
      var kept = reopened.Lookup(reopened.RootNodeId, KeptName);
      Assert.Multiple(() => {
        Assert.That(reopened.Lookup(reopened.RootNodeId, CreatedName), Is.Null);
        Assert.That(reopened.Lookup(reopened.RootNodeId, DeletedName), Is.Null);
        Assert.That(renamed, Is.Not.Null);
        Assert.That(kept, Is.Not.Null);
      });
      Assert.That(ReadAll(reopened, renamed!.Value), Is.EqualTo(CreatedPayload[..769]));
      Assert.That(ReadAll(reopened, kept!.Value), Is.EqualTo(KeptPayload));
    } finally {
      CleanupMountTarget(target);
    }
  }

  private static IFilesystemMountBackend RequireHostBackend() {
    if (OperatingSystem.IsWindows()) {
      var runtime = DokanRuntimeProbe.Probe();
      if (!runtime.IsAvailable)
        Assert.Ignore("Dokan native runtime unavailable: " + (runtime.UnavailableReason ?? "unknown reason"));
      return new DokanFilesystemMountBackend(runtime);
    }

    if (OperatingSystem.IsLinux()) {
      var runtime = FuseRuntimeProbe.Probe();
      if (!runtime.IsAvailable)
        Assert.Ignore("FUSE native runtime unavailable: " + (runtime.UnavailableReason ?? "unknown reason"));
      return new FuseFilesystemMountBackend(runtime);
    }

    Assert.Ignore("Writable native ROMFS mount integration requires Dokan on Windows or FUSE3 on Linux.");
    throw new PlatformNotSupportedException();
  }

  private static MemoryStream CreateRomFsImage() {
    FormatRegistration.EnsureInitialized();
    var creator = FormatRegistry.GetArchiveOps("RomFs") as IArchiveCreatable
      ?? throw new InvalidOperationException("ROMFS creator is not registered.");
    var image = new MemoryStream();
    creator.Create(
      image,
      [
        ArchiveInputInfo.InMemory(KeptName, KeptPayload),
        ArchiveInputInfo.InMemory(DeletedName, DeletedPayload),
      ],
      new FormatCreateOptions());
    image.Position = 0;
    return image;
  }

  private static string CreateMountTarget() {
    if (OperatingSystem.IsWindows()) {
      var used = DriveInfo.GetDrives()
        .Select(static drive => char.ToUpperInvariant(drive.Name[0]))
        .ToHashSet();
      for (var drive = 'Z'; drive >= 'D'; --drive)
        if (!used.Contains(drive))
          return $"{drive}:\\";
      throw new IOException("No free drive letter is available for the Dokan integration test.");
    }

    var target = Path.Combine(Path.GetTempPath(), $"cwb-romfs-rw-{Guid.NewGuid():N}");
    Directory.CreateDirectory(target);
    return target;
  }

  private static void CleanupMountTarget(string target) {
    if (OperatingSystem.IsWindows())
      return;
    try {
      if (Directory.Exists(target))
        Directory.Delete(target, recursive: true);
    } catch {
      // The mount/unmount assertion carries the useful failure. Cleanup remains best effort.
    }
  }

  private static byte[] ReadAll(IFilesystemSession filesystem, FilesystemNodeId nodeId) {
    using var handle = filesystem.OpenFile(nodeId, FileAccess.Read);
    var result = new byte[checked((int)handle.Length)];
    var read = 0;
    while (read < result.Length) {
      var count = handle.Read(read, result.AsSpan(read));
      if (count <= 0)
        throw new EndOfStreamException($"Filesystem handle returned EOF after {read} of {result.Length} bytes.");
      read += count;
    }
    return result;
  }

  private static byte[] BuildPayload(int seed, int length) {
    var result = new byte[length];
    new Random(seed).NextBytes(result);
    return result;
  }
}
