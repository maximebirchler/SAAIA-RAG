using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static LlmStructuredOutputContract
        BuildRouterStructuredOutputContract()
        => new(
            "saaia_router_plan",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    intent = new
                    {
                        type = "string",
                        @enum = new[]
                        {
                            "chat.general",
                            "meta.set_language",
                            "meta.set_style",
                            "meta.set_mode",
                            "meta.rewrite_last",
                            "meta.help",
                            "meta.translate_last_answer",
                            "inventory.count",
                            "inventory.list",
                            "inventory.find",
                            "inventory.changed_since",
                            "inventory.tree",
                            "inventory.categories",
                            "inventory.stats",
                            "rag.answer",
                            "rag.followup",
                            "rag.summarize_doc",
                            "rag.summarize_topic",
                            "rag.compare",
                            "summary.exists",
                            "export.create",
                            "diagnostic.performance"
                        }
                    },
                    toolCalls = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                name = new
                                {
                                    type = "string",
                                    minLength = 2,
                                    maxLength = 80
                                },
                                args = new
                                {
                                    type = "object",
                                    additionalProperties = true
                                }
                            },
                            required = new[] { "name", "args" },
                            additionalProperties = false
                        },
                        minItems = 0,
                        maxItems = 8
                    },
                    sourceBackedMission = new
                    {
                        type = "object",
                        properties = new
                        {
                            planKind = new
                            {
                                type = "string",
                                @enum = new[]
                                {
                                    "single_item",
                                    "multi_item",
                                    "structured_layout"
                                }
                            },
                            deliverable = new
                            {
                                type = "string",
                                minLength = 2,
                                maxLength = 220
                            },
                            atomicEvidenceType = new
                            {
                                type = "string",
                                minLength = 2,
                                maxLength = 180
                            },
                            initialCapability = new
                            {
                                type = "string",
                                @enum = new[]
                                {
                                    "documents_content_cards",
                                    "documents_navigation",
                                    "rag_search"
                                }
                            },
                            structuredLayout = new { type = "boolean" },
                            rowCount = new
                            {
                                type = "integer",
                                minimum = 1,
                                maximum = 80
                            },
                            columnCount = new
                            {
                                type = "integer",
                                minimum = 1,
                                maximum = 40
                            },
                            atomicEvidenceCount = new
                            {
                                type = "integer",
                                minimum = 1,
                                maximum = 80
                            },
                            rowHeader = new
                            {
                                type = "string",
                                minLength = 0,
                                maxLength = 60
                            },
                            rowLabels = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "string",
                                    minLength = 1,
                                    maxLength = 60
                                },
                                minItems = 0,
                                maxItems = 80
                            },
                            columns = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "string",
                                    minLength = 1,
                                    maxLength = 60
                                },
                                minItems = 0,
                                maxItems = 40
                            }
                        },
                        required = new[]
                        {
                            "planKind",
                            "deliverable",
                            "atomicEvidenceType",
                            "initialCapability"
                        },
                        additionalProperties = false
                    },
                    language = new
                    {
                        type = "string",
                        @enum = new[] { "fr", "en", "es", "pt", "de", "it" }
                    },
                    mode = new
                    {
                        type = "string",
                        @enum = new[] { "auto", "standard", "strict" }
                    },
                    responseFormat = new
                    {
                        type = "string",
                        @enum = new[] { "auto", "about", "summary" }
                    },
                    needClarification = new { type = "boolean" },
                    clarificationQuestions = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "string",
                            minLength = 1,
                            maxLength = 180
                        },
                        minItems = 0,
                        maxItems = 2
                    },
                    reasoningTracePublic = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "string",
                            minLength = 1,
                            maxLength = 180
                        },
                        minItems = 0,
                        maxItems = 3
                    },
                    riskFlags = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "string",
                            minLength = 1,
                            maxLength = 100
                        },
                        minItems = 0,
                        maxItems = 6
                    },
                    memoryUpdate = new
                    {
                        type = new[] { "string", "null" }
                    },
                    routerConfidence = new
                    {
                        type = "number",
                        minimum = 0,
                        maximum = 1
                    }
                },
                required = new[] { "intent", "toolCalls" },
                additionalProperties = false
            }, ClientJson.CamelCase));
}
