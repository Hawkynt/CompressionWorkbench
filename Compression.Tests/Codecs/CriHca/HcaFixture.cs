#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.CriHca;

namespace Compression.Tests.Codecs.CriHca;

/// <summary>
/// Builds minimal but byte-valid CRI HCA streams for tests: a CRC-checked header
/// (<c>fmt</c> + <c>comp</c> + optional <c>ciph</c>) followed by fixed-size frames. The
/// default frame carries all-zero scalefactors (delta-bits = 0), which makes every
/// resolution 0, so no spectral coefficients are read and every sub-frame decodes to
/// exact silence — letting tests assert deterministic 1024-samples/frame/channel output.
/// All multi-byte header/frame fields are big-endian and bit-packed MSB-first to match
/// the decoder.
/// </summary>
internal static class HcaFixture {

  /// <summary>
  /// Produces a valid silence HCA. <paramref name="maskMagic"/> applies the per-byte
  /// 0x80 mask to the four header magic bytes (the keyed-stream obfuscation form);
  /// <paramref name="cipherType"/> writes a <c>ciph</c> chunk (0 = none, 1 = static, 56 = keyed).
  /// </summary>
  public static byte[] BuildSilence(int channels = 1, int sampleRate = 44100, int frameCount = 2,
      bool maskMagic = false, int? cipherType = null, int version = 0x0200) {
    const int totalBand = 16;
    var header = BuildHeader(channels, sampleRate, frameCount, totalBand, cipherType, version, maskMagic, out var frameSize);

    var file = new byte[header.Length + frameCount * frameSize];
    header.CopyTo(file, 0);

    var frame = BuildSilenceFrame(channels, frameSize);
    // A type-1 cipher frame must, once decrypted, equal the silence frame; the stored
    // frame is therefore the inverse-mapped (re-encrypted) silence. For type 0 it's stored as-is.
    if (cipherType == 1) {
      var table = HcaCodec.CipherInit(1);
      var inverse = new byte[256];
      for (var i = 0; i < 256; i++)
        inverse[table[i]] = (byte)i;
      for (var i = 0; i < frame.Length; i++)
        frame[i] = inverse[frame[i]];
    }

    for (var f = 0; f < frameCount; f++)
      frame.CopyTo(file, header.Length + f * frameSize);

    return file;
  }

  private static byte[] BuildHeader(int channels, int sampleRate, int frameCount, int totalBand,
      int? cipherType, int version, bool maskMagic, out int frameSize) {
    frameSize = 0x100; // ample fixed frame size

    // Lay out chunks, then fix up header size + CRC. Header size is multiple needs the
    // trailing 2-byte CRC included.
    var w = new ChunkWriter();
    w.Bytes("HCA\0"u8);
    w.U16((ushort)version);
    var headerSizePos = w.Position;
    w.U16(0); // header size placeholder

    w.Bytes("fmt\0"u8);
    w.U8((byte)channels);
    w.U24((uint)sampleRate);
    w.U32((uint)frameCount);
    w.U16(0); // encoder delay
    w.U16(0); // encoder padding

    w.Bytes("comp"u8);
    w.U16((ushort)frameSize);
    w.U8(1);                    // min resolution
    w.U8(15);                   // max resolution
    w.U8(1);                    // track count
    w.U8(0);                    // channel config
    w.U8((byte)totalBand);      // total band count
    w.U8((byte)totalBand);      // base band count (no stereo/HFR bands)
    w.U8(0);                    // stereo band count
    w.U8(0);                    // bands per HFR group
    w.U8(0);                    // ms stereo
    w.U8(0);                    // reserved

    if (cipherType is { } ct) {
      w.Bytes("ciph"u8);
      w.U16((ushort)ct);
    }

    var headerSize = w.Position + 2; // include the 2-byte CRC that follows
    var buf = w.ToArray(headerSize);
    BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(headerSizePos), (ushort)headerSize);

    // A keyed HCA stores its magic with the per-byte 0x80 mask set; the header CRC is
    // computed over those stored (masked) bytes. Mask before checksumming.
    if (maskMagic)
      for (var i = 0; i < 4; i++)
        buf[i] |= 0x80;

    var crc = HcaCodec.Crc16(buf.AsSpan(0, headerSize - 2));
    BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(headerSize - 2), crc);
    return buf;
  }

  private static byte[] BuildSilenceFrame(int channels, int frameSize) {
    var frame = new byte[frameSize];
    var bw = new BitWriter(frame);

    bw.Write(0xFFFF, 16);  // sync
    bw.Write(0, 9);        // packed noise level (hi)
    bw.Write(0, 7);        // packed noise level (lo)
    for (var c = 0; c < channels; c++)
      bw.Write(0, 3);      // delta_bits = 0 → scalefactors cleared → silence

    // CRC-16 over the whole frame must be zero: the last two bytes hold the checksum.
    var crc = HcaCodec.Crc16(frame.AsSpan(0, frameSize - 2));
    BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(frameSize - 2), crc);
    return frame;
  }

  // ── tiny big-endian byte / MSB-first bit writers ──────────────────────────

  private sealed class ChunkWriter {
    private readonly List<byte> _bytes = new();
    public int Position => this._bytes.Count;
    public void Bytes(ReadOnlySpan<byte> b) { foreach (var x in b) this._bytes.Add(x); }
    public void U8(byte v) => this._bytes.Add(v);
    public void U16(ushort v) { this._bytes.Add((byte)(v >> 8)); this._bytes.Add((byte)v); }
    public void U24(uint v) { this._bytes.Add((byte)(v >> 16)); this._bytes.Add((byte)(v >> 8)); this._bytes.Add((byte)v); }
    public void U32(uint v) {
      this._bytes.Add((byte)(v >> 24)); this._bytes.Add((byte)(v >> 16));
      this._bytes.Add((byte)(v >> 8)); this._bytes.Add((byte)v);
    }
    public byte[] ToArray(int totalSize) {
      var buf = new byte[totalSize];
      this._bytes.CopyTo(buf);
      return buf;
    }
  }

  private sealed class BitWriter {
    private readonly byte[] _buf;
    private int _bitPos;
    public BitWriter(byte[] buf) => this._buf = buf;
    public void Write(int value, int bits) {
      for (var i = bits - 1; i >= 0; i--) {
        var bit = (value >> i) & 1;
        if (bit != 0)
          this._buf[this._bitPos >> 3] |= (byte)(1 << (7 - (this._bitPos & 7)));
        this._bitPos++;
      }
    }
  }
}
