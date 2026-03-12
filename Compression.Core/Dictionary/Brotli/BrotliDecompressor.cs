using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Compression.Core.Dictionary.Brotli;

/// <summary>
///   Decompresses data in the Brotli format (RFC 7932).
/// </summary>
/// <remarks>
///   Brotli uses LZ77 with Huffman prefix codes, context-dependent literal
///   coding, and an optional static dictionary. This implementation supports
///   all meta-block types and context modes.
/// </remarks>
public static class BrotliDecompressor {
  /// <summary>Pre-computed literal context lookup tables, one per context mode.</summary>
  private static readonly byte[][] ContextLookup = [
    BrotliConstants.BuildLsb6ContextLookup(),
    BrotliConstants.BuildMsb6ContextLookup(),
    BrotliConstants.BuildUtf8ContextLookup(),
    BrotliConstants.BuildSignedContextLookup()
  ];
  /// <summary>
  ///   Decompresses Brotli-encoded data.
  /// </summary>
  /// <param name="compressed">The Brotli-compressed data.</param>
  /// <returns>The decompressed data.</returns>
  /// <exception cref="InvalidDataException">Thrown when the data is malformed.</exception>
  public static byte[] Decompress(ReadOnlySpan<byte> compressed) {
    var reader = new BrotliBitReader(compressed.ToArray());
    using var output = new MemoryStream();
    var ringBuffer = new byte[1 << BrotliConstants.MaxWindowBits]; // will be resized
    var ringPos = 0;
    var ringMask = 0;

    // Read window bits (WBITS)
    var windowBits = DecodeWindowBits(reader);
    var windowSize = 1 << windowBits;
    ringBuffer = new byte[windowSize];
    ringMask = windowSize - 1;

    // Distance ring buffer: last 4 distances
    int[] distRing = [1, 1, 1, 1]; // RFC 7932 specifies these initial values as implementation-defined but typically 1
    // Note: per the spec, ring buffer for distances starts at 4,11,15,16 or similar
    // but in practice implementations use small defaults

    var distRingIdx = 0;

    // Postfix and direct distance parameters (set per meta-block)
    var nPostfix = 0;
    var nDirect = 0;

    var isLast = false;

    while (!isLast) {
      // Read meta-block header
      isLast = reader.ReadBool();

      if (isLast) {
        var isEmpty = reader.ReadBool();
        if (isEmpty) {
          reader.AlignToByte();
          break;
        }
      }

      // MLEN: meta-block length
      var mlen = DecodeMetaBlockLength(reader, out var mNibbles, out var isMetadata);

      if (isMetadata) {
        // Metadata block — skip metadata bytes
        reader.AlignToByte();
        for (var i = 0; i < mlen; i++)
          reader.ReadBits(8);
        continue;
      }

      // ISUNCOMPRESSED bit: present for all non-metadata blocks
      var isUncompressed = !isLast && reader.ReadBool();
      // For last blocks, ISUNCOMPRESSED is not present — always compressed
      // (But our compressor marks the last block as uncompressed too, so
      // we also need to handle last+uncompressed. Actually per RFC 7932,
      // ISUNCOMPRESSED is only present when ISLAST=0. For ISLAST=1,
      // there's no ISUNCOMPRESSED bit.)

      if (isUncompressed) {
        // Uncompressed meta-block
        reader.AlignToByte();
        for (var i = 0; i < mlen; i++) {
          var b = (byte)reader.ReadBits(8);
          ringBuffer[ringPos & ringMask] = b;
          ringPos++;
          output.WriteByte(b);
        }

        continue;
      }

      // Compressed meta-block
      // Read block type and count descriptors for literal, insert&copy, and distance

      // Number of literal block types
      var numLitTypes = DecodeBlockTypeCount(reader);
      int[][] litTypeTrees = null!;
      int[] litTypeCountTree = null!;
      if (numLitTypes > 1) {
        litTypeTrees = ReadPrefixCode(reader, BrotliConstants.MaxBlockTypeSymbols, numLitTypes + 1);
        litTypeCountTree = ReadSimplePrefixCode(reader, BrotliConstants.BlockLengthTable.Length);
      }

      var litBlockType = 0;
      var litBlockRemaining = numLitTypes > 1 ? ReadBlockLength(reader, litTypeCountTree!) : 1 << 24;
      var litPrevBlockType = 1;

      // Number of insert&copy block types
      var numIacTypes = DecodeBlockTypeCount(reader);
      int[][] iacTypeTrees = null!;
      int[] iacTypeCountTree = null!;
      if (numIacTypes > 1) {
        iacTypeTrees = ReadPrefixCode(reader, BrotliConstants.MaxBlockTypeSymbols, numIacTypes + 1);
        iacTypeCountTree = ReadSimplePrefixCode(reader, BrotliConstants.BlockLengthTable.Length);
      }

      var iacBlockType = 0;
      var iacBlockRemaining = numIacTypes > 1 ? ReadBlockLength(reader, iacTypeCountTree!) : 1 << 24;
      var iacPrevBlockType = 1;

      // Number of distance block types
      var numDistTypes = DecodeBlockTypeCount(reader);
      int[][] distTypeTrees = null!;
      int[] distTypeCountTree = null!;
      if (numDistTypes > 1) {
        distTypeTrees = ReadPrefixCode(reader, BrotliConstants.MaxBlockTypeSymbols, numDistTypes + 1);
        distTypeCountTree = ReadSimplePrefixCode(reader, BrotliConstants.BlockLengthTable.Length);
      }

      var distBlockType = 0;
      var distBlockRemaining = numDistTypes > 1 ? ReadBlockLength(reader, distTypeCountTree!) : 1 << 24;
      var distPrevBlockType = 1;

      // Read postfix bits and direct distance codes
      nPostfix = (int)reader.ReadBits(2);
      if (nPostfix > BrotliConstants.MaxDistancePostfixBits)
        ThrowInvalidData("Invalid NPOSTFIX value.");
      var nDirectBits = (int)reader.ReadBits(4);
      nDirect = nDirectBits << nPostfix;
      if (nDirect > BrotliConstants.MaxDirectDistanceCodes)
        ThrowInvalidData("Invalid NDIRECT value.");

      // Context modes for literal block types (0-3, one of NumLiteralContextModes)
      var litContextModes = new int[numLitTypes];
      for (var i = 0; i < numLitTypes; i++) {
        litContextModes[i] = (int)reader.ReadBits(2);
        if (litContextModes[i] >= BrotliConstants.NumLiteralContextModes)
          ThrowInvalidData("Invalid literal context mode.");
      }

      // Context map for literals: maps (blockType * 64 + context) -> treeIndex
      var numLitTrees = numLitTypes; // default, overridden by context map
      var litContextMap = ReadContextMap(reader, numLitTypes * 64, out numLitTrees);

      // Context map for distances: maps (blockType * 4 + context) -> treeIndex
      var numDistTrees = numDistTypes;
      var distContextMap = ReadContextMap(reader, numDistTypes * 4, out numDistTrees);

      // Read literal prefix code trees
      var litTrees = new int[numLitTrees][];
      for (var i = 0; i < numLitTrees; i++)
        litTrees[i] = ReadSimplePrefixCode(reader, BrotliConstants.LiteralAlphabetSize);

      // Read insert-and-copy prefix code trees
      var iacTrees = new int[numIacTypes][];
      for (var i = 0; i < numIacTypes; i++)
        iacTrees[i] = ReadSimplePrefixCode(reader, BrotliConstants.NumInsertAndCopyLengthCodes);

      // Distance alphabet size = 16 + nDirect + (48 << nPostfix)
      var distAlphabetSize = 16 + nDirect + (48 << nPostfix);
      var distTrees = new int[numDistTrees][];
      for (var i = 0; i < numDistTrees; i++)
        distTrees[i] = ReadSimplePrefixCode(reader, distAlphabetSize);

      // Decompress meta-block
      var metaBytesRemaining = mlen;

      while (metaBytesRemaining > 0) {
        // Switch insert&copy block type if needed
        if (iacBlockRemaining == 0 && numIacTypes > 1)
          SwitchBlockType(
            reader,
            iacTypeTrees,
            iacTypeCountTree!,
            ref iacBlockType,
            ref iacPrevBlockType,
            ref iacBlockRemaining,
            numIacTypes
          );

        --iacBlockRemaining;

        // Read insert-and-copy length code
        var iacTree = iacBlockType;
        var iacCode = DecodeSymbol(reader, iacTrees[iacTree]);
        DecodeInsertAndCopyLength(
          iacCode,
          reader,
          out var insertLength,
          out var copyLength,
          out var useDistanceZero
        );

        // Insert literals
        for (var i = 0; i < insertLength && metaBytesRemaining > 0; i++) {
          // Switch literal block type if needed
          if (litBlockRemaining == 0 && numLitTypes > 1)
            SwitchBlockType(
              reader,
              litTypeTrees,
              litTypeCountTree!,
              ref litBlockType,
              ref litPrevBlockType,
              ref litBlockRemaining,
              numLitTypes
            );

          --litBlockRemaining;

          // Compute literal context
          var p1 = ringPos > 0 ? ringBuffer[(ringPos - 1) & ringMask] : (byte)0;
          var p2 = ringPos > 1 ? ringBuffer[(ringPos - 2) & ringMask] : (byte)0;
          var litContext = ComputeLiteralContext(p1, p2, litContextModes[litBlockType]);
          var litTreeIdx = litContextMap[litBlockType * 64 + litContext];

          var lit = (byte)DecodeSymbol(reader, litTrees[litTreeIdx]);
          ringBuffer[ringPos & ringMask] = lit;
          ++ringPos;
          output.WriteByte(lit);
          --metaBytesRemaining;
        }

        if (metaBytesRemaining <= 0)
          break;

        // Decode distance
        int distance;
        if (useDistanceZero)
          distance = distRing[distRingIdx & 3];
        else {
          // Switch distance block type if needed
          if (distBlockRemaining == 0 && numDistTypes > 1)
            SwitchBlockType(
              reader,
              distTypeTrees,
              distTypeCountTree!,
              ref distBlockType,
              ref distPrevBlockType,
              ref distBlockRemaining,
              numDistTypes
            );

          --distBlockRemaining;

          // Compute distance context from copy length (0..NumDistanceContextValues-1)
          var distContext = Math.Clamp(copyLength - 2, 0, BrotliConstants.NumDistanceContextValues - 1);

          var distTreeIdx = distContextMap[distBlockType * 4 + distContext];

          var distCode = DecodeSymbol(reader, distTrees[distTreeIdx]);
          distance = DecodeDistance(distCode, reader, nPostfix, nDirect, distRing, distRingIdx);

          // Update distance ring buffer
          if (distance > 0) {
            distRingIdx = (distRingIdx + 1) & 3;
            distRing[distRingIdx] = distance;
          }
        }

        // Copy from ring buffer
        if (distance <= 0)
          distance = 1; // safety: distance must be positive

        // Check for static dictionary reference
        if (ringPos < distance) {
          // Reference before the window — may be a static dictionary reference
          if (BrotliStaticDictionary.TryGetStaticReference(
              distance,
              ringPos,
              out var wordLen,
              out var wordIdx,
              out var transformIdx
            )) {
            Span<byte> wordBuf = new byte[wordLen + 16]; // extra for transforms
            var wordBytes = BrotliStaticDictionary.GetWord(
              wordLen,
              wordIdx,
              transformIdx,
              wordBuf
            );
            for (var i = 0; i < copyLength && metaBytesRemaining > 0 && i < wordBytes; ++i) {
              ringBuffer[ringPos & ringMask] = wordBuf[i];
              ++ringPos;
              output.WriteByte(wordBuf[i]);
              --metaBytesRemaining;
            }
          } else
            // Invalid reference — emit zeros
            for (var i = 0; i < copyLength && metaBytesRemaining > 0; ++i) {
              ringBuffer[ringPos & ringMask] = 0;
              ++ringPos;
              output.WriteByte(0);
              --metaBytesRemaining;
            }
        } else
          for (var i = 0; i < copyLength && metaBytesRemaining > 0; ++i) {
            var b = ringBuffer[(ringPos - distance) & ringMask];
            ringBuffer[ringPos & ringMask] = b;
            ++ringPos;
            output.WriteByte(b);
            --metaBytesRemaining;
          }
      }
    }

    return output.ToArray();
  }

