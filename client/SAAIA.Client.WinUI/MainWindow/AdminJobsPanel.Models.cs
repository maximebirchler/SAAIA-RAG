using System.Globalization;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow
{
    private enum AdminJobsLaunchMode
    {
        Default = 0,
        CapabilityAEnrichment = 1,
        CapabilityBBackoffice = 2
    }

    private Window? _adminJobsWindow;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _adminJobsRefreshTimer;
    private AdminJobsOverlayContext? _adminJobsOverlayContext;

    private sealed class AdminJobsOverlayContext
    {
        public required Border Shell { get; init; }
        public required TextBlock SummaryText { get; init; }
        public required TextBlock SelectionText { get; init; }
        public required TextBox SearchBox { get; init; }
        public required ComboBox DateFieldCombo { get; init; }
        public required ComboBox DatePresetCombo { get; init; }
        public required ComboBox SortDirectionCombo { get; init; }
        public required CalendarDatePicker DateFromPicker { get; init; }
        public required CalendarDatePicker DateToPicker { get; init; }
        public required ComboBox TypeCombo { get; init; }
        public required Button IngestionCategoryButton { get; init; }
        public required Button SummaryCategoryButton { get; init; }
        public required ComboBox StatusCombo { get; init; }
        public required ToggleSwitch AutoRefreshToggle { get; init; }
        public required Button CapabilityAKpiButton { get; init; }
        public required Button CapabilityBQualityButton { get; init; }
        public required Button RefreshButton { get; init; }
        public required Button DeleteSelectionButton { get; init; }
        public required Button PurgeButton { get; init; }
        public required StackPanel MetricsHost { get; init; }
        public required StackPanel GroupsHost { get; init; }
        public required Border DetailsCard { get; init; }
        public required ScrollViewer DetailsScrollViewer { get; init; }
        public required StackPanel DetailsHost { get; init; }
        public required ColumnDefinition DetailsColumn { get; init; }
        public AdminJobsLaunchMode LaunchMode { get; set; }
        public List<AdminJobListItem> Items { get; set; } = new();
        public HashSet<string> SelectedTerminalJobIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsRefreshing { get; set; }
        public int HistoryTake { get; set; } = 50;
        public string? SelectedJobId { get; set; }
        public string? LastVisibleRenderSignature { get; set; }
        public string? LastRenderedSelectedJobId { get; set; }
        public bool ResetDetailsScrollPending { get; set; }
        public bool IncludeIngestionCategory { get; set; } = true;
        public bool IncludeSummaryCategory { get; set; } = true;
        public HashSet<string> SelectedMetricFilters { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool HasMetricFilterInteraction { get; set; }
        public string DateField { get; set; } = "finished";
        public string DatePreset { get; set; } = "all";
        public string SortDirection { get; set; } = "desc";
        public DateTimeOffset? DateFrom { get; set; }
        public DateTimeOffset? DateTo { get; set; }
    }

    private sealed class AdminJobListItem
    {
        public string JobId { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
        public string JobType { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string? DocId { get; init; }
        public string? DocPath { get; init; }
        public string? Level { get; init; }
        public string? LastError { get; init; }
        public DateTimeOffset? CreatedAt { get; init; }
        public DateTimeOffset? StartedAt { get; init; }
        public DateTimeOffset? FinishedAt { get; init; }
        public string? ProgressPhase { get; init; }
        public int? ProgressCurrent { get; init; }
        public int? ProgressTotal { get; init; }
        public int? ProgressPercent { get; init; }
        public bool? CancelRequested { get; init; }
        public string? EnqueueSource { get; init; }
        public string? RuntimeCapabilityKey { get; init; }
        public string? ExecutionMode { get; init; }
        public string? DocumentStatus { get; init; }
        public int? DocumentIngestionVersion { get; init; }
        public int? DocumentIndexedVersion { get; init; }
        public bool? DocumentAutoIngestPaused { get; init; }
        public string? DocumentAutoIngestPauseReason { get; init; }

        public bool IsTerminal => IsTrackedJobTerminalStatus(Status);
        public bool IsRunning
            => string.Equals(NormalizeTrackedJobStatus(Status), "running", StringComparison.OrdinalIgnoreCase)
               || string.Equals(NormalizeTrackedJobStatus(Status), "cancel_requested", StringComparison.OrdinalIgnoreCase);
        public bool IsQueued => string.Equals(NormalizeTrackedJobStatus(Status), "queued", StringComparison.OrdinalIgnoreCase);
        public bool IsPaused => string.Equals(NormalizeTrackedJobStatus(Status), "paused", StringComparison.OrdinalIgnoreCase);
        public bool IsFailedLike
            => string.Equals(NormalizeTrackedJobStatus(Status), "failed", StringComparison.OrdinalIgnoreCase)
               || string.Equals(NormalizeTrackedJobStatus(Status), "canceled", StringComparison.OrdinalIgnoreCase);
        public bool IsCanceled
            => string.Equals(NormalizeTrackedJobStatus(Status), "canceled", StringComparison.OrdinalIgnoreCase);
        public bool IsFailed
            => string.Equals(NormalizeTrackedJobStatus(Status), "failed", StringComparison.OrdinalIgnoreCase);

        public string DisplayTitle
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(DocPath))
                    return Path.GetFileName(DocPath) ?? DocPath!;
                if (!string.IsNullOrWhiteSpace(Level))
                    return Level!;
                return JobId;
            }
        }

        public string? DisplaySubtitle
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(DocPath))
                {
                    var directory = Path.GetDirectoryName(DocPath!)?.Replace('\\', '/');
                    if (!string.IsNullOrWhiteSpace(directory) && !string.Equals(directory, ".", StringComparison.Ordinal))
                        return directory;
                }

                return null;
            }
        }
    }

    private static string NormalizeAdminJobsDateField(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "created" => "created",
            "started" => "started",
            _ => "finished"
        };
    }

    private static string NormalizeAdminJobsSortDirection(string? value)
        => string.Equals((value ?? string.Empty).Trim(), "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";

    private static string? GetSelectedComboTag(ComboBox comboBox)
        => (comboBox.SelectedItem as ComboBoxItem)?.Tag as string;

    private static DateTimeOffset? BuildAdminJobsDateLowerBound(DateTimeOffset? selectedDate)
    {
        if (!selectedDate.HasValue)
            return null;

        var localDate = selectedDate.Value.ToLocalTime();
        var localMidnight = new DateTimeOffset(
            localDate.Year,
            localDate.Month,
            localDate.Day,
            0,
            0,
            0,
            localDate.Offset);
        return localMidnight.ToUniversalTime();
    }

    private static DateTimeOffset? BuildAdminJobsDateUpperBound(DateTimeOffset? selectedDate)
    {
        var lowerBound = BuildAdminJobsDateLowerBound(selectedDate);
        return lowerBound?.AddDays(1);
    }

    private static void SyncAdminJobsDateFilters(AdminJobsOverlayContext context)
    {
        context.DateField = NormalizeAdminJobsDateField(GetSelectedComboTag(context.DateFieldCombo));
        context.DatePreset = (GetSelectedComboTag(context.DatePresetCombo) ?? "all").Trim().ToLowerInvariant();
        context.SortDirection = NormalizeAdminJobsSortDirection(GetSelectedComboTag(context.SortDirectionCombo));

        var showCustomRange = string.Equals(context.DatePreset, "custom", StringComparison.OrdinalIgnoreCase);
        context.DateFromPicker.Visibility = showCustomRange ? Visibility.Visible : Visibility.Collapsed;
        context.DateToPicker.Visibility = showCustomRange ? Visibility.Visible : Visibility.Collapsed;

        if (string.Equals(context.DatePreset, "today", StringComparison.OrdinalIgnoreCase))
        {
            var now = DateTimeOffset.Now;
            var today = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset).ToUniversalTime();
            context.DateFrom = today;
            context.DateTo = today.AddDays(1);
            return;
        }

        if (!showCustomRange)
        {
            context.DateFrom = null;
            context.DateTo = null;
            return;
        }

        context.DateFrom = BuildAdminJobsDateLowerBound(context.DateFromPicker.Date);
        context.DateTo = BuildAdminJobsDateUpperBound(context.DateToPicker.Date);
    }

    private static string? FormatAdminJobsDateQueryValue(DateTimeOffset? value)
        => value?.ToString("o", CultureInfo.InvariantCulture);

    private static bool HasInvalidAdminJobsDateRange(AdminJobsOverlayContext context)
        => context.DateFrom.HasValue
           && context.DateTo.HasValue
           && context.DateFrom.Value >= context.DateTo.Value;

    private static bool IsAdminJobsDateFilterActive(AdminJobsOverlayContext context)
        => !string.Equals(context.DatePreset, "all", StringComparison.OrdinalIgnoreCase)
           || context.DateFrom.HasValue
           || context.DateTo.HasValue;
}
