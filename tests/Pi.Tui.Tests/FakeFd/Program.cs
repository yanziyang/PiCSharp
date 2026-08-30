using System.Text.RegularExpressions;

var arguments = args.ToList();
var baseDirectory = GetRequiredOption(arguments, "--base-directory");
var maxResults = int.Parse(GetRequiredOption(arguments, "--max-results"), System.Globalization.CultureInfo.InvariantCulture);
var maxDepth = GetOptionalOption(arguments, "--max-depth");
var query = GetQuery(arguments);

var entries = new List<Entry>();
Walk(baseDirectory, string.Empty, 0, entries, maxDepth, query);

foreach (var entry in entries.Take(maxResults))
{
    Console.WriteLine(entry.IsDirectory ? entry.RelativePath + "/" : entry.RelativePath);
}

return;

static void Walk(
    string directory,
    string relativeDirectory,
    int depth,
    ICollection<Entry> results,
    int? maxDepth,
    string query)
{
    IEnumerable<string> children;
    try
    {
        children = Directory.EnumerateFileSystemEntries(directory)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal);
    }
    catch
    {
        return;
    }

    foreach (var child in children)
    {
        var name = Path.GetFileName(child);
        if (name is ".git")
        {
            continue;
        }

        var relativePath = string.IsNullOrEmpty(relativeDirectory)
            ? name
            : relativeDirectory + "/" + name;
        var isDirectory = Directory.Exists(child);
        var childDepth = depth + 1;

        if ((maxDepth is null || childDepth <= maxDepth.Value) && Matches(relativePath, query))
        {
            results.Add(new Entry(relativePath.Replace('\\', '/'), isDirectory));
        }

        if (isDirectory && (maxDepth is null || childDepth < maxDepth.Value))
        {
            Walk(child, relativePath, childDepth, results, maxDepth, query);
        }
    }
}

static bool Matches(string relativePath, string query)
{
    if (string.IsNullOrEmpty(query))
    {
        return true;
    }

    try
    {
        return Regex.IsMatch(
            relativePath.Replace('\\', '/'),
            query,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
    catch
    {
        return false;
    }
}

static string GetRequiredOption(IReadOnlyList<string> arguments, string option)
{
    var index = -1;
    for (var candidateIndex = 0; candidateIndex < arguments.Count; candidateIndex++)
    {
        if (arguments[candidateIndex] == option)
        {
            index = candidateIndex;
            break;
        }
    }

    return index >= 0 && index + 1 < arguments.Count ? arguments[index + 1] : string.Empty;
}

static int? GetOptionalOption(IReadOnlyList<string> arguments, string option)
{
    var value = GetRequiredOption(arguments, option);
    return int.TryParse(value, out var parsed) ? parsed : null;
}

static string GetQuery(IReadOnlyList<string> arguments)
{
    for (var index = 0; index < arguments.Count;)
    {
        var argument = arguments[index];
        if (argument is "--base-directory" or "--max-results" or "--max-depth" or "--type" or "--exclude")
        {
            index += 2;
            continue;
        }

        if (argument is "--follow" or "--hidden" or "--full-path")
        {
            index++;
            continue;
        }

        return argument;
    }

    return string.Empty;
}

internal readonly record struct Entry(string RelativePath, bool IsDirectory);
