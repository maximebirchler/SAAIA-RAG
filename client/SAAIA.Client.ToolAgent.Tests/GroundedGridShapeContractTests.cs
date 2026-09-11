using System.Reflection;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class GroundedGridShapeContractTests
{
    private const string P09 =
        "Fais un tableau de deux recettes, quiche lorraine et gratin dauphinois, avec trois colonnes : ingrédients principaux, temps indiqué et source.";
    private const string FourCellGrid =
        "Fais un tableau de deux recettes, quiche lorraine et gratin dauphinois, avec deux colonnes : ingrédients principaux et source.";

    [Theory]
    [InlineData(
        P09,
        "deux recettes",
        2,
        "quiche lorraine|gratin dauphinois",
        "trois colonnes",
        3,
        "ingrédients principaux|temps indiqué|source")]
    [InlineData(
        "Make a table for two standards, ISO 9001 and ISO 14001, with three columns: scope, certification requirements, and source.",
        "two standards",
        2,
        "ISO 9001|ISO 14001",
        "three columns",
        3,
        "scope|certification requirements|source")]
    [InlineData(
        "Erstelle eine Tabelle für zwei Normen, ISO 9001 und ISO 14001, mit zwölf Spalten: A, B, C, D, E, F, G, H, I, J, K und L.",
        "zwei Normen",
        2,
        "ISO 9001|ISO 14001",
        "zwölf Spalten",
        12,
        "A|B|C|D|E|F|G|H|I|J|K|L")]
    public void Literal_counted_axes_are_accepted_across_supported_languages(
        string question,
        string rowCountAnchor,
        int rowCount,
        string rows,
        string columnCountAnchor,
        int columnCount,
        string columns)
    {
        var shape = ReadShape(
            question,
            ShapeJson(
                rowCountAnchor,
                rowCount,
                rows.Split('|'),
                columnCountAnchor,
                columnCount,
                columns.Split('|')));

        Assert.Equal(rowCount, Property<int>(shape, "RowCount"));
        Assert.Equal(columnCount, Property<int>(shape, "ColumnCount"));
        Assert.Equal(rowCount * columnCount, Property<int>(shape, "CellCount"));
        Assert.True(Property<bool>(shape, "IsComplete"));
    }

    [Fact]
    public void A_valid_single_axis_observation_remains_incomplete()
    {
        const string question =
            "Fais un tableau pour deux recettes, quiche lorraine et gratin dauphinois, à partir des PDF.";
        var shape = ReadShape(
            question,
            ShapeJson(
                "deux recettes",
                2,
                ["quiche lorraine", "gratin dauphinois"],
                string.Empty,
                0,
                []));

        Assert.Equal(2, Property<int>(shape, "RowCount"));
        Assert.Equal(0, Property<int>(shape, "ColumnCount"));
        Assert.False(Property<bool>(shape, "IsComplete"));
    }

    [Theory]
    [InlineData("deux recettes", 3, "quiche lorraine|gratin dauphinois")]
    [InlineData("trois recettes", 2, "quiche lorraine|gratin dauphinois")]
    [InlineData("deux recettes", 2, "quiche lorraine|quiche lorraine")]
    [InlineData("deux plats", 2, "quiche lorraine|gratin dauphinois")]
    public void Count_mismatch_duplicate_or_invented_anchor_is_rejected(
        string countAnchor,
        int count,
        string rows)
    {
        Assert.False(TryReadShape(
            P09,
            ShapeJson(
                countAnchor,
                count,
                rows.Split('|'),
                "trois colonnes",
                3,
                ["ingrédients principaux", "temps indiqué", "source"]),
            out _));
    }

    [Fact]
    public void Complete_shape_binds_exact_cardinalities_and_literal_axis_members()
    {
        var shape = ReadShape(
            P09,
            ShapeJson(
                "deux recettes",
                2,
                ["quiche lorraine", "gratin dauphinois"],
                "trois colonnes",
                3,
                ["ingrédients principaux", "temps indiqué", "source"]));
        var method = typeof(ToolAgentOrchestrator).GetMethod(
            "BindGroundedGridShape",
            BindingFlags.NonPublic | BindingFlags.Static);
        var toolsMethod = typeof(ToolAgentOrchestrator).GetMethod(
            "BuildNativeRouterTools",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.NotNull(toolsMethod);
        var tools = Assert.IsAssignableFrom<IReadOnlyList<SourceBackedAgentToolDefinition>>(
            toolsMethod!.Invoke(null, null));
        var bound = Assert.IsAssignableFrom<IReadOnlyList<SourceBackedAgentToolDefinition>>(
            method!.Invoke(null, [tools, shape]));
        var schema = Assert.Single(
            bound,
            static tool => tool.Name == "submit_source_backed_grid_route")
            .Parameters;
        var properties = schema.GetProperty("properties");

        Assert.Equal(6, properties.GetProperty("count").GetProperty("minimum").GetInt32());
        Assert.Equal(6, properties.GetProperty("count").GetProperty("maximum").GetInt32());
        Assert.Equal(2, properties.GetProperty("rows").GetProperty("minItems").GetInt32());
        Assert.Equal(2, properties.GetProperty("rows").GetProperty("maxItems").GetInt32());
        Assert.Equal(
            ["quiche lorraine", "gratin dauphinois"],
            properties.GetProperty("rows").GetProperty("items")
                .GetProperty("enum").EnumerateArray()
                .Select(static value => value.GetString()));
        Assert.Equal(3, properties.GetProperty("columns").GetProperty("minItems").GetInt32());
        Assert.Equal(3, properties.GetProperty("columns").GetProperty("maxItems").GetInt32());
    }

    [Fact]
    public async Task Complete_shape_suppresses_missing_input_and_rejects_axis_drift_before_accepting_repair()
    {
        var llm = new GridRouter(
            driftFirstRoute: true,
            completeShape: true,
            gridColumnCount: 2);
        var orchestrator = new ToolAgentOrchestrator(
            new ApiClient(),
            llm,
            new ToolMemory(),
            new AppSettings { ActiveMode = "strict" });

        var plan = await orchestrator.RouteOnlyForTests(FourCellGrid, CancellationToken.None);

        Assert.Equal(1, llm.ShapeCalls);
        Assert.Equal(1, llm.EvidenceModeCalls);
        Assert.Equal(2, llm.NativeCalls);
        Assert.Contains(
            "native_source_route_grid_shape_changed",
            llm.LastUserMessage,
            StringComparison.Ordinal);
        Assert.False(plan.NeedClarification);
        Assert.Equal(2, plan.SourceBackedMission!.RowCount);
        Assert.Equal(2, plan.SourceBackedMission.ColumnCount);
        Assert.Equal(4, plan.SourceBackedMission.AtomicEvidenceCount);
        Assert.Equal(
            ["quiche lorraine", "gratin dauphinois"],
            plan.SourceBackedMission.RowLabels);
        Assert.Equal(
            ["ingrédients principaux", "source"],
            plan.SourceBackedMission.Columns);
    }

    [Fact]
    public async Task Incomplete_shape_keeps_the_missing_input_route_available()
    {
        var llm = new GridRouter(driftFirstRoute: false, completeShape: false);
        var orchestrator = new ToolAgentOrchestrator(
            new ApiClient(),
            llm,
            new ToolMemory(),
            new AppSettings { ActiveMode = "strict" });

        var plan = await orchestrator.RouteOnlyForTests(
            "Fais un tableau pour deux recettes, quiche lorraine et gratin dauphinois, à partir des PDF.",
            CancellationToken.None);

        Assert.Equal(1, llm.ShapeCalls);
        Assert.Equal(0, llm.EvidenceModeCalls);
        Assert.Equal(1, llm.NativeCalls);
        Assert.True(plan.NeedClarification);
        Assert.Null(plan.SourceBackedMission);
    }

    [Fact]
    public async Task Complete_shape_uses_llm_owned_exact_category_and_expands_one_literal_card_query_per_row()
    {
        var llm = new GridRouter(
            driftFirstRoute: false,
            completeShape: true,
            selectedCategoryScope: "Cuisine",
            gridColumnCount: 2);
        var memory = new ToolMemory
        {
            CatalogSnapshotCache = new ToolMemory.RuntimeCatalogSnapshot
            {
                LoadedAtUtc = DateTimeOffset.UtcNow,
                Categories =
                [
                    new ToolMemory.CategorySnapshot
                    {
                        CategoryRef = "cuisine",
                        CategoryPath = "Cuisine",
                        DisplayName = "Cuisine",
                        Ordinal = 1,
                        Aliases = []
                    },
                    new ToolMemory.CategorySnapshot
                    {
                        CategoryRef = "engineering",
                        CategoryPath = "Engineering",
                        DisplayName = "Engineering",
                        Ordinal = 2,
                        Aliases = ["standards"]
                    }
                ]
            }
        };
        var orchestrator = new ToolAgentOrchestrator(
            new ApiClient(),
            llm,
            memory,
            new AppSettings { ActiveMode = "strict" });

        var plan = await orchestrator.RouteOnlyForTests(FourCellGrid, CancellationToken.None);
        var actions = ToolAgentOrchestrator
            .BuildSourceBackedRouterInitialActionsForTests(plan);

        Assert.Equal(1, llm.ShapeCalls);
        Assert.Equal(1, llm.EvidenceModeCalls);
        Assert.Equal(1, llm.CategoryCalls);
        Assert.Equal(1, llm.NativeCalls);
        Assert.Equal(["Cuisine"], plan.SourceBackedMission!.CandidateScopePaths);
        Assert.Equal("content_claim", plan.SourceBackedMission.AtomicEvidenceMode);
        Assert.Equal(
            "source-backed fact about the row subject",
            plan.SourceBackedMission.AtomicEvidenceType);
        Assert.Equal(
            ["quiche lorraine", "gratin dauphinois"],
            plan.GroundedGridDiscoveryQueries);
        Assert.Collection(
            actions,
            first => AssertGroundedRowAction(first, "quiche lorraine"),
            second => AssertGroundedRowAction(second, "gratin dauphinois"));
    }

    [Fact]
    public void Row_category_scope_parser_rejects_a_value_outside_published_categories()
    {
        var categories = new[]
        {
            new SourceBackedCatalogHint("Cuisine", "Cuisine", 10, []),
            new SourceBackedCatalogHint("Engineering", "Engineering", 10, [])
        };
        var method = typeof(ToolAgentOrchestrator).GetMethod(
            "TryReadGroundedGridRowCategoryScope",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        object?[] args =
        [
            new SourceBackedAgentCompletion(
                "{\"rowCategories\":[\"Cuisine\",\"Invented\"]}",
                [],
                "stop"),
            categories,
            2,
            null,
            null
        ];

        Assert.False((bool)method!.Invoke(null, args)!);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<string>>(args[3]));
        Assert.Null(args[4]);
    }

    [Fact]
    public void Row_category_scope_parser_keeps_divergent_exact_rows_open()
    {
        var categories = new[]
        {
            new SourceBackedCatalogHint("Cuisine", "Cuisine", 10, []),
            new SourceBackedCatalogHint("Engineering", "Engineering", 10, [])
        };
        var method = typeof(ToolAgentOrchestrator).GetMethod(
            "TryReadGroundedGridRowCategoryScope",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        object?[] args =
        [
            new SourceBackedAgentCompletion(
                "{\"rowCategories\":[\"Cuisine\",\"Engineering\"]}",
                [],
                "stop"),
            categories,
            2,
            null,
            null
        ];

        Assert.True((bool)method!.Invoke(null, args)!);
        Assert.Equal(
            ["Cuisine", "Engineering"],
            Assert.IsAssignableFrom<IReadOnlyList<string>>(args[3]));
        Assert.Null(args[4]);
    }

    [Fact]
    public void Evidence_mode_parser_accepts_one_known_empty_argument_tool_only()
    {
        var method = typeof(ToolAgentOrchestrator).GetMethod(
            "TryReadGroundedGridEvidenceMode",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        object?[] acceptedArgs =
        [
            new SourceBackedAgentCompletion(
                string.Empty,
                [new SourceBackedAgentToolCall(
                    "family",
                    "classify_grid_values_as_properties_of_row_subjects",
                    JsonSerializer.SerializeToElement(new { }))],
                "tool_calls"),
            null
        ];
        Assert.True((bool)method!.Invoke(null, acceptedArgs)!);
        Assert.Equal("content_claim", acceptedArgs[1]);

        object?[] rejectedArgs =
        [
            new SourceBackedAgentCompletion(
                string.Empty,
                [new SourceBackedAgentToolCall(
                    "family",
                    "unknown_grid_family",
                    JsonSerializer.SerializeToElement(new { }))],
                "tool_calls"),
            null
        ];
        Assert.False((bool)method.Invoke(null, rejectedArgs)!);
        Assert.Null(rejectedArgs[1]);
    }

    private static void AssertGroundedRowAction(
        SourceBackedInitialToolCall action,
        string expectedQuery)
    {
        Assert.Equal("documents_content_cards", action.ToolName);
        Assert.Equal("llm_router_grounded_grid_row", action.DecisionSource);
        Assert.Equal(expectedQuery, action.QueryHint);
        Assert.Equal(
            expectedQuery,
            action.Arguments.GetProperty("q").GetString());
        Assert.Equal(
            "Cuisine",
            action.Arguments.GetProperty("categoryPath").GetString());
        Assert.Equal(8, action.Arguments.GetProperty("limit").GetInt32());
        Assert.Equal(0, action.Arguments.GetProperty("offset").GetInt32());
    }

    private static string ShapeJson(
        string rowCountAnchor,
        int rowCount,
        IReadOnlyList<string> rows,
        string columnCountAnchor,
        int columnCount,
        IReadOnlyList<string> columns)
        => JsonSerializer.Serialize(new
        {
            rowCountAnchor,
            rowCount,
            rowAnchors = rows,
            columnCountAnchor,
            columnCount,
            columnAnchors = columns
        });

    private static bool TryReadShape(
        string question,
        string json,
        out object? shape)
    {
        var method = typeof(ToolAgentOrchestrator).GetMethod(
            "TryReadGroundedGridShape",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        object?[] args =
        [
            new SourceBackedAgentCompletion(json, [], "stop"),
            question,
            null
        ];
        var accepted = (bool)method!.Invoke(null, args)!;
        shape = args[2];
        return accepted;
    }

    private static object ReadShape(string question, string json)
    {
        Assert.True(TryReadShape(question, json, out var shape));
        return Assert.IsAssignableFrom<object>(shape);
    }

    private static T Property<T>(object value, string name)
        => Assert.IsType<T>(value.GetType().GetProperty(name)!.GetValue(value));

    private sealed class GridRouter(
        bool driftFirstRoute,
        bool completeShape,
        string? selectedCategoryScope = null,
        string selectedEvidenceMode = "content_claim",
        int gridColumnCount = 3)
        : ILlmClient,
          ISourceBackedAgentLlmClient,
          ISourceBackedAgentStructuredLlmClient
    {
        public int ShapeCalls { get; private set; }
        public int CategoryCalls { get; private set; }
        public int EvidenceModeCalls { get; private set; }
        public int NativeCalls { get; private set; }
        public string LastUserMessage { get; private set; } = string.Empty;

        public Task<SourceBackedAgentCompletion> CompleteStructuredAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            LlmStructuredOutputContract contract,
            int maxTokens,
            CancellationToken ct,
            double? temperatureOverride = null)
        {
            if (contract.Name == "saaia_work_family_v1")
            {
                return Task.FromResult(new SourceBackedAgentCompletion(
                    "{\"family\":\"grid\"}",
                    [],
                    "stop"));
            }

            if (contract.Name == "saaia_grounded_grid_row_category_scope_v2")
            {
                CategoryCalls++;
                Assert.Equal(64, maxTokens);
                Assert.Equal(0, temperatureOverride);
                return Task.FromResult(new SourceBackedAgentCompletion(
                    JsonSerializer.Serialize(new
                    {
                        rowCategories = new[]
                        {
                            selectedCategoryScope ?? string.Empty,
                            selectedCategoryScope ?? string.Empty
                        }
                    }),
                    [],
                    "stop"));
            }

            Assert.Equal("saaia_explicit_grid_axes_v1", contract.Name);
            ShapeCalls++;
            Assert.Equal(280, maxTokens);
            Assert.Equal(0, temperatureOverride);
            var question = messages[1].Content!;
            var incomplete = question.Contains("à partir des PDF", StringComparison.Ordinal);
            return Task.FromResult(new SourceBackedAgentCompletion(
                incomplete || !completeShape
                    ? ShapeJson(
                        "deux recettes",
                        2,
                        ["quiche lorraine", "gratin dauphinois"],
                        string.Empty,
                        0,
                        [])
                    : ShapeJson(
                        "deux recettes",
                        2,
                        ["quiche lorraine", "gratin dauphinois"],
                        gridColumnCount == 2 ? "deux colonnes" : "trois colonnes",
                        gridColumnCount,
                        gridColumnCount == 2
                            ? new[] { "ingrédients principaux", "source" }
                            : new[] { "ingrédients principaux", "temps indiqué", "source" }),
                [],
                "stop"));
        }

        public Task<SourceBackedAgentCompletion> CompleteAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            int maxTokens,
            CancellationToken ct,
            double? temperatureOverride = null,
            bool requireToolCall = false)
        {
            if (tools.Any(static tool =>
                    tool.Name is
                        "classify_grid_values_as_properties_of_row_subjects"
                        or "classify_grid_values_as_named_items_for_slots"))
            {
                EvidenceModeCalls++;
                Assert.Equal(48, maxTokens);
                Assert.Equal(0, temperatureOverride);
                Assert.True(requireToolCall);
                var selectedTool = selectedEvidenceMode == "content_claim"
                    ? "classify_grid_values_as_properties_of_row_subjects"
                    : "classify_grid_values_as_named_items_for_slots";
                return Task.FromResult(new SourceBackedAgentCompletion(
                    string.Empty,
                    [new SourceBackedAgentToolCall(
                        "grid-family",
                        selectedTool,
                        JsonSerializer.SerializeToElement(new { }))],
                    "tool_calls"));
            }

            NativeCalls++;
            LastUserMessage = messages.Last(static message => message.Role == "user")
                .Content!;
            if (!completeShape)
            {
                Assert.Contains(
                    tools,
                    static tool => tool.Name == "request_missing_user_input");
                return Task.FromResult(new SourceBackedAgentCompletion(
                    string.Empty,
                    [new SourceBackedAgentToolCall(
                        "missing",
                        "request_missing_user_input",
                        JsonSerializer.SerializeToElement(new
                        {
                            question = "Quelles colonnes faut-il afficher ?",
                            userTextAnchor =
                                "Fais un tableau pour deux recettes, quiche lorraine et gratin dauphinois, à partir des PDF.",
                            missingInformation =
                                "Les colonnes du tableau ne sont pas indiquées.",
                            resumeRoute = "source_backed"
                        }))],
                    "tool_calls"));
            }

            Assert.DoesNotContain(
                tools,
                static tool => tool.Name == "request_missing_user_input");
            var gridTool = Assert.Single(
                tools,
                static tool => tool.Name == "submit_source_backed_grid_route");
            var properties = gridTool.Parameters.GetProperty("properties");
            Assert.Equal(
                2 * gridColumnCount,
                properties.GetProperty("count").GetProperty("minimum").GetInt32());
            Assert.Contains(
                gridTool.Parameters.GetProperty("required").EnumerateArray(),
                static value => value.GetString() == "sourceItemMode");
            Assert.Equal(
                selectedEvidenceMode,
                Assert.Single(properties.GetProperty("sourceItemMode")
                        .GetProperty("enum")
                        .EnumerateArray())
                    .GetString());
            if (!string.IsNullOrWhiteSpace(selectedCategoryScope))
            {
                Assert.Contains(
                    gridTool.Parameters.GetProperty("required").EnumerateArray(),
                    static value => value.GetString() == "scope");
                Assert.Equal(
                    selectedCategoryScope,
                    Assert.Single(properties.GetProperty("scope")
                            .GetProperty("enum")
                            .EnumerateArray())
                        .GetString());
            }
            var drift = driftFirstRoute && NativeCalls == 1;
            var rows = drift
                ? new[] { "gratin dauphinois", "quiche lorraine" }
                : new[] { "quiche lorraine", "gratin dauphinois" };
            var args = JsonSerializer.SerializeToElement(new
            {
                tool = "cards",
                pool = 8,
                sourceItemType = selectedEvidenceMode == "content_claim"
                    ? "source-backed fact about the row subject"
                    : "recette",
                sourceItemMode = selectedEvidenceMode,
                intent = "answer",
                useFocusedDocument = false,
                questionFocus = "content",
                namedReferenceKind = "none",
                document = (string?)null,
                scope = selectedCategoryScope,
                count = 2 * gridColumnCount,
                rowHeader = "recette",
                rows,
                columns = gridColumnCount == 2
                    ? new[] { "ingrédients principaux", "source" }
                    : new[] { "ingrédients principaux", "temps indiqué", "source" }
            });
            return Task.FromResult(new SourceBackedAgentCompletion(
                string.Empty,
                [new SourceBackedAgentToolCall(
                    "grid",
                    "submit_source_backed_grid_route",
                    args)],
                "tool_calls"));
        }

        public Task<string> CompleteAsync(
            IReadOnlyList<(string role, string content)> messages,
            bool forceJson,
            CancellationToken ct) => throw new NotSupportedException();

        public Task StreamAsync(
            IReadOnlyList<(string role, string content)> messages,
            bool forceJson,
            Action<string> onDelta,
            CancellationToken ct) => throw new NotSupportedException();
    }
}
