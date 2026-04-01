namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow
{
    private readonly ApiClient _api = new();
    private readonly OpenAiLlmClient _llm = new();
    private AppSettings _appSettings = AppSettings.Load();
    private readonly LlamaCppProcessManager _llmProc = new();
    private readonly DownloadManager _downloads = new();
    private readonly Services.LocalLlmBootstrapper _llmBootstrapper = new();
    private RagChatAgent? _agent;

    private readonly ObservableCollection<ChatMessageItem> _messages = new();
    private readonly ObservableCollection<ChatSessionItem> _sessions = new();

    private string? _sessionId;
    private string _userId = "";
    private CancellationTokenSource? _cts;
    private bool _isGenerating;
    private bool _isLoadingSession;
    private bool _suppressSessionSelectionChanged;
    private bool _isCancellingGeneration;
    private bool _autoFollow = true;          // si true: on suit le bas pendant streaming
    private bool _userScrolledUp = false;     // si true: on ne force plus le scroll
    private DateTime _lastAutoScroll = DateTime.MinValue;
    private bool _isProgrammaticScroll = false;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _statusHideTimer;
    private bool _typingPinned;

    private bool _setupAutoPrompted;
    private string? _pendingOutboundWireText;
    private string? _pendingOutboundDisplayText;

    private int _secretAdminClickCount;
    private DateTimeOffset _secretAdminFirstClickUtc = DateTimeOffset.MinValue;
    private static readonly TimeSpan SecretAdminClickWindow = TimeSpan.FromMilliseconds(1500);

    private bool _chatsCollapsed;
    private readonly object _directCommandTrackerGate = new();
    private readonly List<CancellationTokenSource> _directCommandTrackers = new();

    private sealed class ActiveDirectCommandTrackerState
    {
        public required DirectCommandTrackedJob Job { get; init; }
        public required string? SessionId { get; set; }
        public required CancellationTokenSource Cancellation { get; init; }
        public string? MessageId { get; set; }
        public ChatMessageItem? Message { get; set; }
        public string? LastContent { get; set; }
        public string? LastProgressText { get; set; }
        public string? LastStatusNote { get; set; }
        public bool IsTerminal { get; set; }
        public bool TrackingLoopStarted { get; set; }
        public DateTimeOffset? StartedAtUtc { get; set; }
        public DateTimeOffset? LastSnapshotAtUtc { get; set; }
        public string? LastKnownStatus { get; set; }
        public string? LastKnownProgressPhase { get; set; }
        public int? LastKnownProgressCurrent { get; set; }
        public int? LastKnownProgressTotal { get; set; }
        public int? LastKnownProgressPercent { get; set; }
        public DateTimeOffset LastPersistedAtUtc { get; set; }
        public string? LastPersistedContent { get; set; }
        public string? LastPersistedProgressText { get; set; }
        public string? LastPersistedStatusNote { get; set; }
        public string? LastPersistedTrackingMetaJson { get; set; }
        public bool LastPersistedTerminal { get; set; }
    }

    private readonly Dictionary<string, ActiveDirectCommandTrackerState> _activeDirectCommandTrackers = new(StringComparer.OrdinalIgnoreCase);

}
