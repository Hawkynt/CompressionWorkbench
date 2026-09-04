using System.Runtime.InteropServices;
using System.Text;
using Compression.Mounting;
using Compression.Mounting.Fuse;
using Compression.Registry;

namespace Compression.Tests.Mounting;

[TestFixture]
public sealed class FuseFilesystemOperationsTests {
  private TestFilesystemSession _filesystem = null!;
  private FuseFilesystemOperations _sut = null!;
  private FilesystemNodeId _docs;
  private FilesystemNodeId _readme;

  [SetUp]
  public void SetUp() {
    this._filesystem = new();
    this._docs = this._filesystem.AddDirectory(this._filesystem.RootNodeId, "docs");
    this._readme = this._filesystem.AddFile(this._docs, "readme.txt", "abcdef");
    this._sut = new(this._filesystem);
  }

  [TearDown]
  public void TearDown()
    => this._sut.Dispose();

  [Test]
  public void LookupUsesStableInodesAndTracksKernelReferences() {
    Assert.That(this._sut.Lookup(FuseFilesystemOperations.RootInode, "docs", out var first), Is.Zero);
    Assert.That(this._sut.Lookup(FuseFilesystemOperations.RootInode, "docs", out var second), Is.Zero);

    Assert.Multiple(() => {
      Assert.That(first.Inode, Is.EqualTo(second.Inode));
      Assert.That(first.Node.NodeId, Is.EqualTo(this._docs));
      Assert.That(this._sut.LookupCount(first.Inode), Is.EqualTo(2));
    });

    this._sut.Forget(first.Inode, 1);
    Assert.That(this._sut.LookupCount(first.Inode), Is.EqualTo(1));

    this._sut.Forget(first.Inode, ulong.MaxValue);
    Assert.Multiple(() => {
      Assert.That(this._sut.LookupCount(first.Inode), Is.Zero);
      Assert.That(this._sut.GetAttributes(first.Inode, out _), Is.EqualTo(FuseErrno.NoEntry));
      Assert.That(this._sut.TrackedInodeCount, Is.EqualTo(1));
    });
  }

  [Test]
  public void OpenFilePinsInodeUntilReleaseEvenAfterForget() {
    var inode = LookupReadme();
    Assert.That(this._sut.OpenFile(inode, flags: 0, out var handleId), Is.Zero);

    this._sut.Forget(inode, ulong.MaxValue);

    Span<byte> buffer = stackalloc byte[3];
    var attributesError = this._sut.GetAttributes(inode, out _);
    var readError = this._sut.ReadFile(handleId, 1, buffer, out var count);
    var text = Encoding.ASCII.GetString(buffer);

    Assert.Multiple(() => {
      Assert.That(attributesError, Is.Zero);
      Assert.That(readError, Is.Zero);
      Assert.That(count, Is.EqualTo(3));
      Assert.That(text, Is.EqualTo("bcd"));
    });

    Assert.That(this._sut.ReleaseFile(handleId), Is.Zero);
    Assert.That(this._sut.GetAttributes(inode, out _), Is.EqualTo(FuseErrno.NoEntry));
  }

  [TestCase(0x1)]
  [TestCase(0x2)]
  [TestCase(0x200)]
  public void OpenRejectsEveryWriteCapableMode(int flags) {
    var inode = LookupReadme();

    var error = this._sut.OpenFile(inode, flags, out var handleId);

    Assert.Multiple(() => {
      Assert.That(error, Is.EqualTo(FuseErrno.ReadOnlyFileSystem));
      Assert.That(handleId, Is.Zero);
      Assert.That(this._filesystem.OpenCount, Is.Zero);
    });
  }

