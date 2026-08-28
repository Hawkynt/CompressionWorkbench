using System.Buffers.Binary;
using System.Numerics;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>BLAKE3 hash and XOF.</summary>
public static class Blake3 {
  public static byte[] Compute(ReadOnlySpan<byte> data, int outputBytes = 32) => Blake3Core.Hash(data, outputBytes);
}

/// <summary>Registry-compatible BLAKE3-Enhanced surface, backed by the same complete BLAKE3 core.</summary>
public static class Blake3Enhanced {
  public static byte[] Compute(ReadOnlySpan<byte> data, int outputBytes = 32) => Blake3Core.Hash(data, outputBytes);
}

internal static class Blake3Core {
  private const int BlockLength = 64;
  private const int ChunkLength = 1024;
  private const uint ChunkStart = 1;
  private const uint ChunkEnd = 2;
  private const uint Parent = 4;
  private const uint Root = 8;

  private static readonly uint[] Iv = [
    0x6A09E667U,0xBB67AE85U,0x3C6EF372U,0xA54FF53AU,
    0x510E527FU,0x9B05688CU,0x1F83D9ABU,0x5BE0CD19U
  ];

  private static readonly byte[] Permutation = [2,6,3,10,7,0,4,13,1,11,12,5,9,14,15,8];

  public static byte[] Hash(ReadOnlySpan<byte> data, int outputBytes) {
    if (outputBytes < 1)
      throw new ArgumentOutOfRangeException(nameof(outputBytes));

    var chunkCount = Math.Max(1, (data.Length + ChunkLength - 1) / ChunkLength);
    var stack = new List<uint[]>();

    for (var chunkIndex = 0; chunkIndex < chunkCount - 1; ++chunkIndex) {
      var chunk = data.Slice(chunkIndex * ChunkLength, ChunkLength);
      var cv = ChunkOutput(chunk, (ulong)chunkIndex).ChainingValue();
      AddChunkCv(stack, cv, (ulong)chunkIndex + 1);
    }

    var lastOffset = (chunkCount - 1) * ChunkLength;
    var lastLength = data.Length - lastOffset;
    var output = ChunkOutput(data.Slice(lastOffset, lastLength), (ulong)(chunkCount - 1));
    for (var i = stack.Count - 1; i >= 0; --i)
      output = ParentOutput(stack[i], output.ChainingValue());

    return output.RootBytes(outputBytes);
  }

  private static Output ChunkOutput(ReadOnlySpan<byte> chunk, ulong chunkCounter) {
    var cv = Iv.ToArray();
    var blocks = Math.Max(1, (chunk.Length + BlockLength - 1) / BlockLength);
    for (var blockIndex = 0; blockIndex < blocks - 1; ++blockIndex) {
      var block = chunk.Slice(blockIndex * BlockLength, BlockLength);
      var flags = blockIndex == 0 ? ChunkStart : 0U;
      cv = CompressCv(cv, block, chunkCounter, BlockLength, flags);
    }

    var finalOffset = (blocks - 1) * BlockLength;
    var finalLength = chunk.Length - finalOffset;
    Span<byte> final = stackalloc byte[BlockLength];
    final.Clear();
    if (finalLength > 0)
      chunk.Slice(finalOffset, finalLength).CopyTo(final);
    var finalFlags = ChunkEnd | (blocks == 1 ? ChunkStart : 0U);
    return new Output(cv, Words(final), chunkCounter, finalLength, finalFlags);
  }

  private static void AddChunkCv(List<uint[]> stack, uint[] newCv, ulong totalChunks) {
    while ((totalChunks & 1) == 0) {
      var left = stack[^1];
      stack.RemoveAt(stack.Count - 1);
      newCv = ParentOutput(left, newCv).ChainingValue();
      totalChunks >>= 1;
    }
    stack.Add(newCv);
  }

