using System.Buffers.Binary;
using FileSystem.Refs;

namespace Compression.Tests.Refs;

/// <summary>
/// The ReFS formatter: what it writes is read back by this repository's reader and editors, and,
/// where CWB_REFS_EMPTY_DIR names a directory holding the empty volume Windows Server 2022 formatted
/// (the capture workflow's empty variant, <c>refs-edits-windows-2022/empty/base.refs</c>), every
/// table row and page header matches that volume apart from the volume's own identifiers.
/// </summary>
[TestFixture]
public sealed class RefsVolumeFormatterTests {
  /// <summary>The partition the capture workflow formats: 1 GiB VHD less the 1 MiB partition offset.</summary>
  private const long PartitionBytes = 1072627712;

  private static MemoryStream Fresh(string label = "CWB", int seed = 7) {
    var stream = new MemoryStream();
    RefsVolumeFormatter.Format(stream, new RefsFormatOptions(PartitionBytes, label, new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc), seed));
    stream.Position = 0;
    return stream;
  }

  [Test]
  public void GivenAFreshVolume_WhenItIsOpened_ThenItsHeaderBootstrapAndChecksumsHold() {
    using var stream = Fresh();
    Assert.That(stream.Length, Is.EqualTo(PartitionBytes));
    var header = RefsVolumeHeader.TryParse(stream.GetBuffer().AsSpan(0, 512));
    Assert.Multiple(() => {
      Assert.That(header.Valid, Is.True);
      Assert.That(header.FsrsChecksumValid, Is.True, "boot sector checksum");
      Assert.That(header.MajorVersion, Is.EqualTo(3));
      Assert.That(header.MinorVersion, Is.EqualTo(7));
      Assert.That(header.TotalClusters, Is.EqualTo((ulong)(RefsVolumeFormatter.Geometry / 4096)));
    });
    var backup = RefsVolumeHeader.TryParse(stream.GetBuffer().AsSpan((int)(RefsVolumeFormatter.Geometry - 512), 512));
    Assert.That(backup.FsrsChecksumValid, Is.True, "the boot sector copy at the volume's end");
    var metadata = RefsMetadataReader.Open(stream);
    Assert.That(metadata.Roots.Count, Is.EqualTo(13));
    Assert.That(new RefsMetadataGraph(stream, metadata).FindStaleReferences(), Is.Empty, "every page-reference checksum");
  }

  [Test]
  public void GivenAFreshVolume_WhenListed_ThenItHoldsNoFiles() {
    using var stream = Fresh();
    var entries = new RefsFormatDescriptor().List(stream, null);
    Assert.That(entries.Where(static e => !e.IsDirectory && !e.Name.StartsWith("System Volume Information", StringComparison.Ordinal)
      && !e.Name.Contains("metadata.ini", StringComparison.Ordinal) && !e.Name.StartsWith("$", StringComparison.Ordinal)), Is.Empty);
  }

  [Test]
  public void GivenAFreshVolume_WhenADirectoryIsCreated_ThenItIsListed() {
    using var stream = Fresh();
    new RefsFormatDescriptor().Add(stream, [new Compression.Registry.ArchiveInputInfo("new-folder", "new-folder", true)]);
    stream.Position = 0;
    Assert.That(new RefsFormatDescriptor().List(stream, null).Any(static e => e.IsDirectory && e.Name == "new-folder"), Is.True);
    stream.Position = 0;
    var metadata = RefsMetadataReader.Open(stream);
    Assert.That(new RefsMetadataGraph(stream, metadata).FindStaleReferences(), Is.Empty);
    // The only template a fresh volume offers is System Volume Information (hidden, system, its
    // own descriptor); the new directory is a plain one with the root's descriptor.
    var rootRows = metadata.WalkTree(RefsInPlaceRename.ResolveDirectoryRoot(metadata, ""), true).ToList();
    var nameRow = rootRows.Single(static r => r.Key.Length > 4 && System.Text.Encoding.Unicode.GetString(r.Key, 4, r.Key.Length - 4) == "new-folder");
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(nameRow.Value.AsSpan(0x40)), Is.EqualTo(0x10000000u), "attributes");
    var rootHeader = rootRows.Single(static r => r.Key.Length == 4);
    var folderHeader = metadata.WalkTree(RefsInPlaceRename.ResolveDirectoryRoot(metadata, "new-folder"), true).Single(static r => r.Key.Length == 4);
    Assert.That(folderHeader.Value.AsSpan(0x50, 8).ToArray(), Is.EqualTo(rootHeader.Value.AsSpan(0x50, 8).ToArray()), "security descriptor");
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(folderHeader.Value.AsSpan(0x48)), Is.EqualTo(0x10000000u), "header attributes");
  }

  [Test]
  public void GivenAFreshVolume_WhenDirectoriesAreCreated_ThenTheObjectTableRecordsTheHighestNumber() {
    using var stream = Fresh();
    var descriptor = new RefsFormatDescriptor();
    Assert.That(HighestObject(stream), Is.EqualTo(0x701UL), "fresh: System Volume Information");
    descriptor.Add(stream, [new Compression.Registry.ArchiveInputInfo("one", "one", true)]);
    descriptor.Add(stream, [new Compression.Registry.ArchiveInputInfo("two", "two", true)]);
    stream.Position = 0;
    var metadata = RefsMetadataReader.Open(stream);
    var objects = metadata.WalkRoot(0).Select(static r => BinaryPrimitives.ReadUInt64LittleEndian(r.Key.AsSpan(8))).ToList();
    Assert.Multiple(() => {
      Assert.That(objects, Does.Contain(0x702UL).And.Contain(0x703UL));
      Assert.That(HighestObject(stream), Is.EqualTo(0x703UL));
      Assert.That(new RefsMetadataGraph(stream, metadata).FindStaleReferences(), Is.Empty);
    });

    static ulong HighestObject(MemoryStream volume) {
      volume.Position = 0;
      var metadata = RefsMetadataReader.Open(volume);
      var page = new RefsMetadataGraph(volume, metadata).ReadPage(metadata.WalkRoot(0).First().PhysicalPageLcn);
      return BinaryPrimitives.ReadUInt64LittleEndian(page.AsSpan(0x88));
    }
  }

  [Test]
  public void GivenAFreshVolume_WhenFilesAreAdded_ThenTheyAreReadBack() {
    using var stream = Fresh();
    var descriptor = new RefsFormatDescriptor();
    var files = new Dictionary<string, byte[]> {
      ["one.txt"] = "hello ReFS"u8.ToArray(),
      ["big.bin"] = Enumerable.Range(0, 70000).Select(static i => (byte)(i * 7)).ToArray(),
      ["sub/inner.bin"] = Enumerable.Range(0, 5000).Select(static i => (byte)(i ^ 0x5A)).ToArray(),
    };
    descriptor.Add(stream, [new Compression.Registry.ArchiveInputInfo("sub", "sub", true)]);
    foreach (var (name, data) in files) descriptor.Add(stream, [Compression.Registry.ArchiveInputInfo.InMemory(name, data)]);
    stream.Position = 0;
    var metadata = RefsMetadataReader.Open(stream);
    Assert.That(new RefsMetadataGraph(stream, metadata).FindStaleReferences(), Is.Empty);
    var outDir = Path.Combine(Path.GetTempPath(), "cwb_refs_fresh_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      stream.Position = 0;
      descriptor.Extract(stream, outDir, null, null);
      Assert.Multiple(() => {
        foreach (var (name, data) in files)
          Assert.That(File.ReadAllBytes(Path.Combine(outDir, name.Replace('/', Path.DirectorySeparatorChar))), Is.EqualTo(data), name);
      });
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* best effort */ }
    }
  }

  [Test]
  public void GivenFilesInNestedDirectories_WhenCreated_ThenEveryFileAndDirectoryIsReadBack() {
    var files = new Dictionary<string, byte[]> {
      ["top.txt"] = "top"u8.ToArray(),
      ["a/b/c/deep.bin"] = Enumerable.Range(0, 9000).Select(static i => (byte)(i * 3)).ToArray(),
      ["a/side.bin"] = Enumerable.Range(0, 4096).Select(static i => (byte)i).ToArray(),
    };
    using var stream = new MemoryStream();
    var descriptor = new RefsFormatDescriptor();
    descriptor.Create(stream,
      [.. files.Select(static f => Compression.Registry.ArchiveInputInfo.InMemory(f.Key, f.Value)),
       new Compression.Registry.ArchiveInputInfo("empty", "empty", true)],
      new Compression.Registry.FormatCreateOptions());
    stream.Position = 0;
    var entries = new RefsNamespaceReader(RefsMetadataReader.Open(stream)).ReadAll();
    Assert.That(entries.Where(static e => e.IsDirectory).Select(static e => e.Path),
      Is.SupersetOf(new[] { "a", "a/b", "a/b/c", "empty" }));
    var outDir = Path.Combine(Path.GetTempPath(), "cwb_refs_create_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      stream.Position = 0;
      descriptor.Extract(stream, outDir, null, null);
      Assert.Multiple(() => {
        foreach (var (name, data) in files)
          Assert.That(File.ReadAllBytes(Path.Combine(outDir, name.Replace('/', Path.DirectorySeparatorChar))), Is.EqualTo(data), name);
      });
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* best effort */ }
    }
  }

  [Test]
  public void GivenAnEmptyFile_WhenCreated_ThenItIsRefused() {
    using var stream = new MemoryStream();
    Assert.Throws<NotSupportedException>(() => new RefsFormatDescriptor().Create(stream,
      [Compression.Registry.ArchiveInputInfo.InMemory("zero.bin", Array.Empty<byte>())],
      new Compression.Registry.FormatCreateOptions()));
    Assert.That(stream.Length, Is.Zero, "nothing is written");
  }

  [Test]
  public void GivenTwoInputsWithOneName_WhenCreated_ThenItIsRefused() {
    using var stream = new MemoryStream();
    Assert.Throws<ArgumentException>(() => new RefsFormatDescriptor().Create(stream,
      [Compression.Registry.ArchiveInputInfo.InMemory("x.bin", [1]), Compression.Registry.ArchiveInputInfo.InMemory("X.BIN", [2])],
      new Compression.Registry.FormatCreateOptions()));
  }

  [Test]
  public void GivenTheSameSeed_WhenFormattedTwice_ThenTheVolumesAreIdentical() {
    using var a = Fresh(seed: 3);
    using var b = Fresh(seed: 3);
    Assert.That(a.ToArray().AsSpan().SequenceEqual(b.ToArray()), Is.True);
  }

  [Test]
  public void GivenAnotherSeed_WhenFormatted_ThenTheIdentifiersDiffer() {
    using var a = Fresh(seed: 3);
    using var b = Fresh(seed: 4);
    Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(a.GetBuffer().AsSpan(0x38)), Is.Not.EqualTo(BinaryPrimitives.ReadUInt64LittleEndian(b.GetBuffer().AsSpan(0x38))), "serial numbers");
  }

  [TestCase(RefsVolumeFormatter.Geometry - 4096)]
  [TestCase(RefsVolumeFormatter.Geometry + 64L * 1024 * 1024)]
  [TestCase(2L * 1024 * 1024 * 1024)]
  public void GivenAnUnsupportedSize_WhenFormatted_ThenItIsRefusedWithoutWriting(long bytes) {
    using var stream = new MemoryStream();
    Assert.Throws<NotSupportedException>(() => RefsVolumeFormatter.Format(stream, new RefsFormatOptions(bytes)));
    Assert.That(stream.Length, Is.Zero);
  }

  [Test]
  public void GivenALabelLongerThan32Characters_WhenFormatted_ThenItIsRejected() {
    using var stream = new MemoryStream();
    Assert.Throws<ArgumentException>(() => RefsVolumeFormatter.Format(stream, new RefsFormatOptions(PartitionBytes, new string('x', 33))));
    Assert.That(stream.Length, Is.Zero);
  }

  [Test, Category("ExternalFsInterop")]
  public void GivenTheEmptyVolumeWindowsFormatted_WhenComparedWithOurs_ThenEveryRowAndPageHeaderMatches() {
    var dir = Environment.GetEnvironmentVariable("CWB_REFS_EMPTY_DIR");
    var path = dir is null ? null : Path.Combine(dir, "refs-edits-windows-2022", "empty", "base.refs");
    if (path is null || !File.Exists(path)) Assert.Ignore("CWB_REFS_EMPTY_DIR does not hold the empty volume Windows Server 2022 formatted.");
    using var windows = File.OpenRead(path);
    var mw = RefsMetadataReader.Open(windows);
    // Our volume with Windows' label and the time Windows last mounted it (every row's timestamp).
    var mounted = DateTime.FromFileTimeUtc(BinaryPrimitives.ReadInt64LittleEndian(
      mw.WalkTree(RefsInPlaceRename.ResolveDirectoryRoot(mw, ""), true).First(static r => r.Key.Length == 4).Value.AsSpan(0x28)));
    using var ours = new MemoryStream();
    RefsVolumeFormatter.Format(ours, new RefsFormatOptions(new FileInfo(path).Length, "CWB", mounted, 1));
    var mo = RefsMetadataReader.Open(ours);
    var differences = new List<string>();
    void Compare(string label, RefsMetadataReader m1, RefsPageReference r1, RefsMetadataReader m2, RefsPageReference r2, bool virtualAddresses, Func<string, int, bool> ignore) {
      var a = m1.WalkTree(r1, virtualAddresses).ToDictionary(static r => Convert.ToHexString(r.Key), static r => r.Value);
      var b = m2.WalkTree(r2, virtualAddresses).ToDictionary(static r => Convert.ToHexString(r.Key), static r => r.Value);
      foreach (var key in a.Keys.Union(b.Keys)) {
        if (!a.TryGetValue(key, out var va) || !b.TryGetValue(key, out var vb)) { differences.Add($"{label} {key}: present on one side only"); continue; }
        if (va.Length != vb.Length) { differences.Add($"{label} {key}: {va.Length} vs {vb.Length} bytes"); continue; }
        for (var i = 0; i < va.Length; ++i)
          if (va[i] != vb[i] && !ignore(key, i)) { differences.Add($"{label} {key}: byte 0x{i:X}"); break; }
      }
    }
    // Object-table rows: their checksums (0x48..0x50) follow the pages' contents, which carry the
    // volume's own identifiers; past the declared trailer Windows leaves whatever its buffer held.
    bool ObjectRow(string key, int i) => i is >= 0x48 and < 0x50 || i >= 0xD0 || (i >= 0xC8 && !key.EndsWith("0600000000000000", StringComparison.Ordinal) && !key.EndsWith("0107000000000000", StringComparison.Ordinal) && !key.EndsWith("2005000000000000", StringComparison.Ordinal));
    for (var root = 0; root < 13; ++root)
      Compare($"root {root}", mw, mw.Roots[root], mo, mo.Roots[root], root is not (7 or 8 or 12),
        root is 0 or 5 ? ObjectRow : static (_, _) => false);
    var wo = mw.WalkRoot(0).ToDictionary(static r => BinaryPrimitives.ReadUInt64LittleEndian(r.Key.AsSpan(8)));
    var oo = mo.WalkRoot(0).ToDictionary(static r => BinaryPrimitives.ReadUInt64LittleEndian(r.Key.AsSpan(8)));
    foreach (var (oid, row) in wo) {
      if (!oo.TryGetValue(oid, out var mine)) { differences.Add($"object 0x{oid:X} missing"); continue; }
      // The volume information's format time (0x90) is the time Windows formatted, not the one it last mounted.
      Compare($"object 0x{oid:X}", mw, RefsPageReference.Parse(row.Value.AsSpan(0x20)), mo, RefsPageReference.Parse(mine.Value.AsSpan(0x20)), true,
        oid is 0x500 or 0x501 ? static (key, i) => key == "2005000000000000" && i is >= 0x90 and < 0x98 : static (_, _) => false);
    }
    Assert.That(differences, Is.Empty);
  }
}
