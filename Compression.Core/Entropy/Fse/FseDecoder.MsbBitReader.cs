namespace Compression.Core.Entropy.Fse;

/// <summary>
/// Decodes fse data.
/// </summary>
public sealed partial class FseDecoder {
    /// <summary>
    /// Reads bits from MSB to LSB of a flat bit buffer. The buffer is stored
    /// as bytes where byte 0 contains bits 0..7 (LSB first). Reading from the
    /// top means starting at the highest bit position and working down.
    /// </summary>
    private ref struct MsbBitReader {
        private readonly ReadOnlySpan<byte> _data;
        private long _bitPos; // next bit position to read (counting down from totalBits-1)

        /// <summary>
        /// Initializes the reader with the data and the total number of valid data bits.
        /// </summary>
        /// <param name="data">The byte array containing the bitstream.</param>
        /// <param name="totalBits">Total number of data bits (sentinel excluded).</param>
        public MsbBitReader(ReadOnlySpan<byte> data, long totalBits) {
            this._data = data;
            this._bitPos = totalBits - 1; // start from the highest data bit
        }

        /// <summary>
        /// Reads nbBits bits from the top of the remaining bitstream.
        /// Returns the value with the first bit read in the MSB position.
        /// </summary>
        /// <param name="nbBits">Number of bits to read.</param>
        /// <returns>The value read.</returns>
        public int ReadBitsFromTop(int nbBits) {
            // Read nbBits starting from _bitPos going down.
            // The first bit read (at _bitPos) is the MSB of the result.
            var value = 0;
            for (var i = nbBits - 1; i >= 0; --i) {
                var bit = this.GetBit(this._bitPos);
                value |= bit << i;
                --this._bitPos;
            }

            return value;
        }

        /// <summary>
        /// Gets a single bit at the specified position in the flat buffer.
        /// </summary>
        private readonly int GetBit(long pos) {
            // A span cannot be indexed by a long, but any in-range byte index fits an
            // int because the span itself can be at most Array.MaxLength long.
            var byteIdx = (int)(pos >> 3);
            var bitIdx = (int)(pos & 7);
            return (this._data[byteIdx] >> bitIdx) & 1;
        }
    }
}