  [Test]
  public void AccessReportsReadOnlyAndExecutableSemantics() {
    var inode = LookupReadme();

    Assert.Multiple(() => {
      Assert.That(this._sut.Access(inode, 0), Is.Zero);
      Assert.That(this._sut.Access(inode, 0x4), Is.Zero);
      Assert.That(this._sut.Access(inode, 0x1), Is.EqualTo(FuseErrno.AccessDenied));
      Assert.That(this._sut.Access(inode, 0x2), Is.EqualTo(FuseErrno.ReadOnlyFileSystem));
      Assert.That(this._sut.Access(FuseFilesystemOperations.RootInode, 0x1), Is.Zero);
      Assert.That(this._sut.Access(FuseFilesystemOperations.RootInode, 0x2), Is.EqualTo(FuseErrno.ReadOnlyFileSystem));
      Assert.That(this._sut.Access(inode, 0x8), Is.EqualTo(FuseErrno.InvalidArgument));
    });
  }

  [Test]
  public void FileHandlesPerformIndependentPositionalReads() {
    var inode = LookupReadme();
    Assert.That(this._sut.OpenFile(inode, flags: 0, out var handleId), Is.Zero);

    Span<byte> middle = stackalloc byte[3];
    Span<byte> start = stackalloc byte[2];

    Assert.That(this._sut.ReadFile(handleId, 2, middle, out var middleRead), Is.Zero);
    Assert.That(this._sut.ReadFile(handleId, 0, start, out var startRead), Is.Zero);

    var middleText = Encoding.ASCII.GetString(middle);
    var startText = Encoding.ASCII.GetString(start);

    Assert.Multiple(() => {
      Assert.That(middleRead, Is.EqualTo(3));
      Assert.That(middleText, Is.EqualTo("cde"));
      Assert.That(startRead, Is.EqualTo(2));
      Assert.That(startText, Is.EqualTo("ab"));
    });
  }

  [Test]
  public void ReleaseInvalidatesFileHandle() {
    var inode = LookupReadme();
    Assert.That(this._sut.OpenFile(inode, flags: 0, out var handleId), Is.Zero);
    Assert.That(this._sut.ReleaseFile(handleId), Is.Zero);

    Span<byte> buffer = stackalloc byte[1];
    Assert.That(
      this._sut.ReadFile(handleId, 0, buffer, out _),
      Is.EqualTo(FuseErrno.BadFileDescriptor)
    );
  }

  [Test]
  public void DirectoryHandleIncludesDotEntriesAndKeepsStableSnapshotOffsets() {
    Assert.That(this._sut.Lookup(FuseFilesystemOperations.RootInode, "docs", out var docs), Is.Zero);
    Assert.That(this._sut.OpenDirectory(docs.Inode, out var handleId), Is.Zero);

    this._filesystem.AddFile(this._docs, "later.txt", "new");

    Assert.That(this._sut.ReadDirectory(handleId, 0, out var allEntries), Is.Zero);
    Assert.That(this._sut.ReadDirectory(handleId, 2, out var afterDots), Is.Zero);
    Assert.That(this._sut.ReadDirectory(handleId, 3, out var afterFirstChild), Is.Zero);

    Assert.Multiple(() => {
      Assert.That(allEntries.Select(static entry => entry.Name), Is.EqualTo(new[] { ".", "..", "readme.txt" }));
      Assert.That(allEntries.Select(static entry => entry.NextOffset), Is.EqualTo(new long[] { 1, 2, 3 }));
      Assert.That(allEntries[0].Inode, Is.EqualTo(docs.Inode));
      Assert.That(allEntries[1].Inode, Is.EqualTo(FuseFilesystemOperations.RootInode));
      Assert.That(afterDots.Select(static entry => entry.Name), Is.EqualTo(new[] { "readme.txt" }));
      Assert.That(afterFirstChild, Is.Empty);
    });
  }

  [Test]
  public void DirectorySnapshotPinsUnlookedUpChildInodesOnlyUntilRelease() {
    Assert.That(this._sut.Lookup(FuseFilesystemOperations.RootInode, "docs", out var docs), Is.Zero);
    var before = this._sut.TrackedInodeCount;

    Assert.That(this._sut.OpenDirectory(docs.Inode, out var handleId), Is.Zero);
    Assert.That(this._sut.TrackedInodeCount, Is.EqualTo(before + 1));

    Assert.That(this._sut.ReleaseDirectory(handleId), Is.Zero);
    Assert.That(this._sut.TrackedInodeCount, Is.EqualTo(before));
  }

