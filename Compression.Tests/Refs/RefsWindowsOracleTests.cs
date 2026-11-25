using System.Security.Cryptography;
using System.Text.Json;
using FileSystem.Refs;

namespace Compression.Tests.Refs;

/// <summary>
/// ReFS volumes Windows formatted itself — Server 2022 (ReFS 3.7) and Server 2025 (ReFS 3.14),
/// each with 4 KiB clusters, 64 KiB clusters and integrity streams — captured by
/// <c>tools/foreign-os-oracles/refs-capture.ps1</c> on GitHub-hosted runners. Windows' own
/// Get-FileHash of every file is the oracle (<c>Refs/WindowsOracle/*.json</c>). The volumes are
/// Microsoft-written and not committed: set <c>CWB_REFS_WINDOWS_DIR</c> to the directory holding
/// the workflow's artifacts (<c>windows-2022/default.refs</c>, ...); each image is checked against
/// its recorded SHA-256 before use.
/// </summary>
[TestFixture]
public sealed class RefsWindowsOracleTests {

  private static readonly string[] Volumes = [
    "windows-2022-default", "windows-2022-cluster64k", "windows-2022-integrity",
    "windows-2025-default", "windows-2025-cluster64k", "windows-2025-integrity",
  ];

  private sealed record OracleFile(string Path, long Size, string Sha256);
  private sealed record Oracle(string Variant, string RefsVersion, string ImageFile, long ImageBytes, string ImageSha256, List<OracleFile> Files);

  private static Oracle Load(string volume) {
    using var resource = typeof(RefsWindowsOracleTests).Assembly.GetManifestResourceStream($"RefsWindowsOracle.{volume}.json")
                         ?? throw new InvalidOperationException($"{volume}.json is not embedded.");
    using var doc = JsonDocument.Parse(resource);
    var root = doc.RootElement;
    var image = root.GetProperty("image");
    return new Oracle(
      root.GetProperty("variant").GetString()!, root.GetProperty("refsVersion").GetString()!,
      image.GetProperty("file").GetString()!, image.GetProperty("bytes").GetInt64(), image.GetProperty("sha256").GetString()!,
      root.GetProperty("files").EnumerateArray()
        .Select(static f => new OracleFile(f.GetProperty("path").GetString()!, f.GetProperty("size").GetInt64(), f.GetProperty("sha256").GetString()!))
        .ToList());
  }

