using System.Collections.Generic;
using System.Text;

namespace OggVorbisEncoder;

/// <summary>
/// Represents a comments.
/// </summary>
public class Comments
{
    private readonly List<string> _userComments = new List<string>();

        /// <summary>
    /// Gets the user comments.
    /// </summary>
public List<string> UserComments => _userComments;

        /// <summary>
    /// Performs the add tag operation.
    /// </summary>
public void AddTag(string tag, string contents)
    {
        var stringBuilder = new StringBuilder();
        stringBuilder.Append(tag);
        stringBuilder.Append('=');
        stringBuilder.Append(contents);
        _userComments.Add(stringBuilder.ToString());
    }
}
