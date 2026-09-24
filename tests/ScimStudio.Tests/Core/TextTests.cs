using ScimStudio.Core.Http;
using ScimStudio.Core.Scim;

namespace ScimStudio.Tests.Core;

/// <summary>What the tool writes for others to read: filters, query strings, error lines and shell commands.</summary>
public sealed class TextTests {
    [Theory]
    [InlineData("ada", "\"ada\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("C:\\temp", "\"C:\\\\temp\"")]
    [InlineData("Müller", "\"Müller\"")]
    [InlineData("tab\there", "\"tab\\u0009here\"")]
    public void Filter_values_are_quoted_as_json_strings_with_nothing_else_escaped(string value, string quoted) {
        Assert.Equal(quoted, ScimFilterText.Quote(value));
    }

    [Fact]
    public void A_query_string_escapes_its_values_and_leaves_out_what_is_not_set() {
        var query = new ScimQuery { Filter = "userName eq \"a b\"", Count = 5, ExcludedAttributes = ["members", "meta"] };

        Assert.Equal("?filter=userName%20eq%20%22a%20b%22&count=5&excludedAttributes=members%2Cmeta", query.ToQueryString());
        Assert.Equal(string.Empty, new ScimQuery().ToQueryString());
    }

    [Theory]
    [InlineData("{\"schemas\":[\"x\"],\"status\":\"409\",\"scimType\":\"uniqueness\",\"detail\":\"taken\"}", "uniqueness", "taken")]
    [InlineData("{\"title\":\"Not Found\",\"status\":404}", null, "Not Found")]
    [InlineData("<html>Bad gateway</html>", null, "<html>Bad gateway</html>")]
    public void An_error_is_read_from_whatever_came_back(string body, string? scimType, string detail) {
        var error = ScimError.Read(409, body);

        Assert.Equal((scimType, detail), (error.ScimType, error.Detail));
    }

    [Fact]
    public void Curl_names_the_token_by_variable_and_quotes_what_the_shell_would_read() {
        var exchange = Exchange("{\"displayName\":\"Pat O'Brien\"}");

        var curl = ShellCommand.Curl(exchange);

        Assert.Contains("-X PATCH 'https://example.com/scim/v2/Users/42'", curl, StringComparison.Ordinal);
        Assert.Contains("\"Authorization: Bearer $SCIM_TOKEN\"", curl, StringComparison.Ordinal);
        Assert.Contains("'{\"displayName\":\"Pat O'\\''Brien\"}'", curl, StringComparison.Ordinal);
        Assert.DoesNotContain("••••", curl, StringComparison.Ordinal);
        Assert.DoesNotContain("Content-Length", curl, StringComparison.Ordinal);
    }

    [Fact]
    public void PowerShell_reads_the_token_from_the_environment_and_doubles_single_quotes() {
        var command = ShellCommand.PowerShell(Exchange("{\"displayName\":\"Pat O'Brien\"}"));

        Assert.Contains("Authorization = \"Bearer $env:SCIM_TOKEN\"", command, StringComparison.Ordinal);
        Assert.Contains("-Method PATCH", command, StringComparison.Ordinal);
        Assert.Contains("-Body '{\"displayName\":\"Pat O''Brien\"}'", command, StringComparison.Ordinal);
    }

    private static HttpExchange Exchange(string body) {
        return new HttpExchange {
            Sequence = 1,
            StartedAt = DateTimeOffset.UnixEpoch,
            Method = "PATCH",
            Url = new Uri("https://example.com/scim/v2/Users/42"),
            RequestHeaders = [
                new("Accept", "application/scim+json"),
                new("Authorization", "Bearer ••••1234"),
                new("Content-Type", "application/scim+json; charset=utf-8"),
                new("Content-Length", "31"),
            ],
            RequestBody = body,
        };
    }
}
