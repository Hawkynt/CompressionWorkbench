using System.Buffers.Binary;

namespace Compression.Core.Dictionary.Nintendo;

/// <summary>Shared match finder and wire codecs for Nintendo Yaz0/Yay0 LZ compression.</summary>
internal static class NintendoLzCodecs {
  private const int WindowSize = 4096;
  private const int MinMatch = 3;
  private const int MaxMatch = 273;
  private const int HashSize = 1 << 14;
  private const int HashMask = HashSize - 1;
  private const int MaxChain = 4096;

  internal static byte[] CompressYaz0(ReadOnlySpan<byte> source) {
    var data = source.ToArray();
    var tokens = Tokenize(data);
    using var output = new MemoryStream();

    Span<byte> header = stackalloc byte[16];
    "Yaz0"u8.CopyTo(header);
    BinaryPrimitives.WriteUInt32BigEndian(header[4..], checked((uint)data.Length));
    output.Write(header);

    for (var index = 0; index < tokens.Count;) {
      var control = 0;
      using var payload = new MemoryStream();
      for (var bit = 7; bit >= 0 && index < tokens.Count; --bit, ++index) {
        var token = tokens[index];
        if (token.IsLiteral) {
          control |= 1 << bit;
          payload.WriteByte(token.Literal);
          continue;
        }
        WriteYaz0Reference(payload, token.Distance, token.Length);
      }
      output.WriteByte((byte)control);
      payload.WriteTo(output);
    }

    return output.ToArray();
  }

  internal static byte[] DecompressYaz0(ReadOnlySpan<byte> data) {
    if (data.Length < 16 || !data[..4].SequenceEqual("Yaz0"u8))
      throw new InvalidDataException("Not a valid Yaz0 stream.");

    var declared = BinaryPrimitives.ReadUInt32BigEndian(data[4..8]);
    if (declared > int.MaxValue)
      throw new InvalidDataException("Yaz0 output is too large for an in-memory building block.");
    var output = new byte[(int)declared];
    var input = 16;
    var written = 0;

    while (written < output.Length) {
      if (input >= data.Length)
        throw new InvalidDataException("Yaz0 stream ends before the declared output length.");
      var control = data[input++];
      for (var bit = 7; bit >= 0 && written < output.Length; --bit) {
        if ((control & 1 << bit) != 0) {
          if (input >= data.Length)
            throw new InvalidDataException("Yaz0 stream ends inside a literal.");
          output[written++] = data[input++];
          continue;
        }

        if (input + 2 > data.Length)
          throw new InvalidDataException("Yaz0 stream ends inside a back-reference.");
        var first = data[input++];
        var second = data[input++];
        var distance = ((first & 0x0F) << 8 | second) + 1;
        var length = first >> 4;
        if (length == 0) {
          if (input >= data.Length)
            throw new InvalidDataException("Yaz0 stream ends before a long-match length.");
          length = data[input++] + 0x12;
        } else {
          length += 2;
        }
        CopyReference(output, ref written, distance, length, "Yaz0");
      }
    }

    return output;
  }

  internal static byte[] CompressYay0(ReadOnlySpan<byte> source) {
    var data = source.ToArray();
    var tokens = Tokenize(data);
    var maskWords = (tokens.Count + 31) / 32;
    var masks = new uint[maskWords];
    using var links = new MemoryStream();
    using var chunks = new MemoryStream();

    Span<byte> linkBytes = stackalloc byte[2];
    for (var index = 0; index < tokens.Count; ++index) {
      var token = tokens[index];
      if (token.IsLiteral) {
        masks[index / 32] |= 1u << (31 - index % 32);
        chunks.WriteByte(token.Literal);
        continue;
      }

      var distance = token.Distance - 1;
      var lengthNibble = token.Length <= 17 ? token.Length - 2 : 0;
      var link = (ushort)(lengthNibble << 12 | distance);
      BinaryPrimitives.WriteUInt16BigEndian(linkBytes, link);
      links.Write(linkBytes);
      if (lengthNibble == 0)
        chunks.WriteByte((byte)(token.Length - 0x12));
    }

    var linkOffset = checked(16 + masks.Length * 4);
    var chunkOffset = checked(linkOffset + (int)links.Length);
    using var output = new MemoryStream();
    Span<byte> header = stackalloc byte[16];
    "Yay0"u8.CopyTo(header);
    BinaryPrimitives.WriteUInt32BigEndian(header[4..], checked((uint)data.Length));
    BinaryPrimitives.WriteUInt32BigEndian(header[8..], checked((uint)linkOffset));
    BinaryPrimitives.WriteUInt32BigEndian(header[12..], checked((uint)chunkOffset));
    output.Write(header);

    Span<byte> maskBytes = stackalloc byte[4];
    foreach (var mask in masks) {
      BinaryPrimitives.WriteUInt32BigEndian(maskBytes, mask);
      output.Write(maskBytes);
    }
    links.Position = 0;
    links.CopyTo(output);
    chunks.Position = 0;
    chunks.CopyTo(output);
    return output.ToArray();
  }

