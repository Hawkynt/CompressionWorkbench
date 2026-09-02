namespace OggVorbisEncoder.Setup;

/// <summary>
/// Represents a compand block.
/// </summary>
public class CompandBlock
{
    /// <summary>
    /// Initializes a new instance of <see cref="CompandBlock"/>.
    /// </summary>
public CompandBlock(int[] data)
    {
        Data = data;
    }

    /// <summary>
    /// Gets the data.
    /// </summary>
public int[] Data { get; }
}
