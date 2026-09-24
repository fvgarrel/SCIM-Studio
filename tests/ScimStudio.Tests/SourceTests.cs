using System.Runtime.CompilerServices;

namespace ScimStudio.Tests;

/// <summary>
/// The one rule of the code style that <c>dotnet format</c> cannot apply: lines go up to 150 columns, code and comments alike. Checked here,
/// since a rule nobody checks is a rule that is already broken somewhere.
/// </summary>
public sealed class SourceTests {
    private const int LIMIT = 150;

    private static readonly string[] Folders = ["src", "tests"];

    [Fact]
    public void No_line_is_longer_than_150_columns() {
        var root = Root();
        var offenders = Folders
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*.*", SearchOption.AllDirectories))
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".axaml", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, number: index + 1)))
            .Where(entry => entry.line.Length > LIMIT)
            .Select(entry => $"{Path.GetRelativePath(root, entry.path)}:{entry.number} ({entry.line.Length})")
            .ToList();

        Assert.True(offenders.Count == 0, $"Lines over {LIMIT} columns:\n{string.Join('\n', offenders)}");
    }

    /// <summary>The repository, found from where this file was compiled - the build output may be anywhere.</summary>
    /// <param name="source">This file, as the compiler saw it.</param>
    private static string Root([CallerFilePath] string source = "") {
        var directory = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ScimStudio.slnx"))) {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