  private static string ImagePath(Oracle oracle) {
    var dir = Environment.GetEnvironmentVariable("CWB_REFS_WINDOWS_DIR");
    if (string.IsNullOrWhiteSpace(dir))
      Assert.Ignore("Set CWB_REFS_WINDOWS_DIR to the foreign-os-oracles ReFS artifacts (windows-2022/, windows-2025/).");
    var path = Path.Combine(dir!, oracle.ImageFile.Replace('/', Path.DirectorySeparatorChar));
    if (!File.Exists(path)) Assert.Ignore($"{path} is not there.");
    using (var stream = File.OpenRead(path))
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(stream)), Is.EqualTo(oracle.ImageSha256), "not the captured volume");
    return path;
  }

  [Test, Category("HappyPath")]
  public void GivenTheRecordedOracles_WhenLoaded_ThenEachNamesTheFileSetWindowsHashed([ValueSource(nameof(Volumes))] string volume) {
    var oracle = Load(volume);
    Assert.Multiple(() => {
      Assert.That(oracle.Files, Has.Count.EqualTo(209));
      Assert.That(oracle.Files.Select(static f => f.Path), Does.Contain("dir1/nested/deep.txt"));
      Assert.That(oracle.RefsVersion, Is.EqualTo(volume.StartsWith("windows-2022", StringComparison.Ordinal) ? "3.7" : "3.14"));
    });
  }

  [Test, Category("ExternalFsInterop")]
  public void GivenAVolumeWindowsFormatted_WhenListedAndExtracted_ThenEveryFileHasTheBytesWindowsHashed([ValueSource(nameof(Volumes))] string volume) {
    var oracle = Load(volume);
    var image = ImagePath(oracle);
    var descriptor = new RefsFormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "cwb_refs_win_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(outDir);
    try {
      using var stream = File.OpenRead(image);
      var listed = descriptor.List(stream, null).Where(static e => !e.IsDirectory).ToDictionary(static e => e.Name, static e => e.OriginalSize);
      stream.Position = 0;
      descriptor.Extract(stream, outDir, null, null);
      Assert.Multiple(() => {
        foreach (var file in oracle.Files) {
          Assert.That(listed.TryGetValue(file.Path, out var size), Is.True, $"{file.Path} listed");
          Assert.That(size, Is.EqualTo(file.Size), $"{file.Path} size");
          var extracted = Path.Combine(outDir, file.Path.Replace('/', Path.DirectorySeparatorChar));
          Assert.That(File.Exists(extracted), Is.True, $"{file.Path} extracted");
          Assert.That(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(extracted))), Is.EqualTo(file.Sha256), file.Path);
        }
      });
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* best effort */ }
    }
  }

  [Test, Category("ExternalFsInterop")]
  public void GivenAVolumeWindowsFormatted_WhenAFileIsOverwrittenWithTheSameLength_ThenOnlyItsClustersChange([ValueSource(nameof(Volumes))] string volume) {
    var oracle = Load(volume);
    var source = ImagePath(oracle);
    var copy = Path.Combine(Path.GetTempPath(), "cwb_refs_over_" + Guid.NewGuid().ToString("N")[..8] + ".refs");
    File.Copy(source, copy);
    try {
      var replacement = new byte[65536];
      new Random(30).NextBytes(replacement);
      var descriptor = new RefsFormatDescriptor();
      using (var stream = File.Open(copy, FileMode.Open, FileAccess.ReadWrite)) {
        // On an integrity stream the overwrite also rewrites the per-cluster checksums in the
        // file's directory row.
        descriptor.Add(stream, [Compression.Registry.ArchiveInputInfo.InMemory("replace-me.bin", replacement)]);
      }
      var outDir = Path.Combine(Path.GetTempPath(), "cwb_refs_over_x_" + Guid.NewGuid().ToString("N")[..8]);
      try {
        using (var stream = File.OpenRead(copy)) descriptor.Extract(stream, outDir, null, null);
        Assert.Multiple(() => {
          foreach (var file in oracle.Files) {
            var bytes = File.ReadAllBytes(Path.Combine(outDir, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (file.Path == "replace-me.bin") Assert.That(bytes, Is.EqualTo(replacement));
            else Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo(file.Sha256), file.Path);
          }
        });
      } finally {
        try { Directory.Delete(outDir, recursive: true); } catch { /* best effort */ }
      }
    } finally {
      File.Delete(copy);
    }
  }

  [Test, Category("ExternalFsInterop")]
  public void GivenAVolumeWindowsFormatted_WhenAFileIsRenamed_ThenOnlyItsNameChanges([ValueSource(nameof(Volumes))] string volume) {
    var oracle = Load(volume);
    var source = ImagePath(oracle);
    var copy = Path.Combine(Path.GetTempPath(), "cwb_refs_ren_" + Guid.NewGuid().ToString("N")[..8] + ".refs");
    File.Copy(source, copy);
    var outDir = Path.Combine(Path.GetTempPath(), "cwb_refs_ren_x_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      var descriptor = new RefsFormatDescriptor();
      using (var stream = File.Open(copy, FileMode.Open, FileAccess.ReadWrite)) {
        var before = SHA256.HashData(stream);
        stream.Position = 0;
        Assert.Throws<IOException>(() => descriptor.Rename(stream, "rename-me.txt", "SMALL.TXT"), "an existing name, case-insensitively");
        stream.Position = 0;
        Assert.That(SHA256.HashData(stream), Is.EqualTo(before), "a refused rename leaves the image unchanged");
        descriptor.Rename(stream, "rename-me.txt", "renamed.txt");
      }
      using (var stream = File.OpenRead(copy)) descriptor.Extract(stream, outDir, null, null);
      Assert.Multiple(() => {
        Assert.That(File.Exists(Path.Combine(outDir, "rename-me.txt")), Is.False);
        foreach (var file in oracle.Files) {
          var name = file.Path == "rename-me.txt" ? "renamed.txt" : file.Path;
          var bytes = File.ReadAllBytes(Path.Combine(outDir, name.Replace('/', Path.DirectorySeparatorChar)));
          Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo(file.Sha256), name);
        }
      });
    } finally {
      File.Delete(copy);
      try { Directory.Delete(outDir, recursive: true); } catch { /* best effort */ }
    }
  }

  [Test, Category("ExternalFsInterop")]
  public void GivenAVolumeWindowsFormatted_WhenAFileIsRemoved_ThenItIsGoneItsClustersAreFreeAndTheRestIsIntact([ValueSource(nameof(Volumes))] string volume) {
    var oracle = Load(volume);
    var source = ImagePath(oracle);
    var copy = Path.Combine(Path.GetTempPath(), "cwb_refs_rm_" + Guid.NewGuid().ToString("N")[..8] + ".refs");
    File.Copy(source, copy);
    var outDir = Path.Combine(Path.GetTempPath(), "cwb_refs_rm_x_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      var descriptor = new RefsFormatDescriptor();
      // The 4 KiB volumes must apply; elsewhere a clean refusal (image unchanged) is allowed.
      using (var stream = File.Open(copy, FileMode.Open, FileAccess.ReadWrite)) {
        if (oracle.Variant == "default") {
          descriptor.Remove(stream, ["remove-me.bin"]);
        } else {
          try {
            descriptor.Remove(stream, ["remove-me.bin"]);
          } catch (NotSupportedException refused) {
            TestContext.Out.WriteLine($"{volume}: remove refused: {refused.Message}");
            stream.Position = 0;
            Assert.That(Convert.ToHexStringLower(SHA256.HashData(stream)), Is.EqualTo(oracle.ImageSha256));
            return;
          }
        }
      }
      using (var stream = File.OpenRead(copy)) descriptor.Extract(stream, outDir, null, null);
      Assert.Multiple(() => {
        Assert.That(File.Exists(Path.Combine(outDir, "remove-me.bin")), Is.False);
        foreach (var file in oracle.Files.Where(static f => f.Path != "remove-me.bin")) {
          var bytes = File.ReadAllBytes(Path.Combine(outDir, file.Path.Replace('/', Path.DirectorySeparatorChar)));
          Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo(file.Sha256), file.Path);
        }
      });
    } finally {
      File.Delete(copy);
      try { Directory.Delete(outDir, recursive: true); } catch { /* best effort */ }
    }
  }

  [Test, Category("ExternalFsInterop")]
  public void GivenAVolumeWindowsFormatted_WhenAFileIsReplacedByALongerThenAShorterOne_ThenEachIsReadBackAndTheRestIsIntact(
      [ValueSource(nameof(Volumes))] string volume) {
    var oracle = Load(volume);
    var source = ImagePath(oracle);
    var copy = Path.Combine(Path.GetTempPath(), "cwb_refs_rs_" + Guid.NewGuid().ToString("N")[..8] + ".refs");
    File.Copy(source, copy);
    try {
      var descriptor = new RefsFormatDescriptor();
      foreach (var length in new[] { 300000, 20000 }) {
        var replacement = new byte[length];
        new Random(length).NextBytes(replacement);
        using (var stream = File.Open(copy, FileMode.Open, FileAccess.ReadWrite)) {
          // Proven layouts must apply; the refusals left (ReFS 3.14 at 64 KiB, 3.7 integrity growth past
          // what the record's leaf holds) must leave the image unchanged.
          if (volume != "windows-2022-default") {
            var before = SHA256.HashData(stream);
            stream.Position = 0;
            try {
              descriptor.Add(stream, [Compression.Registry.ArchiveInputInfo.InMemory("replace-me.bin", replacement)]);
            } catch (NotSupportedException refused) when (volume == "windows-2025-cluster64k" || (volume == "windows-2022-integrity" && length == 300000)) {
              TestContext.Out.WriteLine($"{volume}: resize to {length} refused: {refused.Message}");
              stream.Position = 0;
              Assert.That(SHA256.HashData(stream), Is.EqualTo(before), "a refused resize leaves the image unchanged");
              continue;
            }
          } else {
            descriptor.Add(stream, [Compression.Registry.ArchiveInputInfo.InMemory("replace-me.bin", replacement)]);
          }
        }
        var outDir = Path.Combine(Path.GetTempPath(), "cwb_refs_rs_x_" + Guid.NewGuid().ToString("N")[..8]);
        try {
          using (var stream = File.OpenRead(copy)) descriptor.Extract(stream, outDir, null, null);
          Assert.Multiple(() => {
            foreach (var file in oracle.Files) {
              var bytes = File.ReadAllBytes(Path.Combine(outDir, file.Path.Replace('/', Path.DirectorySeparatorChar)));
              if (file.Path == "replace-me.bin") Assert.That(bytes, Is.EqualTo(replacement), $"replace-me.bin at {length} bytes");
              else Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo(file.Sha256), file.Path);
            }
          });
        } finally {
          try { Directory.Delete(outDir, recursive: true); } catch { /* best effort */ }
        }
      }
    } finally {
      File.Delete(copy);
    }
  }

  [Test, Category("ExternalFsInterop")]
  public void GivenAVolumeWindowsFormatted_WhenAFileIsRenamedToANonAsciiName_ThenItIsFoundUnderThatName([ValueSource(nameof(Volumes))] string volume) {
    var oracle = Load(volume);
    var source = ImagePath(oracle);
    var copy = Path.Combine(Path.GetTempPath(), "cwb_refs_uni_" + Guid.NewGuid().ToString("N")[..8] + ".refs");
    File.Copy(source, copy);
    try {
      const string name = "Grüße-Ωμέγα-日本.txt";
      var descriptor = new RefsFormatDescriptor();
      using (var stream = File.Open(copy, FileMode.Open, FileAccess.ReadWrite)) descriptor.Rename(stream, "rename-me.txt", name);
      using var check = File.OpenRead(copy);
      var listed = descriptor.List(check, null).Select(static e => e.Name).ToList();
      Assert.Multiple(() => {
        Assert.That(listed, Does.Contain(name));
        Assert.That(listed, Does.Not.Contain("rename-me.txt"));
        Assert.That(listed.Count(static n => !n.EndsWith('/')), Is.GreaterThanOrEqualTo(oracle.Files.Count));
      });
    } finally {
      File.Delete(copy);
    }
  }

  [Test, Category("ExternalFsInterop")]
  public void GivenAVolumeWindowsFormatted_WhenDirectoryObjectRowsAreRead_ThenTheCounterEqualsTheHighestFileNumber([ValueSource(nameof(Volumes))] string volume) {
    var oracle = Load(volume);
    using var stream = File.OpenRead(ImagePath(oracle));
    var metadata = RefsMetadataReader.Open(stream);
    var directories = 0;
    Assert.Multiple(() => {
      foreach (var row in metadata.WalkRoot(0)) {
        var oid = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(row.Key.AsSpan(8));
        if (oid < 0x600 || RefsDirectoryRows.ObjectRow(metadata, oid) is not { } objectRow) continue;
        var reference = RefsPageReference.Parse(row.Value.AsSpan(0x20));
        var highest = metadata.WalkTree(reference, virtualAddresses: true)
          .Where(static r => r.Key.Length >= 16 && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(r.Key) == 0x20)
          .Select(static r => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(r.Key.AsSpan(8))).DefaultIfEmpty(0UL).Max();
        var counter = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(objectRow.Row.Value.AsSpan(objectRow.CounterOffset));
        Assert.That(counter, Is.EqualTo(Math.Max(highest, 1UL)), $"directory 0x{oid:X} (an empty directory starts at 1)");
        ++directories;
      }
    });
    Assert.That(directories, Is.GreaterThanOrEqualTo(4), "the root and the test tree's directories carry a counter");
  }

  /// <summary>Every integrity-stream cluster of the root directory's files whose CRC32C does not match its entry, as "name#cluster".</summary>
  private static List<string> StaleClusterChecksums(Stream stream) {
    var metadata = RefsMetadataReader.Open(stream);
    var stale = new List<string>();
    var root = RefsInPlaceRename.ResolveDirectoryRoot(metadata, "");
    var buffer = new byte[metadata.ClusterSize];
    foreach (var row in metadata.WalkTree(root, virtualAddresses: true)) {
      if (row.Key.Length <= 4 || System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(row.Key) != 0x30
          || System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(row.Key.AsSpan(2)) != 1) continue;
      var name = System.Text.Encoding.Unicode.GetString(row.Key, 4, row.Key.Length - 4);
      for (uint count = 1; count <= 8; ++count) {
        var leaf = RefsExtentLeaf.Find(row.Value, count);
        if (leaf < 0 || RefsExtentLeaf.Read(row.Value, leaf) is not { } entries) continue;
        foreach (var entry in entries.Where(static e => e.IsIntegrity))
          for (var i = 0; i < entry.Run; ++i) {
            stream.Position = (long)metadata.TranslateVirtualLcn(entry.Vlcn + (ulong)i) * metadata.ClusterSize;
            stream.ReadExactly(buffer);
            if (RefsChecksum.Crc32C(buffer) != entry.Crcs![i]) stale.Add($"{name}#{entry.Vcn + i}");
          }
      }
    }
    return stale;
  }

  private static readonly string[] ManyEdits = ["add-many", "remove-many", "rename-many", "mkdir-many", "grow-many"];

  [Test, Category("ExternalFsInterop")]
  public void GivenADirectoryOverSeveralPages_WhenEdited_ThenTheListingAndTheRowCountFollow(
      [ValueSource(nameof(Volumes))] string volume, [ValueSource(nameof(ManyEdits))] string edit) {
    var oracle = Load(volume);
    var copy = Path.Combine(Path.GetTempPath(), "cwb_refs_many_" + Guid.NewGuid().ToString("N")[..8] + ".refs");
    File.Copy(ImagePath(oracle), copy);
    try {
      var descriptor = new RefsFormatDescriptor();
      var expected = oracle.Files.ToDictionary(static f => f.Path, static f => f.Sha256);
      var added = Pattern(30000);
      var grown = Pattern(70000);
      using (var stream = File.Open(copy, FileMode.Open, FileAccess.ReadWrite)) {
        var before = SHA256.HashData(stream);
        stream.Position = 0;
        try {
          switch (edit) {
            case "add-many":
              descriptor.Add(stream, [Compression.Registry.ArchiveInputInfo.InMemory("many/added.bin", added)]);
              expected["many/added.bin"] = Convert.ToHexStringLower(SHA256.HashData(added));
              break;
            case "remove-many":
              descriptor.Remove(stream, ["many/f007.txt"]);
              expected.Remove("many/f007.txt");
              break;
            case "rename-many":
              descriptor.Rename(stream, "many/f010.txt", "many/f010-renamed.txt");
              expected["many/f010-renamed.txt"] = expected["many/f010.txt"];
              expected.Remove("many/f010.txt");
              break;
            case "mkdir-many":
              descriptor.Add(stream, [new Compression.Registry.ArchiveInputInfo("many/new-folder", "many/new-folder", true)]);
              break;
            case "grow-many":
              descriptor.Add(stream, [Compression.Registry.ArchiveInputInfo.InMemory("many/f020.txt", grown)]);
              expected["many/f020.txt"] = Convert.ToHexStringLower(SHA256.HashData(grown));
              break;
          }
        } catch (NotSupportedException refused) {
          TestContext.Out.WriteLine($"{volume}: {edit} refused: {refused.Message}");
          stream.Position = 0;
          Assert.That(SHA256.HashData(stream), Is.EqualTo(before), "a refused edit leaves the image as it was");
          // ReFS 3.14 keeps the small files of "many" in their rows (resident), which are not resized.
          var expectedRefusal = oracle.RefsVersion == "3.14" && (oracle.Variant == "cluster64k" || edit == "grow-many");
          Assert.That(expectedRefusal, Is.True, $"{edit} is only refused on ReFS 3.14 at 64 KiB or for a resident file: {refused.Message}");
          return;
        }
      }
      using var check = File.OpenRead(copy);
      var metadata = RefsMetadataReader.Open(check);
      var many = RefsInPlaceRename.ResolveDirectory(metadata, "many");
      var tree = RefsDirectoryTree.Open(metadata, many.Root);
      var root = metadata.TryReadReferencedPage(many.Root, virtualAddresses: true)!;
      Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(root.AsSpan(0x70)), Is.EqualTo((ulong)tree.Rows.Count), "the root's row count");
      var files = new RefsNamespaceReader(metadata).ReadAll();
      if (edit == "mkdir-many") Assert.That(files.Any(static f => f.IsDirectory && f.Path == "many/new-folder"), Is.True);
      Assert.That(files.Where(static f => !f.IsDirectory && !f.Path.StartsWith("System Volume Information/", StringComparison.Ordinal)).Select(static f => f.Path).Order().ToList(), Is.EqualTo(expected.Keys.Order().ToList()), "the files listed");
      var outDir = Path.Combine(Path.GetTempPath(), "cwb_refs_many_x_" + Guid.NewGuid().ToString("N")[..8]);
      try {
        check.Position = 0;
        descriptor.Extract(check, outDir, null, null);
        Assert.Multiple(() => {
          foreach (var (path, sha) in expected)
            Assert.That(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(outDir, path.Replace('/', Path.DirectorySeparatorChar))))), Is.EqualTo(sha), path);
        });
      } finally {
        try { Directory.Delete(outDir, recursive: true); } catch { /* best effort */ }
      }
    } finally {
      File.Delete(copy);
    }
  }

  private static byte[] Pattern(int length) {
    var bytes = new byte[length];
    for (var i = 0; i < length; ++i) bytes[i] = (byte)(i * 31 + (i >> 9));
    return bytes;
  }

  private static readonly string[] Edits = ["none", "overwrite", "grow", "shrink", "rename", "rename-unicode", "remove", "add", "mkdir", "add-many", "remove-many", "rename-many", "mkdir-many", "grow-many"];

  [Test, Category("ExternalFsInterop")]
  public void GivenAVolumeWindowsFormatted_WhenAnyEditIsMade_ThenEveryPageReferenceChecksumStillMatches(
      [ValueSource(nameof(Volumes))] string volume, [ValueSource(nameof(Edits))] string edit) {
    var oracle = Load(volume);
    var source = ImagePath(oracle);
    var copy = Path.Combine(Path.GetTempPath(), "cwb_refs_ck_" + Guid.NewGuid().ToString("N")[..8] + ".refs");
    File.Copy(source, copy);
    try {
      var descriptor = new RefsFormatDescriptor();
      using (var stream = File.Open(copy, FileMode.Open, FileAccess.ReadWrite)) {
        try {
          switch (edit) {
            case "overwrite": descriptor.Add(stream, [Compression.Registry.ArchiveInputInfo.InMemory("replace-me.bin", Pattern(65536))]); break;
            case "grow": descriptor.Add(stream, [Compression.Registry.ArchiveInputInfo.InMemory("replace-me.bin", Pattern(300000))]); break;
            case "shrink": descriptor.Add(stream, [Compression.Registry.ArchiveInputInfo.InMemory("replace-me.bin", Pattern(20000))]); break;
            case "rename": descriptor.Rename(stream, "rename-me.txt", "renamed.txt"); break;
            case "rename-unicode": descriptor.Rename(stream, "rename-me.txt", "Grüße-日本.txt"); break;
            case "remove": descriptor.Remove(stream, ["remove-me.bin"]); break;
            case "add": descriptor.Add(stream, [Compression.Registry.ArchiveInputInfo.InMemory("added.bin", Pattern(30000))]); break;
            case "mkdir": descriptor.Add(stream, [new Compression.Registry.ArchiveInputInfo("new-folder", "new-folder", true)]); break;
            // "many" holds 200 files over several leaf pages under one inner root.
            case "add-many": descriptor.Add(stream, [Compression.Registry.ArchiveInputInfo.InMemory("many/added.bin", Pattern(30000))]); break;
            case "remove-many": descriptor.Remove(stream, ["many/f007.txt"]); break;
            case "rename-many": descriptor.Rename(stream, "many/f010.txt", "many/f010-renamed.txt"); break;
            case "mkdir-many": descriptor.Add(stream, [new Compression.Registry.ArchiveInputInfo("many/new-folder", "many/new-folder", true)]); break;
            case "grow-many": descriptor.Add(stream, [Compression.Registry.ArchiveInputInfo.InMemory("many/f020.txt", Pattern(70000))]); break;
          }
        } catch (NotSupportedException refused) {
          Assert.Ignore($"{edit} refused on {volume}: {refused.Message}");
        }
      }
      using var check = File.OpenRead(copy);
      var stale = new RefsMetadataGraph(check, RefsMetadataReader.Open(check)).FindStaleReferences();
      Assert.That(stale, Is.Empty, $"{edit} on {volume} left stale page-reference checksums");
      Assert.That(StaleClusterChecksums(check), Is.Empty, $"{edit} on {volume} left integrity-stream cluster checksums that do not match");
    } finally {
      File.Delete(copy);
    }
  }

  [Test, Category("ExternalFsInterop")]
  public void GivenAVolumeWindowsFormatted_WhenADirectoryIsCreated_ThenItIsListedAndTheRestIsIntact([ValueSource(nameof(Volumes))] string volume) {
    var oracle = Load(volume);
    var source = ImagePath(oracle);
    var copy = Path.Combine(Path.GetTempPath(), "cwb_refs_mkdir_" + Guid.NewGuid().ToString("N")[..8] + ".refs");
    File.Copy(source, copy);
    var outDir = Path.Combine(Path.GetTempPath(), "cwb_refs_mkdir_x_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      var descriptor = new RefsFormatDescriptor();
      using (var stream = File.Open(copy, FileMode.Open, FileAccess.ReadWrite)) {
        var before = SHA256.HashData(stream);
        stream.Position = 0;
        try {
          descriptor.Add(stream, [new Compression.Registry.ArchiveInputInfo("new-folder", "new-folder", true)]);
        } catch (NotSupportedException refused) when (oracle.RefsVersion != "3.7" && oracle.Variant == "cluster64k") {
          TestContext.Out.WriteLine($"{volume}: mkdir refused: {refused.Message}");
          stream.Position = 0;
          Assert.That(SHA256.HashData(stream), Is.EqualTo(before));
          return;
        }
      }
      using (var stream = File.OpenRead(copy)) {
        var listed = descriptor.List(stream, null);
        Assert.That(listed.Any(static e => e.IsDirectory && e.Name == "new-folder"), Is.True, "the new directory is listed");
        stream.Position = 0;
        descriptor.Extract(stream, outDir, null, null);
      }
      Assert.Multiple(() => {
        Assert.That(Directory.Exists(Path.Combine(outDir, "new-folder")), Is.True);
        foreach (var file in oracle.Files) {
          var bytes = File.ReadAllBytes(Path.Combine(outDir, file.Path.Replace('/', Path.DirectorySeparatorChar)));
          Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo(file.Sha256), file.Path);
        }
      });
    } finally {
      File.Delete(copy);
      try { Directory.Delete(outDir, recursive: true); } catch { /* best effort */ }
    }
  }

  [Test, Category("ExternalFsInterop")]
  public void GivenAVolumeWindowsFormatted_WhenANewFileIsAdded_ThenItIsReadBackAndTheRestIsIntact([ValueSource(nameof(Volumes))] string volume) {
    var oracle = Load(volume);
    var source = ImagePath(oracle);
    var copy = Path.Combine(Path.GetTempPath(), "cwb_refs_add_" + Guid.NewGuid().ToString("N")[..8] + ".refs");
    File.Copy(source, copy);
    var outDir = Path.Combine(Path.GetTempPath(), "cwb_refs_add_x_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      var added = new byte[30000];
      new Random(34).NextBytes(added);
      var descriptor = new RefsFormatDescriptor();
      using (var stream = File.Open(copy, FileMode.Open, FileAccess.ReadWrite)) {
        var before = SHA256.HashData(stream);
        stream.Position = 0;
        try {
          descriptor.Add(stream, [Compression.Registry.ArchiveInputInfo.InMemory("added.bin", added)]);
        } catch (NotSupportedException refused) when (oracle.RefsVersion == "3.14" && oracle.Variant == "cluster64k") {
          TestContext.Out.WriteLine($"{volume}: add refused: {refused.Message}");
          stream.Position = 0;
          Assert.That(SHA256.HashData(stream), Is.EqualTo(before));
          return;
        }
      }
      using (var stream = File.OpenRead(copy)) descriptor.Extract(stream, outDir, null, null);
      Assert.Multiple(() => {
        Assert.That(File.ReadAllBytes(Path.Combine(outDir, "added.bin")), Is.EqualTo(added));
        foreach (var file in oracle.Files) {
          var bytes = File.ReadAllBytes(Path.Combine(outDir, file.Path.Replace('/', Path.DirectorySeparatorChar)));
          Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo(file.Sha256), file.Path);
        }
      });
    } finally {
      File.Delete(copy);
      try { Directory.Delete(outDir, recursive: true); } catch { /* best effort */ }
    }
  }

  [Test, Category("ExternalFsInterop")]
  public void GivenAVolumeWindowsFormatted_WhenAnEditIsAsked_ThenItIsRefusedAndTheImageIsUnchanged([ValueSource(nameof(Volumes))] string volume) {
    var oracle = Load(volume);
    var source = ImagePath(oracle);
    var copy = Path.Combine(Path.GetTempPath(), "cwb_refs_edit_" + Guid.NewGuid().ToString("N")[..8] + ".refs");
    File.Copy(source, copy);
    try {
      var descriptor = new RefsFormatDescriptor();
      using (var stream = File.Open(copy, FileMode.Open, FileAccess.ReadWrite)) {
        Assert.Multiple(() => {
          Assert.Throws<NotSupportedException>(() => descriptor.Add(stream, [Compression.Registry.ArchiveInputInfo.InMemory("new-a.bin", new byte[10]), Compression.Registry.ArchiveInputInfo.InMemory("new-b.bin", new byte[10])]), "two new files at once");
          Assert.Throws<NotSupportedException>(() => descriptor.Add(stream, [Compression.Registry.ArchiveInputInfo.InMemory("replace-me.bin", new byte[70000]), Compression.Registry.ArchiveInputInfo.InMemory("small.txt", new byte[5])]), "two length changes at once");
          Assert.Throws<NotSupportedException>(() => descriptor.Defragment(stream));
        });
      }
      using var check = File.OpenRead(copy);
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(check)), Is.EqualTo(oracle.ImageSha256));
    } finally {
      File.Delete(copy);
    }
  }
}
