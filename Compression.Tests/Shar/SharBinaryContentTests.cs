#pragma warning disable CS1591
using FileFormat.Shar;

namespace Compression.Tests.Shar;

/// <summary>
/// A shar archive has to give back the bytes it was given.
/// </summary>
/// <remarks>
/// <para>A shar writes text as a here-document and anything else uuencoded, and
/// deciding which is what makes it safe. The test for "anything else" looked only
/// for NUL and the control characters, so a file made of bytes with the high bit
/// set was written as text — through <c>Encoding.UTF8.GetString</c>, which turns
/// every byte that is not valid UTF-8 into the replacement character. It came
/// back three times its length and made entirely of <c>EF BF BD</c>: every byte
/// lost, while the archive was written and read without an error.</para>
///
/// <para>Only the first eight kilobytes were examined, too, so a file that began
/// as text and turned binary later was written as text and mangled from the point
/// where it turned.</para>
/// </remarks>
[TestFixture]
public class SharBinaryContentTests {

  private static byte[] RoundTrip(string name, byte[] data) {
    var writer = new SharWriter();
    writer.AddFile(name, data);

    using var image = new MemoryStream(writer.ToByteArray());
    var reader = new SharReader(image);
    var entry = reader.Entries.FirstOrDefault(e =>
      string.Equals(Path.GetFileName(e.FileName), name, StringComparison.Ordinal));
    Assert.That(entry, Is.Not.Null, $"'{name}' is not in the archive");
    return entry!.Data;
  }

  /// <summary>Every byte value, one file per value, held for a whole run.</summary>
  [TestCase(0x01), TestCase(0x29), TestCase(0x51), TestCase(0x79)]
  [TestCase(0x80), TestCase(0xA1), TestCase(0xC9), TestCase(0xFF)]
  [Category("Regression")]
  public void AFileOfOneRepeatedByte_ComesBackUnchanged(int value) {
    var data = new byte[20_000];
    Array.Fill(data, (byte)value);

    var got = RoundTrip("SAME.BIN", data);
    Assert.That(got.Length, Is.EqualTo(data.Length),
      $"a file of 0x{value:X2} came back {got.Length} bytes instead of {data.Length}");
    Assert.That(got, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("Regression")]
  public void AFileThatTurnsBinaryPastTheFirstEightKilobytes_ComesBackUnchanged() {
    // Text at the top, binary further in than the old check ever looked.
    var data = new byte[24_000];
    for (var i = 0; i < 10_000; ++i) data[i] = (byte)('A' + i % 26);
    for (var i = 10_000; i < data.Length; ++i) data[i] = (byte)(0x80 + i % 0x7F);

    Assert.That(RoundTrip("MIXED.BIN", data), Is.EqualTo(data).AsCollection);
  }

  [Test, Category("Regression")]
  public void PlainTextStillTravelsAsText() {
    // The here-document path is the point of a shar; widening what counts as
    // binary must not send ordinary text down the uuencoded one.
    var text = System.Text.Encoding.ASCII.GetBytes(
      string.Join('\n', Enumerable.Range(0, 200).Select(i => $"line {i} of a perfectly ordinary file")));

    var writer = new SharWriter();
    writer.AddFile("NOTES.TXT", text);
    var archive = System.Text.Encoding.ASCII.GetString(writer.ToByteArray());

    Assert.That(archive, Does.Contain("SHAR_EOF"), "text should still travel as a here-document");
    Assert.That(archive, Does.Not.Contain("uudecode"), "text should not be uuencoded");
    Assert.That(RoundTrip("NOTES.TXT", text), Is.EqualTo(text).AsCollection);
  }
}
