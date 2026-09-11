namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private readonly ISourceBackedAgentLlmClient _llm;
    private readonly ISourceBackedAgentToolExecutor _toolExecutor;
    private readonly SourceBackedAgentV2Options _options;
    private readonly Action<SourceBackedTraceEvent>? _traceSink;
    private readonly ISourceBackedNamedDocumentResolver? _namedDocumentResolver;

    public SourceBackedAgentV2Runner(
        ISourceBackedAgentLlmClient llm,
        ISourceBackedAgentToolExecutor toolExecutor,
        SourceBackedAgentV2Options options,
        Action<SourceBackedTraceEvent>? traceSink = null,
        ISourceBackedNamedDocumentResolver? namedDocumentResolver = null)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _toolExecutor = toolExecutor
                        ?? throw new ArgumentNullException(nameof(toolExecutor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (_options.SemanticAnswerTransactionEnabled
            && _options.SemanticResolutionWriterReviewEnabled)
        {
            throw new InvalidOperationException(
                "Semantic answer transaction V0 and semantic resolution/writer/review V2 cannot be enabled together.");
        }
        _traceSink = traceSink;
        _namedDocumentResolver = namedDocumentResolver;
    }
}
