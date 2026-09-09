#pragma warning disable CS1591
using Compression.Lib;
using Compression.Mounting;
using Compression.Mounting.Dokan;
using Compression.Mounting.Fuse;
using Compression.Registry;

namespace Compression.Tests.Mounting;

/// <summary>
/// End-to-end coverage for the filesystem layer that sits above Dokan/FUSE.
/// Images are produced by the project's real format writers, reopened through
/// <see cref="FormatRegistry"/>, and only then handed to the host mount backend.
/// </summary>
[TestFixture]
public sealed class FilesystemFormatMountIntegrationTests {
  private const string ProbeName = "MOUNTPRB.BIN";
  private const int ProbePrefixLength = 64 * 1024;
  private static readonly byte[] ProbePayload = BuildPayload(0x5A17, 4096);
  private static readonly byte[] MutationPayload = BuildPayload(0xC0DE, 1536);

  private static IEnumerable<TestCaseData> CreatableFilesystemIds() {
    FormatRegistration.EnsureInitialized();
    foreach (var id in FormatRegistry.FilesystemFormatIds.OrderBy(static id => id, StringComparer.Ordinal)) {
      var descriptor = FormatRegistry.GetById(id)!;
      if (!descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate))
        continue;
      if (!descriptor.Capabilities.HasFlag(FormatCapabilities.CanExtract))
        continue;
      if (FormatRegistry.GetArchiveOps(id) is not IArchiveCreatable)
        continue;
      yield return new TestCaseData(id).SetName($"MountSessionContract_{id}");
    }
  }

  private static IEnumerable<TestCaseData> CreatableModifiableFilesystemIds() {
    FormatRegistration.EnsureInitialized();
    foreach (var id in FormatRegistry.FilesystemFormatIds.OrderBy(static id => id, StringComparer.Ordinal)) {
      var descriptor = FormatRegistry.GetById(id)!;
      if (!descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate)
          || !descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify)
          || FormatRegistry.GetArchiveOps(id) is not IArchiveCreatable)
        continue;
      yield return new TestCaseData(id).SetName($"WritableMountSessionContract_{id}");
    }
  }

  [TestCaseSource(nameof(CreatableFilesystemIds))]
  [Category("Mounting")]
  [CancelAfter(30000)]
  public async Task EveryCreatableFilesystemOpensAsAMountGradeReadOnlySession(string formatId) {
    var imageBytes = CreateImage(formatId);
    using var image = new MemoryStream(imageBytes, writable: true);

    var profile = FormatRegistry.ProbeFilesystem(formatId, image);
    Assert.That(profile.CanMount, Is.True,
      $"{formatId}: its own writer produced an image its filesystem provider does not consider mountable: "
      + string.Join("; ", profile.Limitations));

    image.Position = 0;
    using var filesystem = FormatRegistry.OpenFilesystem(formatId, image, new(ReadOnly: true, LeaveOpen: true));
    var probe = FindReadableFile(filesystem);

    var reads = Enumerable.Range(0, 8)
      .Select(_ => Task.Run(() => ReadPrefix(filesystem, probe.NodeId, probe.Expected.Length)))
      .ToArray();
    await Task.WhenAll(reads);

    foreach (var read in reads)
      Assert.That(read.Result, Is.EqualTo(probe.Expected),
        $"{formatId}: parallel positional reads returned different bytes.");
  }

  [TestCaseSource(nameof(CreatableModifiableFilesystemIds))]
  [Category("Mounting")]
  [CancelAfter(30000)]
  public void ModifiableFormatsEitherProvideCoherentMountedWritesOrRejectThem(string formatId) {
    var imageBytes = CreateImage(formatId);
    using var image = new MemoryStream();
    image.Write(imageBytes);
    image.Position = 0;

    var profile = FormatRegistry.ProbeFilesystem(formatId, image);
    if (!profile.CanMountWritable) {
      image.Position = 0;
      Assert.Throws<NotSupportedException>(() => {
        using var _ = FormatRegistry.OpenFilesystem(formatId, image, new(ReadOnly: false, LeaveOpen: true));
      }, $"{formatId}: a profile that does not advertise writable mounting must fail closed instead of silently opening read-only.");
      return;
    }

    const FilesystemDriverCapabilities required =
      FilesystemDriverCapabilities.WriteData |
      FilesystemDriverCapabilities.Truncate |
      FilesystemDriverCapabilities.CreateFile |
      FilesystemDriverCapabilities.DeleteFile |
      FilesystemDriverCapabilities.Rename |
      FilesystemDriverCapabilities.Flush;
    Assert.That(profile.Capabilities & required, Is.EqualTo(required),
      $"{formatId}: CanMountWritable requires the file mutation primitives needed by a kernel frontend.");
    Assert.That(profile.MutationModel, Is.Not.AnyOf(FilesystemMutationModel.None, FilesystemMutationModel.WholeImageRebuild),
      $"{formatId}: whole-image rebuilding is not a mounted random-write durability model.");

    image.Position = 0;
    using var filesystem = FormatRegistry.OpenFilesystem(formatId, image, new(ReadOnly: false, LeaveOpen: true));
    const string firstName = "RWTST.BIN";
    const string secondName = "RWTST2.BIN";

    var nodeId = filesystem.CreateFile(filesystem.RootNodeId, firstName);
    using (var handle = filesystem.OpenFile(nodeId, FileAccess.ReadWrite)) {
      handle.SetLength(0);
      handle.Write(0, MutationPayload);
      handle.Flush();
    }
    filesystem.Flush();
    Assert.That(ReadPrefix(filesystem, nodeId, MutationPayload.Length), Is.EqualTo(MutationPayload),
      $"{formatId}: data written through the mount-grade session did not read back.");

    filesystem.Rename(filesystem.RootNodeId, firstName, filesystem.RootNodeId, secondName, replace: false);
    var renamed = filesystem.Lookup(filesystem.RootNodeId, secondName);
    Assert.That(renamed, Is.EqualTo(nodeId), $"{formatId}: rename changed or lost the stable node identity.");
    Assert.That(filesystem.Lookup(filesystem.RootNodeId, firstName), Is.Null,
      $"{formatId}: old name remains visible after rename.");

    filesystem.DeleteFile(filesystem.RootNodeId, secondName);
    Assert.That(filesystem.Lookup(filesystem.RootNodeId, secondName), Is.Null,
      $"{formatId}: deleted file remains visible in the namespace.");
    filesystem.Flush();
  }

  [TestCase("Iso", false)]
  [TestCase("D64", true)]
  [Category("OsIntegration")]
  [CancelAfter(90000)]
  public async Task RealFormatsSurviveRepeatedMountReadOnlyMutationAttemptsAndUnmounts(
    string formatId,
    bool expectWritableFilesystemProfile
  ) {
    var backend = RequireHostBackend();

    for (var cycle = 0; cycle < 3; ++cycle) {
      using var fixture = CreateFixture(formatId);
      Assert.That(fixture.Filesystem.Profile.CanMountWritable, Is.EqualTo(expectWritableFilesystemProfile),
        $"{formatId}: unexpected writable-filesystem profile on cycle {cycle}.");

      var target = CreateMountTargets(1)[0];
      IMountSession? mount = null;
      try {
        var plan = ResolveReadOnlyPlan(fixture.Filesystem, backend);
        mount = await backend.MountAsync(new(fixture.Filesystem, target, plan));
        Assert.That(mount.IsMounted, Is.True, $"{formatId}: cycle {cycle} did not report a mounted session.");

        Assert.That(Directory.GetFileSystemEntries(target), Is.Not.Empty,
          $"{formatId}: cycle {cycle} exposed an empty mounted namespace.");
        AssertMountedReadMatches(target, fixture.Probe);
        AssertMountedReadOnly(target, fixture.Probe);
        AssertMountedReadMatches(target, fixture.Probe);

        await mount.UnmountAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(mount.IsMounted, Is.False, $"{formatId}: cycle {cycle} remained mounted after unmount.");
      } finally {
        if (mount is not null)
          await mount.DisposeAsync();
        CleanupMountTarget(target);
      }
    }
  }

  [Test]
  [Category("OsIntegration")]
  [CancelAfter(90000)]
  public async Task RealFormatsCanBeMountedAndUnmountedInParallelWithoutCrossTalk() {
    var backend = RequireHostBackend();
    string[] formatIds = ["D64", "Iso", "D64", "Iso"];
    var fixtures = formatIds.Select(CreateFixture).ToArray();
    var targets = CreateMountTargets(fixtures.Length);
    var mounts = new IMountSession?[fixtures.Length];

    try {
      await Task.WhenAll(fixtures.Select(async (fixture, index) => {
        var plan = ResolveReadOnlyPlan(fixture.Filesystem, backend);
        mounts[index] = await backend.MountAsync(new(fixture.Filesystem, targets[index], plan));
      }));

      Assert.That(mounts, Has.All.Not.Null);
      Assert.That(mounts.Select(static mount => mount!.IsMounted), Has.All.True,
        "At least one parallel mount did not become active.");

      await Task.WhenAll(fixtures.Select((fixture, index) => Task.Run(() => {
        AssertMountedReadMatches(targets[index], fixture.Probe);
        AssertMountedReadOnly(targets[index], fixture.Probe);
        AssertMountedReadMatches(targets[index], fixture.Probe);
      })));

      await Task.WhenAll(mounts.Select(static mount =>
        mount!.UnmountAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10))));
      Assert.That(mounts.Select(static mount => mount!.IsMounted), Has.All.False,
        "At least one parallel mount remained active after unmount.");
    } finally {
      foreach (var mount in mounts)
        if (mount is not null)
          await mount.DisposeAsync();
      foreach (var fixture in fixtures)
        fixture.Dispose();
      foreach (var target in targets)
        CleanupMountTarget(target);
    }
  }

  [Test]
  [Category("Mounting")]
  public void WritableFilesystemProfilesAreNotSilentlyDowngradedByReadOnlyBackends() {
    using var fixture = CreateFixture("D64");
    Assert.That(fixture.Filesystem.Profile.CanMountWritable, Is.True,
      "D64 is the regression fixture for a filesystem provider that has native mounted-write primitives.");

    foreach (var backend in BackendProfiles()) {
      var plan = FilesystemMountCapabilityResolver.Resolve(
        fixture.Filesystem.Profile,
        backend,
        MountAccessMode.ReadWrite,
        sourceCanWrite: true);

      if (backend.SupportsReadWrite) {
        Assert.That(plan.Reasons.Any(static reason => reason.Code == MountSupportReasonCode.BackendDoesNotSupportReadWrite), Is.False,
          $"{backend.Id}: claims writable mounting but the resolver still treats it as read-only.");
        continue;
      }

      Assert.That(plan.IsSupported, Is.False, $"{backend.Id}: must not silently downgrade a requested writable mount.");
      Assert.That(plan.Reasons.Any(static reason => reason.Code == MountSupportReasonCode.BackendDoesNotSupportReadWrite), Is.True,
        $"{backend.Id}: writable refusal must identify the backend capability, not fail later inside a native callback.");
    }
  }

  private static IEnumerable<MountBackendProfile> BackendProfiles() {
    yield return new DokanFilesystemMountBackend(DokanRuntimeProbe.Probe()).GetProfile();
    yield return new FuseFilesystemMountBackend(FuseRuntimeProbe.Probe()).GetProfile();
  }

  private static IFilesystemMountBackend RequireHostBackend() {
    if (OperatingSystem.IsWindows()) {
      var runtime = DokanRuntimeProbe.Probe();
      if (!runtime.IsAvailable)
        Assert.Ignore(runtime.UnavailableReason ?? "Dokan is unavailable on this host.");
      return new DokanFilesystemMountBackend(runtime);
    }

    if (OperatingSystem.IsLinux()) {
      var runtime = FuseRuntimeProbe.Probe();
      if (!runtime.IsAvailable)
        Assert.Ignore(runtime.UnavailableReason ?? "FUSE3 is unavailable on this host.");
      return new FuseFilesystemMountBackend(runtime);
    }

    Assert.Ignore("Filesystem mount integration currently requires Dokan on Windows or FUSE3 on Linux.");
    throw new PlatformNotSupportedException();
  }

  private static MountPlan ResolveReadOnlyPlan(IFilesystemSession filesystem, IFilesystemMountBackend backend) {
    var plan = FilesystemMountCapabilityResolver.Resolve(
      filesystem.Profile,
      backend.GetProfile(),
      MountAccessMode.ReadOnly,
      sourceCanWrite: false);
    Assert.That(plan.IsSupported, Is.True,
      $"{filesystem.Profile.FormatId}: read-only mount should be supported: "
      + string.Join(Environment.NewLine, plan.Reasons.Select(static reason => reason.Message)));
    return plan;
  }

  private static FormatFixture CreateFixture(string formatId) {
    var imageBytes = CreateImage(formatId);
    var image = new MemoryStream();
    image.Write(imageBytes);
    image.Position = 0;
    var profile = FormatRegistry.ProbeFilesystem(formatId, image);
    Assert.That(profile.CanMount, Is.True,
      $"{formatId}: its own writer produced a non-mountable image: {string.Join("; ", profile.Limitations)}");
    image.Position = 0;
    var filesystem = FormatRegistry.OpenFilesystem(formatId, image, new(ReadOnly: true, LeaveOpen: true));
    return new(formatId, image, filesystem, FindReadableFile(filesystem));
  }

  private static byte[] CreateImage(string formatId) {
    FormatRegistration.EnsureInitialized();
    var operations = FormatRegistry.GetArchiveOps(formatId);
    Assert.That(operations, Is.InstanceOf<IArchiveCreatable>(),
      $"{formatId}: mount integration requires a self-created deterministic test image.");

    using var image = new MemoryStream();
    ((IArchiveCreatable)operations!).Create(
      image,
      [ArchiveInputInfo.InMemory(ProbeName, ProbePayload)],
      new FormatCreateOptions());
    Assert.That(image.Length, Is.GreaterThan(0), $"{formatId}: creator produced an empty image.");
    return image.ToArray();
  }

  private static FileProbe FindReadableFile(IFilesystemSession filesystem) {
    var queue = new Queue<(FilesystemNodeId NodeId, string RelativePath)>();
    var visited = new HashSet<FilesystemNodeId>();
    queue.Enqueue((filesystem.RootNodeId, string.Empty));

    while (queue.TryDequeue(out var current)) {
      if (!visited.Add(current.NodeId))
        continue;

      foreach (var entry in filesystem.Enumerate(current.NodeId)) {
        var relative = string.IsNullOrEmpty(current.RelativePath)
          ? entry.Name
          : Path.Combine(current.RelativePath, entry.Name);
        if (entry.Kind == FilesystemNodeKind.Directory) {
          queue.Enqueue((entry.NodeId, relative));
          continue;
        }
        if (entry.Kind != FilesystemNodeKind.RegularFile)
          continue;

        var info = filesystem.Stat(entry.NodeId);
        if (info.Size <= 0)
          continue;
        var length = checked((int)Math.Min(info.Size, ProbePrefixLength));
        var expected = ReadPrefix(filesystem, entry.NodeId, length);
        if (expected.Length != 0)
          return new(entry.NodeId, relative, expected);
      }
    }

    Assert.Fail($"{filesystem.Profile.FormatId}: created image exposes no non-empty regular file through the mount-grade session.");
    throw new InvalidOperationException();
  }

  private static byte[] ReadPrefix(IFilesystemSession filesystem, FilesystemNodeId nodeId, int length) {
    using var handle = filesystem.OpenFile(nodeId, FileAccess.Read);
    var expectedLength = checked((int)Math.Min(handle.Length, length));
    var result = new byte[expectedLength];
    var offset = 0;
    while (offset < result.Length) {
      var read = handle.Read(offset, result.AsSpan(offset));
      if (read <= 0)
        Assert.Fail($"{filesystem.Profile.FormatId}: file handle returned EOF after {offset} of {result.Length} bytes.");
      offset += read;
    }
    return result;
  }

  private static void AssertMountedReadMatches(string target, FileProbe probe) {
    var path = Path.Combine(target, probe.RelativePath);
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    var actual = new byte[probe.Expected.Length];
    stream.ReadExactly(actual);
    Assert.That(actual, Is.EqualTo(probe.Expected), $"Mounted read of '{probe.RelativePath}' returned different bytes.");
  }

  private static void AssertMountedReadOnly(string target, FileProbe probe) {
    var existing = Path.Combine(target, probe.RelativePath);
    var created = Path.Combine(target, "RWTST.BIN");
    var directory = Path.Combine(target, "RWDIR");

    AssertReadOnlyFailure(() => File.WriteAllBytes(created, MutationPayload), "create file");
    AssertReadOnlyFailure(() => {
      using var stream = new FileStream(existing, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
      stream.WriteByte(0x42);
    }, "open existing file for write");
    AssertReadOnlyFailure(() => File.Delete(existing), "delete file");
    AssertReadOnlyFailure(() => Directory.CreateDirectory(directory), "create directory");
  }

  private static void AssertReadOnlyFailure(Action operation, string description) {
    try {
      operation();
    } catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) {
      return;
    }
    Assert.Fail($"Read-only mounted filesystem unexpectedly allowed operation: {description}.");
  }

  private static string[] CreateMountTargets(int count) {
    if (OperatingSystem.IsWindows()) {
      var used = DriveInfo.GetDrives()
        .Select(static drive => char.ToUpperInvariant(drive.Name[0]))
        .ToHashSet();
      var targets = new List<string>(count);
      for (var drive = 'Z'; drive >= 'D' && targets.Count < count; --drive)
        if (used.Add(drive))
          targets.Add($"{drive}:\\");
      if (targets.Count != count)
        throw new IOException($"Need {count} free drive letters for Dokan integration, found {targets.Count}.");
      return targets.ToArray();
    }

    var result = new string[count];
    for (var i = 0; i < count; ++i) {
      result[i] = Path.Combine(Path.GetTempPath(), $"cwb-fuse-{Guid.NewGuid():N}");
      Directory.CreateDirectory(result[i]);
    }
    return result;
  }

  private static void CleanupMountTarget(string target) {
    if (OperatingSystem.IsWindows())
      return;
    try {
      if (Directory.Exists(target))
        Directory.Delete(target, recursive: true);
    } catch {
      // Best-effort cleanup; a failed unmount is already the test failure with useful context.
    }
  }

  private static byte[] BuildPayload(int seed, int length) {
    var data = new byte[length];
    new Random(seed).NextBytes(data);
    return data;
  }

  private sealed record FileProbe(FilesystemNodeId NodeId, string RelativePath, byte[] Expected);

  private sealed class FormatFixture(
    string formatId,
    MemoryStream image,
    IFilesystemSession filesystem,
    FileProbe probe
  ) : IDisposable {
    public string FormatId { get; } = formatId;
    public MemoryStream Image { get; } = image;
    public IFilesystemSession Filesystem { get; } = filesystem;
    public FileProbe Probe { get; } = probe;

    public void Dispose() {
      this.Filesystem.Dispose();
      this.Image.Dispose();
    }
  }
}
