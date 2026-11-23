// Builds OmniRead's offline dictionary from the WordNet 3.1 database files.
//
//   dotnet run tools/BuildDictionary.cs -- <folder holding data.noun, index.noun, noun.exc, ...> <output folder>
//
// WordNet is downloaded from https://wordnetcode.princeton.edu/wn3.1.dict.tar.gz. The output is a gzip-compressed
// text file: first the irregular forms ("!geese<TAB>goose"), then one line per word in ordinal order
// ("lantern<TAB>n:first sense|second sense<TAB>v:..."), with senses in WordNet's own order of frequency.

using System.IO.Compression;
using System.Text;

public static class Program {
    private const int MaxSenses = 5;

    private static readonly (string File, char Tag)[] Parts = [("noun", 'n'), ("verb", 'v'), ("adj", 'a'), ("adv", 'r')];

    public static int Main(string[] args) {
        if (args.Length != 2) {
            Console.Error.WriteLine("Usage: BuildDictionary <wordnet dict folder> <output folder>");
            return 1;
        }
        string source = args[0];
        string target = args[1];
        Directory.CreateDirectory(target);

        SortedDictionary<string, StringBuilder> entries = new(StringComparer.Ordinal);
        SortedDictionary<string, SortedSet<string>> forms = new(StringComparer.Ordinal);
        List<string> license = [];
        foreach ((string file, char tag) in Parts) {
            // Each data line is "offset ... | gloss"; the gloss is the definition, then quoted examples.
            Dictionary<string, string> glosses = [];
            foreach (string line in File.ReadLines(Path.Combine(source, "data." + file))) {
                if (line.StartsWith("  ", StringComparison.Ordinal)) {
                    if (tag == 'n') {
                        // Licence lines are numbered: "  12 text".
                        string text = line.TrimStart().TrimStart("0123456789".ToCharArray());
                        license.Add(text.StartsWith(' ') ? text[1..].TrimEnd() : text.TrimEnd());
                    }
                    continue;
                }
                int bar = line.IndexOf(" | ", StringComparison.Ordinal);
                if (bar < 0) {
                    continue;
                }
                string gloss = line[(bar + 3)..].Trim();
                int examples = gloss.IndexOf("; \"", StringComparison.Ordinal);
                if (examples > 0) {
                    gloss = gloss[..examples];
                }
                glosses[line[..8]] = gloss.Replace('|', '/').Replace('\t', ' ').Trim();
            }
            // Each index line is "lemma pos synset_cnt p_cnt [pointers] sense_cnt tagsense_cnt offset...".
            foreach (string line in File.ReadLines(Path.Combine(source, "index." + file))) {
                if (line.StartsWith("  ", StringComparison.Ordinal)) {
                    continue;
                }
                string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int senses = int.Parse(fields[2]);
                List<string> definitions = [.. fields[^senses..]
                    .Select(offset => glosses.GetValueOrDefault(offset, ""))
                    .Where(static definition => definition.Length > 0)
                    .Take(MaxSenses)];
                if (definitions.Count == 0) {
                    continue;
                }
                string lemma = fields[0].Replace('_', ' ');
                if (!entries.TryGetValue(lemma, out StringBuilder? entry)) {
                    entry = new StringBuilder(lemma);
                    entries[lemma] = entry;
                }
                entry.Append('\t').Append(tag).Append(':').AppendJoin('|', definitions);
            }
            foreach (string line in File.ReadLines(Path.Combine(source, file + ".exc"))) {
                string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 2) {
                    continue;
                }
                string form = fields[0].Replace('_', ' ');
                if (!forms.TryGetValue(form, out SortedSet<string>? lemmas)) {
                    lemmas = new SortedSet<string>(StringComparer.Ordinal);
                    forms[form] = lemmas;
                }
                lemmas.UnionWith(fields[1..].Select(static lemma => lemma.Replace('_', ' ')));
            }
        }

        string output = Path.Combine(target, "dictionary.txt.gz");
        using (FileStream file = File.Create(output))
        using (GZipStream zip = new(file, CompressionLevel.SmallestSize))
        using (StreamWriter writer = new(zip, new UTF8Encoding(false)) { NewLine = "\n" }) {
            foreach ((string form, SortedSet<string> lemmas) in forms) {
                writer.WriteLine($"!{form}\t{string.Join('\t', lemmas)}");
            }
            foreach (StringBuilder entry in entries.Values) {
                writer.WriteLine(entry.ToString());
            }
        }
        File.WriteAllLines(Path.Combine(target, "WORDNET-LICENSE.txt"), license);
        Console.WriteLine($"{entries.Count} words, {forms.Count} irregular forms, {new FileInfo(output).Length / 1024} KB");
        return 0;
    }
}