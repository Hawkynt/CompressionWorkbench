#pragma warning disable CS1591
namespace FileFormat.Matroska;

/// <summary>
/// Raw EBML (Extensible Binary Meta Language) element reader. IDs and size fields
/// are variable-width unsigned integers with a leading-bit marker; the body is
/// opaque bytes or a nested sequence of EBML elements depending on the schema.
/// </summary>
public sealed class EbmlReader {
  public readonly record struct Element(ulong Id, long BodyOffset, long BodyLength);

  private readonly byte[] _data;
  public EbmlReader(byte[] data) { _data = data; }

  /// <summary>Reads one element at <paramref name="pos"/>, advancing it past the element.</summary>
  public Element? Read(ref long pos) {
    if (pos >= _data.Length) return null;
    var idLen = VintLength(_data[(int)pos]);
    if (pos + idLen > _data.Length) return null;
    var id = ReadVint(pos, idLen, keepMarker: true);
    pos += idLen;

    if (pos >= _data.Length) return null;
    var sizeLen = VintLength(_data[(int)pos]);
    if (pos + sizeLen > _data.Length) return null;
    var size = ReadVint(pos, sizeLen, keepMarker: false);
    pos += sizeLen;

    var bodyOff = pos;
    // EBML allows "unknown size" (all-ones size) — treat as remaining file.
    var unknownSize = IsUnknownSize(size, sizeLen);
    var actualSize = unknownSize ? (ulong)(_data.Length - bodyOff) : size;
    pos += (long)actualSize;
    return new Element(id, bodyOff, (long)actualSize);
  }

  /// <summary>Iterates direct child elements of a master element body.</summary>
  public IEnumerable<Element> Children(Element master) {
    var pos = master.BodyOffset;
    var end = master.BodyOffset + master.BodyLength;
    while (pos < end) {
      var el = Read(ref pos);
      if (el == null) yield break;
      if (pos > end) yield break;
      yield return el.Value;
    }
  }

  public ReadOnlySpan<byte> Body(Element el) => _data.AsSpan((int)el.BodyOffset, (int)el.BodyLength);

  public ulong ReadUnsigned(Element el) {
    ulong v = 0;
    var body = Body(el);
    foreach (var b in body) v = (v << 8) | b;
    return v;
  }

  public long ReadSigned(Element el) {
    var body = Body(el);
    if (body.Length == 0) return 0;
    long v = (sbyte)body[0];
    for (var i = 1; i < body.Length; ++i) v = (v << 8) | body[i];
    return v;
  }

  public string ReadString(Element el) => System.Text.Encoding.UTF8.GetString(Body(el)).TrimEnd('\0');

  public byte[] ReadBinary(Element el) => Body(el).ToArray();

  private static int VintLength(byte first) {
    for (var i = 0; i < 8; ++i)
      if ((first & (0x80 >> i)) != 0) return i + 1;
    return 8;
  }

  private ulong ReadVint(long pos, int len, bool keepMarker) {
    ulong value = _data[(int)pos];
    if (!keepMarker) value &= (ulong)(0xFFu >> len); // clear the length-marker bit
    for (var i = 1; i < len; ++i)
      value = (value << 8) | _data[(int)(pos + i)];
    return value;
  }

  private static bool IsUnknownSize(ulong size, int len) {
    var mask = (1UL << (7 * len)) - 1;
    return size == mask;
  }
}
