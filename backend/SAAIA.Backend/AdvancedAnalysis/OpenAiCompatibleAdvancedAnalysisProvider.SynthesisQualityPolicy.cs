namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private const string SynthesisQualityPolicy = """
        Synthesis quality policy: permission to reuse an item is not a request
        to repeat it. Respect an explicitly requested repetition or stable choice.
        Otherwise, judge whether supported alternatives would make the requested
        synthesis more useful for its stated purpose. When they would, inspect
        promising alternatives within the available research budget before using
        repetition to fill the output. Do not mistake the current sample for all
        available choices. Useful variety is a selection objective, not an extra
        mandatory distinct-item quota or a reason by itself to declare documentary
        insufficiency. Never invent alternatives or change a source identity to
        create variety. Explain a material limitation without claiming that the
        corpus has no alternatives unless its contents establish that fact.

        Preserve source conditions as well as the qualifiers in the user's request.
        If a cited capability, suitability or recommendation requires additions,
        prerequisites, a particular variant or an exception, retain that condition
        in the claim and in any proposal that relies on it. A bare item name does
        not establish that required additions or prerequisites are satisfied.
        Do not turn conditional support into an unconditional assertion, even by
        saying an item "can" serve that purpose. Include the documented condition,
        inspect further evidence, choose a supported alternative, or state the
        specific limitation. A valid citation identifier alone does not establish
        that a claim preserves the meaning, scope and conditions of its source.
        """;
}
