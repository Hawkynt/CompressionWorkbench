namespace OggVorbisEncoder.Setup;

/// <summary>
/// Represents an att 3.
/// </summary>
public class Att3
{
    /// <summary>
    /// Initializes a new instance of <see cref="Att3"/>.
    /// </summary>
public Att3(int[] att, float boost, float decay)
    {
        Att = att;
        Boost = boost;
        Decay = decay;
    }

    /// <summary>
    /// Gets the att.
    /// </summary>
public int[] Att { get; }
    /// <summary>
    /// Gets the boost.
    /// </summary>
public float Boost { get; }
    /// <summary>
    /// Gets the decay.
    /// </summary>
public float Decay { get; }
}
