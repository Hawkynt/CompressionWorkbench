#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace CompressionWorkbench.FileFormat.Ani;

/// <summary>
/// Reader for Windows animated cursor (<c>.ani</c>) files. ANI is a RIFF container
/// with the form type <c>"ACON"</c>; the animation data lives in a <c>LIST "fram"</c>
/// chunk that contains one <c>"icon"</c> subchunk per frame, each a full CUR file.
/// </summary>
/// <remarks>
/// <para>
/// Chunk layout (all IDs are 4 ASCII bytes, all sizes are 32-bit little-endian
/// counting the chunk body only — the ID + size header add 8 bytes):
/// </para>
/// <list type="bullet">
///   <item><c>RIFF</c> header + <c>ACON</c> form type (4 + 4 + 4 bytes).</item>
///   <item><c>anih</c> (36 bytes): animation header — <c>cbSize</c>, number of frames, number of steps, width/height/bpp (unused when ICON flag bit 0 is set), number of planes, default jiffies per step, flags.</item>
///   <item><c>LIST fram</c>: wraps the per-frame <c>icon</c> subchunks.</item>
///   <item><c>icon</c> (variable): one CUR file.</item>
///   <item><c>rate</c> (optional, 4 × nSteps bytes): per-step jiffies overriding the default from the header.</item>
///   <item><c>seq </c> (optional, 4 × nSteps bytes): step → frame-index map for non-linear animations.</item>
///   <item><c>LIST INFO</c>: optional ASCII metadata (title, author).</item>
/// </list>
/// </remarks>
public sealed class AniReader {

  public sealed record AnimationHeader(
    uint CbSize,
    uint NumFrames,
    uint NumSteps,
    uint Width,
    uint Height,
    uint BitsPerPixel,
    uint NumPlanes,
    uint DefaultJiffiesPerStep,
    uint Flags
  );

  public sealed record AniFile(
    AnimationHeader Header,
    IReadOnlyList<byte[]> Frames,       // each element is the raw bytes of an ICO/CUR sub-file
    IReadOnlyList<uint> Rates,          // per-step duration overrides (jiffies); empty when the chunk is absent
    IReadOnlyList<uint> Sequence,       // step → frame-index map; empty when linear
    string? Title,
    string? Artist
  );

  public static AniFile Read(ReadOnlySpan<byte> data) {
    if (data.Length < 12) throw new InvalidDataException("ANI: file shorter than RIFF header.");
    if (data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F')
      throw new InvalidDataException("ANI: missing RIFF header.");
    if (data[8] != 'A' || data[9] != 'C' || data[10] != 'O' || data[11] != 'N')
      throw new InvalidDataException("ANI: RIFF form type is not ACON.");

    AnimationHeader? anih = null;
    var frames = new List<byte[]>();
    var rates = new List<uint>();
    var sequence = new List<uint>();
    string? title = null;
    string? artist = null;

    var pos = 12;
    while (pos + 8 <= data.Length) {
      var chunkId = ReadFourCc(data, pos);
      var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 4)..]);
      var bodyStart = pos + 8;
      if (bodyStart + chunkSize > (uint)data.Length) break;
      var body = data.Slice(bodyStart, (int)chunkSize);

      switch (chunkId) {
        case "anih":
          anih = ParseAnih(body);
          break;
        case "LIST": {
          // LIST chunks start with a 4-byte form type followed by child chunks.
          if (body.Length < 4) break;
          var formType = ReadFourCc(body, 0);
          var listBody = body[4..];
          switch (formType) {
            case "fram":
              ParseFramList(listBody, frames);
              break;
            case "INFO":
              ParseInfoList(listBody, ref title, ref artist);
              break;
          }
          break;
        }
        case "rate":
          ReadUint32Array(body, rates);
          break;
        case "seq ":
          ReadUint32Array(body, sequence);
          break;
        // Ignore unknown chunks — the format is extensible.
      }

      // RIFF word-alignment: chunks are padded to an even byte count.
      pos = bodyStart + (int)((chunkSize + 1) & ~1u);
    }

    if (anih == null) throw new InvalidDataException("ANI: anih chunk missing.");

    return new AniFile(
      Header: anih,
      Frames: frames,
      Rates: rates,
      Sequence: sequence,
      Title: title,
      Artist: artist);
  }

  private static string ReadFourCc(ReadOnlySpan<byte> data, int pos)
    => Encoding.ASCII.GetString(data.Slice(pos, 4));

  private static AnimationHeader ParseAnih(ReadOnlySpan<byte> body) {
    if (body.Length < 36) throw new InvalidDataException("ANI: anih body smaller than 36 bytes.");
    return new AnimationHeader(
      CbSize: BinaryPrimitives.ReadUInt32LittleEndian(body),
      NumFrames: BinaryPrimitives.ReadUInt32LittleEndian(body[4..]),
      NumSteps: BinaryPrimitives.ReadUInt32LittleEndian(body[8..]),
      Width: BinaryPrimitives.ReadUInt32LittleEndian(body[12..]),
      Height: BinaryPrimitives.ReadUInt32LittleEndian(body[16..]),
      BitsPerPixel: BinaryPrimitives.ReadUInt32LittleEndian(body[20..]),
      NumPlanes: BinaryPrimitives.ReadUInt32LittleEndian(body[24..]),
      DefaultJiffiesPerStep: BinaryPrimitives.ReadUInt32LittleEndian(body[28..]),
      Flags: BinaryPrimitives.ReadUInt32LittleEndian(body[32..]));
  }

  private static void ParseFramList(ReadOnlySpan<byte> body, List<byte[]> frames) {
    var pos = 0;
    while (pos + 8 <= body.Length) {
      var childId = ReadFourCc(body, pos);
      var childSize = BinaryPrimitives.ReadUInt32LittleEndian(body[(pos + 4)..]);
      var bodyStart = pos + 8;
      if (bodyStart + childSize > (uint)body.Length) break;
      if (childId == "icon")
        frames.Add(body.Slice(bodyStart, (int)childSize).ToArray());
      pos = bodyStart + (int)((childSize + 1) & ~1u);
    }
  }

  private static void ParseInfoList(ReadOnlySpan<byte> body, ref string? title, ref string? artist) {
    var pos = 0;
    while (pos + 8 <= body.Length) {
      var id = ReadFourCc(body, pos);
      var size = BinaryPrimitives.ReadUInt32LittleEndian(body[(pos + 4)..]);
      var bodyStart = pos + 8;
      if (bodyStart + size > (uint)body.Length) break;
      var text = Encoding.ASCII.GetString(body.Slice(bodyStart, (int)size)).TrimEnd('\0', ' ');
      switch (id) {
        case "INAM": title = text; break;
        case "IART": artist = text; break;
      }
      pos = bodyStart + (int)((size + 1) & ~1u);
    }
  }

  private static void ReadUint32Array(ReadOnlySpan<byte> body, List<uint> dest) {
    for (var i = 0; i + 4 <= body.Length; i += 4)
      dest.Add(BinaryPrimitives.ReadUInt32LittleEndian(body[i..]));
  }
}
