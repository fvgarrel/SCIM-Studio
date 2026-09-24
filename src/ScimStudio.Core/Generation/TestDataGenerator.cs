using System.Diagnostics;
using System.Text;
using ScimStudio.Core.Dialects;
using ScimStudio.Core.Http;
using ScimStudio.Core.Scim;
using ScimStudio.Core.Text;

namespace ScimStudio.Core.Generation;

/// <summary>What the generator creates.</summary>
public sealed record GeneratorOptions {
    public int Users { get; init; } = 25;

    public int Groups { get; init; } = 3;

    /// <summary>How many of the new users each new group gets, drawn at random.</summary>
    public int MembersPerGroup { get; init; } = 5;

    /// <summary>The domain the addresses are at. <c>example.com</c> is reserved for exactly this (RFC 2606).</summary>
    public string Domain { get; init; } = "example.com";

    /// <summary>The share of users created switched off, between 0 and 1.</summary>
    public double InactiveShare { get; init; } = 0.1;

    /// <summary>How many requests are in flight at once.</summary>
    public int Parallelism { get; init; } = 4;
}

/// <summary>Where a generator run has got to.</summary>
/// <param name="Done">Resources handled so far.</param>
/// <param name="Total">Resources to handle.</param>
/// <param name="Failed">Resources that could not be created or removed.</param>
/// <param name="Elapsed">Time since the run started.</param>
public sealed record GeneratorProgress(int Done, int Total, int Failed, TimeSpan Elapsed) {
    /// <summary>Resources per second so far.</summary>
    public double Rate => Elapsed.TotalSeconds > 0 ? Done / Elapsed.TotalSeconds : 0;
}

/// <summary>What a generator run did, and what went wrong.</summary>
public sealed record GeneratorReport {
    public int UsersCreated { get; init; }

    public int GroupsCreated { get; init; }

    public int Removed { get; init; }

    public int Failed { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>The distinct failures, each once, with how often it happened.</summary>
    public IReadOnlyList<Message> Problems { get; init; } = [];
}

/// <summary>
/// Fills a server with people who look real enough to page, sort and filter through, and takes them away again. Everything it creates has an
/// externalId starting with <see cref="MARKER"/>, which is how the clean-up finds it - on a later day, too.
/// </summary>
public sealed class TestDataGenerator {
    public const string MARKER = "scimstudio-gen-";

    private static readonly string[] GivenNames = [
        "Anna", "Lukas", "Sofia", "Jonas", "Mia", "Elias", "Emma", "Noah", "Lea", "Paul", "Hannah", "Felix", "Lina", "Leon", "Clara", "Ben",
        "Yuki", "Haruto", "Aiko", "Omar", "Layla", "Karim", "Amara", "Kwame", "Priya", "Arjun", "Mei", "Wei", "Isabella", "Mateo", "Lucía",
        "Diego", "Chloé", "Louis", "Freya", "Oskar", "Ingrid", "Nils", "Zofia", "Jakub", "Aleksander", "Elif", "Emre", "Ava", "Liam", "Olivia",
        "Ethan", "Grace", "Samuel", "Nora",
    ];

    private static readonly string[] FamilyNames = [
        "Schmidt", "Müller", "Weber", "Fischer", "Wagner", "Becker", "Hoffmann", "Schulz", "Koch", "Richter", "Klein", "Wolf", "Neumann",
        "Tanaka", "Sato", "Haddad", "Mansour", "Okafor", "Mensah", "Sharma", "Patel", "Chen", "Wang", "Rossi", "García", "Fernández", "Dubois",
        "Laurent", "Andersen", "Nilsson", "Kowalski", "Nowak", "Yilmaz", "Demir", "Smith", "Johnson", "Brown", "Taylor", "O'Brien", "Novak",
    ];

    private static readonly string[] Teams = [
        "Engineering", "Platform", "Design", "Marketing", "Sales", "Support", "Finance", "Legal", "People", "Security", "Data", "Research",
        "Operations", "Mobile", "Growth", "Infrastructure", "Customer Success", "Procurement",
    ];

    private readonly ScimClient _client;
    private readonly ScimDialect _dialect;
    private readonly Random _random;

    /// <summary>A generator writing through a dialect.</summary>
    /// <param name="client">The client.</param>
    /// <param name="dialect">How users and groups are created.</param>
    /// <param name="random">The source of names; a seeded one gives the same people twice.</param>
    public TestDataGenerator(ScimClient client, ScimDialect dialect, Random? random = null) {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(dialect);

        _client = client;
        _dialect = dialect;
        _random = random ?? Random.Shared;
    }

