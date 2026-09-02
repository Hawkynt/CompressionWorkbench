namespace OggVorbisEncoder.Setup;

/// <summary>
/// Represents an adj block.
/// </summary>
public class AdjBlock
{
        /// <summary>
    /// Initializes a new instance of <see cref="AdjBlock"/>.
    /// </summary>
public AdjBlock(int[] block)
    {
        Block = block;
    }

        /// <summary>
    /// Gets the block.
    /// </summary>
public int[] Block { get; }
}