  /// <summary>
  ///   Decodes the window bits (WBITS) from the stream header.
  /// </summary>
  private static int DecodeWindowBits(BrotliBitReader reader) {
    // RFC 7932 Section 9.1
    if (!reader.ReadBool())
      return 16; // WBITS = 16

    var n = reader.ReadBits(3);
    if (n != 0)
      return (int)(17 + n); // WBITS = 17..24

    n = reader.ReadBits(3);
    if (n == 1)
      return -1; // reserved, treat as invalid → use default

    if (n != 0)
      return (int)(8 + n); // WBITS = 10..14

    return 17;
  }

  /// <summary>
  ///   Decodes a meta-block length (MLEN) value (RFC 7932 Section 9.2).
  /// </summary>
  private static int DecodeMetaBlockLength(BrotliBitReader reader, out int mNibbles, out bool isMetadata) {
    isMetadata = false;
    var mnibblesBits = (int)reader.ReadBits(2);
    mNibbles = mnibblesBits + 4;

    if (mNibbles == 7) {
      // Metadata block
      mNibbles = 0;
      isMetadata = true;
      // Read reserved bit (must be 0)
      reader.ReadBits(1);
      var sizeBytes = (int)reader.ReadBits(2);
      if (sizeBytes == 0)
        return 0;

      var mlen = 0;
      for (var i = 0; i < sizeBytes; ++i)
        mlen |= (int)reader.ReadBits(8) << (8 * i);

      return mlen + 1;
    }

    var len = 0;
    for (var i = 0; i < mNibbles; ++i)
      len |= (int)reader.ReadBits(4) << (4 * i);

    ++len;

    return len;
  }

