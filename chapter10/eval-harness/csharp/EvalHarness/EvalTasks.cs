// The task corpus: prompt + outcome checks, per §10.1.2's measurement axes. Each check is
// tagged Behavioral (tool selection, sequencing, efficiency, recovery — properties of how
// the model used the surface) or Outcome (was the answer actually right — properties that
// need real data). Rung-1 mock mode (§10.1.4) runs the behavioral checks only: with
// fabricated results there is no "right answer" to check against.
//
// The outcome checks are a demonstration of outcome scoring, not a production scorer: a
// pattern match can't tell "30 days for annual plans" from "Invoices are payable within
// 30 days." Real evals extract a structured answer and compare it to the expected value, or
// grade against a rubric.

using System.Text.RegularExpressions;

enum CheckKind { Behavioral, Outcome }

sealed record Check(string Description, CheckKind Kind, Func<Transcript, bool> Passes);

sealed record EvalTask(string Id, string Prompt, IReadOnlyList<Check> Checks);

sealed record ToolCallRecord(string Name, string ArgumentsJson, string ResultText, bool IsError);

sealed class Transcript
{
    public List<ToolCallRecord> ToolCalls { get; } = [];
    public string FinalAnswer { get; set; } = "";
    public int ModelTurns { get; set; }
    public string Termination { get; set; } = "turn_budget"; // or "final_answer"
}

static class Checks
{
    // Applied to every task in every mode, ahead of the task's own checks.
    public static IReadOnlyList<Check> Common => [Completed()];

    // A run that ran out of turns, or ended on an empty reply, fails whatever else it did.
    public static Check Completed() => new(
        "gave a final answer", CheckKind.Behavioral,
        t => t.Termination == "final_answer" && !string.IsNullOrWhiteSpace(t.FinalAnswer));

    public static Check ToolCalled(string name) => new(
        $"called {name}", CheckKind.Behavioral,
        t => t.ToolCalls.Any(c => c.Name == name));

    public static Check NoToolCalls() => new(
        "answered without calling any tool", CheckKind.Behavioral,
        t => t.ToolCalls.Count == 0);

    public static Check CalledInOrder(params string[] names) => new(
        $"called {string.Join(" then ", names)}", CheckKind.Behavioral,
        t =>
        {
            int next = 0;
            foreach (var call in t.ToolCalls)
                if (next < names.Length && call.Name == names[next])
                    next++;
            return next == names.Length;
        });

    public static Check MaxToolCalls(int max) => new(
        $"at most {max} tool calls", CheckKind.Behavioral,
        t => t.ToolCalls.Count <= max);

    // Recovery (§10.1.2): a tool call failed, and the model went on to complete anyway.
    public static Check RecoveredAfterError() => new(
        "recovered after a failed tool call", CheckKind.Behavioral,
        t => t.ToolCalls.Any(c => c.IsError) &&
             t.ToolCalls.FindLastIndex(c => c.IsError) < t.ToolCalls.Count - 1 &&
             t.FinalAnswer.Length > 0);

    // Bounded match: "3 documents" must not match inside "13 documents", and a negated
    // mention ("not 30 days") doesn't count as stating the value.
    public static Check AnswerContains(string text) => new(
        $"answer contains \"{text}\"", CheckKind.Outcome,
        t => Mentions(t.FinalAnswer, text, countNegated: false));

    // The competing wrong value: an answer that mentions it at all hedges or contradicts
    // itself, so any bounded mention fails, negated or not.
    public static Check AnswerOmits(string text) => new(
        $"answer omits \"{text}\"", CheckKind.Outcome,
        t => !Mentions(t.FinalAnswer, text, countNegated: true));

    private static bool Mentions(string answer, string text, bool countNegated) =>
        Regex.IsMatch(answer,
            (countNegated ? "" : @"(?<!\bnot\s+)") + @"(?<!\d)" + Regex.Escape(text) + @"(?!\d)",
            RegexOptions.IgnoreCase);
}

static class EvalCorpus
{
    public static IReadOnlyList<EvalTask> Tasks =>
    [
        new("simple-search",
            "What is the standard shipping time for orders?",
            [
                Checks.ToolCalled("search_documents"),
                Checks.MaxToolCalls(2),
                Checks.AnswerContains("3-5 business days"),
            ]),

        // The dud task (§10.1.2). Keyword search ranks the deprecated 2024 FAQ above the
        // current policy, and a model that reads the first hit answers "60 days" — every
        // call valid, no errors, a confident answer. Only the outcome checks catch it.
        new("refund-window-annual",
            "What is the refund window for annual plans?",
            [
                Checks.ToolCalled("search_documents"),
                Checks.AnswerContains("30 days"),
                Checks.AnswerOmits("60 days"),
            ]),

        // The flaky task. The scripted model only sometimes reaches for count_documents
        // (see ScriptedChatClient) — a stand-in for the marginal-context variance that
        // §10.1.5's N-run pass rates exist to surface.
        new("count-billing-docs",
            "How many documents are tagged 'billing'?",
            [
                Checks.ToolCalled("count_documents"),
                Checks.AnswerContains("3 documents"),
            ]),

        // Recovery path (§10.1.2): the prompt plants a wrong id, the first read fails, and
        // the error message has to earn its keep by steering the model to search first.
        new("recover-from-bad-id",
            "What is the hourly API rate limit? Start by reading the document with id 'api-limits'.",
            [
                Checks.RecoveredAfterError(),
                Checks.MaxToolCalls(4),
                Checks.AnswerContains("10,000"),
            ]),

        // Tool selection, negative axis: did the model refrain when no tool was needed?
        new("no-tool-needed",
            "What does HTTP status code 404 mean?",
            [
                Checks.NoToolCalls(),
                Checks.AnswerContains("not found"),
            ]),

        new("summarize-onboarding",
            "Summarize the onboarding checklist document.",
            [
                Checks.CalledInOrder("search_documents", "read_document"),
                Checks.MaxToolCalls(3),
                Checks.AnswerContains("sandbox"),
            ]),
    ];
}