  [Test]
  public void BackendAdvertisesReadOnlyOnly() {
    var backend = new FuseFilesystemMountBackend(
      new FuseRuntimeStatus(true, "libfuse3.so.3", "/usr/bin/fusermount3", null)
    );

    var profile = backend.GetProfile();

    Assert.Multiple(() => {
      Assert.That(profile.Id, Is.EqualTo("fuse3"));
      Assert.That(profile.IsAvailable, Is.True);
      Assert.That(profile.SupportsReadOnly, Is.True);
      Assert.That(profile.SupportsReadWrite, Is.False);
    });
  }

  [Test]
  public void LinuxX64InteropLayoutsMatchLibfuseAbi() {
    Assert.Multiple(() => {
      Assert.That(Marshal.SizeOf<LinuxStat>(), Is.EqualTo(144));
      Assert.That(Marshal.SizeOf<FuseFileInfo>(), Is.EqualTo(64));
      Assert.That(Marshal.SizeOf<FuseEntryParam>(), Is.EqualTo(176));
      Assert.That(Marshal.SizeOf<FuseLowLevelOps>(), Is.EqualTo(30 * IntPtr.Size));
    });
  }

  [Test]
  public void DirectoryStatsExposePosixMinimumLinkCount() {
    var stat = FuseStatFactory.Create(
      FuseFilesystemOperations.RootInode,
      this._filesystem.Stat(this._filesystem.RootNodeId)
    );

    Assert.That(stat.LinkCount, Is.GreaterThanOrEqualTo(2));
  }

  [Test]
  public void ExecutableProbeSearchesProvidedPath() {
    var temp = Path.Combine(Path.GetTempPath(), $"cwb-fuse-probe-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temp);
    try {
      var executable = Path.Combine(temp, "fusermount3");
      File.WriteAllText(executable, string.Empty);

      Assert.That(FuseRuntimeProbe.FindExecutableOnPath("fusermount3", temp), Is.EqualTo(executable));
    } finally {
      Directory.Delete(temp, recursive: true);
    }
  }

  private ulong LookupReadme() {
    Assert.That(this._sut.Lookup(FuseFilesystemOperations.RootInode, "docs", out var docs), Is.Zero);
    Assert.That(this._sut.Lookup(docs.Inode, "readme.txt", out var readme), Is.Zero);
    Assert.That(readme.Node.NodeId, Is.EqualTo(this._readme));
    return readme.Inode;
  }

  private sealed class TestFilesystemSession : IFilesystemSession {
    private static readonly FilesystemDriverProfile TestProfile = new(
      "testfs",
      "synthetic",
      FilesystemMountCapabilityResolver.CoreReadCapabilities,
      FilesystemMutationModel.None,
      CanMount: true,
      CanMountWritable: false,
      Array.Empty<string>()
    );

    private readonly Dictionary<FilesystemNodeId, FilesystemNodeInfo> _nodes = [];
    private readonly Dictionary<FilesystemNodeId, List<FilesystemDirectoryEntry>> _children = [];
    private readonly Dictionary<FilesystemNodeId, byte[]> _files = [];
    private ulong _nextNodeId = 100;

    public TestFilesystemSession() {
      this.RootNodeId = this.CreateNode(FilesystemNodeKind.Directory, 0);
    }

    public FilesystemDriverProfile Profile => TestProfile;
    public FilesystemNodeId RootNodeId { get; }
    public int OpenCount { get; private set; }

    public FilesystemNodeId AddDirectory(FilesystemNodeId parent, string name) {
      var nodeId = this.CreateNode(FilesystemNodeKind.Directory, 0);
      this.AddChild(parent, name, nodeId, FilesystemNodeKind.Directory);
      return nodeId;
    }

    public FilesystemNodeId AddFile(FilesystemNodeId parent, string name, string content) {
      var data = Encoding.ASCII.GetBytes(content);
      var nodeId = this.CreateNode(FilesystemNodeKind.RegularFile, data.Length);
      this._files.Add(nodeId, data);
      this.AddChild(parent, name, nodeId, FilesystemNodeKind.RegularFile);
      return nodeId;
    }

    public FilesystemNodeInfo Stat(FilesystemNodeId nodeId)
      => this._nodes.TryGetValue(nodeId, out var info)
        ? info
        : throw new KeyNotFoundException($"Unknown node '{nodeId}'.");

    public FilesystemNodeId? Lookup(FilesystemNodeId parentDirectory, string name)
      => this._children.TryGetValue(parentDirectory, out var entries)
        ? entries.FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.Ordinal))?.NodeId
        : null;

