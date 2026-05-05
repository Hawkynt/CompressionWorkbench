#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Gif;

/// <summary>
/// Decodes a multi-frame GIF87a/GIF89a file into composed RGBA32 frames. Each
/// frame is a snapshot of the logical screen after applying the frame's disposal
/// method and rendering the new image atop the canvas — i.e. what an animated
/// GIF viewer would display at that step. This is the input form expected by
/// the colorspace splitter pipeline.
/// </summary>
/// <remarks>
/// Spec references:
/// <list type="bullet">
///   <item>GIF89a Specification (CompuServe, 1990) — block layout, GCE,
///   Image Descriptor, sub-blocks, transparent index, disposal methods.</item>
///   <item>GIF89a Appendix F — variable-width LSB-first LZW with explicit
///   clear/EOI codes and code-width that grows when <c>nextCode == 1 &lt;&lt; currentBits</c>
///   (off-by-one warning: width grows BEFORE the next code is read).</item>
/// </list>
/// Disposal methods supported: 0 (no disposal — leave canvas), 1 (do not dispose —
/// leave canvas), 2 (restore to background), 3 (restore to previous). Methods 4–7
/// are reserved and treated as 0.
/// </remarks>
public sealed class GifPixelDecoder {

  /// <summary>One composed frame as RGBA32 pixels with width/height.</summary>
  public readonly record struct DecodedFrame(int Width, int Height, byte[] Rgba32, int DelayMs);

  // GIF block markers.
  private const byte BlockExtension = 0x21;
  private const byte BlockImageDescriptor = 0x2C;
  private const byte BlockTrailer = 0x3B;
  private const byte ExtGraphicControl = 0xF9;

