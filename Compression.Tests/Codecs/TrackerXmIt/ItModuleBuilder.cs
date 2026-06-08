#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace Compression.Tests.Codecs.TrackerXmIt;

/// <summary>
/// Builds minimal but valid IT modules in-memory for engine tests: one instrument, one sample,
/// and explicit patterns. Layout matches ITTECH.TXT: fixed 192-byte header, order list,
/// instrument/sample/pattern offset tables, then the blocks concatenated.
/// </summary>
internal sealed class ItModuleBuilder {

  public int NewNoteAction = 1; // continue
  public int InitialSpeed = 6;
  public int InitialTempo = 125;
  public byte[] Orders = [0];

  // Each row is a list of (channel, note, instrument, command, param). note 255 = off.
  public List<List<(int Channel, int Note, int Instrument, int Command, int Param)>> Rows = [];

  public byte[] Build() {
    // Sizes.
    const int impiSize = 0x230 + 0x40; // generous instrument header
    const int impsSize = 80;
    const int sampleLen = 64;

    var header = new byte[192];
    var order = (byte[])Orders.Clone();

    var insOffsetsLen = 4;   // 1 instrument
    var smpOffsetsLen = 4;   // 1 sample
    var patOffsetsLen = 4;   // 1 pattern

    // Build the packed pattern body.
    var patBody = BuildPatternBody(out var rowCount);
    var patBlock = new byte[8 + patBody.Length];
    BinaryPrimitives.WriteUInt16LittleEndian(patBlock.AsSpan(0), (ushort)patBody.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(patBlock.AsSpan(2), (ushort)rowCount);
    patBody.CopyTo(patBlock, 8);

    var cursor = 192 + order.Length + insOffsetsLen + smpOffsetsLen + patOffsetsLen;
    var insOff = cursor; cursor += impiSize;
    var smpOff = cursor; cursor += impsSize;
    var patOff = cursor; cursor += patBlock.Length;
    var dataOff = cursor; cursor += sampleLen;
    var total = cursor;

    var buf = new byte[total];
    header.CopyTo(buf, 0);

    "IMPM"u8.ToArray().CopyTo(buf, 0);
    Encoding.ASCII.GetBytes("TEST").CopyTo(buf, 4);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(32), (ushort)order.Length); // OrdNum
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(34), 1); // InsNum
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(36), 1); // SmpNum
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(38), 1); // PatNum
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(42), 0x0214); // cmwt
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(44), 0x04);   // flags: instrument mode
    buf[48] = 128; // global vol
    buf[49] = 48;  // mix vol
    buf[50] = (byte)InitialSpeed;
    buf[51] = (byte)InitialTempo;
    for (var i = 0; i < 64; ++i) { buf[64 + i] = 32; buf[128 + i] = 64; } // centre pan, full vol

    order.CopyTo(buf, 192);
    var insTable = 192 + order.Length;
    var smpTable = insTable + insOffsetsLen;
    var patTable = smpTable + smpOffsetsLen;
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(insTable), (uint)insOff);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(smpTable), (uint)smpOff);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(patTable), (uint)patOff);

    // Instrument (IMPI).
    "IMPI"u8.ToArray().CopyTo(buf, insOff);
    Encoding.ASCII.GetBytes("Inst").CopyTo(buf, insOff + 32);
    buf[insOff + 0x11] = (byte)NewNoteAction; // NNA
    buf[insOff + 0x12] = 0; // DCT off
    buf[insOff + 0x18] = 128; // global vol
    buf[insOff + 0x19] = 0x20; // default pan centre, bit7 clear → don't use → fall through
    // Note→sample map: every note maps to sample 1, note unchanged.
    for (var n = 0; n < 120; ++n) {
      buf[insOff + 0x40 + n * 2] = (byte)n;     // note
      buf[insOff + 0x40 + n * 2 + 1] = 1;       // sample 1
    }

    // Sample (IMPS).
    "IMPS"u8.ToArray().CopyTo(buf, smpOff);
    Encoding.ASCII.GetBytes("Smp").CopyTo(buf, smpOff + 20);
    buf[smpOff + 17] = 64;        // global vol
    buf[smpOff + 18] = 0x01;      // flags: has data, 8-bit, uncompressed, no loop
    buf[smpOff + 19] = 64;        // default vol
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(smpOff + 48), sampleLen);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(smpOff + 60), 8363); // C5
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(smpOff + 72), (uint)dataOff);

    patBlock.CopyTo(buf, patOff);

    // Sample data: a simple ramp so a sounding voice is audibly non-zero.
    for (var i = 0; i < sampleLen; ++i)
      buf[dataOff + i] = unchecked((byte)((i * 4) - 128));

    return buf;
  }

  private byte[] BuildPatternBody(out int rowCount) {
    rowCount = Math.Max(Rows.Count, 1);
    var bytes = new List<byte>();
    foreach (var row in Rows) {
      foreach (var (channel, note, instrument, command, param) in row) {
        byte mask = 0;
        if (note >= 0) mask |= 0x01;
        if (instrument > 0) mask |= 0x02;
        if (command > 0) mask |= 0x08;
        bytes.Add((byte)((channel + 1) | 0x80));
        bytes.Add(mask);
        if ((mask & 0x01) != 0) bytes.Add((byte)note);
        if ((mask & 0x02) != 0) bytes.Add((byte)instrument);
        if ((mask & 0x08) != 0) { bytes.Add((byte)command); bytes.Add((byte)param); }
      }
      bytes.Add(0); // end of row
    }
    // Ensure at least one empty row terminator.
    if (Rows.Count == 0) bytes.Add(0);
    return bytes.ToArray();
  }
}
