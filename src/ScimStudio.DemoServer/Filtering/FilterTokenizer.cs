using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScimStudio.DemoServer.Filtering;

internal enum TokenKind {
    Word,
    String,
    OpenParenthesis,
    CloseParenthesis,
    OpenBracket,
    CloseBracket,
    End,
}

/// <summary>A piece of a filter; <see cref="Text"/> holds a string literal already unescaped.</summary>
internal readonly record struct Token(TokenKind Kind, string Text, int Position) {
    public override string ToString() {
        return Kind == TokenKind.End ? "the end of the filter" : $"'{Text}' at position {Position}";
    }
}

/// <summary>Splits a filter into words (attribute paths, operators, keywords, literals), string literals and brackets.</summary>
internal static class FilterTokenizer {
    public static List<Token> Tokenize(string filter) {
        var tokens = new List<Token>();
        var position = 0;
        while (position < filter.Length) {
            var character = filter[position];
            if (char.IsWhiteSpace(character)) {
                position++;
                continue;
            }
            var kind = character switch {
                '(' => TokenKind.OpenParenthesis,
                ')' => TokenKind.CloseParenthesis,
                '[' => TokenKind.OpenBracket,
                ']' => TokenKind.CloseBracket,
                '"' => TokenKind.String,
                _ => TokenKind.Word,
            };
            if (kind == TokenKind.String) {
                tokens.Add(ReadString(filter, ref position));
            } else if (kind == TokenKind.Word) {
                var start = position;
                while (position < filter.Length && !IsDelimiter(filter[position])) {
                    position++;
                }
                tokens.Add(new Token(TokenKind.Word, filter[start..position], start));
            } else {
                tokens.Add(new Token(kind, filter[position..(position + 1)], position));
                position++;
            }
        }
        tokens.Add(new Token(TokenKind.End, "", filter.Length));
        return tokens;
    }

    private static bool IsDelimiter(char character) {
        return char.IsWhiteSpace(character) || character is '(' or ')' or '[' or ']' or '"';
    }

    /// <summary>Reads a string literal, which RFC 7644 writes as a JSON string, escapes included.</summary>
    private static Token ReadString(string filter, ref int position) {
        var start = position;
        position++;
        while (position < filter.Length && filter[position] != '"') {
            position += filter[position] == '\\' ? 2 : 1;
        }
        if (position >= filter.Length) {
            throw FilterParser.Error($"The string starting at position {start} is not closed.");
        }
        position++;
        try {
            return new Token(TokenKind.String, JsonNode.Parse(filter[start..position])!.GetValue<string>(), start);
        } catch (JsonException) {
            throw FilterParser.Error($"The string starting at position {start} is not a valid JSON string.");
        }
    }
}