  /// <summary>
  ///   Reads the number of block types (NBLTYPES) for a category.
  /// </summary>
  private static int DecodeBlockTypeCount(BrotliBitReader reader) {
    var code = ReadSimpleVarint(reader);
    if (code > BrotliConstants.MaxBlockTypes)
      ThrowInvalidData("Block type count exceeds maximum.");
    return code == 1 ? 1 : code;
  }

  /// <summary>
  ///   Reads a simple variable-length integer for block type counts.
  /// </summary>
  private static int ReadSimpleVarint(BrotliBitReader reader) {
    // RFC 7932 Section 6: NBLTYPES is encoded with a variable-length code
    var code = reader.ReadBits(1);
    if (code == 0)
      return 1;

    code = reader.ReadBits(3);
    switch (code) {
      case 0: return 2;
      case <= 4: {
        var extra = reader.ReadBits((int)code);
        return (int)((1u << (int)code) + extra + 1);
      }

      default: {
        // code 5..7: read more bits
        var extraBits2 = reader.ReadBits((int)(code - 3 + 4));
        return (int)((1u << ((int)code - 3 + 4)) + extraBits2 + 1);
      }
    }

  }

  /// <summary>
  ///   Reads a block length using the block length prefix code.
  /// </summary>
  private static int ReadBlockLength(BrotliBitReader reader, int[] tree) {
    var code = DecodeSymbol(reader, tree);
    if (code >= BrotliConstants.BlockLengthTable.Length)
      ThrowInvalidData("Invalid block length code.");

    var (baseVal, extraBits) = BrotliConstants.BlockLengthTable[code];
    if (extraBits > 0)
      return baseVal + (int)reader.ReadBits(extraBits);

    return baseVal;
  }

