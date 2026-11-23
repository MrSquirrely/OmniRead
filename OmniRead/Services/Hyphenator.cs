using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace OmniRead.Services;

/// <summary>
/// Finds where words may be broken at the end of a line, using Liang's algorithm and the TeX hyphenation
/// patterns in Data\Hyphenation. The web view has no hyphenation dictionaries of its own, so break points are
/// marked with soft hyphens before the text is shown.
/// </summary>
public sealed class Hyphenator {
    public const char SoftHyphen = '­';

    private static readonly ConcurrentDictionary<string, Hyphenator?> Loaded = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, byte[]> _patterns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _exceptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]>.AlternateLookup<ReadOnlySpan<char>> _lookup;
    private readonly int _left;
    private readonly int _right;
    private readonly int _longest;

    private Hyphenator(string file, int left, int right) {
        _left = left;
        _right = right;
        string source = StripComments(File.ReadAllText(file, Encoding.UTF8));
        foreach (string pattern in Group(source, @"\patterns")) {
            // "a1b2c" means: between a and b a break scores 1, between b and c it scores 2.
            StringBuilder letters = new();
            List<byte> scores = [0];
            foreach (char c in pattern) {
                if (char.IsAsciiDigit(c)) {
                    scores[^1] = (byte)(c - '0');
                }
                else {
                    letters.Append(c);
                    scores.Add(0);
                }
            }
            _patterns[letters.ToString()] = [.. scores];
            _longest = Math.Max(_longest, letters.Length);
        }
        foreach (string word in Group(source, @"\hyphenation")) {
            // Words spelled out with their only permitted breaks.
            string plain = word.Replace("-", "");
            byte[] scores = new byte[plain.Length + 1];
            int at = 0;
            foreach (char c in word) {
                if (c == '-') {
                    scores[at] = 1;
                }
                else {
                    at++;
                }
            }
            _exceptions[plain] = scores;
        }
        _lookup = _patterns.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>The hyphenator for a language code such as "en" or "es-MX", or null when there are no patterns for it.</summary>
    public static Hyphenator? For(string? language) {
        string code = (language ?? "").Split('-', '_')[0].ToLowerInvariant();
        return Loaded.GetOrAdd(code, static key => {
            (string? file, int left, int right) = key switch {
                "en" => ("hyph-en-us.tex", 2, 3),
                "es" => ("hyph-es.tex", 2, 2),
                _ => (null, 0, 0)
            };
            if (file is null) {
                return null;
            }
            try {
                return new Hyphenator(Path.Combine(AppContext.BaseDirectory, "Data", "Hyphenation", file), left, right);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // Without the pattern file the text is simply not hyphenated.
                return null;
            }
        });
    }

    /// <summary>Returns the text with a soft hyphen at every place a word may be broken.</summary>
    public string Hyphenate(string text) {
        StringBuilder? result = null;
        int copied = 0;
        int start = -1;
        for (int i = 0; i <= text.Length; i++) {
            bool letter = (i < text.Length) && char.IsLetter(text[i]);
            if (letter && (start < 0)) {
                start = i;
            }
            else if (!letter && (start >= 0)) {
                ReadOnlySpan<char> word = text.AsSpan(start, i - start);
                if (word.Length >= (_left + _right + 1)) {
                    byte[] scores = Score(word);
                    for (int at = _left; at <= (word.Length - _right); at++) {
                        if ((scores[at] & 1) == 1) {
                            result ??= new StringBuilder(text.Length + 32);
                            result.Append(text, copied, start + at - copied).Append(SoftHyphen);
                            copied = start + at;
                        }
                    }
                }
                start = -1;
            }
        }
        return (result is null) ? text : result.Append(text, copied, text.Length - copied).ToString();
    }

    /// <summary>The break score before each letter of a word; odd scores are permitted breaks.</summary>
    private byte[] Score(ReadOnlySpan<char> word) {
        string lower = word.ToString().ToLowerInvariant();
        if (_exceptions.TryGetValue(lower, out byte[]? known)) {
            return known;
        }
        string dotted = $".{lower}.";
        byte[] scores = new byte[dotted.Length + 1];
        for (int from = 0; from < dotted.Length; from++) {
            int longest = Math.Min(_longest, dotted.Length - from);
            for (int length = 1; length <= longest; length++) {
                if (_lookup.TryGetValue(dotted.AsSpan(from, length), out byte[]? pattern)) {
                    for (int i = 0; i < pattern.Length; i++) {
                        scores[from + i] = Math.Max(scores[from + i], pattern[i]);
                    }
                }
            }
        }
        // Drop the leading dot, so that index i is the break before letter i of the word.
        return scores[1..^1];
    }

    private static string StripComments(string source) {
        StringBuilder stripped = new(source.Length);
        foreach (string line in source.Split('\n')) {
            int comment = line.IndexOf('%');
            stripped.Append((comment >= 0) ? line[..comment] : line).Append('\n');
        }
        return stripped.ToString();
    }

    /// <summary>The whitespace-separated entries of every "\name{ ... }" group in a TeX pattern file.</summary>
    private static IEnumerable<string> Group(string source, string name) {
        int at = 0;
        while ((at = source.IndexOf(name + "{", at, StringComparison.Ordinal)) >= 0) {
            int open = at + name.Length + 1;
            int close = source.IndexOf('}', open);
            if (close < 0) {
                yield break;
            }
            foreach (string entry in source[open..close].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) {
                yield return entry;
            }
            at = close;
        }
    }
}