  private static Output ParentOutput(ReadOnlySpan<uint> left, ReadOnlySpan<uint> right) {
    var block = new uint[16];
    left.CopyTo(block);
    right.CopyTo(block.AsSpan(8));
    return new Output(Iv.ToArray(), block, 0, BlockLength, Parent);
  }

  private static uint[] CompressCv(ReadOnlySpan<uint> cv, ReadOnlySpan<byte> block, ulong counter, int blockLength, uint flags) =>
    Compress(cv, Words(block), counter, blockLength, flags)[..8];

  private static uint[] Words(ReadOnlySpan<byte> block) {
    var words = new uint[16];
    for (var i = 0; i < Math.Min(16, block.Length / 4); ++i)
      words[i] = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(i * 4, 4));
    var remainder = block.Length & 3;
    if (remainder != 0) {
      var offset = block.Length - remainder;
      uint value = 0;
      for (var i = 0; i < remainder; ++i)
        value |= (uint)block[offset + i] << (8 * i);
      words[offset / 4] = value;
    }
    return words;
  }

  private static uint[] Compress(ReadOnlySpan<uint> cv, ReadOnlySpan<uint> block, ulong counter, int blockLength, uint flags) {
    Span<uint> state = stackalloc uint[16];
    Span<uint> message = stackalloc uint[16];
    Span<uint> permuted = stackalloc uint[16];
    cv.CopyTo(state);
    state[8]=Iv[0]; state[9]=Iv[1]; state[10]=Iv[2]; state[11]=Iv[3];
    state[12]=(uint)counter; state[13]=(uint)(counter>>32); state[14]=(uint)blockLength; state[15]=flags;
    block.CopyTo(message);

    for (var round=0; round<7; ++round) {
      G(state,0,4,8,12,message[0],message[1]); G(state,1,5,9,13,message[2],message[3]);
      G(state,2,6,10,14,message[4],message[5]); G(state,3,7,11,15,message[6],message[7]);
      G(state,0,5,10,15,message[8],message[9]); G(state,1,6,11,12,message[10],message[11]);
      G(state,2,7,8,13,message[12],message[13]); G(state,3,4,9,14,message[14],message[15]);
      if (round != 6) {
        for (var i=0;i<16;++i) permuted[i]=message[Permutation[i]];
        permuted.CopyTo(message);
      }
    }

    var result = new uint[16];
    for (var i=0;i<8;++i) {
      result[i]=state[i]^state[i+8];
      result[i+8]=state[i+8]^cv[i];
    }
    return result;
  }

  private static void G(Span<uint> s,int a,int b,int c,int d,uint x,uint y) {
    s[a]=unchecked(s[a]+s[b]+x); s[d]=BitOperations.RotateRight(s[d]^s[a],16);
    s[c]=unchecked(s[c]+s[d]); s[b]=BitOperations.RotateRight(s[b]^s[c],12);
    s[a]=unchecked(s[a]+s[b]+y); s[d]=BitOperations.RotateRight(s[d]^s[a],8);
    s[c]=unchecked(s[c]+s[d]); s[b]=BitOperations.RotateRight(s[b]^s[c],7);
  }

  private sealed class Output(uint[] inputCv, uint[] block, ulong counter, int blockLength, uint flags) {
    public uint[] ChainingValue() => Compress(inputCv, block, counter, blockLength, flags)[..8];

    public byte[] RootBytes(int length) {
      var result = new byte[length];
      var written = 0;
      ulong outputCounter = 0;
      Span<byte> word = stackalloc byte[4];
      while (written < length) {
        var words = Compress(inputCv, block, outputCounter++, blockLength, flags | Root);
        for (var i=0;i<16 && written<length;++i) {
          BinaryPrimitives.WriteUInt32LittleEndian(word,words[i]);
          var take=Math.Min(4,length-written);
          word[..take].CopyTo(result.AsSpan(written));
          written+=take;
        }
      }
      return result;
    }
  }
}