  /// <summary>
  ///   Switches to a new block type.
  /// </summary>
  private static void SwitchBlockType(
    BrotliBitReader reader,
    int[][] typeTrees,
    int[] countTree,
    ref int currentType,
    ref int prevType,
    ref int remaining,
    int numTypes
  ) {
    var code = DecodeSymbol(reader, typeTrees[0]); // single type tree
    var newType = code switch {
      0 => prevType,
      1 => (currentType + 1) % numTypes,
      _ => code - 2
    };

    prevType = currentType;
    currentType = newType;
    remaining = ReadBlockLength(reader, countTree);
  }

  /// <summary>
  ///   Decodes insert and copy lengths from a combined code (RFC 7932 Section 5, Table 8).
  ///   The 704 insert-and-copy codes are organized as a 24×24 matrix with some cells
  ///   using implicit distance zero.
  /// </summary>
  private static void DecodeInsertAndCopyLength(
    int code,
    BrotliBitReader reader,
    out int insertLength,
    out int copyLength,
    out bool useDistanceZero
  ) {
    // RFC 7932 Table 8: 704 = 4 blocks of 64 + 4 blocks of 64 + 8 blocks of 8 + ...
    // Layout: the code is split into insert_length_code and copy_length_code.
    //
    // Codes 0-63: 8 rows × 8 cols
    //   row = code / 8, col = code % 8
    //   insertCode = InsertRangeMap[row], copyCode = CopyRangeMap[col]
    //   useDistanceZero = (col >= 4) for rows 0-3; (col < 4) for rows 4-7
    //
    // Codes 64-127: same structure but shifted insert/copy ranges
    // Codes 128-703: remaining ranges
    //
    // Simplified mapping per RFC 7932 Table 8:

    int insertCode;
    int copyCode;

    if (code < 128) {
      // First 128 codes: organized as a grid
      // Insert code from bits [6..3] of code, copy code from bits [2..0] + [6]
      insertCode = InsertAndCopyInsertCode(code);
      copyCode = InsertAndCopyCopyCode(code);
      // Distance zero is used when the insert-and-copy code falls in certain ranges
      useDistanceZero = code >= 64;
    } else {
      // Codes 128-703: extended ranges
      var adjusted = code - 128;
      var block = adjusted / 64;
      var within = adjusted % 64;
      var row = within / 8;
      var col = within % 8;

      insertCode = 8 + block * 8 + row;
      copyCode = col;
      if (col >= 4) {
        useDistanceZero = true;
        copyCode -= 4;
      } else
        useDistanceZero = false;

      copyCode += 8 + block * 4;

      if (insertCode >= BrotliConstants.InsertLengthTable.Length)
        insertCode = BrotliConstants.InsertLengthTable.Length - 1;
      if (copyCode >= BrotliConstants.CopyLengthTable.Length)
        copyCode = BrotliConstants.CopyLengthTable.Length - 1;
    }

    var (insBase, insExtra) = BrotliConstants.InsertLengthTable[insertCode];
    insertLength = insExtra > 0 ? insBase + (int)reader.ReadBits(insExtra) : insBase;

    var (cpBase, cpExtra) = BrotliConstants.CopyLengthTable[copyCode];
    copyLength = cpExtra > 0 ? cpBase + (int)reader.ReadBits(cpExtra) : cpBase;
  }

