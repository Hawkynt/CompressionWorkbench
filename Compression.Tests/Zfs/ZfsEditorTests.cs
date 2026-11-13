using System.Text;
using Compression.Registry;
using FileSystem.Zfs;

namespace Compression.Tests.Zfs;

/// <summary>
/// Editing pools in place: Add and Remove on pools this repository wrote and on the version 28
/// pool OpenZFS made (<c>ReferenceVectors/cwbv28</c>: a system-attribute root dataset and a
/// ZPL version 3 child with lzjb and gzip blocks, a hard link, a sparse file). The reader checks
/// every edit here; the external tests have OpenZFS check them — <c>zdb -bcc</c> must find no
/// leak, <c>zdb -r</c> must copy files out, and the kernel must import, scrub and mount the pool
/// and serve exactly the expected files.
/// </summary>
[TestFixture]
public sealed class ZfsEditorTests {
  private static readonly ZfsFormatDescriptor Descriptor = new();

  private static void Add(MemoryStream image, params (string Path, byte[]? Data)[] files) =>
    Descriptor.Add(image, files.Select(f => f.Data == null
      ? new ArchiveInputInfo(f.Path, f.Path, IsDirectory: true)
      : ArchiveInputInfo.InMemory(f.Path, f.Data)).ToList());

  private static void Remove(MemoryStream image, params string[] names) => Descriptor.Remove(image, names);