    /// <summary>Creates the users, then the groups with members drawn from them.</summary>
    /// <param name="options">What to create.</param>
    /// <param name="progress">Told after each resource.</param>
    /// <param name="cancellationToken">Stops the run; what exists by then stays, and the clean-up finds it.</param>
    public async Task<GeneratorReport> GenerateAsync(
        GeneratorOptions options, IProgress<GeneratorProgress>? progress, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(options);

        using var scope = ExchangeScope.Begin("generator:create");
        var tally = new Tally(options.Users + options.Groups, progress);

        var drafts = People(options);
        var users = new List<ScimUser>();
        var gate = new Lock();

        await Parallel.ForEachAsync(drafts, Throttle(options, cancellationToken), async (draft, token) => {
            try {
                var created = await _dialect.CreateUserAsync(_client, draft, token);
                lock (gate) {
                    users.Add(created.Resource);
                }

                tally.Succeeded();
            } catch (ScimException failure) {
                tally.FailedWith(failure.Error.ToString());
            } catch (HttpRequestException failure) {
                tally.FailedWith(failure.Message);
            }
        });

        var groups = 0;
        foreach (var name in GroupNames(options.Groups)) {
            cancellationToken.ThrowIfCancellationRequested();

            var members = users.OrderBy(_ => _random.Next()).Take(options.MembersPerGroup).Select(u => new ScimMember(u.Id, u.Label, "User"));
            var draft = new GroupDraft { DisplayName = name, ExternalId = $"{MARKER}{Guid.NewGuid():N}", Members = [.. members] };
            try {
                await _dialect.CreateGroupAsync(_client, draft, cancellationToken);
                groups++;
                tally.Succeeded();
            } catch (ScimException failure) {
                tally.FailedWith(failure.Error.ToString());
            } catch (HttpRequestException failure) {
                tally.FailedWith(failure.Message);
            }
        }

        return tally.Report() with { UsersCreated = users.Count, GroupsCreated = groups };
    }

    /// <summary>Deletes every group and user whose externalId carries the marker - groups first, so no member is left pointing nowhere.</summary>
    /// <param name="parallelism">How many requests are in flight at once.</param>
    /// <param name="progress">Told after each resource.</param>
    /// <param name="cancellationToken">Stops the run.</param>
    public async Task<GeneratorReport> RemoveAsync(int parallelism, IProgress<GeneratorProgress>? progress, CancellationToken cancellationToken) {
        using var scope = ExchangeScope.Begin("generator:remove");

        var marked = new ScimQuery { Filter = ScimFilterText.Sw("externalId", MARKER), ExcludedAttributes = ["members"] };
        var groups = await ScimClient.ReadAllAsync(_client.ListGroupsAsync, marked, 10_000, cancellationToken);
        var users = await ScimClient.ReadAllAsync(_client.ListUsersAsync, marked with { ExcludedAttributes = null }, 10_000, cancellationToken);

        // A server may ignore the filter's operator and answer with everything; only what carries the marker goes.
        var paths = groups.Where(g => Marked(g.ExternalId)).Select(g => ScimClient.GroupPath(g.Id))
            .Concat(users.Where(u => Marked(u.ExternalId)).Select(u => ScimClient.UserPath(u.Id)))
            .ToList();

        var tally = new Tally(paths.Count, progress);
        var throttle = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = cancellationToken };

        // Groups one after another before the users, so a user is never deleted while a group still lists it.
        var groupCount = groups.Count(g => Marked(g.ExternalId));
        foreach (var path in paths.Take(groupCount)) {
            await DeleteAsync(path, tally, cancellationToken);
        }

        await Parallel.ForEachAsync(paths.Skip(groupCount), throttle, async (path, token) => {
            await DeleteAsync(path, tally, token);
        });

        return tally.Report() with { Removed = tally.Done - tally.Failures };
    }

    private async Task DeleteAsync(string path, Tally tally, CancellationToken cancellationToken) {
        try {
            var response = await _client.SendAsync(HttpMethod.Delete, path, null, cancellationToken);
            if (response.IsSuccess || response.StatusCode == 404) {
                tally.Succeeded();
            } else {
                tally.FailedWith(ScimError.Read(response.StatusCode, response.Text).ToString());
            }
        } catch (HttpRequestException failure) {
            tally.FailedWith(failure.Message);
        }
    }

