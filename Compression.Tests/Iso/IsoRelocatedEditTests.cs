#pragma warning disable CS1591
using System.IO.Compression;
using System.Text;
using Compression.Registry;
using FileSystem.Iso;

namespace Compression.Tests.Iso;

/// <summary>
/// In-place editing of Rock Ridge images whose deep folders are relocated (RRIP 1.10 4.1.5):
/// genisoimage's rr-relocated reference image and our own Rock Ridge + Joliet images. Files and folders
/// are added and removed where a reader shows them; the RE record in rr_moved, the CL
/// placeholder and the PL entry stay consistent, a newly deep folder is relocated, and the
/// Joliet tree keeps the real hierarchy. The kernel, xorriso and isoinfo verdicts on the same
/// edits are in <see cref="IsoEditInterleavedRelocatedOracleTests"/>.
/// </summary>
[TestFixture]
[Category("RoundTrip")]
public sealed class IsoRelocatedEditTests {
  private static readonly IsoFormatDescriptor Iso = new();

  internal const string MovedChain = "a/b/c/d/e/f/g/h/i/j";

  internal static readonly string NewChain = string.Join('/', Enumerable.Range(1, 11).Select(i => $"x{i:D2}"));

  internal static MemoryStream Relocated() {
    using var resource = typeof(IsoRelocatedEditTests).Assembly.GetManifestResourceStream("IsoVectors.rr-relocated.iso.gz")!;
    using var gzip = new GZipStream(resource, CompressionMode.Decompress);
    var image = new MemoryStream();
    gzip.CopyTo(image);
    image.Position = 0;
    return image;
  }

  /// <summary>Rock Ridge and Joliet, with a writer-relocated 12-level tree.</summary>
  internal static MemoryStream Ours(bool deep = true) {
    var w = new IsoWriter { EnableRockRidge = true, EnableJoliet = true };
    w.AddFile("top.txt", "top\n"u8.ToArray());
    if (deep) w.AddFile("d01/d02/d03/d04/d05/d06/d07/d08/d09/d10/d11/d12/deepest.txt", "twelve levels down\n"u8.ToArray());
    var image = new MemoryStream();
    image.Write(w.Build());
    image.Position = 0;
    return image;
  }

  private static Dictionary<string, string> Files(Stream image, bool joliet) {
    image.Position = 0;
    using var reader = new IsoReader(image, leaveOpen: true, useJoliet: joliet);
    return reader.Entries.Where(e => !e.IsDirectory && !e.IsSymlink)
      .ToDictionary(e => e.Name, e => Encoding.Latin1.GetString(reader.Extract(e)), StringComparer.Ordinal);
  }

  private static HashSet<string> Folders(Stream image, bool joliet) {
    image.Position = 0;
    using var reader = new IsoReader(image, leaveOpen: true, useJoliet: joliet);
    return reader.Entries.Where(e => e.IsDirectory).Select(e => e.Name.TrimEnd('/')).ToHashSet(StringComparer.Ordinal);
  }

  /// <summary>The primary tree as recorded, by ISO 9660 identifier ("." and ".." left out): folders end in '/'.</summary>
  internal static List<string> Recorded(byte[] image) {
    var result = new List<string>();
    void Walk(int lba, int length, string path) {
      for (var s = 0; s < (length + 2047) / 2048; ++s)
        for (var pos = (lba + s) * 2048; pos < (lba + s + 1) * 2048 && image[pos] != 0; pos += image[pos]) {
          var idLen = image[pos + 32];
          if (idLen == 1 && image[pos + 33] <= 1) continue;
          var id = Encoding.ASCII.GetString(image, pos + 33, idLen);
          if ((image[pos + 25] & 2) != 0) {
            result.Add(path + id + "/");
            Walk(BitConverter.ToInt32(image, pos + 2), BitConverter.ToInt32(image, pos + 10), path + id + "/");
          } else result.Add(path + id);
        }
    }
    Walk(BitConverter.ToInt32(image, 16 * 2048 + 158), BitConverter.ToInt32(image, 16 * 2048 + 166), "");
    return result;
  }

