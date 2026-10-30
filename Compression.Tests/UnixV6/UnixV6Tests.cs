#pragma warning disable CS1591
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using FileSystem.UnixV6;

namespace Compression.Tests.UnixV6;

/// <summary>
/// The V6 reader against the 1975 root volume from Dennis Ritchie's tapes (TUHS
/// <c>Dennis_v6/v6root.gz</c>); the expected files, sizes and times come from the
/// independent extraction TUHS publishes beside it (<c>v6root.tar.gz</c>).
/// Provenance in <c>ReferenceVectors/README.md</c>.
/// </summary>
[TestFixture]
public sealed class UnixV6Tests {

  private static byte[] Volume() {
    using var resource = typeof(UnixV6Tests).Assembly.GetManifestResourceStream("UnixV6Vectors.v6root.gz")
      ?? throw new FileNotFoundException("embedded v6root.gz is missing");
    using var gzip = new GZipStream(resource, CompressionMode.Decompress);
    using var ms = new MemoryStream();
    gzip.CopyTo(ms);
    return ms.ToArray();
  }

  private static IEnumerable<string[]> Manifest() {
    using var resource = typeof(UnixV6Tests).Assembly.GetManifestResourceStream("UnixV6Vectors.manifest.txt")!;
    using var text = new StreamReader(resource);
    while (text.ReadLine() is { } line)
      if (line.Length > 0) yield return line.Split(' ', line[0] == 'F' ? 5 : 3);
  }

  [Test, Category("ThirdParty")]
  public void GivenTheV6RootVolume_WhenRead_ThenEveryFileMatchesTheTarExtraction() {
    using var r = new UnixV6Reader(new MemoryStream(Volume()));
    var byName = r.Entries.ToDictionary(e => e.Name, StringComparer.Ordinal);
    var mismatches = new List<string>();
    var files = 0;
    var dirs = 0;
    foreach (var m in Manifest()) {
      if (m[0] == "D") {
        if (!byName.TryGetValue(m[2], out var d) || !d.IsDirectory) mismatches.Add("dir " + m[2]);
        ++dirs;
        continue;
      }
      var (sha, size, mtime, name) = (m[1], long.Parse(m[2]), long.Parse(m[3]), m[4]);
      if (!byName.TryGetValue(name, out var e)) { mismatches.Add("missing " + name); continue; }
      if (e.Size != size) mismatches.Add($"size {name}: {e.Size} != {size}");
      if (Convert.ToHexStringLower(SHA256.HashData(r.Extract(e))) != sha) mismatches.Add("content " + name);
      if (e.LastModified != DateTimeOffset.FromUnixTimeSeconds(mtime).UtcDateTime) mismatches.Add("mtime " + name);
      ++files;
    }
    Assert.That(mismatches, Is.Empty);
    Assert.That(files, Is.EqualTo(289));
    Assert.That(dirs, Is.EqualTo(23));
    Assert.That(r.Entries, Has.Count.EqualTo(files + dirs), "devices are not listed; everything else is");
  }

  [Test]
  public void GivenTheDescriptor_WhenListing_ThenItMatchesTheReader() {
    var d = new UnixV6FormatDescriptor();
    var list = d.List(new MemoryStream(Volume()), null);
    Assert.That(list.Any(e => e.Name == "bin/sh" && !e.IsDirectory), Is.True);
    Assert.That(list.Any(e => e.Name == "usr" && e.IsDirectory), Is.True);
  }

  [Test]
  public void GivenARootInodeThatIsNotADirectory_WhenRead_ThenItIsRejected() {
    var image = Volume();
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(1024), 0x8000 | 0x1A4); // IALLOC | plain file
    Assert.Throws<InvalidDataException>(() => _ = new UnixV6Reader(new MemoryStream(image)));
  }

  [TestCase(4, 101)]    // s_nfree past 100
  [TestCase(206, 101)]  // s_ninode past 100
  [TestCase(0, 0)]      // s_isize zero
  public void GivenAnImplausibleSuperblockField_WhenRead_ThenItIsRejected(int offset, int value) {
    var image = Volume();
    BinaryPrimitives.WriteInt16LittleEndian(image.AsSpan(512 + offset), (short)value);
    Assert.Throws<InvalidDataException>(() => _ = new UnixV6Reader(new MemoryStream(image)));
  }

  [Test]
  public void GivenAnImageTooSmall_WhenRead_ThenItIsRejected()
    => Assert.Throws<InvalidDataException>(() => _ = new UnixV6Reader(new MemoryStream(new byte[1000])));

  [Test]
  public void GivenTheVolumeCutShort_WhenListed_ThenListingSucceedsAndLostFilesRefuseToExtract() {
    var image = Volume();
    using var full = new UnixV6Reader(new MemoryStream(image));
    var cut = image.AsSpan(0, image.Length / 2).ToArray();
    using var r = new UnixV6Reader(new MemoryStream(cut));
    Assert.That(r.Entries, Is.Not.Empty);
    var lost = 0;
    foreach (var e in r.Entries.Where(e => !e.IsDirectory)) {
      try {
        var bytes = r.Extract(e);
        Assert.That(bytes, Is.EqualTo(full.Extract(full.Entries.Single(f => f.Name == e.Name))), $"{e.Name} must be right or refused");
      } catch (InvalidDataException) {
        ++lost;
      }
    }
    Assert.That(lost, Is.GreaterThan(0));
  }
}
