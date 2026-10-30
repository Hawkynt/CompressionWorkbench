using System.IO.Compression;
using System.Reflection;
using System.Text;
using Compression.Registry;
using FileFormat.BagIt;

namespace Compression.Tests.BagIt;

/// <summary>
/// BagIt bags, against bagit-python (the Library of Congress reference implementation): its bags
/// validate here, ours validate there. See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class BagItTests {
  private static readonly byte[] Readme = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("A BagIt payload file.\n", 40)));
  private static readonly byte[] Notes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Unicode été text\n", 10)));

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"BagItVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded BagIt vector '{name}' is missing.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  // Test-only: rewrites one member of a ZIP (the BCL is allowed as a test tool, never in product code).
  private static byte[] Tamper(byte[] zip, string member, Func<byte[]?, byte[]?> change) {
    using var ms = new MemoryStream();
    ms.Write(zip);
    using (var archive = new ZipArchive(ms, ZipArchiveMode.Update, leaveOpen: true)) {
      var entry = archive.GetEntry(member);
      byte[]? old = null;
      if (entry != null) {
        using (var s = entry.Open()) {
          using var copy = new MemoryStream();
          s.CopyTo(copy);
          old = copy.ToArray();
        }

        entry.Delete();
      }

      var updated = change(old);
      if (updated != null) {
        using var w = archive.CreateEntry(member).Open();
        w.Write(updated);
      }
    }

    return ms.ToArray();
  }

  [TestCase("loc-sha256-sha512.zip", "sha512+sha256")]
  [TestCase("loc-md5.zip", "md5")]
  [Category("ThirdPartyVector")]
  public void Given_BagitPythonBag_When_Listed_Then_PayloadAndAlgorithms(string vector, string algorithms) {
    var list = new BagItFormatDescriptor().List(new MemoryStream(Vector(vector)), null);
    Assert.That(list.Select(e => (e.Name, e.OriginalSize)), Is.EqualTo(new[] {
      ("readme.txt", (long)Readme.Length), ("docs/empty.bin", 0L), ("docs/notes with space.txt", (long)Notes.Length),
    }));
    Assert.That(list.Select(e => e.Method), Is.All.EqualTo(algorithms));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_BagitPythonBag_When_Read_Then_PayloadBytesAndTagFields() {
    var bag = new BagItBag(new MemoryStream(Vector("loc-sha256-sha512.zip")));
    Assert.That(bag.Version, Is.EqualTo("0.97"), "bagit-python 1.8.1 still declares 0.97");
    Assert.That(bag.RootName, Is.EqualTo("loc-sha256-sha512"));
    Assert.That(bag.Read(bag.Payload.Single(p => p.Path == "readme.txt")), Is.EqualTo(Readme));
    Assert.That(bag.Read(bag.Payload.Single(p => p.Path == "docs/notes with space.txt")), Is.EqualTo(Notes));
    Assert.That(bag.BagInfo, Does.Contain("Contact-Name: Reference Bag"));
  }

  [Test, Category("EdgeCase")]
  public void Given_ChangedPayloadByte_When_Opened_Then_InvalidData() {
    var zip = Tamper(Vector("loc-md5.zip"), "loc-md5/data/readme.txt", old => [.. old!, (byte)'!']);
    Assert.Throws<InvalidDataException>(() => new BagItBag(new MemoryStream(zip)));
  }

  [Test, Category("EdgeCase")]
  public void Given_PayloadFileNotInManifest_When_Opened_Then_InvalidData() {
    var zip = Tamper(Vector("loc-md5.zip"), "loc-md5/data/stowaway.txt", _ => "not in any manifest"u8.ToArray());
    Assert.Throws<InvalidDataException>(() => new BagItBag(new MemoryStream(zip)));
  }

  [Test, Category("EdgeCase")]
  public void Given_ManifestEntryWithoutFile_When_Opened_Then_InvalidData() {
    var zip = Tamper(Vector("loc-md5.zip"), "loc-md5/data/readme.txt", _ => null);
    Assert.Throws<InvalidDataException>(() => new BagItBag(new MemoryStream(zip)));
  }

  [Test, Category("EdgeCase")]
  public void Given_ChangedTagFile_When_Opened_Then_InvalidData() {
    var zip = Tamper(Vector("loc-sha256-sha512.zip"), "loc-sha256-sha512/bag-info.txt", old => [.. old!, (byte)'\n']);
    Assert.Throws<InvalidDataException>(() => new BagItBag(new MemoryStream(zip)));
  }

  [Test, Category("EdgeCase")]
  public void Given_ZipWithoutBag_When_Opened_Then_InvalidData() {
    var zip = Tamper(Vector("loc-md5.zip"), "loc-md5/bagit.txt", _ => null);
    Assert.Throws<InvalidDataException>(() => new BagItBag(new MemoryStream(zip)));
  }

  [Test, Category("RoundTrip")]
  public void Given_Files_When_Created_Then_ValidBagWithShaManifests() {
    using var ms = new MemoryStream();
    new BagItFormatDescriptor().Create(ms, [ArchiveInputInfo.InMemory("a/b.txt", Readme), ArchiveInputInfo.InMemory("c.bin", [])], new FormatCreateOptions());
    var bag = new BagItBag(ms);
    Assert.That(bag.ManifestAlgorithms, Is.EqualTo(new[] { "sha512" }));
    Assert.That(bag.Payload.Select(p => p.Path), Is.EqualTo(new[] { "a/b.txt", "c.bin" }));
    Assert.That(bag.BagInfo, Does.Contain($"Payload-Oxum: {Readme.Length}.2"));
  }

  [Test, Category("RoundTrip")]
  public void Given_BagitPythonBag_When_Edited_Then_TagFieldsAndAlgorithmsKept() {
    var d = new BagItFormatDescriptor();
    using var ms = new MemoryStream();
    ms.Write(Vector("loc-sha256-sha512.zip"));
    d.Add(ms, [ArchiveInputInfo.InMemory("added.txt", "added\n"u8.ToArray())]);
    d.Remove(ms, ["docs/empty.bin"]);
    var bag = new BagItBag(ms);
    Assert.That(bag.RootName, Is.EqualTo("loc-sha256-sha512"));
    Assert.That(bag.ManifestAlgorithms, Is.EqualTo(new[] { "sha512", "sha256" }));
    Assert.That(bag.Payload.Select(p => p.Path), Is.EqualTo(new[] { "readme.txt", "docs/notes with space.txt", "added.txt" }));
    Assert.That(bag.BagInfo, Does.Contain("Contact-Name: Reference Bag").And.Contain($"Payload-Oxum: {Readme.Length + Notes.Length + 6}.3"));
  }

  [Test, Category("EdgeCase")]
  public void Given_PercentInName_When_Written_Then_EncodedPerRfc8493AndReadBack() {
    using var ms = new MemoryStream();
    BagItBag.Write(ms, [("100% done.txt", "x"u8.ToArray())], new DateTime(2024, 5, 6));
    var bag = new BagItBag(ms);
    Assert.That(bag.Payload.Single().Path, Is.EqualTo("100% done.txt"));
    using var zip = new ZipArchive(new MemoryStream(ms.ToArray()));
    using var manifest = new StreamReader(zip.GetEntry("bag/manifest-sha512.txt")!.Open());
    Assert.That(manifest.ReadToEnd(), Does.Contain("data/100%25 done.txt"));
  }

  // ours.zip and ours-edited.zip were checked by bagit-python's Bag.validate() (bagit-listing.txt).
  [TestCase("ours.zip")]
  [TestCase("ours-edited.zip")]
  [Category("ThirdPartyVector")]
  public void Given_OurBagThatBagitPythonValidated_When_Opened_Then_Valid(string vector) =>
    Assert.That(new BagItBag(new MemoryStream(Vector(vector))).Payload, Is.Not.Empty);
}
