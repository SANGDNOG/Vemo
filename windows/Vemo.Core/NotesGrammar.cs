using System.Globalization;
using System.Text;

namespace Vemo.Core;

public static class NotesGrammar
{
    // b11714 applies the grammar to this Qwen generation prefix as well as JSON.
    public const string AssistantPrefix = "<|im_start|>assistant\n<think>\n\n</think>\n\n";

    static string Literal(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

    public static string Create(int sourceCount)
    {
        if (sourceCount < 1 || sourceCount > LongNotes.SourceByteLimit)
            throw new ArgumentOutOfRangeException(nameof(sourceCount));
        var grammar = new StringBuilder();
        grammar.Append("root ::= ").Append(Literal(AssistantPrefix)).AppendLine(" object");
        grammar.AppendLine("""
            object ::= "{" ws "\"schema_version\"" ws ":" ws "1" ws "," ws "\"topics\"" ws ":" ws "[" ws topic (ws "," ws topic){0,7} ws "]" ws "}"
            topic ::= "{" ws "\"heading\"" ws ":" ws heading ws "," ws "\"summary\"" ws ":" ws summary ws "," ws "\"condition_refs\"" ws ":" ws groups ws "," ws "\"example_refs\"" ws ":" ws groups ws "," ws "\"refs\"" ws ":" ws refs ws "}"
            heading ::= "\"" char{1,100} "\""
            summary ::= "\"" char{1,700} "\""
            char ::= [^"\\\x00-\x1f] | "\\" (["\\/bfnrt] | "u" [0-9a-fA-F]{4})
            groups ::= "[" ws (group (ws "," ws group){0,2})? ws "]"
            refs ::= "[" ws index (ws "," ws index){0,63} ws "]"
            ws ::= [ \t\n\r]{0,8}
            """);
        string Number(int n) => n.ToString(CultureInfo.InvariantCulture);
        grammar.Append("index ::= ").AppendLine(string.Join(" | ", Enumerable.Range(1, sourceCount).Select(n => Literal(Number(n)))));
        grammar.Append("group ::= ").AppendLine(string.Join(" | ", Enumerable.Range(1, sourceCount).Select(n => "group-" + Number(n))));
        for (int start = 1; start <= sourceCount; start++)
        {
            grammar.Append("group-").Append(Number(start)).Append(" ::= \"[\" ws ").Append(Literal(Number(start)));
            int extra = Math.Min(7, sourceCount - start);
            for (int offset = 1; offset <= extra; offset++)
                grammar.Append(" (ws \",\" ws ").Append(Literal(Number(start + offset)));
            for (int offset = 0; offset < extra; offset++) grammar.Append(")?");
            grammar.AppendLine(" ws \"]\"");
        }
        return grammar.ToString();
    }
}
