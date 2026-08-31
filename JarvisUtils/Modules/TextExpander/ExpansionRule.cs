namespace JarvisUtils.Modules.TextExpander;

public sealed class ExpansionRule
{
    public string TriggerPattern { get; set; } = "";      // например ":rand%1-%2:"
    public string ReplacementTemplate { get; set; } = ""; // например "{rand(%1,%2)}"
}