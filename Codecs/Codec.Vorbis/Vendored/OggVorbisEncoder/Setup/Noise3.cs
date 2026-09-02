namespace OggVorbisEncoder.Setup;

/// <summary>
/// Represents a noise 3.
/// </summary>
public class Noise3
{
    /// <summary>
    /// Initializes a new instance of <see cref="Noise3"/>.
    /// </summary>
    public Noise3(int[][] data)
    {
        Data = data;
    }

    /// <summary>
    /// Gets the data.
    /// </summary>
    public int[][] Data { get; }
}
