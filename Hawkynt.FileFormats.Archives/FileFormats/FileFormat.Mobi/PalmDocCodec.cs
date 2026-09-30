namespace FileFormat.Mobi;

/// <summary>
/// PalmDOC's byte-oriented LZ77 variant (MobileRead "PalmDOC" page) and the MOBI text-record
/// framing around it: each record decompresses on its own, and MOBI 6+ books may append trailing
/// entries to every text record (MobileRead "MOBI", "Trailing entries").
/// </summary>
internal static class PalmDocCodec {
  public static byte[] Encode(ReadOnlySpan<byte> source) {
    var data = source.ToArray();
    using var output = new MemoryStream();
    var literals = new List<byte>(8);
    var positions = new Dictionary<int, int>();
    var index = 0;

    void FlushLiterals() {
      for (var offset = 0; offset < literals.Count;) {
        var length = Math.Min(8, literals.Count - offset);
        output.WriteByte((byte)length);
        for (var i = 0; i < length; ++i)
          output.WriteByte(literals[offset + i]);
        offset += length;
      }
      literals.Clear();
    }

    while (index < data.Length) {
      var bestLength = 0;
      var bestDistance = 0;
      if (index + 2 < data.Length) {
        var key = (data[index] << 16) | (data[index + 1] << 8) | data[index + 2];
        if (positions.TryGetValue(key, out var candidate)) {
          var distance = index - candidate;
          if (distance <= 2047) {
            var length = 3;
            while (length < 10 && index + length < data.Length && data[index + length] == data[index + length - distance])
              ++length;
            bestLength = length;
            bestDistance = distance;
          }
        }
      }

      if (bestLength >= 3) {
        FlushLiterals();
        var pair = (bestDistance << 3) | (bestLength - 3);
        output.WriteByte((byte)(0x80 | ((pair >> 8) & 0x3f)));
        output.WriteByte((byte)pair);
        for (var i = 0; i < bestLength; ++i)
          AddPosition(index + i);
        index += bestLength;
      } else {
        var value = data[index];
        if (value is >= 0x09 and <= 0x7f) {
          FlushLiterals();
          output.WriteByte(value);
        } else {
          literals.Add(value);
          if (literals.Count == 8) FlushLiterals();
        }
        AddPosition(index++);
      }
    }
    FlushLiterals();
    return output.ToArray();

    void AddPosition(int position) {
      if (position + 2 >= data.Length) return;
      var key = (data[position] << 16) | (data[position + 1] << 8) | data[position + 2];
      positions[key] = position;
    }
  }

  /// <summary>Largest output one text record may decode to. Records hold at most 4096 bytes of
  /// text; the headroom only bounds a hostile record (a back-reference expands 2 bytes to 10).</summary>
  internal const int MaxRecordOutput = 5 * 4096;

  /// <summary>Decodes one PalmDOC record completely.</summary>
  public static byte[] Decode(ReadOnlySpan<byte> source) {
    var output = new byte[Math.Min(MaxRecordOutput, checked(source.Length * 5))];
    var input = 0;
    var written = 0;
    while (input < source.Length) {
      var value = source[input++];
      switch (value) {
        case 0:
          Put(1)[0] = 0;
          break;
        case <= 8:
          if (input + value > source.Length)
            throw new InvalidDataException("Truncated PalmDOC literal run.");
          source.Slice(input, value).CopyTo(Put(value));
          input += value;
          break;
        case <= 0x7f:
          Put(1)[0] = value;
          break;
        case <= 0xbf: {
          if (input >= source.Length)
            throw new InvalidDataException("Truncated PalmDOC back-reference.");
          var pair = (value << 8) | source[input++];
          var distance = (pair >> 3) & 0x7ff;
          var length = (pair & 7) + 3;
          if (distance == 0 || distance > written)
            throw new InvalidDataException("Invalid PalmDOC back-reference.");
          var start = written;
          var target = Put(length);
          for (var i = 0; i < length; ++i)
            target[i] = output[start + i - distance];
          break;
        }
        default: {
          var target = Put(2);
          target[0] = (byte)' ';
          target[1] = (byte)(value ^ 0x80);
          break;
        }
      }
    }
    return output.AsSpan(0, written).ToArray();

    Span<byte> Put(int count) {
      if (written + count > output.Length)
        throw new InvalidDataException($"PalmDOC record expands past {MaxRecordOutput} bytes.");
      var span = output.AsSpan(written, count);
      written += count;
      return span;
    }
  }

  /// <summary>
  /// Strips the trailing entries <paramref name="extraDataFlags"/> announces from one text record.
  /// Entries follow the text in bit order, so the highest bit's entry is at the very end: bits 1-15
  /// each end in their own size (a backward-encoded variable-width integer counting the whole entry),
  /// and bit 0, the multibyte-overlap entry, ends in a byte whose low two bits count the overlapping
  /// bytes before it.
  /// </summary>
  public static ReadOnlySpan<byte> StripTrailingEntries(ReadOnlySpan<byte> record, ushort extraDataFlags) {
    var end = record.Length;
    for (var bit = 15; bit >= 1; --bit) {
      if ((extraDataFlags & (1 << bit)) == 0)
        continue;
      var size = 0;
      var shift = 0;
      var pos = end;
      while (true) {
        if (pos == 0 || shift > 21)
          throw new InvalidDataException($"Trailing entry {bit} has no terminated size.");
        var b = record[--pos];
        size |= (b & 0x7F) << shift;
        shift += 7;
        if ((b & 0x80) != 0)
          break;
      }
      if (size < end - pos || size > end)
        throw new InvalidDataException($"Trailing entry {bit} declares {size} bytes, which does not fit the record.");
      end -= size;
    }
    if ((extraDataFlags & 1) != 0) {
      if (end == 0)
        throw new InvalidDataException("The multibyte-overlap entry is missing.");
      var overlap = (record[end - 1] & 3) + 1;
      if (overlap > end)
        throw new InvalidDataException("The multibyte-overlap entry is longer than the record.");
      end -= overlap;
    }
    return record[..end];
  }

  public static byte[] DecodeRecords(IReadOnlyList<byte[]> records, int compression, int expectedLength, ushort extraDataFlags = 0) {
    if (compression is not (1 or 2))
      throw new NotSupportedException($"PalmDOC compression type {compression} is not supported (HUFF/CDIC and proprietary codecs are not decoded).");

    var output = new byte[expectedLength];
    var written = 0;
    foreach (var record in records) {
      var body = StripTrailingEntries(record, extraDataFlags);
      ReadOnlySpan<byte> text = compression == 1 ? body : Decode(body);
      if (text.Length > expectedLength - written)
        throw new InvalidDataException("Text records are longer than the declared text length.");
      text.CopyTo(output.AsSpan(written));
      written += text.Length;
    }
    if (written != expectedLength)
      throw new InvalidDataException("Text records are shorter than the declared text length.");
    return output;
  }
}
