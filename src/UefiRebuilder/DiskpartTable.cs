using System;
using System.Collections.Generic;

namespace UefiRebuilder
{
    /// <summary>
    /// Parses the fixed-width tables diskpart prints for "list disk" and "detail disk".
    /// Column positions come from the "-----  ---" ruler line, so it works whatever
    /// language the column headers are in.
    /// </summary>
    internal static class DiskpartTable
    {
        public static List<string[]> Parse(IList<string> lines, string rowPrefix)
        {
            var rows = new List<string[]>();
            for (int i = 0; i < lines.Count; i++)
            {
                string ruler = lines[i];
                if (!IsRuler(ruler)) continue;

                var spans = new List<int[]>();
                int pos = 0;
                while (pos < ruler.Length)
                {
                    if (ruler[pos] == '-')
                    {
                        int start = pos;
                        while (pos < ruler.Length && ruler[pos] == '-') pos++;
                        spans.Add(new[] { start, pos - start });
                    }
                    else pos++;
                }

                for (int j = i + 1; j < lines.Count; j++)
                {
                    string line = lines[j];
                    if (line.Trim().Length == 0) break;
                    // Selected rows start with "*" instead of a space.
                    string clean = line.Length > 0 && line[0] == '*' ? " " + line.Substring(1) : line;
                    if (!clean.TrimStart().StartsWith(rowPrefix, StringComparison.OrdinalIgnoreCase)) continue;

                    var cells = new string[spans.Count];
                    for (int c = 0; c < spans.Count; c++)
                    {
                        int start = spans[c][0];
                        // The last column runs to the end of the line.
                        int len = c == spans.Count - 1 ? Math.Max(0, clean.Length - start) : spans[c][1];
                        cells[c] = start >= clean.Length ? "" :
                            clean.Substring(start, Math.Min(len, clean.Length - start)).Trim();
                    }
                    rows.Add(cells);
                }
                i += 1;
            }
            return rows;
        }

        private static bool IsRuler(string line)
        {
            string t = line.Trim();
            return t.Length > 8 && t.StartsWith("---") && t.Replace("-", "").Replace(" ", "").Length == 0;
        }

        /// <summary>"Volume 4" -> 4, "Disk 0" -> 0.</summary>
        public static int Number(string cell)
        {
            string[] parts = cell.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 && int.TryParse(parts[parts.Length - 1], out int n) ? n : -1;
        }
    }

    internal sealed class DiskEntry
    {
        public int Number;
        public string Line = "";
        public override string ToString() => Line;
    }

    internal sealed class VolumeEntry
    {
        public int Number;
        public string Letter = "";
        public string Label = "";
        public string Fs = "";
        public string Info = "";
        public string Line = "";

        public bool LooksLikeEfi =>
            Info.IndexOf("System", StringComparison.OrdinalIgnoreCase) >= 0 ||
            (Fs.Equals("FAT32", StringComparison.OrdinalIgnoreCase) && Info.IndexOf("Hidden", StringComparison.OrdinalIgnoreCase) >= 0);

        public override string ToString() => Line + (LooksLikeEfi ? "   <- EFI" : "");
    }
}