  /// <summary>
  ///   Extracts the insert length code from an insert-and-copy code (0-127).
  ///   RFC 7932 Section 5 Table 8.
  /// </summary>
  private static int InsertAndCopyInsertCode(int code) {
    // Codes 0-63: insertCode in [0..5], codes 64-127: insertCode in [0..5]
    // Row = code / 8 (within each 64-code block)
    if (code < 64)
      return code / 8; // 0-7

    return (code - 64) / 8; // 0-7
  }

  /// <summary>
  ///   Extracts the copy length code from an insert-and-copy code (0-127).
  ///   RFC 7932 Section 5 Table 8.
  /// </summary>
  private static int InsertAndCopyCopyCode(int code) {
    if (code < 64) {
      var col = code % 8;
      return col < 4 ? col : col - 4;
    }

    var col2 = (code - 64) % 8;
    return col2 < 4 ? col2 + 8 : col2 - 4 + 8;
  }

  /// <summary>
  ///   Decodes a distance value from a distance code (RFC 7932 Section 4).
  /// </summary>
  /// <summary>
  ///   Decodes a distance value from a distance code (RFC 7932 Section 4).
  ///   Codes 0-3: last 4 distances. Codes 4-15: last distances ± small offset.
  ///   Codes 16+: direct codes or complex distance codes with extra bits.
  /// </summary>
  private static int DecodeDistance(
    int code,
    BrotliBitReader reader,
    int nPostfix,
    int nDirect,
    int[] distRing,
    int distRingIdx
  ) {
    switch (code) {
      // Last four distances: 0=last, 1=second-to-last, etc.
      case < 4: return distRing[(distRingIdx - code) & 3];
      case < 16: {
        // RFC 7932 Table 9: codes 4-15 reference last distances with offsets
        // Code 4:  last - 1        Code 5:  last + 1
        // Code 6:  last - 2        Code 7:  last + 2
        // Code 8:  last - 3        Code 9:  last + 3
        // Code 10: 2nd_last - 1    Code 11: 2nd_last + 1
        // Code 12: 2nd_last - 2    Code 13: 2nd_last + 2
        // Code 14: 2nd_last - 3    Code 15: 2nd_last + 3
        var adjusted = code - 4;
        var ringIdx = adjusted < 6 ? 0 : 1; // which ring entry (0 = last, 1 = 2nd last)
        var pairIdx = adjusted < 6 ? adjusted : adjusted - 6;
        var sign = (pairIdx & 1) == 0 ? -1 : 1;
        var offset = (pairIdx >> 1) + 1;
        var baseDist = distRing[(distRingIdx - ringIdx) & 3];
        return Math.Max(1, baseDist + sign * offset);
      }
    }

    if (code < 16 + nDirect)
      // Direct distance codes
      return code - 15;

    // Complex distance codes with extra bits
    var dCode = code - 16 - nDirect;
    var postfixMask = (1 << nPostfix) - 1;
    var postfix = dCode & postfixMask;
    dCode >>= nPostfix;
    var nBits = 1 + (dCode >> 1);
    var baseDist2 = ((2 + (dCode & 1)) << nBits) - 4;
    var extra = (int)reader.ReadBits(nBits);
    return ((baseDist2 + extra) << nPostfix) + postfix + nDirect + 1;
  }

