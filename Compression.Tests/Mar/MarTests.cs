using System.Reflection;
using System.Security.Cryptography;
using Compression.Registry;
using FileFormat.Mar;

namespace Compression.Tests.Mar;

/// <summary>
/// Mozilla ARchive. Oracle: mardor (Mozilla release engineering's MAR library). The two
/// <c>test-*.mar</c> fixtures are mardor's own Thunderbird test packages (one signed); the
/// <c>mardor-*.mar</c> ones mardor wrote for this suite. <c>mardor-listing.txt</c> is mardor's view of
/// every fixture. See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class MarTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"MarVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded MAR vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static readonly byte[] ATxt = "hello mar\n"u8.ToArray();
  private static readonly byte[] BBin = [.. Enumerable.Range(0, 1024).Select(i => (byte)i)];

  private static IEnumerable<string[]> Listing()
    => System.Text.Encoding.ASCII.GetString(Vector("mardor-listing.txt"))
      .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
      .Select(l => l.Split(' '));

  [TestCase("test-xz.mar")]
  [TestCase("test-bz2.mar")]
  [TestCase("mardor-old.mar")]
  [TestCase("mardor-product.mar")]
  [TestCase("mardor-xz.mar")]
  [Category("ThirdPartyVector")]
  public void Given_MardorFixture_When_Read_Then_MatchesMardorListing(string vector) {
    var lines = Listing().Where(l => l[0] == vector || (l[0] == "#" && l[1] == vector)).ToList();
    var header = lines.Single(l => l[0] == "#");
    var expectedEntries = lines.Where(l => l[0] != "#").ToList();

    var r = new MarReader(new MemoryStream(Vector(vector)));
    var sigs = int.Parse(header[2]["sigs=".Length..]);
    Assert.That(r.HasSignatureBlock, Is.EqualTo(sigs >= 0));
    Assert.That(r.Signatures, Has.Count.EqualTo(Math.Max(sigs, 0)));
    var product = header[3]["product=".Length..];
    Assert.That(r.ProductInformation is { } pi ? $"{pi.Channel}|{pi.ProductVersion}" : "-", Is.EqualTo(product));

    Assert.That(r.Entries.Select(e => e.Name), Is.EqualTo(expectedEntries.Select(l => l[1])));
    for (var i = 0; i < r.Entries.Count; ++i) {
      var e = r.Entries[i];
      Assert.That(e.Size, Is.EqualTo(uint.Parse(expectedEntries[i][2])), e.Name);
      Assert.That(Convert.ToString(e.Flags, 8), Is.EqualTo(expectedEntries[i][3]), e.Name);
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(r.Extract(e))), Is.EqualTo(expectedEntries[i][4]), e.Name);
    }
  }

  [TestCase("test-xz.mar", "xz")]
  [TestCase("test-bz2.mar", "bzip2")]
  [TestCase("mardor-product.mar", "stored")]
  [Category("ThirdPartyVector")]
  public void Given_UpdatePackage_When_Listed_Then_MethodNamesTheMemberCompression(string vector, string method) {
    var list = new MarFormatDescriptor().List(new MemoryStream(Vector(vector)), null);
    Assert.That(list.Select(e => e.Method).Distinct(), Is.EqualTo(new[] { method }));
  }

  [TestCase("mardor-old.mar", false)]
  [TestCase("mardor-product.mar", true)]
  [Category("ThirdPartyVector")]
  public void Given_MardorInputs_When_WeWrite_Then_ByteIdenticalToMardor(string vector, bool product) {
    using var ms = new MemoryStream();
    MarWriter.Write(ms, [("a.txt", ATxt, 0x1B6u), ("dir/b.bin", BBin, 0x1B6u)],
      product ? [MarWriter.ProductInformation("release", "130.0")] : null);
    Assert.That(ms.ToArray(), Is.EqualTo(Vector(vector)));
  }

  [Test, Category("RoundTrip")]
  public void Given_ProductOptions_When_Created_Then_ReadBackWithProductBlock() {
    var d = new MarFormatDescriptor();
    using var ms = new MemoryStream();
    var options = new FormatCreateOptions { FormatSpecific = new() { ["channel"] = "beta", ["productversion"] = "131.0b1" } };
    d.Create(ms, [ArchiveInputInfo.InMemory("a.txt", ATxt), ArchiveInputInfo.InMemory("dir/b.bin", BBin)], options);
    ms.Position = 0;
    var r = new MarReader(ms);
    Assert.That(r.ProductInformation, Is.EqualTo(("beta", "131.0b1")));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(BBin));
    Assert.That(r.DeclaredFileSize, Is.EqualTo((ulong)ms.Length));
  }

  [Test, Category("EdgeCase")]
  public void Given_OnlyChannel_When_Created_Then_Throws() {
    var options = new FormatCreateOptions { FormatSpecific = new() { ["channel"] = "beta" } };
    Assert.Throws<ArgumentException>(() => new MarFormatDescriptor().Create(new MemoryStream(), [], options));
  }

  [Test, Category("EdgeCase")]
  public void Given_NoFiles_When_Created_Then_EmptyListing() {
    var d = new MarFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [], new FormatCreateOptions());
    Assert.That(ms.Length, Is.EqualTo(12));
    ms.Position = 0;
    Assert.That(d.List(ms, null), Is.Empty);
  }

  [Test, Category("RoundTrip")]
  public void Given_UnsignedProductMar_When_AddedAndRemoved_Then_ProductBlockAndFlagsSurvive() {
    var d = new MarFormatDescriptor();
    using var ms = new MemoryStream();
    ms.Write(Vector("mardor-product.mar"));
    ((IArchiveModifiable)d).Add(ms, [ArchiveInputInfo.InMemory("c.txt", "added"u8.ToArray())]);
    ((IArchiveModifiable)d).Remove(ms, ["a.txt"]);
    ms.Position = 0;
    var r = new MarReader(ms);
    Assert.That(r.ProductInformation, Is.EqualTo(("release", "130.0")));
    Assert.That(r.Entries.Select(e => e.Name), Is.EqualTo(new[] { "dir/b.bin", "c.txt" }));
    Assert.That(r.Entries[0].Flags, Is.EqualTo(0x1B6u));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(BBin));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo("added"u8.ToArray()));
  }

  [Test, Category("EdgeCase")]
  public void Given_SignedMar_When_Modified_Then_RefusedAndUntouched() {
    var original = Vector("test-bz2.mar");
    using var ms = new MemoryStream();
    ms.Write(original);
    var d = (IArchiveModifiable)new MarFormatDescriptor();
    Assert.Throws<NotSupportedException>(() => d.Remove(ms, ["update.manifest"]));
    Assert.Throws<NotSupportedException>(() => d.Add(ms, [ArchiveInputInfo.InMemory("x", [1])]));
    Assert.That(ms.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("EdgeCase")]
  public void Given_WrongMagic_When_Read_Then_InvalidData() {
    var bytes = Vector("mardor-old.mar");
    bytes[3] = (byte)'2';
    Assert.Throws<InvalidDataException>(() => _ = new MarReader(new MemoryStream(bytes)));
  }

  [Test, Category("EdgeCase")]
  public void Given_IndexOffsetPastTheEnd_When_Read_Then_InvalidData() {
    var bytes = Vector("mardor-old.mar");
    bytes[4] = 0x7F;
    Assert.Throws<InvalidDataException>(() => _ = new MarReader(new MemoryStream(bytes)));
  }

  [Test, Category("EdgeCase")]
  public void Given_EntryPointingIntoTheIndex_When_Read_Then_InvalidData() {
    var bytes = Vector("mardor-old.mar");
    // first index entry's size field (index at 0x412: UInt32 indexSize, then offset, size)
    bytes[0x412 + 4 + 4 + 2] = 0xFF;
    Assert.Throws<InvalidDataException>(() => _ = new MarReader(new MemoryStream(bytes)));
  }

  [Test, Category("EdgeCase")]
  public void Given_TruncatedFile_When_Read_Then_InvalidData() {
    var bytes = Vector("mardor-product.mar")[..20];
    Assert.Catch<Exception>(() => _ = new MarReader(new MemoryStream(bytes)));
  }
}
