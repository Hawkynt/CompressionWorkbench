namespace OggVorbisEncoder.Setup;

/// <summary>
/// Defines the contract for i static book block.
/// </summary>
public interface IStaticBookBlock
{
    IStaticCodeBook[][] Books { get; }
}