    /// <summary>The users to create: names drawn at random, addresses made unique within the run by a number where two collide.</summary>
    /// <param name="options">What to create.</param>
    internal List<UserDraft> People(GeneratorOptions options) {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var people = new List<UserDraft>();

        for (var i = 0; i < options.Users; i++) {
            people.Add(Person(_random, options.Domain, taken) with {
                ExternalId = $"{MARKER}{Guid.NewGuid():N}",
                Active = _random.NextDouble() >= options.InactiveShare,
            });
        }

        return people;
    }

    /// <summary>One person as the generator makes them, for filling in a form by hand; without the marker, since a person keeps it.</summary>
    /// <param name="random">The source of the name.</param>
    /// <param name="domain">The domain of the address.</param>
    /// <param name="taken">Addresses already given out, which a number after the name keeps this one apart from.</param>
    public static UserDraft Person(Random random, string domain = "example.com", ISet<string>? taken = null) {
        ArgumentNullException.ThrowIfNull(random);

        var given = GivenNames[random.Next(GivenNames.Length)];
        var family = FamilyNames[random.Next(FamilyNames.Length)];
        var local = $"{Ascii(given)}.{Ascii(family)}";

        var address = $"{local}@{domain}";
        for (var n = 2; taken is not null && !taken.Add(address); n++) {
            address = $"{local}{n}@{domain}";
        }

        return new UserDraft {
            UserName = address,
            GivenName = given,
            FamilyName = family,
            DisplayName = $"{given} {family}",
            Email = address,
        };
    }

    private IEnumerable<string> GroupNames(int count) {
        var shuffled = Teams.OrderBy(_ => _random.Next()).ToList();
        for (var i = 0; i < count; i++) {
            var team = shuffled[i % shuffled.Count];
            yield return i < shuffled.Count ? $"{team} (generated)" : $"{team} {(i / shuffled.Count) + 1} (generated)";
        }
    }

    private static ParallelOptions Throttle(GeneratorOptions options, CancellationToken cancellationToken) {
        return new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Parallelism), CancellationToken = cancellationToken };
    }

    private static bool Marked(string? externalId) {
        return externalId?.StartsWith(MARKER, StringComparison.Ordinal) == true;
    }

    /// <summary>
    /// A name as the local part of an address: lowercase ASCII, German letters spelled out as German addresses spell them, other accents and
    /// apostrophes dropped, so every mail system takes it.
    /// </summary>
    /// <param name="name">The name.</param>
    internal static string Ascii(string name) {
        var spelled = name.ToLowerInvariant()
            .Replace("ä", "ae", StringComparison.Ordinal)
            .Replace("ö", "oe", StringComparison.Ordinal)
            .Replace("ü", "ue", StringComparison.Ordinal)
            .Replace("ß", "ss", StringComparison.Ordinal);

        var decomposed = spelled.Normalize(NormalizationForm.FormD);
        return new string([.. decomposed.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-')]);
    }

    /// <summary>Counts a run across threads, and reports each step.</summary>
    /// <param name="total">Resources to handle.</param>
    /// <param name="progress">Told after each resource.</param>
    private sealed class Tally(int total, IProgress<GeneratorProgress>? progress) {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, int> _problems = new(StringComparer.Ordinal);
        private readonly long _started = Stopwatch.GetTimestamp();

        public int Done { get; private set; }

        public int Failures { get; private set; }

        public void Succeeded() {
            lock (_gate) {
                Done++;
            }

            Report(progress);
        }

        public void FailedWith(string problem) {
            lock (_gate) {
                Done++;
                Failures++;
                _problems[problem] = _problems.GetValueOrDefault(problem) + 1;
            }

            Report(progress);
        }

        public GeneratorReport Report() {
            lock (_gate) {
                return new GeneratorReport {
                    Failed = Failures,
                    Elapsed = Stopwatch.GetElapsedTime(_started),
                    Problems = [.. _problems.Select(p => Message.Of("generator.problem", p.Key, p.Value))],
                };
            }
        }

        private void Report(IProgress<GeneratorProgress>? sink) {
            GeneratorProgress step;
            lock (_gate) {
                step = new GeneratorProgress(Done, total, Failures, Stopwatch.GetElapsedTime(_started));
            }

            sink?.Report(step);
        }
    }
}
