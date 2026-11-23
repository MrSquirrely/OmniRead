using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace OmniRead.Services;

/// <summary>
/// Looks words up in the dictionary shipped with the reader (Data/dictionary.txt.gz, built from WordNet 3.1 by
/// tools/BuildDictionary.cs). Nothing leaves the computer. The file stays compressed on disk and is read
/// through for each lookup, which takes a fraction of a second and keeps it out of memory.
/// </summary>
public static partial class WordLookup {
    private static readonly string DataFile = Path.Combine(AppContext.BaseDirectory, "Data", "dictionary.txt.gz");

    /// <summary>Endings WordNet strips to reach a dictionary form, with what replaces them.</summary>
    private static readonly (string Ending, string Replacement)[] Endings = [
        ("ies", "y"), ("ses", "s"), ("xes", "x"), ("zes", "z"), ("ches", "ch"), ("shes", "sh"), ("men", "man"),
        ("s", ""), ("es", "e"), ("es", ""), ("ed", "e"), ("ed", ""), ("ing", "e"), ("ing", ""),
        ("er", ""), ("est", ""), ("er", "e"), ("est", "e")
    ];

    public static string SourceName => "WordNet 3.1";

    [GeneratedRegex(@"[\p{L}\p{M}][\p{L}\p{M}'’\-]*")]
    private static partial Regex Word();

    /// <summary>The first word of a selection, without surrounding punctuation; null when there is none.</summary>
    public static string? FirstWord(string selection) {
        Match match = Word().Match(selection);
        return match.Success ? match.Value.Trim('\'', '’', '-').Replace('’', '\'') : null;
    }

    /// <summary>Returns the definitions as readable text, or null when the word is not in the dictionary.</summary>
    public static Task<string?> DefineAsync(string word) {
        return Task.Run(() => Define(word));
    }

    private static string? Define(string word) {
        string lower = word.ToLowerInvariant();
        // Candidates in order of preference: the word as written, then forms with a regular ending removed.
        List<string> candidates = [lower];
        if (lower.EndsWith("'s", StringComparison.Ordinal)) {
            candidates.Add(lower[..^2]);
        }
        foreach ((string ending, string replacement) in Endings) {
            if ((lower.Length > (ending.Length + 1)) && lower.EndsWith(ending, StringComparison.Ordinal)) {
                candidates.Add(lower[..^ending.Length] + replacement);
            }
        }
        if (lower.Contains('-')) {
            candidates.Add(lower.Replace('-', ' '));
        }

        Dictionary<string, string> found = new(StringComparer.Ordinal);
        List<string> irregular = [];
        try {
            using FileStream file = File.OpenRead(DataFile);
            using GZipStream zip = new(file, CompressionMode.Decompress);
            using StreamReader reader = new(zip, Encoding.UTF8);
            while (reader.ReadLine() is string line) {
                int tab = line.IndexOf('\t');
                if (tab <= 0) {
                    continue;
                }
                if (line[0] == '!') {
                    // Irregular forms come first in the file: "!geese<TAB>goose".
                    if (line.AsSpan(1, tab - 1).SequenceEqual(lower)) {
                        irregular.AddRange(line[(tab + 1)..].Split('\t'));
                    }
                    continue;
                }
                string key = line[..tab];
                if (candidates.Contains(key) || irregular.Contains(key)) {
                    found[key] = line[(tab + 1)..];
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) {
            return null;
        }

        // The word as written, its irregular base ("geese" to "goose"), then the first regular guess that exists.
        List<string> shown = [];
        foreach (string key in new[] { lower }.Concat(irregular).Concat(candidates.Skip(1))) {
            if (found.ContainsKey(key) && !shown.Contains(key)) {
                shown.Add(key);
            }
            if (shown.Count == 2) {
                break;
            }
        }
        if (shown.Count == 0) {
            return null;
        }
        StringBuilder text = new();
        foreach (string key in shown) {
            if (text.Length > 0) {
                text.AppendLine();
            }
            if ((shown.Count > 1) || (key != lower)) {
                text.AppendLine(key.ToUpperInvariant());
            }
            foreach (string part in found[key].Split('\t')) {
                text.AppendLine(part[0] switch {
                    'n' => "noun",
                    'v' => "verb",
                    'a' => "adjective",
                    _ => "adverb"
                });
                string[] senses = part[2..].Split('|');
                for (int i = 0; i < senses.Length; i++) {
                    text.AppendLine($"{i + 1}. {senses[i]}");
                }
                text.AppendLine();
            }
        }
        return text.ToString().TrimEnd();
    }
}