#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Swav;

/// <summary>
/// Builds a Nintendo DS <c>.swav</c> sample from mono PCM16. Only the lossless PCM16 wave type
/// (<c>1</c>) is written, so the result round-trips exactly through <see cref="SwavReader"/>.
/// </summary>
public sealed class SwavWriter {

  /// <summary>Writes a non-looping PCM16 SWAV at <paramref name="sampleRate"/>.</summary>
  public byte[] Write(short[] pcm, int sampleRate) {
    ArgumentNullException.ThrowIfNull(pcm);

    var sampleData = new byte[pcm.Length * 2];
    for (var i = 0; i < pcm.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(sampleData.AsSpan(i * 2), pcm[i]);

    // DATA block payload = SWAVINFO(12) + sample bytes.
    var dataPayload = 12 + sampleData.Length;
    var dataBlockSize = 8 + dataPayload;       // includes "DATA" marker + size field
    var fileSize = 0x10 + dataBlockSize;       // NDS header(0x10) + full DATA block

    var buffer = new byte[fileSize];
    var s = buffer.AsSpan();

    "SWAV"u8.CopyTo(s);
    BinaryPrimitives.WriteUInt16LittleEndian(s[4..], 0xFEFF);   // BOM
    BinaryPrimitives.WriteUInt16LittleEndian(s[6..], 0x0100);   // version
    BinaryPrimitives.WriteUInt32LittleEndian(s[8..], (uint)fileSize);
    BinaryPrimitives.WriteUInt16LittleEndian(s[12..], 0x10);    // headerSize
    BinaryPrimitives.WriteUInt16LittleEndian(s[14..], 1);       // numBlocks

    "DATA"u8.CopyTo(s[0x10..]);
    BinaryPrimitives.WriteUInt32LittleEndian(s[0x14..], (uint)dataBlockSize);

    // SWAVINFO.
    s[0x18] = 1;                                                // waveType = PCM16
    s[0x19] = 0;                                                // loop = false
    BinaryPrimitives.WriteUInt16LittleEndian(s[0x1A..], (ushort)sampleRate);
    BinaryPrimitives.WriteUInt16LittleEndian(s[0x1C..], (ushort)(sampleRate == 0 ? 0 : 16756710 / sampleRate)); // time
    BinaryPrimitives.WriteUInt16LittleEndian(s[0x1E..], 0);     // loopOffset (words)
    BinaryPrimitives.WriteUInt32LittleEndian(s[0x20..], (uint)(sampleData.Length / 4)); // nonLoopLength (words)

    sampleData.CopyTo(s[0x24..]);
    return buffer;
  }
}