    public IReadOnlyList<FilesystemDirectoryEntry> Enumerate(FilesystemNodeId directory)
      => this._children.TryGetValue(directory, out var entries) ? entries.ToArray() : [];

    public IFilesystemFileHandle OpenFile(FilesystemNodeId nodeId, FileAccess access) {
      if (access != FileAccess.Read)
        throw new UnauthorizedAccessException();
      if (!this._files.TryGetValue(nodeId, out var data))
        throw new FileNotFoundException();

      ++this.OpenCount;
      return new TestFileHandle(nodeId, data);
    }

    public FilesystemNodeId CreateFile(FilesystemNodeId parentDirectory, string name) => throw new NotSupportedException();
    public FilesystemNodeId CreateDirectory(FilesystemNodeId parentDirectory, string name) => throw new NotSupportedException();
    public void DeleteFile(FilesystemNodeId parentDirectory, string name) => throw new NotSupportedException();
    public void RemoveDirectory(FilesystemNodeId parentDirectory, string name) => throw new NotSupportedException();
    public void Rename(FilesystemNodeId oldParent, string oldName, FilesystemNodeId newParent, string newName, bool replace) => throw new NotSupportedException();
    public void CreateHardLink(FilesystemNodeId existingNode, FilesystemNodeId newParent, string newName) => throw new NotSupportedException();
    public FilesystemNodeId CreateSymbolicLink(FilesystemNodeId parentDirectory, string name, string target) => throw new NotSupportedException();
    public string ReadSymbolicLink(FilesystemNodeId nodeId) => throw new NotSupportedException();
    public void SetMetadata(FilesystemNodeId nodeId, FilesystemMetadataPatch patch) => throw new NotSupportedException();
    public void Flush() { }
    public IFilesystemTransaction BeginTransaction() => throw new NotSupportedException();
    public void Dispose() { }

    private FilesystemNodeId CreateNode(FilesystemNodeKind kind, long size) {
      var nodeId = new FilesystemNodeId(this._nextNodeId++);
      this._nodes.Add(nodeId, new(nodeId, kind, size, size));
      if (kind == FilesystemNodeKind.Directory)
        this._children.Add(nodeId, []);
      return nodeId;
    }

    private void AddChild(FilesystemNodeId parent, string name, FilesystemNodeId nodeId, FilesystemNodeKind kind)
      => this._children[parent].Add(new(name, nodeId, kind));
  }

  private sealed class TestFileHandle(FilesystemNodeId nodeId, byte[] data) : IFilesystemFileHandle {
    private readonly byte[] _data = data;

    public FilesystemNodeId NodeId { get; } = nodeId;
    public long Length => this._data.Length;

    public int Read(long offset, Span<byte> destination) {
      if (offset < 0 || offset >= this._data.Length)
        return 0;
      var count = Math.Min(destination.Length, this._data.Length - checked((int)offset));
      this._data.AsSpan(checked((int)offset), count).CopyTo(destination);
      return count;
    }

    public void Write(long offset, ReadOnlySpan<byte> source) => throw new NotSupportedException();
    public void SetLength(long length) => throw new NotSupportedException();
    public void Flush() { }
    public void Dispose() { }
  }
}
