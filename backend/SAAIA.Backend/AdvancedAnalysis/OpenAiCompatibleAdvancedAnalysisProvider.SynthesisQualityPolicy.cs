namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private const string SynthesisQualityPolicy = """
        Synthesis quality policy: permission to reuse an item is not a request
        to repeat it. Respect an explicitly requested repetition or stable choice.
        Assess the whole deliverable's usefulness for the stated purpose, not
        only the support of each separate unit. When unrequested repetition
        materially weakens that usefulness, compare supported alternatives and
        inspect promising observed alternatives whose substantive content is
        still missing, using offered research tools within the available budget
        before finalizing. A supported selection may be replaced for this
        substantive improvement even when it has no documentary defect. Preserve
        unaffected facts and citations; a cosmetic rewrite is not an improvement.
        Keep repetition that is requested or useful; do not change choices merely
        to increase a count. For repeated coordinates with the same purpose,
        prefer an unused choice when it is equally suitable and well supported.
        Do not mistake the current sample for all available
        choices. Useful variety is a selection objective, not an extra
        mandatory distinct-item quota or a reason by itself to declare documentary
        insufficiency. Never invent alternatives or change a source identity to
        create variety. Explain a material limitation without claiming that the
        corpus has no alternatives unless its contents establish that fact.

        Descriptive categories, example contexts and positive recommendations
        are not exclusive restrictions unless the source establishes that
        exclusivity. "Recommended for X" does not mean "only permitted in X";
        it does not by itself prove suitability elsewhere. For a requested
        synthesis, judge ordinary compatibility from the documented item and
        the actual request. Do not impose an unstated date, season or other
        eligibility condition, or require a source label matching SAAIA's
        proposed placement.

        For a coordinate that asks for a standalone choice, check whether the
        evidence describes the item as a component, dependency or auxiliary choice.
        An auxiliary use alone does not establish standalone suitability.
        Choose a supported standalone alternative, or, if the format allows a
        combination, cite support for each substantive part. This does not
        disqualify an auxiliary choice when requested, or an item whose
        substantive evidence also supports independent use.

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
