using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileSystem.BcacheFs;

namespace Compression.Tests.BcacheFs;

/// <summary>
/// Volumes <c>bcachefs format</c> spread over several image files: edited in place through every
/// member at once, with the copies the volume's replicas settings ask for, and proven with
/// <c>bcachefs fsck</c> over all members.
/// </summary>
[TestFixture]
public sealed class BcacheFsMultiDeviceTests {

  private string _tmpDir = null!;

  [SetUp]
  public void SetUp() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), "cwb_bcachefs_multi_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmpDir, recursive: true); } catch { /* best effort */ }
  }

  private string[] Format(string options, params string[] sizes) {
    var paths = sizes.Select((_, i) => Path.Combine(this._tmpDir, $"member{i}.img")).ToArray();
    var wsl = paths.Select(FsInteropToolbox.WinToWsl).ToArray();
    var truncate = string.Join(" && ", wsl.Select((p, i) => $"truncate -s {sizes[i]} {p}"));
    var run = FsInteropToolbox.RunWsl($"{truncate} && bcachefs format -q {options} {string.Join(" ", wsl)}");
    Assert.That(run.ExitCode, Is.Zero, $"bcachefs format {options}: {run.StdOut}{run.StdErr}");
    return paths;
  }

  private static byte[] Payload(int length, int seed) {
    var bytes = new byte[length];
    new Random(seed).NextBytes(bytes);
    return bytes;
  }

  private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  private static Dictionary<string, string> Read(IReadOnlyList<Stream> members) {
    using var reader = new BcacheFsFormatDescriptor().OpenVolume(members);
    Assert.That(reader.Valid, Is.True, reader.Status);
    return reader.Entries.Where(static e => e.LinkTarget == null)
      .ToDictionary(e => e.Name, e => Hash(reader.Read(e)), StringComparer.Ordinal);
  }

  /// <summary>Adds a spread of files (several spanning many buckets), removes one, replaces one.</summary>
  private static Dictionary<string, string> Edit(IReadOnlyList<Stream> members, int fileCount = 30) {
    var descriptor = new BcacheFsFormatDescriptor();
    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) {
      ["ours/small.txt"] = Encoding.ASCII.GetBytes("a small file\n"),
      ["ours/big.bin"] = Payload(1_500_000, 1),
      ["ours/gone.bin"] = Payload(300_000, 3),
      ["ours/replaced.bin"] = Payload(40_000, 4),
    };
    for (var i = 0; i < fileCount; ++i) files[$"many/d{i % 4}/f{i:D3}.bin"] = Payload(5_000 + 1_000 * i, 100 + i);
    descriptor.AddToVolume(members, [.. files.Select(static f => ArchiveInputInfo.InMemory(f.Key, f.Value))]);
    descriptor.RemoveFromVolume(members, ["ours/gone.bin"]);
    var replacement = Payload(200_000, 5);
    descriptor.AddToVolume(members, [ArchiveInputInfo.InMemory("ours/replaced.bin", replacement)]);
    files.Remove("ours/gone.bin");
    files["ours/replaced.bin"] = replacement;
    return files.ToDictionary(static f => f.Key, static f => Hash(f.Value), StringComparer.Ordinal);
  }

  [TestCase("", "64M", "96M", TestName = "GivenTwoMembersOneCopy_WhenEditedThroughBoth_ThenFsckIsCleanAndEveryFileReadsBack")]
  [TestCase("--replicas=2", "64M", "96M", TestName = "GivenTwoMembersTwoCopies_WhenEditedThroughBoth_ThenFsckIsCleanAndEveryFileReadsBack")]
  [TestCase("--replicas=2", "64M", "64M", "64M", TestName = "GivenThreeMembersTwoCopies_WhenEditedThroughAll_ThenFsckIsCleanAndEveryFileReadsBack")]
  [TestCase("--bucket=64k --btree_node_size=32k --replicas=2", "64M", "64M", TestName = "GivenSmallNodesOnTwoMembersTwoCopies_WhenEdited_ThenFsckIsClean")]
  [Category("ExternalFsInterop")]
  public void FormattedMembersEdited(string options, params string[] sizes) {
    BcacheFsOracleGate.RequireBcachefsTools();
    var paths = this.Format(options, sizes);
    BcacheFsKernel.AssertFsckClean(paths);
    Dictionary<string, string> expected;
    var members = paths.Select(p => (Stream)new FileStream(p, FileMode.Open, FileAccess.ReadWrite)).ToArray();
    try {
      // Supplied in reverse: each member says which one it is.
      expected = Edit([.. members.Reverse()]);
      Assert.That(Read(members), Is.EquivalentTo(expected));
    } finally {
      foreach (var member in members) member.Dispose();
    }
    BcacheFsKernel.AssertFsckClean(paths);
  }

  /// <summary>
  /// Defragmenting several members: every file is read back unchanged, fsck over all members is
  /// clean, and with one copy everything the first member can hold moves onto it — data crossing
  /// from one member to another — each file's runs one after the other.
  /// </summary>
  [TestCase("", DefragMode.ConsolidateAtStart, "64M", "96M", TestName = "GivenTwoMembersOneCopy_WhenConsolidatedAtStart_ThenDataMovesOntoTheFirstMemberAndFsckIsClean")]
  [TestCase("", DefragMode.ConsolidateAtEnd, "64M", "96M", TestName = "GivenTwoMembersOneCopy_WhenConsolidatedAtEnd_ThenFsckIsClean")]
  [TestCase("--replicas=2", DefragMode.ConsolidateAtStart, "64M", "96M", TestName = "GivenTwoMirroredMembers_WhenConsolidated_ThenFsckIsClean")]
  [TestCase("--replicas=2", DefragMode.ConsolidateAtStart, "64M", "64M", "64M", TestName = "GivenThreeMembersTwoCopies_WhenConsolidated_ThenFsckIsClean")]
  [Category("ExternalFsInterop")]
  public void DefragmentMembers(string options, DefragMode mode, params string[] sizes) {
    BcacheFsOracleGate.RequireBcachefsTools();
    var paths = this.Format(options, sizes);
    var members = paths.Select(p => (Stream)new FileStream(p, FileMode.Open, FileAccess.ReadWrite)).ToArray();
    try {
      // Edit through the members in reverse so the data is spread over them, then gather it.
      var expected = Edit([.. members.Reverse()]);
      var lengths = members.Select(m => m.Length).ToArray();
      new BcacheFsFormatDescriptor().DefragmentVolume(members, new DefragOptions { Mode = mode });
      Assert.That(members.Select(m => m.Length), Is.EqualTo(lengths), "every member keeps its size");
      Assert.That(Read(members), Is.EquivalentTo(expected));

      if (options.Length == 0 && mode == DefragMode.ConsolidateAtStart) {
        using var reader = new BcacheFsReader(members);
        var extents = reader.Entries.Where(e => e.LinkTarget == null).OrderBy(e => e.Name, StringComparer.Ordinal)
          .SelectMany(e => e.Extents).ToArray();
        Assert.That(extents.All(x => x.Device == 0), Is.True, "with one copy, the data the first member holds is all on it");
        Assert.That(extents.Select(x => x.FirstSector), Is.Ordered, "file after file, run after run");
      }
    } finally {
      foreach (var member in members) member.Dispose();
    }
    BcacheFsKernel.AssertFsckClean(paths);
  }

  [Test, Category("ErrorHandling")]
  public void GivenTwoMembers_WhenDefragmentedWithAnUnsupportedMode_ThenItIsRefusedAndBothAreUnchanged() {
    BcacheFsOracleGate.RequireBcachefsTools();
    var paths = this.Format("", "64M", "64M");
    var before = paths.Select(File.ReadAllBytes).ToArray();
    var members = paths.Select(p => (Stream)new FileStream(p, FileMode.Open, FileAccess.ReadWrite)).ToArray();
    try {
      Assert.Throws<NotSupportedException>(() =>
        new BcacheFsFormatDescriptor().DefragmentVolume(members, new DefragOptions { Mode = DefragMode.FillHolesLazy }));
      Assert.Throws<NotSupportedException>(() =>
        new BcacheFsFormatDescriptor().DefragmentVolume(members, new DefragOptions { MetadataZonePlacement = MetadataZone.Back }));
    } finally {
      foreach (var member in members) member.Dispose();
    }
    for (var i = 0; i < paths.Length; ++i)
      Assert.That(File.ReadAllBytes(paths[i]).SequenceEqual(before[i]), Is.True, $"member {i} unchanged");
  }

  [Test, Category("ExternalFsInterop")]
  public void GivenTwoCopies_WhenOneMemberIsLeftOut_ThenTheOtherStillReadsEveryFile() {
    BcacheFsOracleGate.RequireBcachefsTools();
    var paths = this.Format("--replicas=2", "64M", "64M");
    Dictionary<string, string> expected;
    var members = paths.Select(p => (Stream)new FileStream(p, FileMode.Open, FileAccess.ReadWrite)).ToArray();
    try {
      expected = Edit(members, fileCount: 5);
    } finally {
      foreach (var member in members) member.Dispose();
    }
    foreach (var path in paths) {
      using var alone = File.OpenRead(path);
      Assert.That(Read([alone]), Is.EquivalentTo(expected), $"every file has a copy on {Path.GetFileName(path)}");
    }
  }

  [Test, Category("ErrorHandling")]
  public void GivenTwoMembers_WhenOnlyOneIsEdited_ThenItIsRefusedAndBothAreUnchanged() {
    BcacheFsOracleGate.RequireBcachefsTools();
    var paths = this.Format("", "64M", "64M");
    var before = paths.Select(File.ReadAllBytes).ToArray();
    using (var image = new FileStream(paths[0], FileMode.Open, FileAccess.ReadWrite))
      Assert.That(() => new BcacheFsFormatDescriptor().Add(image, [ArchiveInputInfo.InMemory("x.txt", [1])]),
        Throws.InstanceOf<NotSupportedException>().With.Message.Contains("member"));
    for (var i = 0; i < paths.Length; ++i)
      Assert.That(File.ReadAllBytes(paths[i]).SequenceEqual(before[i]), Is.True, $"member {i} unchanged");
  }
}