  /// <summary>Decodes <paramref name="data"/> into composed RGBA32 frames.</summary>
  public List<DecodedFrame> Decode(ReadOnlySpan<byte> data) {
    if (data.Length < 13 || data[0] != 'G' || data[1] != 'I' || data[2] != 'F')
      throw new InvalidDataException("Not a GIF file.");

    var canvasW = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6, 2));
    var canvasH = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8, 2));
    var packed = data[10];
    var bgIndex = data[11];
    var hasGlobalCt = (packed & 0x80) != 0;
    var globalCtSize = hasGlobalCt ? 3 * (1 << ((packed & 0x07) + 1)) : 0;
    var pos = 13;
    byte[] globalCt = Array.Empty<byte>();
    if (hasGlobalCt) {
      if (pos + globalCtSize > data.Length) throw new InvalidDataException("Truncated GCT.");
      globalCt = data.Slice(pos, globalCtSize).ToArray();
      pos += globalCtSize;
    }

    // Logical screen canvas in RGBA32.
    var canvas = new byte[canvasW * canvasH * 4];
    // Initial background fill: opaque if a global CT + valid bg index, else transparent.
    if (hasGlobalCt && bgIndex < globalCt.Length / 3) {
      var br = globalCt[bgIndex * 3];
      var bg = globalCt[bgIndex * 3 + 1];
      var bb = globalCt[bgIndex * 3 + 2];
      for (var i = 0; i < canvasW * canvasH; i++) {
        canvas[i * 4] = br; canvas[i * 4 + 1] = bg; canvas[i * 4 + 2] = bb; canvas[i * 4 + 3] = 0;
      }
    }
    // (else: all zero — RGBA(0,0,0,0) transparent. Suits frame composition.)

    var frames = new List<DecodedFrame>();
    int gceDelay = 0;
    int gceTransparent = -1;
    int gceDisposal = 0;

    while (pos < data.Length) {
      var marker = data[pos];
      if (marker == BlockTrailer) break;

      if (marker == BlockExtension) {
        if (pos + 2 > data.Length) throw new InvalidDataException("Truncated extension.");
        var label = data[pos + 1];
        pos += 2;
        if (label == ExtGraphicControl) {
          // GCE: 4 1 packed delay*2 transp 0
          if (pos + 6 > data.Length) throw new InvalidDataException("Truncated GCE.");
          // pos points at sub-block length byte (=4)
          var blockSize = data[pos];
          if (blockSize != 4) throw new InvalidDataException("Bad GCE block size.");
          var gPacked = data[pos + 1];
          gceDelay = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos + 2, 2)) * 10;
          var transparentIndex = data[pos + 4];
          gceTransparent = (gPacked & 0x01) != 0 ? transparentIndex : -1;
          gceDisposal = (gPacked >> 2) & 0x07;
          pos += 5; // block contents
          if (data[pos] != 0) throw new InvalidDataException("GCE missing terminator.");
          pos++;
        } else {
          // Skip sub-blocks of any other extension.
          SkipSubBlocks(data, ref pos);
        }
        continue;
      }

      if (marker != BlockImageDescriptor)
        throw new InvalidDataException($"Unknown GIF block 0x{marker:X2} @ {pos}.");

      // Image Descriptor: separator(1) left(2) top(2) width(2) height(2) packed(1)
      if (pos + 10 > data.Length) throw new InvalidDataException("Truncated Image Descriptor.");
      var fLeft = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos + 1, 2));
      var fTop = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos + 3, 2));
      var fW = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos + 5, 2));
      var fH = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos + 7, 2));
      var fPacked = data[pos + 9];
      var hasLocalCt = (fPacked & 0x80) != 0;
      var interlaced = (fPacked & 0x40) != 0;
      var localCtSize = hasLocalCt ? 3 * (1 << ((fPacked & 0x07) + 1)) : 0;
      pos += 10;
      var palette = hasLocalCt
        ? data.Slice(pos, localCtSize).ToArray()
        : globalCt;
      pos += localCtSize;

      // LZW minimum code size, then sub-blocks.
      if (pos >= data.Length) throw new InvalidDataException("Truncated image data.");
      var lzwMin = data[pos++];
      var lzwBytes = ReadSubBlocks(data, ref pos);

      // Snapshot canvas BEFORE drawing for "restore to previous" (disposal=3).
      byte[]? previous = gceDisposal == 3 ? (byte[])canvas.Clone() : null;

      // Decode LZW indices then composite.
      var indices = LzwDecode(lzwBytes, lzwMin, fW * fH);
      Composite(canvas, canvasW, canvasH, fLeft, fTop, fW, fH, interlaced, indices, palette, gceTransparent);

      // Emit a copy of the post-composition canvas as the visible frame.
      frames.Add(new DecodedFrame(canvasW, canvasH, (byte[])canvas.Clone(), gceDelay));

      // Apply disposal for the NEXT frame.
      switch (gceDisposal) {
        case 2: // restore to background
          var bgR = (hasGlobalCt && bgIndex * 3 + 2 < globalCt.Length) ? globalCt[bgIndex * 3] : (byte)0;
          var bgG = (hasGlobalCt && bgIndex * 3 + 2 < globalCt.Length) ? globalCt[bgIndex * 3 + 1] : (byte)0;
          var bgB = (hasGlobalCt && bgIndex * 3 + 2 < globalCt.Length) ? globalCt[bgIndex * 3 + 2] : (byte)0;
          for (var y = 0; y < fH; y++) {
            for (var x = 0; x < fW; x++) {
              var cy = fTop + y; var cx = fLeft + x;
              if ((uint)cy >= (uint)canvasH || (uint)cx >= (uint)canvasW) continue;
              var idx = (cy * canvasW + cx) * 4;
              canvas[idx] = bgR; canvas[idx + 1] = bgG; canvas[idx + 2] = bgB; canvas[idx + 3] = 0;
            }
          }
          break;
        case 3: // restore to previous
          if (previous != null) Array.Copy(previous, canvas, canvas.Length);
          break;
      }

      // Reset GCE state so it only applies to the next image descriptor that has one.
      gceTransparent = -1;
      gceDelay = 0;
      gceDisposal = 0;
    }

    if (frames.Count == 0)
      throw new InvalidDataException("GIF contains no image frames.");
    return frames;
  }

  // ============================================================ helpers

  private static void SkipSubBlocks(ReadOnlySpan<byte> data, ref int pos) {
    while (pos < data.Length) {
      var len = data[pos++];
      if (len == 0) return;
      if (pos + len > data.Length) throw new InvalidDataException("Truncated sub-block.");
      pos += len;
    }
    throw new InvalidDataException("Unterminated sub-block chain.");
  }

  private static byte[] ReadSubBlocks(ReadOnlySpan<byte> data, ref int pos) {
    using var ms = new MemoryStream();
    while (pos < data.Length) {
      var len = data[pos++];
      if (len == 0) return ms.ToArray();
      if (pos + len > data.Length) throw new InvalidDataException("Truncated image sub-block.");
      ms.Write(data.Slice(pos, len));
      pos += len;
    }
    throw new InvalidDataException("Unterminated image sub-block chain.");
  }

  /// <summary>
  /// Decodes GIF LZW: variable-width LSB-first codes, explicit clear/EOI, code
  /// width grows when <c>nextCode == (1 &lt;&lt; currentBits)</c> after appending
  /// a new dictionary entry. Output is byte indices into the active palette.
  /// </summary>
  private static byte[] LzwDecode(byte[] compressed, int lzwMin, int expected) {
    var minBits = lzwMin + 1;
    var clearCode = 1 << lzwMin;
    var eoiCode = clearCode + 1;
    var firstUsable = eoiCode + 1;

    var dict = new List<byte[]>(4096);
    void InitDict() {
      dict.Clear();
      for (var i = 0; i < clearCode; i++) dict.Add([(byte)i]);
      dict.Add([]); // clear placeholder
      dict.Add([]); // EOI placeholder
    }
    InitDict();

    var output = new List<byte>(expected);
    var currentBits = minBits;
    var maxBits = 12;
    var maxDict = 1 << maxBits;

    // LSB-first bit reader over compressed bytes.
    var bitBuf = 0;
    var bitCount = 0;
    var bp = 0;
    int ReadCode() {
      while (bitCount < currentBits) {
        if (bp >= compressed.Length) return -1;
        bitBuf |= compressed[bp++] << bitCount;
        bitCount += 8;
      }
      var code = bitBuf & ((1 << currentBits) - 1);
      bitBuf >>= currentBits;
      bitCount -= currentBits;
      return code;
    }

    byte[]? prev = null;
    while (true) {
      var code = ReadCode();
      if (code < 0) break;
      if (code == eoiCode) break;
      if (code == clearCode) {
        InitDict();
        currentBits = minBits;
        prev = null;
        continue;
      }

      byte[] entry;
      if (code < dict.Count) {
        entry = dict[code];
      } else if (code == dict.Count && prev != null) {
        // KwKwK
        entry = new byte[prev.Length + 1];
        prev.CopyTo(entry, 0);
        entry[^1] = prev[0];
      } else {
        // Malformed — stop gracefully so a partially decoded GIF still produces something.
        break;
      }

      output.AddRange(entry);

      if (prev != null && dict.Count < maxDict) {
        var added = new byte[prev.Length + 1];
        prev.CopyTo(added, 0);
        added[^1] = entry[0];
        dict.Add(added);
        // Width grows once dict size HAS reached 2^currentBits and we still have headroom.
        if (dict.Count == (1 << currentBits) && currentBits < maxBits)
          currentBits++;
      }
      prev = entry;
      if (output.Count >= expected) break;
    }
    return output.ToArray();
  }

  private static void Composite(
    byte[] canvas, int canvasW, int canvasH,
    int left, int top, int frameW, int frameH, bool interlaced,
    byte[] indices, byte[] palette, int transparent
  ) {
    // Interlaced row order: 0,8,16,...; 4,12,20,...; 2,6,10,...; 1,3,5,...
    var rowOrder = new int[frameH];
    if (interlaced) {
      var r = 0;
      for (var y = 0; y < frameH; y += 8) rowOrder[r++] = y;
      for (var y = 4; y < frameH; y += 8) rowOrder[r++] = y;
      for (var y = 2; y < frameH; y += 4) rowOrder[r++] = y;
      for (var y = 1; y < frameH; y += 2) rowOrder[r++] = y;
    } else {
      for (var y = 0; y < frameH; y++) rowOrder[y] = y;
    }

    for (var py = 0; py < frameH; py++) {
      var fy = rowOrder[py];
      var cy = top + fy;
      if ((uint)cy >= (uint)canvasH) continue;
      for (var fx = 0; fx < frameW; fx++) {
        var cx = left + fx;
        if ((uint)cx >= (uint)canvasW) continue;
        var pi = py * frameW + fx;
        if (pi >= indices.Length) return; // truncated
        var idx = indices[pi];
        if (idx == transparent) continue; // leave existing canvas pixel alone
        if (idx * 3 + 2 >= palette.Length) continue;
        var co = (cy * canvasW + cx) * 4;
        canvas[co] = palette[idx * 3];
        canvas[co + 1] = palette[idx * 3 + 1];
        canvas[co + 2] = palette[idx * 3 + 2];
        canvas[co + 3] = 0xFF;
      }
    }
  }
}
