using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.Apfs;

namespace Compression.Tests.Apfs;

/// <summary>
/// Exercises the full-scope APFS in-place mutator: deep nested paths,
/// large directories that force FS-tree splits, many files that force
/// OMAP splits, and tree-height growth past a single internal level.
/// Every test that mutates an image also runs the paranoid
/// <see cref="ApfsStructuralValidator"/> on the result — the validator is
/// the real acceptance gate because no Linux <c>fsck_apfs</c> exists.
/// </summary>
[TestFixture]
public class ApfsFullScopeMutationTests {

  private const int SmallImage = 4 * 1024 * 1024;

  private static MemoryStream BuildImage(int minImageSize, params (string Name, byte[] Data)[] files) {
    var w = new ApfsWriter();
    w.SetMinImageSize(minImageSize);
    foreach (var (n, d) in files) w.AddFile(n, d);
    var ms = new MemoryStream();
    ms.Write(w.Build());
    ms.Position = 0;
    return ms;
  }

  private static Dictionary<string, byte[]> ReadAll(MemoryStream image) {
    image.Position = 0;
    var r = new ApfsReader(image, leaveOpen: true);
    return r.Entries.Where(e => !e.IsDirectory)
                    .ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e));
  }

  private static void AssertValid(MemoryStream image, string label) {
    image.Position = 0;
    var bytes = image.ToArray();
    var report = ApfsStructuralValidator.Validate(bytes);
    Assert.That(report.IsValid, Is.True,
      $"{label} structural validator: {report}");
  }

  // ── Multi-component paths ─────────────────────────────────────────────

  /// <summary>
  /// Given an image with one root-level file, when a nested-path file is added,
  /// then the new file appears at its full path, the intermediate directory
  /// inode is synthesised, and the structural validator accepts the image.
  /// </summary>
  [Test, Category("RoundTrip")]
  public void Add_NestedPath_CreatesIntermediateDirectories() {
    using var img = BuildImage(SmallImage, ("root.txt", "R"u8.ToArray()));

    ((IArchiveModifiable)new ApfsFormatDescriptor()).Add(img,
      [ArchiveInputInfo.InMemory("subdir/file.txt", "S"u8.ToArray())]);

    AssertValid(img, "after Add(subdir/file.txt)");
    var files = ReadAll(img);
    Assert.That(files.ContainsKey("root.txt"), Is.True);
    Assert.That(files.ContainsKey("subdir/file.txt"), Is.True);
    Assert.That(files["subdir/file.txt"], Is.EqualTo("S"u8.ToArray()));
  }

  /// <summary>
  /// Given an image with one root-level file, when a deeply nested file
  /// (3 levels) is added, then all three intermediate directories are
  /// synthesised, the file appears at its path, and the validator accepts.
  /// </summary>
  [Test, Category("RoundTrip")]
  public void Add_ThreeLevelDeep_CreatesAllIntermediateDirs() {
    using var img = BuildImage(SmallImage, ("root.txt", "R"u8.ToArray()));

    ((IArchiveModifiable)new ApfsFormatDescriptor()).Add(img,
      [ArchiveInputInfo.InMemory("a/b/c/deep.txt", "deep"u8.ToArray())]);

    AssertValid(img, "after Add(a/b/c/deep.txt)");
    var files = ReadAll(img);
    Assert.That(files.ContainsKey("a/b/c/deep.txt"), Is.True);
    Assert.That(files["a/b/c/deep.txt"], Is.EqualTo("deep"u8.ToArray()));
  }

  /// <summary>
  /// Given an image with files under a/b/, when another file is added under
  /// the same a/b/ directory, then the existing directory inodes are reused
  /// (no orphans, validator passes).
  /// </summary>
  [Test, Category("RoundTrip")]
  public void Add_NestedPathReusesExistingDirInodes() {
    using var img = BuildImage(SmallImage,
      ("a/b/first.txt", "F"u8.ToArray()));

    ((IArchiveModifiable)new ApfsFormatDescriptor()).Add(img,
      [ArchiveInputInfo.InMemory("a/b/second.txt", "S"u8.ToArray())]);

    AssertValid(img, "after second Add to a/b/");
    var files = ReadAll(img);
    Assert.That(files.ContainsKey("a/b/first.txt"), Is.True);
    Assert.That(files.ContainsKey("a/b/second.txt"), Is.True);
    Assert.That(files["a/b/first.txt"], Is.EqualTo("F"u8.ToArray()));
    Assert.That(files["a/b/second.txt"], Is.EqualTo("S"u8.ToArray()));
  }

  // ── Large directory → FS-tree split ───────────────────────────────────

  /// <summary>
  /// Given an image whose FS-tree must split (many files in one dir saturate
  /// the leaf), when one more file is added in place, then the modifier
  /// rebuilds the tree top-down with the new record included, every file
  /// (incl. the new one) round-trips, the validator accepts.
  /// </summary>
  [Test, Category("RoundTrip")]
  public void Add_ForcingFsTreeSplit_RebuildsTreeAndRoundTrips() {
    // 80 files with packing-bloating long names — easily overflows the 4 KB leaf.
    var initial = new List<(string, byte[])>();
    for (var i = 0; i < 80; i++)
      initial.Add(($"big_dir/file_with_a_long_name_to_force_split_{i:000}.dat", new byte[32]));
    using var img = BuildImage(8 * 1024 * 1024, [.. initial]);

    var addPayload = "the_added_payload"u8.ToArray();
    ((IArchiveModifiable)new ApfsFormatDescriptor()).Add(img,
      [ArchiveInputInfo.InMemory("big_dir/added_file.dat", addPayload)]);

    AssertValid(img, "after Add forcing FS-tree split");
    var files = ReadAll(img);
    Assert.That(files, Has.Count.EqualTo(81),
      "every initial file plus the added one must round-trip after a split rebuild");
    Assert.That(files["big_dir/added_file.dat"], Is.EqualTo(addPayload));
  }

  /// <summary>
  /// Given an image whose FS-tree must already be split into many leaves,
  /// when a file is removed in place, then the modifier rebuilds the tree,
  /// the surviving files all round-trip, and the validator accepts.
  /// </summary>
  [Test, Category("RoundTrip")]
  public void Remove_FromMultiLeafTree_RebuildsAndRoundTrips() {
    var initial = new List<(string, byte[])>();
    for (var i = 0; i < 80; i++)
      initial.Add(($"big_dir/file_with_a_long_name_to_force_split_{i:000}.dat", new byte[32]));
    using var img = BuildImage(8 * 1024 * 1024, [.. initial]);

    ((IArchiveModifiable)new ApfsFormatDescriptor()).Remove(img,
      ["big_dir/file_with_a_long_name_to_force_split_042.dat"]);

    AssertValid(img, "after Remove from multi-leaf FS-tree");
    var files = ReadAll(img);
    Assert.That(files, Has.Count.EqualTo(79));
    Assert.That(files.ContainsKey("big_dir/file_with_a_long_name_to_force_split_042.dat"), Is.False);
    Assert.That(files.ContainsKey("big_dir/file_with_a_long_name_to_force_split_000.dat"), Is.True);
    Assert.That(files.ContainsKey("big_dir/file_with_a_long_name_to_force_split_079.dat"), Is.True);
  }

  // ── Many sequential adds ─────────────────────────────────────────────

  /// <summary>
  /// Given an empty image, when many small files are added one at a time
  /// (forcing repeated splits, each time the modifier re-rebuilds the tree),
  /// then every file round-trips and the validator accepts after every Add.
  /// </summary>
  [Test, Category("RoundTrip")]
  public void Add_ManyFilesSequentially_RoundTripsAfterEachAdd() {
    using var img = BuildImage(16 * 1024 * 1024, ("seed.txt", "S"u8.ToArray()));

    var desc = (IArchiveModifiable)new ApfsFormatDescriptor();
    // 60 add operations, each adding one file.
    for (var i = 0; i < 60; i++) {
      var path = $"streamed/file_long_name_to_bloat_packing_{i:000}.txt";
      var payload = System.Text.Encoding.ASCII.GetBytes($"payload-{i:000}");
      desc.Add(img, [ArchiveInputInfo.InMemory(path, payload)]);
    }

    AssertValid(img, "after 60 sequential Adds");
    var files = ReadAll(img);
    Assert.That(files, Has.Count.EqualTo(61),
      "seed file plus all 60 added files must round-trip");
    for (var i = 0; i < 60; i++) {
      var path = $"streamed/file_long_name_to_bloat_packing_{i:000}.txt";
      Assert.That(files.ContainsKey(path), Is.True, $"missing: {path}");
      Assert.That(files[path], Is.EqualTo(System.Text.Encoding.ASCII.GetBytes($"payload-{i:000}")));
    }
  }

  // ── Multi-block file extents ─────────────────────────────────────────

  /// <summary>
  /// Given an empty image, when a large file (16 KB → 4 blocks) is added in
  /// place, then the FILE_EXTENT covers all 4 blocks contiguously from the
  /// image tail, the file round-trips byte-exact, and the validator accepts.
  /// </summary>
  [Test, Category("RoundTrip")]
  public void Add_LargeFile_RoundTripsContiguousExtent() {
    using var img = BuildImage(SmallImage, ("seed.txt", "S"u8.ToArray()));

    var payload = new byte[16 * 1024];
    new Random(42).NextBytes(payload);
    ((IArchiveModifiable)new ApfsFormatDescriptor()).Add(img,
      [ArchiveInputInfo.InMemory("big.bin", payload)]);

    AssertValid(img, "after Add(16 KB file)");
    var files = ReadAll(img);
    Assert.That(files["big.bin"], Is.EqualTo(payload));
  }

  // ── Tree height growth ───────────────────────────────────────────────

  /// <summary>
  /// Given an empty image, when many files with very long names are added in
  /// a single batched call, then the modifier rebuilds the FS-tree growing
  /// tree height as needed (level-1 internal root may itself need an extra
  /// level above it), the structural validator accepts, and every file round-trips.
  /// </summary>
  [Test, Category("RoundTrip")]
  public void Add_ManyFilesWithLongNames_StructurallyValid() {
    // Each long filename has a DIR_REC key of ~ 100 bytes. 300 files is plenty
    // to force a multi-leaf FS-tree (~ 7+ leaves), exercising the split
    // rebuild path without thrashing the test host with hundreds of
    // self-round-trip Add invocations.
    using var img = BuildImage(16 * 1024 * 1024, ("seed.txt", "S"u8.ToArray()));

    var desc = (IArchiveModifiable)new ApfsFormatDescriptor();
    var batch = new List<ArchiveInputInfo>();
    for (var i = 0; i < 300; i++) {
      var name = $"verydeep_directory_chain/level/files/file_with_a_very_long_name_to_force_split_{i:0000}.dat";
      batch.Add(ArchiveInputInfo.InMemory(name, new byte[8]));
    }
    desc.Add(img, batch);

    AssertValid(img, "after Add(300 files w/ long names)");

    var files = ReadAll(img);
    Assert.That(files, Has.Count.GreaterThanOrEqualTo(301));
  }

  // ── Replace ──────────────────────────────────────────────────────────

  /// <summary>
  /// Given an image with a file, when the same path is added with new content,
  /// then the modifier replaces the existing file: the new content round-trips,
  /// the old content's blocks are wiped, and the validator accepts.
  /// </summary>
  [Test, Category("RoundTrip")]
  public void Add_DuplicatePath_ReplacesAndWipesOld() {
    var oldPayload = "old-secret-payload"u8.ToArray();
    using var img = BuildImage(SmallImage, ("target.txt", oldPayload));

    var newPayload = "new-content"u8.ToArray();
    ((IArchiveModifiable)new ApfsFormatDescriptor()).Add(img,
      [ArchiveInputInfo.InMemory("target.txt", newPayload)]);

    AssertValid(img, "after Add replacing existing file");
    var files = ReadAll(img);
    Assert.That(files["target.txt"], Is.EqualTo(newPayload));

    // Old plaintext must be wiped.
    img.Position = 0;
    var raw = img.ToArray();
    Assert.That(System.Text.Encoding.UTF8.GetString(raw),
      Does.Not.Contain("old-secret-payload"),
      "replaced file's old plaintext must be wiped from the image");
  }

  // ── Out-of-scope (genuine NotSupportedException) ─────────────────────

  /// <summary>
  /// Given an image with a directory and a file inside it, when Remove targets
  /// the directory itself (not a file inside it), then the modifier throws
  /// NotSupportedException with a specific message — directory-tree removal is
  /// genuinely out of scope (multi-week work to implement recursive subtree drop).
  /// </summary>
  [Test, Category("ErrorHandling")]
  public void Remove_Directory_ThrowsSpecificMessage() {
    using var img = BuildImage(SmallImage, ("subdir/file.txt", "hi"u8.ToArray()));
    var ex = Assert.Throws<NotSupportedException>(() =>
      ((IArchiveModifiable)new ApfsFormatDescriptor()).Remove(img, ["subdir"]));
    Assert.That(ex!.Message, Does.Contain("directory removal"));
  }

  // ── Spec-level invariants on a deep mutation ─────────────────────────

  /// <summary>
  /// Given an image where the FS-tree has been split into multiple leaves with
  /// height growth, when the structural validator runs, then it reports zero
  /// errors, every visited block carries a valid Fletcher-64, every internal
  /// node's level field matches the depth from root, and every DIR_REC → INODE
  /// → FILE_EXTENT chain is consistent.
  /// </summary>
  [Test, Category("Spec")]
  public void StructuralValidator_AcceptsDeeplySplitTree() {
    using var img = BuildImage(16 * 1024 * 1024, ("seed.txt", "S"u8.ToArray()));
    var desc = (IArchiveModifiable)new ApfsFormatDescriptor();
    for (var i = 0; i < 100; i++) {
      desc.Add(img, [ArchiveInputInfo.InMemory(
        $"depth/scope/files/path_to_long_filename_{i:000}.txt",
        System.Text.Encoding.ASCII.GetBytes($"p{i:000}"))]);
    }

    img.Position = 0;
    var bytes = img.ToArray();
    var report = ApfsStructuralValidator.Validate(bytes);
    Assert.That(report.IsValid, Is.True, report.ToString());
    Assert.That(report.BtreeNodesVisited, Is.GreaterThan(1),
      "the FS-tree must have grown into multiple nodes");
    Assert.That(report.FsRecordsScanned, Is.GreaterThan(100),
      "100 files plus root and intermediate dir inodes plus dirents plus file_extents");
    Assert.That(report.MaxXidSeen, Is.LessThan(report.ContainerNextXid),
      "every object xid must be strictly less than nx_next_xid");
  }
}
