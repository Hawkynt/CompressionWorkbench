using Compression.Mounting;
using Compression.Registry;

namespace Compression.Tests.Mounting;

[TestFixture]
public sealed class FilesystemPathResolverTests {
  private TestFilesystemSession _sut = null!;

  [SetUp]
  public void SetUp() {
    this._sut = new();
    var games = this._sut.AddDirectory(this._sut.RootNodeId, "games");
    var saves = this._sut.AddDirectory(games, "saves");
    this._sut.AddFile(saves, "slot1.dat", 42);
    this._sut.AddFile(this._sut.RootNodeId, "README", 7);
  }

  [TestCase("")]
  [TestCase("/")]
  [TestCase("\\")]
  [TestCase("//\\/")]
  [TestCase(".")]
  public void RootPathsResolveToRootNode(string path) {
    var resolved = FilesystemPathResolver.TryResolve(this._sut, path, out var nodeId);

    Assert.Multiple(() => {
      Assert.That(resolved, Is.True);
      Assert.That(nodeId, Is.EqualTo(this._sut.RootNodeId));
    });
  }

  [TestCase("games/saves/slot1.dat")]
  [TestCase("/games/saves/slot1.dat")]
  [TestCase("\\games\\saves\\slot1.dat")]
  [TestCase("games\\saves/slot1.dat")]
  [TestCase("games/./saves//slot1.dat")]
  public void ResolveAcceptsBackendPathStyles(string path) {
    var resolved = FilesystemPathResolver.TryResolve(this._sut, path, out var nodeId);

    Assert.Multiple(() => {
      Assert.That(resolved, Is.True);
      Assert.That(this._sut.Stat(nodeId).Kind, Is.EqualTo(FilesystemNodeKind.RegularFile));
      Assert.That(this._sut.Stat(nodeId).Size, Is.EqualTo(42));
    });
  }

  [TestCase("missing")]
  [TestCase("games/missing")]
  [TestCase("README/child")]
  [TestCase("../README")]
  [TestCase("games/../README")]
  public void ResolveRejectsMissingInvalidOrEscapingPaths(string path) {
    Assert.That(FilesystemPathResolver.TryResolve(this._sut, path, out _), Is.False);
  }

  [TestCase("new.bin", "new.bin")]
  [TestCase("/games/saves/new.bin", "new.bin")]
  [TestCase("\\games\\saves\\new.bin", "new.bin")]
  [TestCase("games/./saves/new.bin", "new.bin")]
  public void ResolveParentReturnsContainingDirectoryAndLeafName(string path, string expectedName) {
    var resolved = FilesystemPathResolver.TryResolveParent(
      this._sut,
      path,
      out var parentDirectory,
      out var name
    );

    Assert.Multiple(() => {
      Assert.That(resolved, Is.True);
      Assert.That(name, Is.EqualTo(expectedName));
      Assert.That(this._sut.Stat(parentDirectory).Kind, Is.EqualTo(FilesystemNodeKind.Directory));
    });
  }

  [TestCase("")]
  [TestCase("/")]
  [TestCase(".")]
  [TestCase("../new.bin")]
  [TestCase("games/../new.bin")]
  [TestCase("README/child")]
  [TestCase("missing/new.bin")]
  public void ResolveParentRejectsRootEscapesAndNonDirectories(string path) {
    Assert.That(FilesystemPathResolver.TryResolveParent(this._sut, path, out _, out _), Is.False);
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
    private ulong _nextNodeId = 1;

    public TestFilesystemSession() {
      this.RootNodeId = this.CreateNode(FilesystemNodeKind.Directory, 0);
    }

    public FilesystemDriverProfile Profile => TestProfile;
    public FilesystemNodeId RootNodeId { get; }

    public FilesystemNodeId AddDirectory(FilesystemNodeId parent, string name) {
      var nodeId = this.CreateNode(FilesystemNodeKind.Directory, 0);
      this.AddChild(parent, name, nodeId, FilesystemNodeKind.Directory);
      return nodeId;
    }

    public FilesystemNodeId AddFile(FilesystemNodeId parent, string name, long size) {
      var nodeId = this.CreateNode(FilesystemNodeKind.RegularFile, size);
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
      => this._children.TryGetValue(directory, out var entries) ? entries : [];

    public IFilesystemFileHandle OpenFile(FilesystemNodeId nodeId, FileAccess access)
      => throw new NotSupportedException();

    public FilesystemNodeId CreateFile(FilesystemNodeId parentDirectory, string name)
      => throw new NotSupportedException();

    public FilesystemNodeId CreateDirectory(FilesystemNodeId parentDirectory, string name)
      => throw new NotSupportedException();

    public void DeleteFile(FilesystemNodeId parentDirectory, string name)
      => throw new NotSupportedException();

    public void RemoveDirectory(FilesystemNodeId parentDirectory, string name)
      => throw new NotSupportedException();

    public void Rename(
      FilesystemNodeId oldParent,
      string oldName,
      FilesystemNodeId newParent,
      string newName,
      bool replace
    ) => throw new NotSupportedException();

    public void CreateHardLink(FilesystemNodeId existingNode, FilesystemNodeId newParent, string newName)
      => throw new NotSupportedException();

    public FilesystemNodeId CreateSymbolicLink(FilesystemNodeId parentDirectory, string name, string target)
      => throw new NotSupportedException();

    public string ReadSymbolicLink(FilesystemNodeId nodeId)
      => throw new NotSupportedException();

    public void SetMetadata(FilesystemNodeId nodeId, FilesystemMetadataPatch patch)
      => throw new NotSupportedException();

    public void Flush() { }

    public IFilesystemTransaction BeginTransaction()
      => throw new NotSupportedException();

    public void Dispose() { }

    private FilesystemNodeId CreateNode(FilesystemNodeKind kind, long size) {
      var nodeId = new FilesystemNodeId(this._nextNodeId++);
      this._nodes.Add(nodeId, new(nodeId, kind, size, size));
      if (kind == FilesystemNodeKind.Directory)
        this._children.Add(nodeId, []);
      return nodeId;
    }

    private void AddChild(
      FilesystemNodeId parent,
      string name,
      FilesystemNodeId nodeId,
      FilesystemNodeKind kind
    ) => this._children[parent].Add(new(name, nodeId, kind));
  }
}
