using System.Text;

public sealed record LineEdit(int LineNumber, string Before, string After, int Occurrences);

public sealed record DocumentEdit(string DocumentId, int DocumentVersion, IReadOnlyList<LineEdit> Lines);

public sealed record FindReplacePlan(string Find, string Replace, IReadOnlyList<DocumentEdit> Documents)
{
    public int TotalLines => Documents.Sum(d => d.Lines.Count);
    public int TotalOccurrences => Documents.Sum(d => d.Lines.Sum(l => l.Occurrences));
}

/// <summary>
/// Computes a find-and-replace plan and renders it for the model. The plan is
/// the single source of truth for both the preview text and the execution, so
/// what was previewed is exactly what runs.
/// </summary>
public static class FindReplacePlanner
{
    public static FindReplacePlan Build(DocumentStore store, string find, string replace)
    {
        var documentEdits = new List<DocumentEdit>();
        foreach (var doc in store.All())
        {
            var lineEdits = new List<LineEdit>();
            for (var i = 0; i < doc.Lines.Length; i++)
            {
                var line = doc.Lines[i];
                if (!line.Contains(find, StringComparison.Ordinal))
                    continue;
                var occurrences = (line.Length - line.Replace(find, "", StringComparison.Ordinal).Length) / find.Length;
                lineEdits.Add(new LineEdit(i + 1, line, line.Replace(find, replace, StringComparison.Ordinal), occurrences));
            }

            if (lineEdits.Count > 0)
                documentEdits.Add(new DocumentEdit(doc.Id, doc.Version, lineEdits));
        }

        return new FindReplacePlan(find, replace, documentEdits);
    }

    /// <summary>
    /// The preview: which lines change, and the sequence of internal API
    /// operations the server will run — the granularity we gave up by folding
    /// search + edit batch + commit into one logical operation, reintroduced
    /// where it matters.
    /// </summary>
    public static string RenderPreview(FindReplacePlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            $"{plan.TotalOccurrences} occurrence(s) of \"{plan.Find}\" on {plan.TotalLines} line(s) " +
            $"in {plan.Documents.Count} document(s) would be replaced with \"{plan.Replace}\":");
        foreach (var edit in plan.Documents)
        {
            sb.AppendLine();
            sb.AppendLine($"{edit.DocumentId}:");
            foreach (var line in edit.Lines)
            {
                sb.AppendLine($"  line {line.LineNumber}:");
                sb.AppendLine($"    - {line.Before}");
                sb.AppendLine($"    + {line.After}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Internal operations that execution will run, in order:");
        var step = 1;
        sb.AppendLine($"  {step++}. documents.search(\"{plan.Find}\") -> {plan.Documents.Count} document(s)");
        sb.AppendLine($"  {step++}. edits.create_batch()");
        foreach (var edit in plan.Documents)
        {
            foreach (var line in edit.Lines)
                sb.AppendLine($"  {step++}. edits.replace_line(document: \"{edit.DocumentId}\", line: {line.LineNumber})");
        }

        sb.AppendLine($"  {step}. edits.commit_batch() -- all edits become visible atomically here");
        return sb.ToString();
    }

    public static string RenderExecuted(FindReplacePlan plan)
    {
        var ids = string.Join(", ", plan.Documents.Select(d => d.DocumentId));
        return $"Replaced {plan.TotalOccurrences} occurrence(s) of \"{plan.Find}\" with \"{plan.Replace}\" " +
               $"on {plan.TotalLines} line(s) across {plan.Documents.Count} document(s): {ids}. " +
               "All edits were applied in one atomic batch. " +
               "Call read_document to see the updated content.";
    }
}