  /// <summary>
  ///   Computes the literal context ID from the two previous bytes and the context mode.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static int ComputeLiteralContext(byte p1, byte p2, int contextMode) {
    var lut = ContextLookup[contextMode];
    return contextMode == 0
      ? lut[p1] | ((p2 >> 6) << 6) // LSB6: low 6 bits from p1 LUT, high 2 bits from p2
      : lut[p1];
  }

  /// <summary>
  ///   Reads a context map from the stream (RFC 7932 Section 7.3).
  /// </summary>
  private static int[] ReadContextMap(BrotliBitReader reader, int contextMapSize, out int numTrees) {
    numTrees = DecodeBlockTypeCount(reader);
    if (numTrees == 1)
      return new int[contextMapSize]; // all zeros → single tree

    var useRleEncoding = reader.ReadBool();
    var maxRleSymbol = 0;
    if (useRleEncoding)
      maxRleSymbol = (int)reader.ReadBits(4) + 1;

    // Read the context map Huffman tree
    var alphabetSize = numTrees + maxRleSymbol;
    var mapTree = ReadSimplePrefixCode(reader, alphabetSize);

    var contextMap = new int[contextMapSize];
    var i = 0;
    while (i < contextMapSize) {
      var sym = DecodeSymbol(reader, mapTree);
      if (sym == 0)
        contextMap[i++] = 0;
      else if (sym <= maxRleSymbol) {
        // RLE of zeros
        var runLen = 1 << sym;
        runLen += (int)reader.ReadBits(sym);
        for (var j = 0; j < runLen && i < contextMapSize; j++)
          contextMap[i++] = 0;
      } else
        contextMap[i++] = sym - maxRleSymbol;
    }

    // Inverse move-to-front transform
    if (reader.ReadBool())
      InverseMtf(contextMap);

    return contextMap;
  }

  /// <summary>
  ///   Applies the inverse move-to-front transform to a context map.
  /// </summary>
  private static void InverseMtf(int[] data) {
    var mtf = new int[256];
    for (var i = 0; i < 256; i++)
      mtf[i] = i;

    for (var i = 0; i < data.Length; i++) {
      var idx = data[i];
      data[i] = mtf[idx];
      if (idx <= 0)
        continue;

      var val = mtf[idx];
      mtf.AsSpan(0, idx).CopyTo(mtf.AsSpan(1));
      mtf[0] = val;
    }
  }

  /// <summary>
  ///   Reads a prefix code (Huffman tree) from the stream.
  ///   Returns a flat decode table: table[bits] = symbol | (length &lt;&lt; 16).
  /// </summary>
  private static int[] ReadSimplePrefixCode(BrotliBitReader reader, int alphabetSize) {
    var hSkip = (int)reader.ReadBits(2);

    if (hSkip == 1)
      // Simple prefix code
      return ReadSimpleDirectPrefixCode(reader, alphabetSize);

    // Complex prefix code
    var codeLengths = new int[alphabetSize];
    var clCodeLengths = new int[BrotliConstants.NumCodeLengthCodes];

    var numClCodes = hSkip;
    // Read code length code lengths
    var space = 32;
    for (var i = numClCodes; i < BrotliConstants.NumCodeLengthCodes && space > 0; i++) {
      int idx = BrotliConstants.CodeLengthCodeOrder[i];
      var len = ReadSmallCodeLength(reader);
      clCodeLengths[idx] = len;
      if (len > 0)
        space -= 32 >> len;
      numClCodes = i + 1;
    }

    // Build code length tree
    var clTree = BuildHuffmanTable(clCodeLengths, BrotliConstants.NumCodeLengthCodes);

    // Read actual code lengths
    var prevCodeLen = 8;
    var repeat = 0;
    var repeatCodeLen = 0;
    space = 32768;
    var symbolsRead = 0;

    while (symbolsRead < alphabetSize && space > 0) {
      var sym = DecodeSymbol(reader, clTree);
      switch (sym) {
        case < 16: {
          codeLengths[symbolsRead++] = sym;
          if (sym > 0) {
            prevCodeLen = sym;
            space -= 32768 >> sym;
          }

          repeat = 0;
          break;
        }

        case 16: {
          // Repeat previous
          var extra = (int)reader.ReadBits(2) + 3;
          if (repeat > 0 && repeatCodeLen == prevCodeLen)
            extra += repeat;
          else
            repeat = 0;
          repeatCodeLen = prevCodeLen;
          for (var j = 0; j < extra && symbolsRead < alphabetSize; j++) {
            codeLengths[symbolsRead++] = prevCodeLen;
            space -= 32768 >> prevCodeLen;
          }

          repeat += extra;
          break;
        }

        default: {
          // sym == 17: repeat zero
          var extra = (int)reader.ReadBits(3) + 3;
          if (repeat > 0 && repeatCodeLen == 0)
            extra += repeat;
          else
            repeat = 0;
          repeatCodeLen = 0;
          for (var j = 0; j < extra && symbolsRead < alphabetSize; j++)
            codeLengths[symbolsRead++] = 0;
          repeat += extra;
          break;
        }
      }
    }

    return BuildHuffmanTable(codeLengths, alphabetSize);
  }

  /// <summary>
  ///   Reads a simple direct prefix code (1-4 symbols, fixed lengths).
  /// </summary>
  private static int[] ReadSimpleDirectPrefixCode(BrotliBitReader reader, int alphabetSize) {
    var numSymBits = 0;
    var a = alphabetSize;
    while (a > 1) {
      a >>= 1;
      ++numSymBits;
    }

    if (numSymBits == 0) 
      numSymBits = 1;

    var numSyms = (int)reader.ReadBits(2) + 1;
    var symbols = new int[numSyms];
    for (var i = 0; i < numSyms; i++) {
      symbols[i] = (int)reader.ReadBits(numSymBits);
      if (symbols[i] >= alphabetSize)
        symbols[i] = 0;
    }

    // Assign lengths based on number of symbols
    var codeLengths = new int[alphabetSize];
    switch (numSyms) {
      case 1:
        codeLengths[symbols[0]] = 1; // single symbol, but needs length 1 for table
        break;

      case 2:
        codeLengths[symbols[0]] = 1;
        codeLengths[symbols[1]] = 1;
        break;

      case 3:
        codeLengths[symbols[0]] = 1;
        codeLengths[symbols[1]] = 2;
        codeLengths[symbols[2]] = 2;
        break;

      case 4:
        var treeSelect = reader.ReadBool();
        if (treeSelect) {
          codeLengths[symbols[0]] = 2;
          codeLengths[symbols[1]] = 2;
          codeLengths[symbols[2]] = 2;
          codeLengths[symbols[3]] = 2;
        } else {
          codeLengths[symbols[0]] = 1;
          codeLengths[symbols[1]] = 2;
          codeLengths[symbols[2]] = 3;
          codeLengths[symbols[3]] = 3;
        }

        break;
    }

    return BuildHuffmanTable(codeLengths, alphabetSize);
  }

  /// <summary>
  ///   Reads a code length code length (0-5) using the variable-length prefix code
  ///   from RFC 7932 Section 3.5.
  /// </summary>
  private static int ReadSmallCodeLength(BrotliBitReader reader) {
    // Variable-length encoding: 0=0, 10=1, 110=2, 1110=3, 11110=4, 11111=5
    if (!reader.ReadBool()) return 0;
    if (!reader.ReadBool()) return 1;
    if (!reader.ReadBool()) return 2;
    if (!reader.ReadBool()) return 3;

    return reader.ReadBool() ? 5 : 4;
  }

  /// <summary>
  ///   Reads prefix codes for block type switching.
  /// </summary>
  private static int[][] ReadPrefixCode(BrotliBitReader reader, int alphabetSize, int numTrees) {
    var trees = new int[numTrees][];
    // For block type switching, we only need one tree
    trees[0] = ReadSimplePrefixCode(reader, alphabetSize);
    return trees;
  }

  /// <summary>
  ///   Builds a Huffman decode table from code lengths.
  ///   table[peekBits] = symbol | (codeLength &lt;&lt; 16).
  ///   Uses a flat table with max code length determined from the input.
  /// </summary>
  private static int[] BuildHuffmanTable(int[] codeLengths, int numSymbols) {
    var maxLen = 0;
    var numUsed = 0;
    var singleSymbol = 0;

    for (var i = 0; i < numSymbols; i++)
      if (codeLengths[i] > 0) {
        maxLen = Math.Max(maxLen, codeLengths[i]);
        numUsed++;
        singleSymbol = i;
      }

    switch (numUsed) {
      // Empty tree — return single-entry table
      case 0: return [0 | (1 << 16)];
      case 1: {
        // Single symbol — fill all entries
        var tableSize = 1 << Math.Max(1, maxLen);
        var table = new int[tableSize];
        var entry = singleSymbol | (maxLen << 16);
        table.AsSpan().Fill(entry);
        return table;
      }
    }

    if (maxLen > BrotliConstants.MaxHuffmanCodeLength) maxLen = BrotliConstants.MaxHuffmanCodeLength;
    var size = 1 << maxLen;
    var result = new int[size];
    result.AsSpan().Fill(-1);

    // Assign canonical codes
    var blCount = new int[maxLen + 1];
    for (var i = 0; i < numSymbols; i++)
      if (codeLengths[i] > 0 && codeLengths[i] <= maxLen)
        blCount[codeLengths[i]]++;

    var nextCode = new int[maxLen + 1];
    var currentCode = 0;
    for (var bits = 1; bits <= maxLen; bits++) {
      currentCode = (currentCode + blCount[bits - 1]) << 1;
      nextCode[bits] = currentCode;
    }

    for (var sym = 0; sym < numSymbols; sym++) {
      var len = codeLengths[sym];
      if (len == 0 || len > maxLen) continue;

      var code = nextCode[len]++;
      // Reverse bits for LSB-first lookup
      var reversed = 0;
      for (var b = 0; b < len; b++)
        reversed |= ((code >> (len - 1 - b)) & 1) << b;

      var entry = sym | (len << 16);
      var step = 1 << len;
      for (var j = reversed; j < size; j += step)
        result[j] = entry;
    }

    return result;
  }

  /// <summary>
  ///   Decodes a single symbol from a Huffman decode table.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static int DecodeSymbol(BrotliBitReader reader, int[] table) {
    var tableBits = 0;
    var ts = table.Length;
    while (ts > 1) {
      ts >>= 1;
      tableBits++;
    }

    if (tableBits == 0) tableBits = 1;

    reader.Fill(tableBits);
    var bits = reader.PeekBits(tableBits);
    var idx = (int)(bits & (uint)(table.Length - 1));
    if (idx >= table.Length || table[idx] == -1) {
      // Fallback: try smaller lookup
      reader.DropBits(1);
      return 0;
    }

    var entry = table[idx];
    var symbol = entry & 0xFFFF;
    var length = entry >> 16;
    reader.DropBits(length);
    return symbol;
  }

  [DoesNotReturn]
  private static void ThrowInvalidData(string message) =>
    throw new InvalidDataException(message);
}