  private static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);

  /// <summary>The reader lists exactly <paramref name="expected"/> as regular files, with these contents.</summary>
  private static void AssertFiles(MemoryStream image, IReadOnlyDictionary<string, string> expected, ulong? txg = null) {
    image.Position = 0;
    using var reader = new ZfsReader(image);
    Assert.That(reader.Warnings, Is.Empty);
    if (txg is { } t) Assert.That(reader.Txg, Is.EqualTo(t));
    var files = reader.Entries.Where(e => !e.IsDirectory && e.LinkTarget == null).ToDictionary(e => e.Name, StringComparer.Ordinal);
    Assert.That(files.Keys, Is.EquivalentTo(expected.Keys));
    Assert.Multiple(() => {
      foreach (var (name, sha) in expected)
        if (files.TryGetValue(name, out var entry))
          Assert.That(ZfsOracle.Sha(reader.Extract(entry)), Is.EqualTo(sha), name);
    });
  }

  private static ulong TxgOf(MemoryStream image) {
    image.Position = 0;
    using var reader = new ZfsReader(image);
    return reader.Txg;
  }

  // ── pools this repository wrote ───────────────────────────────────────────────────────

  /// <summary>Six edits in a row on a written pool; returns the image, the files it must hold and the image before.</summary>
  private static (MemoryStream Image, Dictionary<string, string> Expected, byte[] Before) EditOwnPool() {
    var files = ZfsWriterTests.Corpus(large: false);
    var image = new MemoryStream();
    var before = ZfsWriterTests.Write(files);
    image.Write(before);
    var expected = files.ToDictionary(f => f.Key, f => ZfsOracle.Sha(f.Value), StringComparer.Ordinal);
    var txg = TxgOf(image);

    // 1: new nested directories, a file over several records, a replacement, enough new
    // objects to need more dnode blocks, and a file in a directory that was empty
    var big = ZfsWriterTests.Pattern(1_000_000, 7);
    var adds = new List<(string, byte[]?)> {
      ("added/new.txt", Text("new\n")), ("added/deeper/big.bin", big), ("hello.txt", Text("replaced\n")),
      ("emptydir/inside.txt", Text("inside\n")), ("made-empty", null),
    };
    for (var i = 0; i < 40; ++i) adds.Add(($"many/f{i:D2}.txt", Text($"file {i}\n")));
    Add(image, adds.ToArray());
    foreach (var (path, data) in adds)
      if (data != null) expected[path] = ZfsOracle.Sha(data);
    AssertFiles(image, expected, txg + 1);

    // 2: a directory tree, a multi-record file and one name of a fat-free crowd go
    var crowd = files.Keys.First(k => k.StartsWith("crowd/", StringComparison.Ordinal));
    Remove(image, "dir", "multi.bin", crowd);
    foreach (var gone in new[] { "dir/sub/deep.txt", "multi.bin", crowd }) expected.Remove(gone);
    AssertFiles(image, expected, txg + 2);

    // 3: an empty file arrives, a fresh file leaves again
    Add(image, ("empty-after.txt", []));
    Remove(image, "added/new.txt");
    expected["empty-after.txt"] = ZfsOracle.Sha([]);
    expected.Remove("added/new.txt");
    AssertFiles(image, expected, txg + 4);

    // 4: a file larger than a metaslab, so space maps are created for metaslabs that had none
    // and the metaslab array is rewritten; 5: a removal that frees in an older metaslab
    var huge = ZfsWriterTests.Pattern(24 * 1024 * 1024 + 777, 13);
    Add(image, ("huge.bin", huge));
    expected["huge.bin"] = ZfsOracle.Sha(huge);
    Remove(image, "block.bin");
    expected.Remove("block.bin");
    AssertFiles(image, expected, txg + 6);
    return (image, expected, before);
  }

  [Test]
  public void Given_AWrittenPool_When_FilesAreAddedReplacedAndRemoved_Then_TheReaderSeesEveryEdit() {
    var (image, _, _) = EditOwnPool();
    image.Position = 0;
    using var reader = new ZfsReader(image);
    var dirs = reader.Entries.Where(e => e.IsDirectory).Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
    Assert.That(dirs, Does.Contain("made-empty").And.Contain("added/deeper").And.Not.Contain("dir").And.Not.Contain("dir/sub"));
  }

  [Test]
  public void Given_AWrittenPool_When_Edited_Then_ItsAccountingMovesWithItsBlocks() {
    var (image, _, before) = EditOwnPool();
    var start = ZfsAccountingProbe.Probe(before);
    var end = ZfsAccountingProbe.Probe(image.ToArray());
    Assert.Multiple(() => {
      foreach (var (key, ledger) in start.Concat(end))
        Assert.That(ledger.Recorded, Is.EqualTo(ledger.Counted), $"{key}: the pool records what its trees hold");
    });
    ZfsAccountingProbe.AssertMovedTogether(start, end);
  }

  // ── the pool OpenZFS made ─────────────────────────────────────────────────────────────

  private static (MemoryStream Image, Dictionary<string, string> Expected) EditReferencePool() {
    var image = ZfsReferencePools.Copy("cwbv28");
    var expected = ZfsReferencePools.Manifest("cwbv28").Where(m => m.Kind == 'F')
      .ToDictionary(m => m.Path, m => m.Sha256!, StringComparer.Ordinal);
    var txg = TxgOf(image);

    // a name of a hard link, a compressed file, a sparse file in the ZPL 3 child and a whole tree there
    Remove(image, "text.txt", "rand.bin", "zpl3/sparse.bin", "zpl3/dir", "zpl3/hardlink.txt");
    foreach (var gone in new[] { "text.txt", "rand.bin", "zpl3/sparse.bin", "zpl3/dir/sub/deep.txt", "zpl3/hardlink.txt" })
      expected.Remove(gone);
    AssertFiles(image, expected, txg + 1);

    var added = ZfsWriterTests.Pattern(200_000, 11);
    var adds = new (string, byte[]?)[] {
      ("new-root.txt", Text("system attributes\n")), ("dir/sub/added.bin", added), ("tiny.txt", Text("replaced tiny\n")),
      ("zpl3/new-zpl3.txt", Text("znode\n")), ("zpl3/fresh/dir/f.txt", Text("fresh\n")),
    };
    Add(image, adds);
    foreach (var (path, data) in adds) expected[path] = ZfsOracle.Sha(data!);
    AssertFiles(image, expected, txg + 2);
    return (image, expected);
  }

  [Test]
  public void Given_ThePoolOpenZfsMade_When_Edited_Then_ItsAccountingMovesWithItsBlocks() {
    var start = ZfsAccountingProbe.Probe(ZfsReferencePools.ImageBytes("cwbv28"));
    Assert.Multiple(() => {
      foreach (var (key, ledger) in start.Where(l => l.Key != "$MOS")) {
        Assert.That(ledger.Recorded, Is.EqualTo(ledger.Counted), $"{key}: OpenZFS records what the trees hold");
        Assert.That(ledger.UsersRecorded, Is.EquivalentTo(ledger.UsersCounted), $"{key}: OpenZFS user accounting");
      }
    });
    var (image, _) = EditReferencePool();
    ZfsAccountingProbe.AssertMovedTogether(start, ZfsAccountingProbe.Probe(image.ToArray()));
  }

  [Test]
  public void Given_ThePoolOpenZfsMade_When_FilesAreAddedAndRemoved_Then_TheReaderSeesEveryEdit() {
    var (image, _) = EditReferencePool();
    image.Position = 0;
    using var reader = new ZfsReader(image);
    var links = reader.Entries.Where(e => e.LinkTarget != null).Select(e => e.Name).ToList();
    Assert.That(links, Is.EquivalentTo(new[] { "link", "longlink", "zpl3/link", "zpl3/longlink" }), "symbolic links survive");
  }

  // ── refusals leave the image untouched ───────────────────────────────────────────────

  private static void AssertRefusedUntouched<TException>(MemoryStream image, Action<MemoryStream> edit) where TException : Exception {
    var before = image.ToArray();
    Assert.Throws<TException>(() => edit(image));
    Assert.That(image.ToArray().SequenceEqual(before), Is.True, "the image changed");
  }

  [Test]
  public void Given_AFileASnapshotStillHolds_When_Removed_Then_ItIsRefusedUntouched() =>
    AssertRefusedUntouched<NotSupportedException>(ZfsReferencePools.Copy("cwbmain"), i => Remove(i, "c-lz4/rand.bin"));

  [Test]
  public void Given_GangBlocks_When_TheirFileIsRemoved_Then_ItIsRefusedUntouched() =>
    AssertRefusedUntouched<NotSupportedException>(ZfsReferencePools.Copy("cwbgang"), i => Remove(i, "big.txt"));

  [Test]
  public void Given_AMirror_When_Edited_Then_ItIsRefusedUntouched() =>
    AssertRefusedUntouched<NotSupportedException>(ZfsReferencePools.Copy("cwbmirror-0"), i => Add(i, ("x.txt", [1])));

  [Test]
  public void Given_AnAbsentName_When_Removed_Then_TheOthersGoAndItIsIgnored() {
    var image = ZfsReferencePools.Copy("cwbv28");
    var expected = ZfsReferencePools.Manifest("cwbv28").Where(m => m.Kind == 'F' && m.Path != "tiny.txt")
      .ToDictionary(m => m.Path, m => m.Sha256!, StringComparer.Ordinal);
    Remove(image, "tiny.txt", "no/such.txt", "tiny.txt/below-a-file", "absent.txt");
    AssertFiles(image, expected);
  }

  [Test]
  public void Given_OnlyAbsentNames_When_Removed_Then_TheImageIsUntouched() {
    var image = ZfsReferencePools.Copy("cwbv28");
    var before = image.ToArray();
    Remove(image, "absent.txt");
    Assert.That(image.ToArray().SequenceEqual(before), Is.True, "nothing to do writes nothing");
    AssertFiles(image, ZfsReferencePools.Manifest("cwbv28").Where(m => m.Kind == 'F').ToDictionary(m => m.Path, m => m.Sha256!, StringComparer.Ordinal));
  }

  [Test]
  public void Given_AFileWhereADirectoryIs_When_Added_Then_ItIsRefusedUntouched() =>
    AssertRefusedUntouched<ArgumentException>(ZfsReferencePools.Copy("cwbv28"), i => Add(i, ("dir", [1])));

  [Test]
  public void Given_ADirectoryBelowAFile_When_Added_Then_ItIsRefusedUntouched() =>
    AssertRefusedUntouched<ArgumentException>(ZfsReferencePools.Copy("cwbv28"), i => Add(i, ("tiny.txt/x", [1])));

  [Test]
  public void Given_ADatasetRoot_When_Removed_Then_ItIsRefusedUntouched() =>
    AssertRefusedUntouched<NotSupportedException>(ZfsReferencePools.Copy("cwbv28"), i => Remove(i, "zpl3"));

  [Test]
  public void Given_ASnapshotPath_When_Added_Then_ItIsRefusedUntouched() =>
    AssertRefusedUntouched<NotSupportedException>(ZfsReferencePools.Copy("cwbv28"), i => Add(i, (".zfs/snapshot/x", [1])));

  [Test]
  public void Given_ADescriptor_Then_ItClaimsModify() =>
    Assert.That(Descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);

  // ── OpenZFS ───────────────────────────────────────────────────────────────────────────

  [Test, Category("Slow"), Category("ExternalTool")]
  public void Given_AnEditedWrittenPool_When_ZdbTraversesIt_Then_NothingLeaksAndFilesCopyOut() {
    ZfsOracle.RequireZdb();
    var (image, expected, _) = EditOwnPool();
    var (dir, wsl) = ZfsOracle.Stage(image.ToArray(), "cwbw");
    try {
      ZfsOracle.AssertNoLeaks(wsl, "cwbw");
      foreach (var name in new[] { "hello.txt", "added/deeper/big.bin", "many/f39.txt", "emptydir/inside.txt", "huge.bin" })
        ZfsOracle.AssertCopiesOut(dir, wsl, "cwbw", name, expected[name]);
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }

  [Test, Category("Slow"), Category("ExternalTool")]
  public void Given_AnEditedOpenZfsPool_When_ZdbTraversesIt_Then_NothingLeaksAndFilesCopyOut() {
    ZfsOracle.RequireZdb();
    var (image, expected) = EditReferencePool();
    var (dir, wsl) = ZfsOracle.Stage(image.ToArray(), "cwbv28");
    try {
      ZfsOracle.AssertNoLeaks(wsl, "cwbv28");
      foreach (var name in new[] { "new-root.txt", "dir/sub/added.bin", "tiny.txt", "hardlink.txt" })
        ZfsOracle.AssertCopiesOut(dir, wsl, "cwbv28", name, expected[name]);
      foreach (var name in new[] { "new-zpl3.txt", "fresh/dir/f.txt", "text.txt" })
        ZfsOracle.AssertCopiesOut(dir, wsl, "cwbv28/zpl3", name, expected["zpl3/" + name]);
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }

  [Test, Category("Slow"), Category("ExternalTool")]
  public void Given_AnEditedWrittenPool_When_TheKernelImportsAndScrubsIt_Then_ItServesExactlyTheEdit() {
    ZfsOracle.RequireAppliance();
    var (image, expected, _) = EditOwnPool();
    var (dir, wsl) = ZfsOracle.Stage(image.ToArray(), "cwbw");
    try {
      Assert.That(ZfsOracle.KernelManifest(dir, wsl, "cwbw"), Is.EquivalentTo(expected));
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }

  [Test, Category("Slow"), Category("ExternalTool")]
  public void Given_AnEditedOpenZfsPool_When_TheKernelImportsAndScrubsIt_Then_ItServesExactlyTheEdit() {
    ZfsOracle.RequireAppliance();
    var (image, expected) = EditReferencePool();
    var (dir, wsl) = ZfsOracle.Stage(image.ToArray(), "cwbv28");
    try {
      Assert.That(ZfsOracle.KernelManifest(dir, wsl, "cwbv28"), Is.EquivalentTo(expected));
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }
}
