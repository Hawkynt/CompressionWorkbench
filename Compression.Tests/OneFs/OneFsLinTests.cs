using FileSystem.OneFs;

namespace Compression.Tests.OneFs;

/// <summary>
/// Pins only OneFS LIN representations Dell documents publicly: the 64-bit
/// hexadecimal CLI value and the little-endian 8-byte NFS-filehandle field.
/// </summary>
[TestFixture]
public sealed class OneFsLinTests {

  [Test, Category("Interop")]
  public void TryParse_ParsesDellPublishedIsiGetDisplayLin() {
    Assert.That(OneFsLin.TryParse("1:2d29:4204", out var lin), Is.True);

    Assert.Multiple(() => {
      Assert.That(lin.Value, Is.EqualTo(0x000000012d294204UL));
      Assert.That(lin.ToDisplayString(), Is.EqualTo("1:2d29:4204"));
      Assert.That(lin.ToLookupString(), Is.EqualTo("000000012d294204"));
      Assert.That(lin.ToString(), Is.EqualTo("1:2d29:4204"));
    });
  }

  [Test, Category("Interop")]
  public void FileHandleBytes_RoundTripDellPublishedNfsLin() {
    byte[] encoded = [0x4f, 0x02, 0x55, 0x02, 0x01, 0x00, 0x00, 0x00];

    var lin = OneFsLin.FromFileHandleBytes(encoded);
    var roundTrip = new byte[8];
    lin.WriteFileHandleBytes(roundTrip);

    Assert.Multiple(() => {
      Assert.That(lin.Value, Is.EqualTo(0x000000010255024fUL));
      Assert.That(lin.ToLookupString(), Is.EqualTo("000000010255024f"));
      Assert.That(lin.ToDisplayString(), Is.EqualTo("1:0255:024f"));
      Assert.That(roundTrip, Is.EqualTo(encoded));
    });
  }

  [Test, Category("HappyPath")]
  public void TryParse_AcceptsContiguousIsiGetLookupForm() {
    Assert.That(OneFsLin.TryParse("000000010255024f", out var lin), Is.True);
    Assert.That(lin.Value, Is.EqualTo(0x000000010255024fUL));
  }

  [Test, Category("Interop")]
  public void FileHandleBytes_ParseDellPublishedRootLin() {
    byte[] encodedRootLin = [0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    var lin = OneFsLin.FromFileHandleBytes(encodedRootLin);

    Assert.Multiple(() => {
      Assert.That(lin.Value, Is.EqualTo(2UL));
      Assert.That(lin.ToLookupString(), Is.EqualTo("0000000000000002"));
      Assert.That(lin.ToDisplayString(), Is.EqualTo("0:0000:0002"));
    });
  }

  [TestCase("")]
  [TestCase(":")]
  [TestCase("1:")]
  [TestCase(":1")]
  [TestCase("1::0001")]
  [TestCase("1:2d29")]
  [TestCase("1:2:0001")]
  [TestCase("1:0001:2")]
  [TestCase("123456789:0001:0002")]
  [TestCase("1:2d29:4204:0000")]
  [TestCase("0x000000010255024f")]
  [TestCase("000000010255024fg")]
  [TestCase("10000000000000000")]
  [Category("Malformed")]
  public void TryParse_RejectsMalformedLin(string text)
    => Assert.That(OneFsLin.TryParse(text, out _), Is.False);

  [Test, Category("Malformed")]
  public void FileHandleEncoding_RequiresExactlyEightInputBytesAndEnoughOutputSpace() {
    Assert.Multiple(() => {
      Assert.That(
        () => OneFsLin.FromFileHandleBytes(new byte[7]),
        Throws.TypeOf<ArgumentException>());
      Assert.That(
        () => new OneFsLin(1).WriteFileHandleBytes(new byte[7]),
        Throws.TypeOf<ArgumentException>());
    });
  }
}
