namespace ScimStudio.Core.Text;

/// <summary>
/// Something to tell the person using the tool, as a key into the interface's catalogues and the values it names. The core speaks no language;
/// the interface looks the key up in the one it is showing, so a switch of language reaches messages that were written before it.
/// </summary>
/// <param name="Key">The catalogue key.</param>
/// <param name="Args">The values the text names, in the order its placeholders count them.</param>
public sealed record Message(string Key, IReadOnlyList<object?> Args) {
    /// <summary>A message with its values.</summary>
    /// <param name="key">The catalogue key.</param>
    /// <param name="args">The values the text names.</param>
    public static Message Of(string key, params object?[] args) {
        return new Message(key, args);
    }
}
