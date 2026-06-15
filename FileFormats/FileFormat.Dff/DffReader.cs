#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Dff;

/// <summary>
/// Philips DSDIFF (<c>.dff</c>/<c>.dsdiff</c>) parser. The container is IFF-like with
/// big-endian unsigned 64-bit chunk sizes; chunk bodies pad to an even byte boundary. Layout:
/// <list type="bullet">
///   <item>Top form: <c>FRM8</c> | u64 size | formType <c>DSD&#160;</c> | sub-chunks.</item>
///   <item><c>FVER</c> — u32 version.</item>
///   <item><c>PROP</c> with property type <c>SND&#160;</c> containing: <c>FS&#160;&#160;</c>
///         (u32 sample rate), <c>CHNL</c> (u16 numChannels + numChannels × 4-char channel IDs),
///         <c>CMPR</c> (4-char compression type + pascal-string name; <c>DSD&#160;</c> =
///         uncompressed, <c>DST&#160;</c> = DST-compressed).</item>
///   <item><c>DSD&#160;</c> data chunk — sample data interleaved by byte round-robin across
///         channels (one byte ch0, one byte ch1, …), bits MSB-first within each byte.</item>
/// </list>
/// </summary>
public sealed class DffReader {

  public sealed record ParsedDff(
    int SampleRate,
    int NumChannels,
    IReadOnlyList<string> ChannelIds,
    string Compression,
    byte[][] ChannelDsd,
    long BytesPerChannel);

  public ParsedDff Read(ReadOnlySpan<byte> data) {
    if (data.Length < 16 || !data[..4].SequenceEqual("FRM8"u8))
      throw new InvalidDataException("Missing 'FRM8' magic.");

    var formSize = (long)BinaryPrimitives.ReadUInt64BigEndian(data[4..]);
    if (!data.Slice(12, 4).SequenceEqual("DSD "u8))
      throw new InvalidDataException("FRM8 form type is not 'DSD '.");

    var formEnd = Math.Min(data.Length, 12 + (int)formSize);
    var pos = 16; // after FRM8 + size + 'DSD ' form type

    var sampleRate = 0;
    var compression = "DSD ";
    var channelIds = new List<string>();
    byte[] dsdPayload = [];

    while (pos + 12 <= formEnd) {
      var ckId = Encoding.ASCII.GetString(data.Slice(pos, 4));
      var ckSize = (long)BinaryPrimitives.ReadUInt64BigEndian(data.Slice(pos + 4));
      var bodyStart = pos + 12;
      if (bodyStart + ckSize > data.Length)
        ckSize = data.Length - bodyStart;
      var body = data.Slice(bodyStart, (int)ckSize);

      switch (ckId) {
        case "PROP":
          ParseProp(body, ref sampleRate, ref compression, channelIds);
          break;
        case "DSD ":
          dsdPayload = body.ToArray();
          break;
      }

      // Advance past body, padding to an even boundary.
      var advance = ckSize + (ckSize & 1);
      pos = bodyStart + (int)advance;
    }

    var numChannels = channelIds.Count > 0 ? channelIds.Count : 1;

    // 'DST ' (or any non-'DSD ') compression: we cannot de-interleave coded data.
    var isUncompressed = compression.TrimEnd() == "DSD";
    byte[][] channels;
    long bytesPerChannel;
    if (isUncompressed && dsdPayload.Length > 0) {
      bytesPerChannel = dsdPayload.Length / numChannels;
      channels = new byte[numChannels][];
      for (var c = 0; c < numChannels; ++c)
        channels[c] = new byte[bytesPerChannel];
      for (long i = 0; i < bytesPerChannel * numChannels; ++i) {
        var c = (int)(i % numChannels);
        channels[c][i / numChannels] = dsdPayload[i];
      }
    } else {
      channels = [];
      bytesPerChannel = 0;
    }

    return new ParsedDff(sampleRate, numChannels, channelIds, compression, channels, bytesPerChannel);
  }

  private static void ParseProp(ReadOnlySpan<byte> body, ref int sampleRate, ref string compression, List<string> channelIds) {
    if (body.Length < 4 || !body[..4].SequenceEqual("SND "u8))
      return;

    var pos = 4;
    while (pos + 12 <= body.Length) {
      var ckId = Encoding.ASCII.GetString(body.Slice(pos, 4));
      var ckSize = (long)BinaryPrimitives.ReadUInt64BigEndian(body.Slice(pos + 4));
      var bodyStart = pos + 12;
      if (bodyStart + ckSize > body.Length)
        ckSize = body.Length - bodyStart;
      var sub = body.Slice(bodyStart, (int)ckSize);

      switch (ckId) {
        case "FS  ":
          if (sub.Length >= 4)
            sampleRate = (int)BinaryPrimitives.ReadUInt32BigEndian(sub);
          break;
        case "CHNL":
          if (sub.Length >= 2) {
            var n = BinaryPrimitives.ReadUInt16BigEndian(sub);
            for (var i = 0; i < n && 2 + i * 4 + 4 <= sub.Length; ++i)
              channelIds.Add(Encoding.ASCII.GetString(sub.Slice(2 + i * 4, 4)));
          }
          break;
        case "CMPR":
          if (sub.Length >= 4)
            compression = Encoding.ASCII.GetString(sub.Slice(0, 4));
          break;
      }

      var advance = ckSize + (ckSize & 1);
      pos = bodyStart + (int)advance;
    }
  }
}