  [Test]
  public void GivenGenisoimagesRelocatedTree_WhenFilesAreAddedInsideIt_ThenTheyAppearWhereTheFolderIsShown() {
    using var image = Relocated();
    var before = Files(image, joliet: false);

    Iso.Add(image, [
      ArchiveInputInfo.InMemory("a/b/c/d/e/f/g/h/beside-i.txt", "in the relocated folder\n"u8.ToArray()),
      ArchiveInputInfo.InMemory(MovedChain + "/added.txt", "next to deep.txt\n"u8.ToArray()),
      ArchiveInputInfo.InMemory(MovedChain + "/k/l/m/further.txt", "below the relocated folder\n"u8.ToArray()),
    ]);

    var after = Files(image, joliet: false);
    Assert.Multiple(() => {
      foreach (var (name, content) in before) Assert.That(after[name], Is.EqualTo(content), name);
      Assert.That(after["a/b/c/d/e/f/g/h/beside-i.txt"], Is.EqualTo("in the relocated folder\n"));
      Assert.That(after[MovedChain + "/added.txt"], Is.EqualTo("next to deep.txt\n"));
      Assert.That(after[MovedChain + "/k/l/m/further.txt"], Is.EqualTo("below the relocated folder\n"));
      Assert.That(Folders(image, joliet: false), Has.None.Contains("rr_moved"));
      Assert.That(Files(image, joliet: true), Is.EquivalentTo(Files(image, joliet: false)), "Joliet shows the same files at the same paths");
    });
    var recorded = Recorded(image.ToArray());
    Assert.That(recorded, Has.Some.Matches<string>(p => p.StartsWith("RR_MOVED/H/I/J/K/L/M/", StringComparison.OrdinalIgnoreCase)),
      "the new folders are recorded below the relocated one, in rr_moved");
  }

  [Test]
  public void GivenGenisoimagesRelocatedTree_WhenAFileInsideItIsRemoved_ThenOnlyThatFileGoes() {
    using var image = Relocated();
    var before = Files(image, joliet: false);

    Iso.Remove(image, [MovedChain + "/deep.txt"]);

    var after = Files(image, joliet: false);
    before.Remove(MovedChain + "/deep.txt");
    Assert.That(after, Is.EquivalentTo(before));
      Assert.That(Files(image, joliet: true), Is.EquivalentTo(after), "Joliet shows the same files at the same paths");
    Assert.That(Folders(image, joliet: false), Does.Contain(MovedChain));
  }

  [Test]
  public void GivenGenisoimagesRelocatedTree_WhenTheRelocatedFolderIsRemoved_ThenItsRecordAndTheEmptyRrMovedGo() {
    using var image = Relocated();
    var before = Files(image, joliet: false);

    Iso.Remove(image, ["a/b/c/d/e/f/g/h"]);

    var after = Files(image, joliet: false);
    before.Remove(MovedChain + "/deep.txt");
    var recorded = Recorded(image.ToArray());
    Assert.Multiple(() => {
      Assert.That(after, Is.EquivalentTo(before));
      Assert.That(Files(image, joliet: true), Is.EquivalentTo(after), "Joliet shows the same files at the same paths");
      Assert.That(Folders(image, joliet: false), Does.Contain("a/b/c/d/e/f/g").And.Not.Contain("a/b/c/d/e/f/g/h"));
      Assert.That(recorded, Has.None.StartsWith("RR_MOVED").IgnoreCase, "rr_moved held only that folder");
      Assert.That(recorded, Has.None.EqualTo("A/B/C/D/E/F/G/H"), "the CL placeholder went too");
    });
  }

  [Test]
  public void GivenGenisoimagesRelocatedTree_WhenTheTopFolderIsRemoved_ThenTheRelocatedPartGoesToo() {
    using var image = Relocated();
    Iso.Remove(image, ["a"]);
    var recorded = Recorded(image.ToArray());
    Assert.Multiple(() => {
      Assert.That(Files(image, joliet: false).Keys, Has.None.StartsWith("a/"));
      Assert.That(recorded, Has.None.StartsWith("RR_MOVED").IgnoreCase);
      Assert.That(recorded, Has.None.StartsWith("A/").IgnoreCase);
    });
  }

  [Test]
  public void GivenGenisoimagesRelocatedTree_WhenRrMovedItselfIsRemoved_ThenItIsRefusedAndUntouched() {
    using var image = Relocated();
    var before = image.ToArray();
    Assert.Throws<NotSupportedException>(() => Iso.Remove(image, ["rr_moved"]));
    Assert.That(image.ToArray(), Is.EqualTo(before));
  }

