#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Gettext;

/// <summary>
/// Parses a GNU gettext .mo binary catalog per
/// https://www.gnu.org/software/gettext/manual/html_node/MO-Files.html.
/// Magic <c>0x950412DE</c> (little-endian) or <c>0xDE120495</c> (big-endian / swapped).
/// Handles msgctxt (separator U+0004) and plural forms (separator U+0000).
/// </summary>
public sealed class MoReader {
  public List<CatalogEntry> Read(ReadOnlySpan<byte> data) {
    if (data.Length < 28)
      throw new InvalidDataException("MO file too short for header.");

    var first = BinaryPrimitives.ReadUInt32LittleEndian(data);
    bool bigEndian;
    if (first == 0x950412DEu) bigEndian = false;
    else if (first == 0xDE120495u) bigEndian = true;
    else throw new InvalidDataException($"Unrecognised MO magic 0x{first:X8}.");

    static uint R(ReadOnlySpan<byte> s, bool be) => be
      ? BinaryPrimitives.ReadUInt32BigEndian(s)
      : BinaryPrimitives.ReadUInt32LittleEndian(s);

    var numStrings = R(data[8..], bigEndian);
    var origTableOffset = R(data[12..], bigEndian);
    var transTableOffset = R(data[16..], bigEndian);

    if (origTableOffset + 8 * numStrings > data.Length ||
        transTableOffset + 8 * numStrings > data.Length)
      throw new InvalidDataException("MO string tables out of range.");

    var result = new List<CatalogEntry>((int)numStrings);
    for (var i = 0; i < numStrings; ++i) {
      var origLen = R(data[(int)(origTableOffset + 8 * i)..], bigEndian);
      var origOff = R(data[(int)(origTableOffset + 8 * i + 4)..], bigEndian);
      var tranLen = R(data[(int)(transTableOffset + 8 * i)..], bigEndian);
      var tranOff = R(data[(int)(transTableOffset + 8 * i + 4)..], bigEndian);

      if (origOff + origLen > data.Length || tranOff + tranLen > data.Length)
        throw new InvalidDataException($"MO entry {i} string out of range.");

      var orig = Encoding.UTF8.GetString(data.Slice((int)origOff, (int)origLen));
      var tran = Encoding.UTF8.GetString(data.Slice((int)tranOff, (int)tranLen));

      // Context separator is U+0004 (EOT). Plural separator within a string is NUL.
      string? context = null;
      var eot = orig.IndexOf('\u0004');
      if (eot >= 0) {
        context = orig[..eot];
        orig = orig[(eot + 1)..];
      }

      var origParts = orig.Split('\0');
      var tranParts = tran.Split('\0');

      result.Add(new CatalogEntry(
        Index: i,
        Context: context,
        MsgId: origParts[0],
        MsgIdPlural: origParts.Length > 1 ? origParts[1] : null,
        MsgStr: tranParts[0],
        MsgStrPlural: tranParts.Length > 1 ? tranParts : null
      ));
    }
    return result;
  }
}
