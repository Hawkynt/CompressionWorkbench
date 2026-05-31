using Compression.Lib;
using Compression.Registry;
using FileSystem.Fat;

namespace Compression.Tests.Lib;

/// <summary>
/// The in-memory processing framework: a configurable size threshold routes
/// small images through an all-RAM reconfigure/convert pipeline, and inputs can
/// be fed straight from memory so a conversion never extracts to a temp
/// directory. Write-back is atomic (temp-file + rename).
/// </summary>
[TestFixture]
public class InMemoryProcessingTests {

  [Test, Category("Spec")]
  public void Threshold_RoutesSmallInMemory_LargeToDisk() {
    var saved = InMemoryProcessing.ThresholdBytes;
    try {
      InMemoryProcessing.ThresholdBytes = 128 * 1024; // tiny test threshold
      Assert.That(InMemoryProcessing.FitsInMemory(100 * 1024), Is.True, "below threshold → in memory");
      Assert.That(InMemoryProcessing.FitsInMemory(200 * 1024), Is.False, "above threshold → disk path");
    } finally {
      InMemoryProcessing.ThresholdBytes = saved;
    }
  }

  [Test, Category("RoundTrip")]
  public void InMemoryInputs_FromSpanStreamAndFile_AllRoundTrip() {
    var desc = new FatFormatDescriptor();
    var tmp = Path.Combine(Path.GetTempPath(), $"cwb_in_{Guid.NewGuid():N}.bin");
    File.WriteAllBytes(tmp, "from a real file"u8.ToArray());
    try {
      var inputs = new List<ArchiveInputInfo> {
        ArchiveInputInfo.InMemory("span.txt", "from a span"u8.ToArray().AsSpan()),
        ArchiveInputInfo.InMemory("stream.txt", new MemoryStream("from a stream"u8.ToArray())),
        ArchiveInputInfo.FromFile(new FileInfo(tmp), "onfile.txt"),
      };
      var image = InMemoryProcessing.BuildInMemory(desc, inputs, new FormatCreateOptions());

      using var ms = new MemoryStream(image);
      var r = new FileSystem.Fat.FatReader(ms);
      var byName = r.Entries.Where(e => !e.IsDirectory)
                            .ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e));
      Assert.That(byName["span.txt"], Is.EqualTo("from a span"u8.ToArray()), "span input round-trips");
      Assert.That(byName["stream.txt"], Is.EqualTo("from a stream"u8.ToArray()), "stream input round-trips");
      Assert.That(byName["onfile.txt"], Is.EqualTo("from a real file"u8.ToArray()), "FileInfo input round-trips");
    } finally {
      File.Delete(tmp);
    }
  }

  [Test, Category("RoundTrip")]
  public void Build_FromInMemoryInputs_NeverTouchesDisk() {
    var desc = new FatFormatDescriptor();
    // The archive names are nested paths that do NOT exist on disk. If Create
    // tried File.ReadAllBytes(FullPath) it would throw FileNotFound — so a
    // successful round-trip proves the bytes came from memory.
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("readme.txt", "root file"u8.ToArray()),
      ArchiveInputInfo.InMemory("docs/guide.txt", "in docs"u8.ToArray()),
    };

    var image = InMemoryProcessing.BuildInMemory(desc, inputs, new FormatCreateOptions());

    using var ms = new MemoryStream(image);
    var r = new FatReader(ms);
    var byName = r.Entries.Where(e => !e.IsDirectory)
                          .ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e));
    Assert.That(byName["readme.txt"], Is.EqualTo("root file"u8.ToArray()));
    Assert.That(byName["docs/guide.txt"], Is.EqualTo("in docs"u8.ToArray()), "nested file built from memory");
  }

  [Test, Category("RoundTrip")]
  public void ReconfigureInMemory_RebuildsAndWritesBackAtomically_NoTempExtraction() {
    var desc = new FatFormatDescriptor();

    // 1. Build an initial image entirely in memory.
    var original = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("a/one.bin", new byte[5000]),
      ArchiveInputInfo.InMemory("a/b/two.bin", new byte[3000]),
    };
    var image = InMemoryProcessing.BuildInMemory(desc, original, new FormatCreateOptions());

    // 2. Extract entries back into memory (no temp directory) and reconfigure the
    //    image with a different cluster size — a full in-memory rebuild.
    List<ArchiveInputInfo> extracted;
    using (var ms = new MemoryStream(image)) {
      var r = new FatReader(ms);
      extracted = r.Entries.Where(e => !e.IsDirectory)
                           .Select(e => ArchiveInputInfo.InMemory(e.Name.Replace('\\', '/'), r.Extract(e)))
                           .ToList();
    }
    var opts = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["ClusterSize"] = "2 KB" },
    };

    var target = Path.Combine(Path.GetTempPath(), $"cwb_inmem_{Guid.NewGuid():N}.fat");
    try {
      InMemoryProcessing.RebuildToFileAtomic(target, desc, extracted, opts);

      Assert.That(File.Exists(target), Is.True, "atomic write-back produced the target file");
      var leftovers = Directory.EnumerateFiles(Path.GetDirectoryName(target)!,
        Path.GetFileName(target) + ".tmp.*");
      Assert.That(leftovers, Is.Empty, "no leftover temp file after atomic rename");

      using var ms2 = new MemoryStream(File.ReadAllBytes(target));
      var r2 = new FatReader(ms2);
      var byName = r2.Entries.Where(e => !e.IsDirectory)
                             .ToDictionary(e => e.Name.Replace('\\', '/'), e => r2.Extract(e).Length);
      Assert.That(byName.ContainsKey("a/one.bin"), Is.True, "nested file preserved through in-memory reconfigure");
      Assert.That(byName.ContainsKey("a/b/two.bin"), Is.True, "deep nested file preserved");
      Assert.That(byName["a/one.bin"], Is.EqualTo(5000), "content length intact");
    } finally {
      File.Delete(target);
    }
  }
}