  [Test]
  public void GivenAnImageWithoutRelocation_WhenAFolderBelowTheEighthLevelIsAdded_ThenItIsRelocatedAndJolietKeepsTheRealPath() {
    using var image = Ours(deep: false);

    Iso.Add(image, [ArchiveInputInfo.InMemory(NewChain + "/new.txt", "eleven levels down\n"u8.ToArray())]);

    var recorded = Recorded(image.ToArray());
    Assert.Multiple(() => {
      Assert.That(Files(image, joliet: false)[NewChain + "/new.txt"], Is.EqualTo("eleven levels down\n"), "Rock Ridge shows the real path");
      Assert.That(Files(image, joliet: true)[NewChain + "/new.txt"], Is.EqualTo("eleven levels down\n"), "Joliet records the real path");
      Assert.That(Folders(image, joliet: false), Has.None.Contains("rr_moved"));
      Assert.That(Files(image, joliet: true), Is.EquivalentTo(Files(image, joliet: false)), "Joliet shows the same files at the same paths");
      // Root 1, x01 2 … x07 8: x08 would be level 9 and moves into rr_moved.
      Assert.That(recorded, Does.Contain("X01/X02/X03/X04/X05/X06/X07/X08"), "the CL placeholder, a file record");
      Assert.That(recorded, Does.Contain("RR_MOVED/X08/X09/X10/X11/NEW.TXT;1"));
      Assert.That(recorded.Max(p => p.TrimEnd('/').Count(c => c == '/')), Is.LessThanOrEqualTo(7), "no record deeper than level 8");
    });
  }

  [Test]
  public void GivenOurRelocatedTree_WhenEditedInsideIt_ThenRockRidgeAndJolietAgree() {
    using var image = Ours();
    const string Deep = "d01/d02/d03/d04/d05/d06/d07/d08/d09/d10/d11/d12";

    Iso.Add(image, [
      ArchiveInputInfo.InMemory(Deep + "/second.txt", "second\n"u8.ToArray()),
      ArchiveInputInfo.InMemory("d01/d02/d03/d04/d05/d06/d07/d08/sibling/in-sibling.txt", "sibling\n"u8.ToArray()),
      ArchiveInputInfo.InMemory("d01/d02/d03/d04/d05/d06/d07/other/x/y/z.txt", "another relocation\n"u8.ToArray()),
    ]);
    Iso.Remove(image, [Deep + "/deepest.txt"]);

    var rr = Files(image, joliet: false);
    var joliet = Files(image, joliet: true);
    Assert.Multiple(() => {
      Assert.That(joliet, Is.EquivalentTo(rr), "both trees show the same files at the same paths");
      Assert.That(Folders(image, joliet: true), Is.EquivalentTo(Folders(image, joliet: false)));
      Assert.That(rr.Keys, Is.EquivalentTo(new[] {
        "top.txt", Deep + "/second.txt", "d01/d02/d03/d04/d05/d06/d07/d08/sibling/in-sibling.txt", "d01/d02/d03/d04/d05/d06/d07/other/x/y/z.txt",
      }));
    });
    var recorded = Recorded(image.ToArray());
    Assert.That(recorded, Does.Contain("RR_MOVED/OTHER/"), "a second folder relocated next to the writer's");

    Iso.Remove(image, ["d01/d02/d03/d04/d05/d06/d07/d08", "d01/d02/d03/d04/d05/d06/d07/other"]);
    Assert.Multiple(() => {
      Assert.That(Files(image, joliet: false).Keys, Is.EquivalentTo(new[] { "top.txt" }));
      Assert.That(Files(image, joliet: true).Keys, Is.EquivalentTo(new[] { "top.txt" }));
      Assert.That(Recorded(image.ToArray()), Has.None.StartsWith("RR_MOVED"));
    });
  }

  [Test]
  public void GivenARelocatedFolder_WhenItOutgrowsItsSector_ThenItsPlaceholderAndChildrenFollowIt() {
    using var image = Ours();
    const string Deep = "d01/d02/d03/d04/d05/d06/d07/d08/d09/d10/d11/d12";
    var inputs = Enumerable.Range(0, 60)
      .Select(i => ArchiveInputInfo.InMemory($"d01/d02/d03/d04/d05/d06/d07/d08/a-rather-long-file-name-number-{i:D3}.txt", Encoding.ASCII.GetBytes($"{i}\n")))
      .ToList();

    Iso.Add(image, inputs);

    var rr = Files(image, joliet: false);
    Assert.Multiple(() => {
      Assert.That(rr[Deep + "/deepest.txt"], Is.EqualTo("twelve levels down\n"));
      Assert.That(rr["d01/d02/d03/d04/d05/d06/d07/d08/a-rather-long-file-name-number-059.txt"], Is.EqualTo("59\n"));
      Assert.That(Files(image, joliet: true), Is.EquivalentTo(rr));
    });
  }
}
