namespace Compression.Tests.Csc;

/// <summary>
/// libcsc ends every segment's range-coder output with a guard byte its decoder never reads and its
/// encoder never initialises. Zeroing the last byte of every short range-coder block blanks exactly
/// those bytes, so two streams can be compared for everything that carries meaning.
/// </summary>
internal static class CscGuardBytes {
  public static byte[] Mask(byte[] stream) {
    var result = (byte[])stream.Clone();
    var blockSize = (result[4] << 16) | (result[5] << 8) | result[6];
    for (var pos = 10; pos < result.Length;) {
      var flag = result[pos++];
      int size;
      if ((flag & 0x40) != 0)
        size = blockSize;
      else {
        size = (result[pos] << 16) | (result[pos + 1] << 8) | result[pos + 2];
        pos += 3;
      }

      if ((flag & 0x80) != 0 && (flag & 0x40) == 0 && size > 0)
        result[pos + size - 1] = 0;
      pos += size;
    }

    return result;
  }
}
