using System.Buffers.Binary;

namespace Compression.Core.Transforms;

/// <summary>
/// BCJ2 filter for 7z: splits x86 code into 4 sub-streams.
/// </summary>
public static class Bcj2Filter {
  private const int NumMoveBits = 5;

  /// <summary>
  /// Decodes BCJ2-filtered data from 4 input streams.
  /// </summary>
  public static byte[] Decode(ReadOnlySpan<byte> mainStream, ReadOnlySpan<byte> callStream,
    ReadOnlySpan<byte> jumpStream, ReadOnlySpan<byte> rangeStream, int outputSize) {
    var output = new byte[outputSize];
    if (rangeStream.Length < 5)
      throw new InvalidDataException("BCJ2 range stream too short.");

    var range = 0xFFFFFFFF;
    uint code = 0;
    var rcPos = 1;
    for (var i = 0; i < 4; ++i)
      code = (code << 8) | (rcPos < rangeStream.Length ? rangeStream[rcPos++] : 0u);

    var probs = new ushort[2 * 256];
    probs.AsSpan().Fill(1024);

    int mainPos = 0, callPos = 0, jumpPos = 0, outPos = 0;
    byte prevByte = 0;
    while (outPos < outputSize) {
      if (mainPos >= mainStream.Length) break;
      var b = mainStream[mainPos++];
      if (b != 0xE8 && b != 0xE9) {
        output[outPos++] = b;
        prevByte = b;
        continue;
      }

      var probIndex = (b == 0xE8 ? 0 : 256) + prevByte;
      ref var prob = ref probs[probIndex];
      var bound = (range >> 11) * prob;
      bool isInstruction;
      if (code < bound) {
        range = bound;
        prob += (ushort)((2048 - prob) >> Bcj2Filter.NumMoveBits);
        isInstruction = false;
      } else {
        range -= bound;
        code -= bound;
        prob -= (ushort)(prob >> Bcj2Filter.NumMoveBits);
        isInstruction = true;
      }
      
      while (range < 0x01000000) {
        range <<= 8;
        code = (code << 8) | (rcPos < rangeStream.Length ? rangeStream[rcPos++] : 0u);
      }
      
      if (!isInstruction) {
        output[outPos++] = b;
        prevByte = b;
        continue;
      }

      output[outPos++] = b;
      uint target;
      if (b == 0xE8) {
        if (callPos + 4 > callStream.Length)
          throw new InvalidDataException("BCJ2 call stream underflow.");

        target = BinaryPrimitives.ReadUInt32LittleEndian(callStream[callPos..]);
        callPos += 4;
      } else {
        if (jumpPos + 4 > jumpStream.Length)
          throw new InvalidDataException("BCJ2 jump stream underflow.");

        target = BinaryPrimitives.ReadUInt32LittleEndian(jumpStream[jumpPos..]);
        jumpPos += 4;
      }
      var currentPos = (uint)(outPos + 4);
      var relTarget = target - currentPos;
      if (outPos + 4 > outputSize)
        throw new InvalidDataException("BCJ2 output overflow.");

      BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), relTarget);
      outPos += 4;
      prevByte = (byte)(relTarget >> 24);
    }
    return output;
  }

  /// <summary>
  /// Encodes data using the BCJ2 filter, producing 4 output streams.
  /// </summary>
  public static (byte[] Main, byte[] Call, byte[] Jump, byte[] Range) Encode(ReadOnlySpan<byte> data) {
    var main = new List<byte>();
    var call = new List<byte>();
    var jump = new List<byte>();
    var rcOutput = new MemoryStream();
    var rcRange = 0xFFFFFFFF;
    ulong rcLow = 0;
    var rcCacheSize = 1;
    byte rcCache = 0;
    var probs = new ushort[2 * 256];
    probs.AsSpan().Fill(1024);

    byte prevByte = 0;
    var i = 0;
    while (i < data.Length) {
      var b = data[i];
      if (b != 0xE8 && b != 0xE9 || i + 4 >= data.Length) {
        main.Add(b);
        prevByte = b;
        ++i;
        continue;
      }

      var rel = BinaryPrimitives.ReadInt32LittleEndian(data[(i + 1)..]);
      var absTarget = rel + (i + 5);
      var isReal = absTarget >= 0 && absTarget < data.Length;
      var probIndex = (b == 0xE8 ? 0 : 256) + prevByte;
      if (isReal) {
        RcEncodeBit(ref probs[probIndex], true);
        main.Add(b);
        var absTargetU = (uint)absTarget;
        var t0 = (byte)absTargetU;
        var t1 = (byte)(absTargetU >> 8);
        var t2 = (byte)(absTargetU >> 16);
        var t3 = (byte)(absTargetU >> 24);
        if (b == 0xE8) {
          call.Add(t0); 
          call.Add(t1); 
          call.Add(t2); 
          call.Add(t3);
        } else {
          jump.Add(t0); 
          jump.Add(t1); 
          jump.Add(t2); 
          jump.Add(t3);
        }
        prevByte = data[i + 4];
        i += 5;
      } else {
        RcEncodeBit(ref probs[probIndex], false);
        main.Add(b);
        prevByte = b;
        ++i;
      }
    }

    for (var f = 0; f < 5; ++f) 
      RcShiftLow();

    return ([.. main], [.. call], [.. jump], rcOutput.ToArray());

    void RcEncodeBit(ref ushort prob, bool bit) {
      var bound = (rcRange >> 11) * prob;
      if (!bit) {
        rcRange = bound;
        prob += (ushort)((2048 - prob) >> Bcj2Filter.NumMoveBits);
      } else {
        rcRange -= bound;
        rcLow += bound;
        prob -= (ushort)(prob >> Bcj2Filter.NumMoveBits);
      }
      RcNormalize();
    }

    void RcNormalize() {
      while (rcRange < 0x01000000) {
        rcRange <<= 8;
        RcShiftLow();
      }
    }

    void RcShiftLow() {
      if ((uint)rcLow < 0xFF000000u || (rcLow >> 32) != 0) {
        var temp = rcCache;
        do {
          rcOutput.WriteByte((byte)(temp + (byte)(rcLow >> 32)));
          temp = 0xFF;
        } while (--rcCacheSize > 0);
        rcCache = (byte)((uint)rcLow >> 24);
      }
      ++rcCacheSize;
      rcLow = (uint)(rcLow << 8);
    }
  }
}
