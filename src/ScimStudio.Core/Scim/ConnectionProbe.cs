using System.Text.Json.Nodes;
using ScimStudio.Core.Text;

namespace ScimStudio.Core.Scim;

/// <summary>What a connection test found out: whether the server answers, whether it takes the token, and what it offers.</summary>
public sealed record ProbeReport {
    public bool Reachable { get; init; }

    public bool Authenticated { get; init; }

    public ServiceProviderConfig? Configuration { get; init; }

    public int? UserCount { get; init; }

    public int? GroupCount { get; init; }

    public TimeSpan Duration { get; init; }

    /// <summary>What went wrong, most important first; empty when nothing did.</summary>
    public IReadOnlyList<Message> Problems { get; init; } = [];

    public bool Succeeded => Reachable && Authenticated && Problems.Count == 0;
}

/// <summary>
/// Tests a connection the way a person would: the configuration first, since it says what the server supports, then one user and one group,
/// since some servers leave discovery open and only the resources tell whether the token works.
/// </summary>
public static class ConnectionProbe {
    public static async Task<ProbeReport> RunAsync(ScimClient client, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(client);

        var started = DateTimeOffset.UtcNow;
        var problems = new List<Message>();
        ServiceProviderConfig? configuration = null;
        int? users = null;
        int? groups = null;

        try {
            var answer = await client.SendAsync(HttpMethod.Get, "/ServiceProviderConfig", null, cancellationToken);
            if (answer.StatusCode is 401 or 403) {
                return Report(started, reachable: true, authenticated: false, [Refused(answer)]);
            }

            if (answer.IsSuccess && answer.Body is JsonObject document) {
                configuration = ServiceProviderConfig.Read(document);
            } else {
                problems.Add(answer.StatusCode == 404
                    ? Message.Of("probe.configMissing", client.Resolve("/ServiceProviderConfig").AbsoluteUri)
                    : Message.Of("probe.configFailed", ScimError.Read(answer.StatusCode, answer.Text).ToString()));
            }

            var userList = await client.SendAsync(HttpMethod.Get, "/Users?startIndex=1&count=1", null, cancellationToken);
            if (userList.StatusCode is 401 or 403) {
                return Report(started, reachable: true, authenticated: false, [Refused(userList)]);
            }

            if (userList.IsSuccess && userList.Body is JsonObject userPage) {
                users = ScimJson.Number(ScimJson.Get(userPage, "totalResults"));
            } else {
                problems.Add(Message.Of("probe.usersFailed", ScimError.Read(userList.StatusCode, userList.Text).ToString()));
            }

            var groupPath = "/Groups?startIndex=1&count=1&excludedAttributes=members";
            var groupList = await client.SendAsync(HttpMethod.Get, groupPath, null, cancellationToken);
            if (groupList.IsSuccess && groupList.Body is JsonObject groupPage) {
                groups = ScimJson.Number(ScimJson.Get(groupPage, "totalResults"));
            } else {
                problems.Add(Message.Of("probe.groupsFailed", ScimError.Read(groupList.StatusCode, groupList.Text).ToString()));
            }
        } catch (HttpRequestException failure) {
            return Report(started, reachable: false, authenticated: false, [Message.Of("probe.unreachable", Innermost(failure))]);
        } catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) {
            return Report(started, reachable: false, authenticated: false, [Message.Of("probe.timeout", client.Connection.Timeout.TotalSeconds)]);
        }

        return Report(started, reachable: true, authenticated: true, problems) with {
            Configuration = configuration,
            UserCount = users,
            GroupCount = groups,
        };
    }

    private static Message Refused(ScimResponse answer) {
        var error = ScimError.Read(answer.StatusCode, answer.Text);
        return Message.Of(answer.StatusCode == 401 ? "probe.unauthorized" : "probe.forbidden", error.Detail ?? string.Empty);
    }

    private static ProbeReport Report(DateTimeOffset started, bool reachable, bool authenticated, List<Message> problems) {
        return new ProbeReport {
            Reachable = reachable,
            Authenticated = authenticated,
            Problems = problems,
            Duration = DateTimeOffset.UtcNow - started,
        };
    }

    private static string Innermost(Exception failure) {
        var innermost = failure;
        while (innermost.InnerException is not null) {
            innermost = innermost.InnerException;
        }

        return innermost.Message;
    }
}
