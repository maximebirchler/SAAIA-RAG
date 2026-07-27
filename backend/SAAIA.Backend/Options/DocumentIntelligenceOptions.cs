sealed class DocumentIntelligenceOptions
{
    public const int DefaultTimeoutSeconds = 900;
    public const long DefaultMaxResponseBytes = 512L * 1024 * 1024;

    public bool Enabled { get; set; }
    public string Provider { get; set; } = "docling";
    public string BaseUrl { get; set; } = "http://docling:5001";
    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;
    public long MaxResponseBytes { get; set; } = DefaultMaxResponseBytes;
    public bool DoOcr { get; set; } = true;
    public bool ForceOcr { get; set; }
    public string OcrPreset { get; set; } = "auto";
    public string PdfBackend { get; set; } = "docling_parse";
    public string TableMode { get; set; } = "accurate";
    public bool TableCellMatching { get; set; } = true;
    public bool DoTableStructure { get; set; } = true;
    public bool IncludeImages { get; set; }
    public bool IncludePageImages { get; set; }
    public bool HeadingHierarchyEnabled { get; set; } = true;
    public bool HeadingHierarchyUseBookmarks { get; set; } = true;
    public bool HeadingHierarchyUseNumbering { get; set; } = true;
    public bool HeadingHierarchyUseStyle { get; set; } = true;
    public int HeadingHierarchyMaxLevel { get; set; } = 6;
    public double HeadingHierarchyBookmarkMatchThreshold { get; set; } = 0.8;
    public bool RegionalTableRepairEnabled { get; set; } = true;
    public double RegionalTableRepairScale { get; set; } = 2.5;
    public double RegionalTableRepairPaddingPoints { get; set; } = 12;
    public double RegionalTableRepairMinimumScore { get; set; } = 0.5;
    public bool NativeTextCoverageReconciliationEnabled { get; set; } = true;
    public double NativeTextCoverageMinimumLineCoverage { get; set; } = 0.90;
    public string Device { get; set; } = "cpu";
    public int NumThreads { get; set; } = 8;
    public int ServiceWorkerConcurrency { get; set; } = 1;
    public string EngineVersion { get; set; } =
        "docling-serve-1.27.0_docling-slim-2.113.0_saaia-structure-2";
    public string ParseEngineVersion { get; set; } = "docling-parse-7.8.1";
    public string ModelPackageVersion { get; set; } = "docling-ibm-models-3.13.3";
    public string OcrEngineVersion { get; set; } = "rapidocr-3.9.1_onnxruntime-1.27.0";
    public string DeploymentRevision { get; set; } =
        "sha256:a70cd391c5d9e7ad23e8d76adffa5e5f03c536b4d1ea007f512c94e0b435ba45+saaia-structure-2";

    public TimeSpan ResolveTimeout()
        => TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 30, 86_400));

    public long ResolveMaxResponseBytes()
        => Math.Clamp(MaxResponseBytes, 1L * 1024 * 1024, 2L * 1024 * 1024 * 1024);
}
