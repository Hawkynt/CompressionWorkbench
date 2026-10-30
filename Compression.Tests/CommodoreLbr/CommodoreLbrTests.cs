using System.Reflection;
using System.Security.Cryptography;
using Compression.Registry;
using FileFormat.CommodoreLbr;

namespace Compression.Tests.CommodoreLbr;

/// <summary>
/// Commodore 64 LBR containers. Fixtures are real Library-built containers from the Archive Team
/// sample corpus; see ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class CommodoreLbrTests {
  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"CommodoreLbrVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded LBR vector '{name}' is missing.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static string Sha16(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data))[..16];

  [TestCase("chess.lbr", "CHESS|P|2961|0c5608f4880e72f8;CHESS DOCS|P|2506|a04b92a21daa29ec;CHESS2|P|4819|5864eeda62a65a86")]
  [TestCase("joplin.lbr", "MAPLE LEAF.MUS|P|2828|bab0b637c7a9943a;MOMENTS.MUS|P|2991|ec5c47c2534c3bd9;ENTERTAINER.MUS|P|3137|1246c71fdebe9ae2")]
  [TestCase("blkbox.lbr", "BLACK BOX|P|111|abf2485775fcc042;CHAR.SET|P|2050|9d35ba0ee8c12fda;SPRITES|P|963|c724c4b2c6e7b336;BB.OBJ|P|3396|0ef5074df2500b63;BLACK BOX DOCS|S|4282|ffed6ceec9bbfbfb")]
  [Category("ThirdPartyVector")]
  public void Given_LibraryContainer_When_Read_Then_MembersMatch(string vector, string expected) {
    var data = Vector(vector);
    var listing = string.Join(";", global::FileFormat.CommodoreLbr.CommodoreLbr.Read(data)
      .Select(e => $"{e.Name}|{e.FileType}|{e.Length}|{Sha16(data.AsSpan((int)e.Offset, (int)e.Length))}"));
    Assert.That(listing, Is.EqualTo(expected));
  }

  // Library itself wrote these; writing the same members back must reproduce its bytes exactly
  // (the files only add the 0x1A/0x00 padding of the XMODEM download they came from).
  [TestCase("chess.lbr")]
  [TestCase("joplin.lbr")]
  [TestCase("blkbox.lbr")]
  [Category("ThirdPartyVector")]
  public void Given_LibraryContainer_When_Rewritten_Then_ByteIdenticalUpToTransferPadding(string vector) {
    var data = Vector(vector);
    var entries = global::FileFormat.CommodoreLbr.CommodoreLbr.Read(data);
    var rewritten = global::FileFormat.CommodoreLbr.CommodoreLbr.Write(
      [.. entries.Select(e => (e.Name, e.FileType, data.AsSpan((int)e.Offset, (int)e.Length).ToArray()))]);
    Assert.That(rewritten, Is.EqualTo(data[..rewritten.Length]));
    Assert.That(data[rewritten.Length..], Is.All.EqualTo(0x1A).Or.EqualTo(0x00));
  }

  [Test, Category("RoundTrip")]
  public void Given_Files_When_CreatedAndListed_Then_PrgMembersInOrder() {
    var d = new CommodoreLbrFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [ArchiveInputInfo.InMemory("HELLO", [1, 8, 0x0B, 8]), ArchiveInputInfo.InMemory("WORLD", [])], new FormatCreateOptions());
    Assert.That(ms.ToArray()[..7], Is.EqualTo("DWB 2 \r"u8.ToArray()));
    ms.Position = 0;
    var list = d.List(ms, null);
    Assert.That(list.Select(e => (e.Name, e.OriginalSize, e.Method)), Is.EqualTo(new[] { ("HELLO", 4L, "Stored/P"), ("WORLD", 0L, "Stored/P") }));
  }

  [Test, Category("RoundTrip")]
  public void Given_RealContainer_When_MemberRemovedAndAdded_Then_OthersKeepTypeAndBytes() {
    var original = Vector("blkbox.lbr");
    var d = new CommodoreLbrFormatDescriptor();
    using var ms = new MemoryStream();
    ms.Write(original);
    d.Remove(ms, ["SPRITES"]);
    d.Add(ms, [ArchiveInputInfo.InMemory("NEW FILE", [1, 8, 0, 0])]);
    var data = ms.ToArray();
    var entries = global::FileFormat.CommodoreLbr.CommodoreLbr.Read(data);
    Assert.That(entries.Select(e => $"{e.Name}|{e.FileType}"), Is.EqualTo(new[] {
      "BLACK BOX|P", "CHAR.SET|P", "BB.OBJ|P", "BLACK BOX DOCS|S", "NEW FILE|P",
    }));
    var docs = entries.Single(e => e.Name == "BLACK BOX DOCS");
    Assert.That(Sha16(data.AsSpan((int)docs.Offset, (int)docs.Length)), Is.EqualTo("ffed6ceec9bbfbfb"));
  }

  [Test, Category("EdgeCase")]
  public void Given_NoSignature_When_Read_Then_InvalidData() =>
    Assert.Throws<InvalidDataException>(() => global::FileFormat.CommodoreLbr.CommodoreLbr.Read("LBR 1 \r"u8));

  [Test, Category("EdgeCase")]
  public void Given_MemberPastEnd_When_Read_Then_InvalidData() {
    var data = Vector("joplin.lbr");
    Assert.Throws<InvalidDataException>(() => global::FileFormat.CommodoreLbr.CommodoreLbr.Read(data.AsSpan(0, data.Length - 1)));
  }

  [Test, Category("EdgeCase")]
  public void Given_NonNumericSize_When_Read_Then_InvalidData() =>
    Assert.Throws<InvalidDataException>(() => global::FileFormat.CommodoreLbr.CommodoreLbr.Read("DWB 1 \rA\rP\r 1x \r"u8));

  [Test, Category("EdgeCase")]
  public void Given_UnterminatedDirectory_When_Read_Then_InvalidData() =>
    Assert.Throws<InvalidDataException>(() => global::FileFormat.CommodoreLbr.CommodoreLbr.Read("DWB 1 \rNAME"u8));

  [Test, Category("EdgeCase")]
  public void Given_RelType_When_Written_Then_Refused() =>
    Assert.Throws<ArgumentException>(() => global::FileFormat.CommodoreLbr.CommodoreLbr.Write([("DATA", 'R', [])]));
}