  internal static byte[] DecompressYay0(ReadOnlySpan<byte> data) {
    if (data.Length < 16 || !data[..4].SequenceEqual("Yay0"u8))
      throw new InvalidDataException("Not a valid Yay0 stream.");

    var declared = BinaryPrimitives.ReadUInt32BigEndian(data[4..8]);
    if (declared > int.MaxValue)
      throw new InvalidDataException("Yay0 output is too large for an in-memory building block.");
    var linkOffset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[8..12]));
    var chunkOffset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[12..16]));
    if (linkOffset < 16 || linkOffset > chunkOffset || chunkOffset > data.Length)
      throw new InvalidDataException("Yay0 table offsets are outside the stream.");

    var output = new byte[(int)declared];
    var maskPosition = 16;
    var linkPosition = linkOffset;
    var chunkPosition = chunkOffset;
    var written = 0;
    uint mask = 0;
    var maskBits = 0;

    while (written < output.Length) {
      if (maskBits == 0) {
        if (maskPosition + 4 > linkOffset)
          throw new InvalidDataException("Yay0 mask table ends before the declared output length.");
        mask = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(maskPosition, 4));
        maskPosition += 4;
        maskBits = 32;
      }

      if ((mask & 0x80000000u) != 0) {
        if (chunkPosition >= data.Length)
          throw new InvalidDataException("Yay0 literal table ends inside the stream.");
        output[written++] = data[chunkPosition++];
      } else {
        if (linkPosition + 2 > chunkOffset)
          throw new InvalidDataException("Yay0 link table ends inside a back-reference.");
        var link = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(linkPosition, 2));
        linkPosition += 2;
        var distance = (link & 0x0FFF) + 1;
        var length = link >> 12;
        if (length == 0) {
          if (chunkPosition >= data.Length)
            throw new InvalidDataException("Yay0 chunk table ends before a long-match length.");
          length = data[chunkPosition++] + 0x12;
        } else {
          length += 2;
        }
        CopyReference(output, ref written, distance, length, "Yay0");
      }

      mask <<= 1;
      --maskBits;
    }

    return output;
  }

  private static void WriteYaz0Reference(Stream output, int distance, int length) {
    var encodedDistance = distance - 1;
    if (length <= 17) {
      output.WriteByte((byte)((length - 2) << 4 | encodedDistance >> 8));
      output.WriteByte((byte)encodedDistance);
      return;
    }
    output.WriteByte((byte)(encodedDistance >> 8));
    output.WriteByte((byte)encodedDistance);
    output.WriteByte((byte)(length - 0x12));
  }

  private static void CopyReference(byte[] output, ref int written, int distance, int length, string format) {
    var source = written - distance;
    if (source < 0)
      throw new InvalidDataException($"{format} back-reference points before the output buffer.");
    if (length > output.Length - written)
      throw new InvalidDataException($"{format} back-reference expands past the declared output length.");
    for (var i = 0; i < length; ++i)
      output[written++] = output[source + i];
  }

  private static List<Token> Tokenize(byte[] data) {
    var result = new List<Token>(data.Length);
    if (data.Length == 0)
      return result;

    var head = new int[HashSize];
    var previous = new int[data.Length];
    Array.Fill(head, -1);
    Array.Fill(previous, -1);

    for (var position = 0; position < data.Length;) {
      var (distance, length) = FindMatch(data, position, head, previous);
      if (length >= MinMatch) {
        result.Add(Token.Reference(distance, length));
        for (var i = 0; i < length; ++i)
          UpdateHash(data, position + i, head, previous);
        position += length;
      } else {
        result.Add(Token.LiteralByte(data[position]));
        UpdateHash(data, position, head, previous);
        ++position;
      }
    }
    return result;
  }

  private static (int Distance, int Length) FindMatch(byte[] data, int position, int[] head, int[] previous) {
    if (position + MinMatch > data.Length)
      return (0, 0);

    var hash = Hash3(data, position);
    var minimum = Math.Max(0, position - WindowSize);
    var maximumLength = Math.Min(MaxMatch, data.Length - position);
    var bestLength = 0;
    var bestDistance = 0;
    var candidate = head[hash];

    for (var walked = 0; candidate >= minimum && candidate >= 0 && walked < MaxChain; ++walked) {
      var length = 0;
      while (length < maximumLength && data[candidate + length] == data[position + length])
        ++length;
      if (length > bestLength) {
        bestLength = length;
        bestDistance = position - candidate;
        if (length == maximumLength)
          break;
      }
      candidate = previous[candidate];
    }

    return bestLength >= MinMatch ? (bestDistance, bestLength) : (0, 0);
  }

  private static void UpdateHash(byte[] data, int position, int[] head, int[] previous) {
    if (position + MinMatch > data.Length)
      return;
    var hash = Hash3(data, position);
    previous[position] = head[hash];
    head[hash] = position;
  }

  private static int Hash3(byte[] data, int position)
    => ((data[position] << 6) ^ (data[position + 1] << 3) ^ data[position + 2]) & HashMask;

  private readonly record struct Token(bool IsLiteral, byte Literal, int Distance, int Length) {
    internal static Token LiteralByte(byte value) => new(true, value, 0, 0);
    internal static Token Reference(int distance, int length) => new(false, 0, distance, length);
  }
}
