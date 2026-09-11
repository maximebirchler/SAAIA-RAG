using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LiveSemanticAnswerTransactionCanaryTests(ITestOutputHelper output)
{
    private const string LiveFlag =
        "SAAIA_LIVE_SEMANTIC_ANSWER_TRANSACTION_CANARY";
    private const string OutputVariable =
        "SAAIA_LIVE_SEMANTIC_ANSWER_TRANSACTION_CANARY_OUTPUT";
    private const string CorrectedLiveFlag =
        "SAAIA_LIVE_CORRECTED_SEMANTIC_ANSWER_TRANSACTION_CANARY";
    private const string CorrectedOutputVariable =
        "SAAIA_LIVE_CORRECTED_SEMANTIC_ANSWER_TRANSACTION_CANARY_OUTPUT";
    private const string CoverageV3LiveFlag =
        "SAAIA_LIVE_SEMANTIC_ANSWER_TRANSACTION_V3_COVERAGE_CANARY";
    private const string CoverageV3OutputVariable =
        "SAAIA_LIVE_SEMANTIC_ANSWER_TRANSACTION_V3_COVERAGE_CANARY_OUTPUT";
    private const string FourthV3LiveFlag =
        "SAAIA_LIVE_SEMANTIC_ANSWER_TRANSACTION_V3_FOURTH_CANARY";
    private const string FourthV3OutputVariable =
        "SAAIA_LIVE_SEMANTIC_ANSWER_TRANSACTION_V3_FOURTH_CANARY_OUTPUT";
    private const string FifthV3LiveFlag =
        "SAAIA_LIVE_SEMANTIC_ANSWER_TRANSACTION_V3_FIFTH_SCOPE_CANARY";
    private const string FifthV3OutputVariable =
        "SAAIA_LIVE_SEMANTIC_ANSWER_TRANSACTION_V3_FIFTH_SCOPE_CANARY_OUTPUT";
    private const string ContractName =
        "source_backed_semantic_answer_transaction_v3";

    [Fact]
    public async Task Live_qwen_semantic_answer_transaction_passes_five_frozen_domains_when_enabled()
        => await RunCanaryWhenEnabledAsync(
            LiveFlag,
            OutputVariable,
            "a442-live-semantic-transaction-canary",
            "semantic_answer_transaction_canary_v1",
            BuildCases());

    [Fact]
    public async Task Live_qwen_corrected_semantic_answer_transaction_passes_five_unseen_domains_when_enabled()
        => await RunCanaryWhenEnabledAsync(
            CorrectedLiveFlag,
            CorrectedOutputVariable,
            "a452-live-corrected-semantic-transaction-canary",
            "corrected_semantic_answer_transaction_canary_v1",
            BuildCorrectedCases());

    [Fact]
    public async Task Live_qwen_semantic_answer_transaction_v3_covers_four_decisions_and_full_pool_when_enabled()
        => await RunCanaryWhenEnabledAsync(
            CoverageV3LiveFlag,
            CoverageV3OutputVariable,
            "a467-disarmed-semantic-transaction-v3-coverage-template",
            "semantic_answer_transaction_v3_decision_coverage_canary_v1",
            BuildCoverageV3Cases());

    [Fact]
    public async Task Live_qwen_semantic_answer_transaction_v3_fourth_canary_passes_five_distinct_domains_when_enabled()
        => await RunCanaryWhenEnabledAsync(
            FourthV3LiveFlag,
            FourthV3OutputVariable,
            "a472-live-semantic-transaction-v3-fourth-canary",
            "semantic_answer_transaction_v3_fourth_canary_v1",
            BuildFourthV3Cases());

    [Fact]
    public async Task Live_qwen_semantic_answer_transaction_v3_fifth_scope_canary_passes_five_distinct_domains_when_enabled()
        => await RunCanaryWhenEnabledAsync(
            FifthV3LiveFlag,
            FifthV3OutputVariable,
            "a482-live-semantic-transaction-v3-fifth-scope-canary",
            "semantic_answer_transaction_v3_fifth_scope_canary_v1",
            BuildFifthV3Cases());

    private async Task RunCanaryWhenEnabledAsync(
        string liveFlag,
        string outputVariable,
        string runDirectory,
        string schemaVersion,
        IReadOnlyList<CanaryCase> cases)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(liveFlag),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine($"Skipped: set {liveFlag}=1 to run the canary.");
            return;
        }

        var settings = AppSettings.Load();
        var llmBaseUrl = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_BASE_URL"),
            settings.LlmBaseUrl);
        var model = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_MODEL"),
            settings.ModelId);
        if (string.IsNullOrWhiteSpace(llmBaseUrl)
            || string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException(
                "The semantic transaction canary requires local LLM URL and model settings.");
        }

        var artifact = FirstNonBlank(
                           Environment.GetEnvironmentVariable(outputVariable))
                       ?? Path.Combine(
                           FindRepoRoot(),
                           "artifacts",
                           "goal-rag-product-20260827-1041",
                           "phase5",
                           runDirectory,
                           "semantic-answer-transaction-canary.json");
        var results = new List<CanaryResult>();
        var globalErrors = new List<string>();
        LlamaCppProcessManager? runtimeManager = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));

        try
        {
            var liveSettings = settings.Clone();
            liveSettings.UseLocalLlm = true;
            liveSettings.LlmMaxOutputTokens = 640;
            if (liveSettings.ManageLocalLlmProcess)
            {
                runtimeManager = new LlamaCppProcessManager();
                runtimeManager.SetIdleStopSuppressionProvider(static () => true);
                var (ok, message) = await runtimeManager.EnsureRunningAsync(
                    liveSettings,
                    cts.Token);
                if (!ok)
                {
                    throw new InvalidOperationException(
                        "Managed LLM runtime did not start: " + message);
                }
            }
            foreach (var canaryCase in cases)
            {
                var inner = new OpenAiLlmClient();
                inner.Configure(NormalizeLlmBaseUrl(llmBaseUrl), model);
                var llm = new RecordingNativeLlmAdapter(inner);
                var runner = new SourceBackedAgentV2Runner(
                    llm,
                    new UnusedToolExecutor(),
                    new SourceBackedAgentV2Options(
                        MaximumTurns: 1,
                        MaximumToolCalls: 1,
                        MaximumObservationItems: 12,
                        MaximumObservationExcerptCharacters: 520,
                        MaximumOutputTokens: 900,
                        MaximumContextTokens: 4096,
                        SeparateActionAndWriter: true,
                        RequireEvidenceSelectionBeforeWriter: true,
                        StructuredFlatWriterEnabled: true,
                        SemanticAnswerTransactionEnabled: true));
                var bundle = EvidenceBundleBuilder.FromToolResults(
                    BuildToolResults(canaryCase),
                    canaryCase.Question);
                var intake = new SourceBackedIntake(
                    canaryCase.Question,
                    "rag.answer",
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    AllowsPartialAnswer: false,
                    Language: canaryCase.Language)
                {
                    QuestionFocus = canaryCase.QuestionFocus
                };
                var execution = await InvokeTransactionAsync(
                    runner,
                    intake,
                    canaryCase.SemanticPlan,
                    bundle,
                    cts.Token);
                var observed = ReadObserved(execution, llm);
                var errors = Evaluate(canaryCase, observed);
                globalErrors.AddRange(errors.Select(error =>
                    canaryCase.Id + ": " + error));
                results.Add(new CanaryResult(
                    canaryCase.Id,
                    canaryCase.Domain,
                    canaryCase.Question,
                    canaryCase.ExpectedDecision,
                    canaryCase.ExpectedEvidenceIds,
                    canaryCase.RequiredPatterns,
                    observed,
                    errors));
                output.WriteLine(
                    $"{canaryCase.Id}: decision={observed.Decision}; "
                    + $"adequacy={observed.AnswerAdequacy}; "
                    + $"complete/input/visible="
                    + $"{observed.RequestedDeliverableComplete}/"
                    + $"{observed.MissingUserInputPreventsUniqueResult}/"
                    + $"{observed.VisibleContextEvidenceId}; "
                    + $"ids={string.Join(',', observed.EvidenceIds)}; "
                    + $"pool={string.Join(',', observed.PresentedEvidenceIds)}; "
                    + $"eligible/presented/truncated="
                    + $"{observed.EvidencePoolEligibleItemCount}/"
                    + $"{observed.SourceWindowItemCount}/"
                    + $"{observed.EvidencePoolTruncatedItemCount}; "
                    + $"calls={observed.StructuredCallCount}; "
                    + $"tokens={observed.PromptTokens}/{observed.CompletionTokens}; "
                    + $"ms={observed.ElapsedMilliseconds}; "
                    + $"valid={observed.ProtocolValid}");
            }
        }
        catch (Exception exception)
        {
            globalErrors.Add("transport_or_harness: " + exception);
        }
        finally
        {
            try
            {
                var payload = new
                {
                    SchemaVersion = schemaVersion,
                    Model = model,
                    LlmBaseUrl = llmBaseUrl,
                    ExpectedCaseCount = cases.Count,
                    ExecutedCaseCount = results.Count,
                    ExpectedMaximumStructuredCalls = cases.Count,
                    StructuredCallCount = results.Sum(static result =>
                        result.Observed.StructuredCallCount),
                    PassedCaseCount = results.Count(static result => result.Errors.Count == 0),
                    RuntimeManaged = runtimeManager is not null,
                    GlobalErrors = globalErrors,
                    Cases = results
                };
                Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
                await File.WriteAllTextAsync(
                    artifact,
                    JsonSerializer.Serialize(
                        payload,
                        new JsonSerializerOptions { WriteIndented = true }),
                    CancellationToken.None);
                output.WriteLine("Artifact: " + artifact);
            }
            finally
            {
                runtimeManager?.Stop();
            }
        }

        Assert.Equal(cases.Count, results.Count);
        Assert.Equal(cases.Count, results.Sum(static result =>
            result.Observed.StructuredCallCount));
        Assert.True(
            globalErrors.Count == 0,
            string.Join(Environment.NewLine, globalErrors));
    }

    private static IReadOnlyList<CanaryCase> BuildCases()
        => new[]
        {
            new CanaryCase(
                "industrial_two_claims",
                "industrial_safety",
                "Which two safeguards are explicitly required by the visible standard?",
                "en",
                "content",
                "LIVRABLE: two exact documented safeguards\nPREUVES_ATOMIQUES: source-backed content claims",
                "answer",
                new[] { "E1", "E2" },
                new[] { "(?i)guard", "(?i)hazard", "(?i)stop", "(?i)accessible" },
                new[]
                {
                    "The guard shall prevent access to the hazard zone.",
                    "The stop control shall remain readily accessible to the operator."
                }),
            new CanaryCase(
                "recipe_shortest_total",
                "cooking",
                "Quel dessert a le temps total documenté le plus court ? Donne son nom et cette durée, sans proposer d'étapes.",
                "fr",
                "content",
                "LIVRABLE: le dessert au temps total minimal et sa durée\nPREUVES_ATOMIQUES: comparaison de faits documentés",
                "answer",
                new[] { "E2" },
                new[] { "(?i)pommes", "(?<!\\d)25(?!\\d)" },
                new[]
                {
                    "Mousse au chocolat. Préparation 15 minutes. Repos 120 minutes. Temps total 135 minutes.",
                    "Pommes à la cannelle. Préparation 10 minutes. Cuisson 15 minutes. Temps total 25 minutes.",
                    "Tiramisu. Préparation 25 minutes. Repos 240 minutes. Temps total 265 minutes."
                }),
            new CanaryCase(
                "administrative_deadline_exception",
                "administrative_policy",
                "Quel est le délai normal de recours et quelle extension est documentée en cas de maladie avec justificatif ?",
                "fr",
                "content",
                "LIVRABLE: délai normal et exception documentée\nPREUVES_ATOMIQUES: deux claims de politique administrative",
                "answer",
                new[] { "E1", "E2" },
                new[] { "(?<!\\d)30(?!\\d)", "(?<!\\d)15(?!\\d)", "(?i)maladie", "(?i)justificatif" },
                new[]
                {
                    "Le recours doit être déposé dans les 30 jours suivant la notification.",
                    "En cas de maladie attestée par un justificatif, le délai peut être prolongé de 15 jours."
                }),
            new CanaryCase(
                "document_type_procedure",
                "document_identity",
                "Quel est le type documentaire le plus précis de PR-17 ?",
                "fr",
                "document_family_or_type",
                "LIVRABLE: type documentaire précis\nPREUVES_ATOMIQUES: identité et contenu du document",
                "answer",
                new[] { "E1" },
                new[] { "(?i)procédure|procedure" },
                new[]
                {
                    "PROCÉDURE DE SÉCURITÉ PR-17. Cette procédure définit les étapes obligatoires de consignation avant maintenance."
                }),
            new CanaryCase(
                "insufficient_warranty_duration",
                "insufficient_evidence",
                "Quelle est la durée exacte de garantie du produit AX-9 ?",
                "fr",
                "content",
                "LIVRABLE: durée exacte de garantie\nPREUVES_ATOMIQUES: clause explicite de durée",
                "continue",
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[]
                {
                    "Pour contacter le support AX-9, utilisez support@example.invalid.",
                    "Préparez le numéro de série du produit avant de contacter le support."
                })
        };

    private static IReadOnlyList<CanaryCase> BuildCorrectedCases()
        => new[]
        {
            new CanaryCase(
                "metrology_zero_and_interval",
                "metrology",
                "Quelles sont les deux exigences documentées avant la première mesure et pour le recalibrage périodique du capteur MZ-4 ?",
                "fr",
                "content",
                "LIVRABLE: deux exigences métrologiques exactes\nPREUVES_ATOMIQUES: deux claims documentés",
                "answer",
                new[] { "E1", "E2" },
                new[] { "(?i)zéro|zero", "(?<!\\d)12(?!\\d)", "(?i)mois" },
                new[]
                {
                    "Avant la première mesure, effectuer le réglage du zéro du capteur MZ-4.",
                    "Le capteur MZ-4 doit être recalibré tous les 12 mois."
                }),
            new CanaryCase(
                "cold_chain_shortest_eligible_route",
                "cold_chain_logistics",
                "Parmi les routes documentées qui maintiennent 2 à 8 °C, laquelle a la durée totale la plus courte ? Donne son nom et sa durée.",
                "fr",
                "content",
                "LIVRABLE: route éligible la plus courte et durée\nPREUVES_ATOMIQUES: comparaison sous contrainte documentée",
                "answer",
                new[] { "E2" },
                new[] { "(?i)route lac", "(?<!\\d)18(?!\\d)" },
                new[]
                {
                    "Route Nord : durée totale 26 heures ; température maintenue entre 2 et 8 °C.",
                    "Route Lac : durée totale 18 heures ; température maintenue entre 2 et 8 °C.",
                    "Route Express : durée totale 12 heures ; température maintenue entre 15 et 20 °C."
                }),
            new CanaryCase(
                "hr_remote_work_rule_and_exception",
                "human_resources_policy",
                "Quelle est la limite ordinaire de télétravail et quelle exception est documentée avec accord de la direction ?",
                "fr",
                "content",
                "LIVRABLE: règle ordinaire et exception RH\nPREUVES_ATOMIQUES: deux claims de politique",
                "answer",
                new[] { "E1", "E2" },
                new[] { "(?<!\\d)2(?!\\d)", "(?<!\\d)5(?!\\d)", "(?i)direction" },
                new[]
                {
                    "Le télétravail ordinaire est limité à 2 jours par semaine.",
                    "Une situation exceptionnelle peut autoriser jusqu'à 5 jours par semaine avec l'accord de la direction."
                }),
            new CanaryCase(
                "document_type_inspection_checklist",
                "document_identity",
                "Quel est le type documentaire le plus précis de CL-09 ?",
                "fr",
                "document_family_or_type",
                "LIVRABLE: type documentaire précis\nPREUVES_ATOMIQUES: identité et contenu du document",
                "answer",
                new[] { "E1" },
                new[] { "(?i)liste de contrôle|liste de controle|checklist", "(?i)inspection" },
                new[]
                {
                    "CL-09 — LISTE DE CONTRÔLE D'INSPECTION. Ce document énumère les points à vérifier avant la remise en service."
                }),
            new CanaryCase(
                "insufficient_sensor_battery_autonomy",
                "insufficient_evidence",
                "Quelle est la durée exacte d'autonomie de la batterie du capteur MK-2 ?",
                "fr",
                "content",
                "LIVRABLE: durée exacte d'autonomie\nPREUVES_ATOMIQUES: valeur explicite d'autonomie",
                "research",
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[]
                {
                    "Rechargez le capteur MK-2 avec le câble USB-C fourni.",
                    "Pour l'assistance, communiquez le numéro de série du capteur MK-2 au support."
                })
        };

    private static IReadOnlyList<CanaryCase> BuildCoverageV3Cases()
        => new[]
        {
            new CanaryCase(
                "incident_response_shortest_continuous_plan",
                "cybersecurity_incident_response",
                "Which continuously available incident-response plan activates fastest? Give its name and activation time.",
                "en",
                "content",
                "DELIVERABLE: fastest continuously available plan and activation time\nATOMIC_EVIDENCE: constrained comparison of documented alternatives",
                "answer",
                new[] { "E3" },
                new[] { "(?i)summit", "(?<!\\d)19(?!\\d)" },
                new[]
                {
                    "Plan Dawn activates in 12 minutes but operates only on weekdays from 08:00 to 18:00.",
                    "Plan Harbor operates continuously 24/7 and activates in 28 minutes.",
                    "Plan Summit operates continuously 24/7 and activates in 19 minutes."
                },
                "requested_information_present",
                "write"),
            new CanaryCase(
                "records_retention_period_missing",
                "records_governance",
                "What is the exact records retention period in months?",
                "en",
                "content",
                "DELIVERABLE: exact retention period in months\nATOMIC_EVIDENCE: explicit retention duration",
                "research",
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[]
                {
                    "Archived records are encrypted at rest with organization-managed keys.",
                    "A deletion request must be submitted through the records governance portal."
                },
                "requested_information_missing",
                "research"),
            new CanaryCase(
                "training_variant_requires_user_role",
                "professional_training",
                "Je dois réserver exactement une session TR-8 pour cet utilisateur, mais son rôle n'est pas indiqué. Quelle durée dois-je réserver ?",
                "fr",
                "content",
                "LIVRABLE: durée de l'unique session adaptée au rôle\nPREUVES_ATOMIQUES: variante et durée documentées",
                "clarify",
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[]
                {
                    "La formation TR-8 Opérateur dure 6 heures.",
                    "La formation TR-8 Superviseur dure 10 heures."
                },
                "user_clarification_required",
                "clarification"),
            new CanaryCase(
                "calibration_table_requires_visible_context",
                "technical_manual_navigation",
                "Quelles sont les valeurs exactes de tolérance de calibration du module NV-3 ?",
                "fr",
                "content",
                "LIVRABLE: valeurs exactes de tolérance\nPREUVES_ATOMIQUES: table de calibration documentée",
                "context",
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[]
                {
                    "Manuel NV-3, section 7.4 — Calibration d'urgence : la table des tolérances se trouve dans cette section. Cette entrée d'index ne contient aucune valeur de la table."
                },
                "visible_context_required",
                "documents_context",
                new[] { "E1" }),
            new CanaryCase(
                "laboratory_storage_and_register_review",
                "laboratory_quality_assurance",
                "Quelle plage de température de stockage et quelle fréquence de revue du registre sont documentées ?",
                "fr",
                "content",
                "LIVRABLE: condition de stockage et fréquence de contrôle\nPREUVES_ATOMIQUES: deux claims documentés",
                "answer",
                new[] { "E1", "E2" },
                new[] { "(?<!\\d)18(?!\\d)", "(?<!\\d)22(?!\\d)", "(?<!\\d)90(?!\\d)", "(?i)jours" },
                new[]
                {
                    "Les échantillons doivent être stockés entre 18 et 22 °C.",
                    "Le registre de stockage doit être revu tous les 90 jours."
                },
                "requested_information_present",
                "write")
        };

    private static IReadOnlyList<CanaryCase> BuildFourthV3Cases()
        => new[]
        {
            new CanaryCase(
                "generator_fastest_compliant_start",
                "electrical_backup_systems",
                "Which generator rated at least 90 kW starts fastest? Give its name and start time.",
                "en",
                "content",
                "DELIVERABLE: fastest generator satisfying the minimum output and its start time\nATOMIC_EVIDENCE: constrained comparison of documented alternatives",
                "answer",
                new[] { "E3" },
                new[] { "(?i)cirrus", "(?<!\\d)41(?!\\d)" },
                new[]
                {
                    "Generator Atlas is rated at 80 kW and starts in 25 seconds.",
                    "Generator Boreal is rated at 100 kW and starts in 62 seconds.",
                    "Generator Cirrus is rated at 100 kW and starts in 41 seconds."
                },
                "requested_information_present",
                "write"),
            new CanaryCase(
                "shipping_label_requires_destination_region",
                "shipping_compliance",
                "Je dois imprimer exactement une étiquette R-21, mais la région de destination n'est pas indiquée. Quelle couleur dois-je imprimer ?",
                "fr",
                "content",
                "LIVRABLE: couleur de l'unique etiquette adaptee a la destination\nPREUVES_ATOMIQUES: variante regionale et couleur documentees",
                "clarify",
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[]
                {
                    "Pour une destination nationale, l'étiquette R-21 doit être bleue.",
                    "Pour une destination export, l'étiquette R-21 doit être verte."
                },
                "user_clarification_required",
                "clarification"),
            new CanaryCase(
                "assembly_torque_appendix_requires_context",
                "aerospace_maintenance_navigation",
                "Quelles sont les valeurs exactes de couple de l'assemblage ZX-5 ?",
                "fr",
                "content",
                "LIVRABLE: valeurs exactes de couple\nPREUVES_ATOMIQUES: tableau de maintenance documente",
                "context",
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[]
                {
                    "Index du manuel ZX-5 : les valeurs exactes de couple de l'assemblage figurent dans l'annexe C, page 47. Cette entrée d'index ne contient pas les valeurs."
                },
                "visible_context_required",
                "documents_context",
                new[] { "E1" }),
            new CanaryCase(
                "museum_humidity_alarm_delay_missing",
                "museum_environmental_monitoring",
                "Quel est le délai exact de déclenchement de l'alarme d'humidité, en secondes ?",
                "fr",
                "content",
                "LIVRABLE: delai exact de declenchement en secondes\nPREUVES_ATOMIQUES: valeur explicite du delai",
                "research",
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[]
                {
                    "La plage d'humidité cible des vitrines est comprise entre 45 et 55 %.",
                    "Le capteur d'humidité doit être calibré chaque mois."
                },
                "requested_information_missing",
                "research"),
            new CanaryCase(
                "membership_both_notice_periods",
                "membership_policy",
                "Quels sont les délais de préavis de résiliation documentés pour les formules Basic et Premium ?",
                "fr",
                "content",
                "LIVRABLE: les deux delais explicitement demandes\nPREUVES_ATOMIQUES: formule et delai documentes",
                "answer",
                new[] { "E1", "E2" },
                new[]
                {
                    "(?i)basic", "(?i)premium",
                    "(?<!\\d)30(?!\\d)", "(?<!\\d)7(?!\\d)"
                },
                new[]
                {
                    "La formule Basic exige un préavis de résiliation de 30 jours.",
                    "La formule Premium exige un préavis de résiliation de 7 jours."
                },
                "requested_information_present",
                "write")
        };

    private static IReadOnlyList<CanaryCase> BuildFifthV3Cases()
        => new[]
        {
            new CanaryCase(
                "survey_drone_shortest_recharge",
                "field_survey_equipment",
                "Which survey drone with a payload capacity of at least 4 kg has the shortest documented full recharge time? Give its name and time.",
                "en",
                "content",
                "DELIVERABLE: fastest documented recharge among equipment satisfying the payload threshold, with name and duration\nATOMIC_EVIDENCE: payload capacity and full recharge time for each visible alternative",
                "answer",
                new[] { "E3" },
                new[] { "(?i)cygnus", "(?<!\\d)36(?!\\d)" },
                new[]
                {
                    "Survey drone Arden carries 3 kg and requires 20 minutes for a full recharge.",
                    "Survey drone Brio carries 4.5 kg and requires 58 minutes for a full recharge.",
                    "Survey drone Cygnus carries 5 kg and requires 36 minutes for a full recharge."
                },
                "requested_information_present",
                "write"),
            new CanaryCase(
                "valve_global_exclusivity_beyond_pool",
                "industrial_procurement_scope",
                "Is the Oriole pressure valve the only valve certified anywhere in the world for hydrogen service? Answer yes or no.",
                "en",
                "content",
                "DELIVERABLE: globally exhaustive yes-or-no exclusivity conclusion\nATOMIC_EVIDENCE: certification and explicit catalogue coverage",
                "research",
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[]
                {
                    "In the North Region procurement catalogue, the Oriole pressure valve is certified for hydrogen service under standard S-9.",
                    "This catalogue covers approved suppliers for the North Region only and does not list products offered in other regions."
                },
                "requested_information_missing",
                "research"),
            new CanaryCase(
                "harbor_beacon_delay_missing",
                "harbor_safety_operations",
                "Quel est le délai exact, en secondes, avant que la balise bascule après la détection du brouillard ?",
                "fr",
                "content",
                "LIVRABLE: delai exact de bascule en secondes\nPREUVES_ATOMIQUES: valeur explicite du delai",
                "research",
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[]
                {
                    "En présence de brouillard, la balise de chenal doit afficher une lumière ambre.",
                    "Le capteur de visibilité de la balise doit être testé chaque mois."
                },
                "requested_information_missing",
                "research"),
            new CanaryCase(
                "greenhouse_profile_requires_crop",
                "greenhouse_nutrient_operations",
                "Je dois choisir exactement un profil N-4 pour aujourd'hui, mais la culture n'est pas indiquée. Quelle concentration appliquer ?",
                "fr",
                "content",
                "LIVRABLE: concentration unique du profil adaptee a la culture\nPREUVES_ATOMIQUES: type de culture et concentration documentee",
                "clarify",
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[]
                {
                    "Pour les légumes-feuilles, le profil nutritif N-4 utilise une concentration de 45 mg/L.",
                    "Pour les cultures fruitières, le profil nutritif N-4 utilise une concentration de 70 mg/L."
                },
                "user_clarification_required",
                "clarification"),
            new CanaryCase(
                "rail_wear_annex_requires_context",
                "rail_maintenance_navigation",
                "Quelle est la limite d'usure exacte, en millimètres, du composant Q-17 ?",
                "fr",
                "content",
                "LIVRABLE: limite d'usure exacte en millimetres\nPREUVES_ATOMIQUES: valeur du tableau de maintenance documente",
                "context",
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[]
                {
                    "Index du manuel Q-17 : la limite d'usure exacte figure dans l'annexe H, page 92. Cette entrée d'index ne contient pas la valeur."
                },
                "visible_context_required",
                "documents_context",
                new[] { "E1" })
        };

    private static ToolResults BuildToolResults(CanaryCase canaryCase)
    {
        var hits = canaryCase.Excerpts.Select((excerpt, index) => new
        {
            docId = "canary-" + canaryCase.Id,
            docName = canaryCase.Id + ".pdf",
            docPath = "Synthetic/" + canaryCase.Domain + "/" + canaryCase.Id + ".pdf",
            revisionId = "canary-v1",
            sourceHash = new string((char)('a' + index), 64),
            pageStart = canaryCase.Id == "recipe_shortest_total" ? index + 1 : 1,
            pageEnd = canaryCase.Id == "recipe_shortest_total" ? index + 1 : 1,
            chunkId = canaryCase.Id + ":" + (index + 1),
            excerpt,
            score = 1d - index / 100d
        }).ToArray();
        var results = new ToolResults();
        results.Items.Add(new ToolResults.Item
        {
            ToolName = "rag.search",
            Result = JsonSerializer.SerializeToElement(new { hits }),
            DurationMs = 0
        });
        return results;
    }

    private static async Task<object> InvokeTransactionAsync(
        SourceBackedAgentV2Runner runner,
        SourceBackedIntake intake,
        string semanticPlan,
        EvidenceBundle bundle,
        CancellationToken ct)
    {
        var method = typeof(SourceBackedAgentV2Runner).GetMethod(
            "ReviewSemanticAnswerTransactionAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method!.Invoke(
            runner,
            new object?[]
            {
                intake,
                semanticPlan,
                bundle,
                bundle.Items.Select(static item => item.EvidenceId).ToArray(),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                6,
                ct
            }));
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private static ObservedTransaction ReadObserved(
        object execution,
        RecordingNativeLlmAdapter llm)
    {
        var completion = ReadProperty<SourceBackedAgentCompletion>(
            execution,
            "Completion");
        return new ObservedTransaction(
            ReadProperty<string>(execution, "Decision"),
            ReadProperty<string>(execution, "NextCapability"),
            ReadProperty<string>(execution, "AnswerAdequacy"),
            ReadProperty<bool>(execution, "RequestedDeliverableComplete"),
            ReadProperty<bool>(execution, "MissingUserInputPreventsUniqueResult"),
            ReadProperty<string>(execution, "VisibleContextEvidenceId"),
            ReadProperty<bool>(execution, "ProtocolValid"),
            ReadProperty<IReadOnlyList<string>>(execution, "EvidenceIds"),
            ReadProperty<IReadOnlyList<string>>(execution, "LeadEvidenceIds"),
            ReadProperty<IReadOnlyList<string>>(execution, "PresentedEvidenceIds"),
            ReadProperty<int>(execution, "SourceWindowItemCount"),
            ReadProperty<int>(execution, "EvidencePoolBudget"),
            ReadProperty<int>(execution, "EvidencePoolEligibleItemCount"),
            ReadProperty<int>(execution, "EvidencePoolTruncatedItemCount"),
            ReadProperty<string>(execution, "Assessment"),
            ReadProperty<string>(execution, "Answer"),
            completion.ProtocolError,
            llm.RawOutputs.LastOrDefault() ?? string.Empty,
            llm.StructuredCallCount,
            llm.ContractNames.ToArray(),
            llm.MaximumTokens.ToArray(),
            llm.PromptCharacters.ToArray(),
            completion.PromptTokens,
            completion.CompletionTokens,
            completion.ServerCacheTokens,
            completion.ServerPromptTokensEvaluated,
            completion.ServerPromptMilliseconds,
            completion.ServerPredictedTokens,
            completion.ServerPredictedMilliseconds,
            ReadProperty<long>(execution, "ElapsedMilliseconds"));
    }

    private static IReadOnlyList<string> Evaluate(
        CanaryCase canaryCase,
        ObservedTransaction observed)
    {
        var errors = new List<string>();
        if (!observed.ProtocolValid)
            errors.Add("protocol_invalid:" + observed.ProtocolError);
        var expectedAdequacy = canaryCase.ExpectedAdequacy
            ?? canaryCase.ExpectedDecision switch
            {
                "answer" => "requested_information_present",
                "research" or "continue" => "requested_information_missing",
                "clarify" => "user_clarification_required",
                "context" => "visible_context_required",
                _ => string.Empty
            };
        if (observed.AnswerAdequacy != expectedAdequacy)
            errors.Add("answer_adequacy=" + observed.AnswerAdequacy);
        var expectedDeliverableComplete =
            canaryCase.ExpectedDecision == "answer";
        var expectedMissingUserInput =
            canaryCase.ExpectedDecision == "clarify";
        var expectedVisibleContextEvidenceId =
            canaryCase.ExpectedDecision == "context"
                ? canaryCase.ExpectedLeadEvidenceIds?.FirstOrDefault() ?? "NONE"
                : "NONE";
        if (observed.RequestedDeliverableComplete
            != expectedDeliverableComplete)
        {
            errors.Add("requested_deliverable_complete="
                       + observed.RequestedDeliverableComplete);
        }
        if (observed.MissingUserInputPreventsUniqueResult
            != expectedMissingUserInput)
        {
            errors.Add("missing_user_input_prevents_unique_result="
                       + observed.MissingUserInputPreventsUniqueResult);
        }
        if (observed.VisibleContextEvidenceId
            != expectedVisibleContextEvidenceId)
        {
            errors.Add("visible_context_evidence_id="
                       + observed.VisibleContextEvidenceId);
        }
        if (observed.StructuredCallCount != 1)
            errors.Add("structured_call_count=" + observed.StructuredCallCount);
        if (observed.ContractNames.Count != 1
            || observed.ContractNames[0] != ContractName)
        {
            errors.Add("contract=" + string.Join(',', observed.ContractNames));
        }
        if (!observed.PresentedEvidenceIds.OrderBy(static id => id)
                .SequenceEqual(
                    Enumerable.Range(1, canaryCase.Excerpts.Count)
                        .Select(static index => "E" + index)
                        .OrderBy(static id => id),
                    StringComparer.OrdinalIgnoreCase))
        {
            errors.Add("presented_pool=" + string.Join(',', observed.PresentedEvidenceIds));
        }
        if (observed.EvidencePoolBudget < canaryCase.Excerpts.Count)
            errors.Add("evidence_pool_budget=" + observed.EvidencePoolBudget);
        if (observed.EvidencePoolEligibleItemCount != canaryCase.Excerpts.Count)
            errors.Add("evidence_pool_eligible_item_count="
                       + observed.EvidencePoolEligibleItemCount);
        if (observed.SourceWindowItemCount != canaryCase.Excerpts.Count)
            errors.Add("source_window_item_count="
                       + observed.SourceWindowItemCount);
        if (observed.EvidencePoolTruncatedItemCount != 0)
            errors.Add("evidence_pool_truncated_item_count="
                       + observed.EvidencePoolTruncatedItemCount);
        if (canaryCase.ExpectedNextCapability is not null
            && observed.NextCapability != canaryCase.ExpectedNextCapability)
        {
            errors.Add("next_capability=" + observed.NextCapability);
        }
        if (canaryCase.ExpectedLeadEvidenceIds is not null
            && !observed.LeadEvidenceIds.OrderBy(static id => id).SequenceEqual(
                canaryCase.ExpectedLeadEvidenceIds.OrderBy(static id => id),
                StringComparer.OrdinalIgnoreCase))
        {
            errors.Add("lead_evidence_ids="
                       + string.Join(',', observed.LeadEvidenceIds));
        }
        if (canaryCase.ExpectedDecision == "answer")
        {
            if (observed.Decision != "ready")
                errors.Add("decision=" + observed.Decision);
            if (string.IsNullOrWhiteSpace(observed.Answer))
                errors.Add("answer_empty");
            if (!observed.EvidenceIds.OrderBy(static id => id).SequenceEqual(
                    canaryCase.ExpectedEvidenceIds.OrderBy(static id => id),
                    StringComparer.OrdinalIgnoreCase))
            {
                errors.Add("evidence_ids=" + string.Join(',', observed.EvidenceIds));
            }
            foreach (var pattern in canaryCase.RequiredPatterns)
            {
                if (!Regex.IsMatch(
                        observed.Answer,
                        pattern,
                        RegexOptions.CultureInvariant))
                {
                    errors.Add("missing_pattern=" + pattern);
                }
            }
        }
        else if (canaryCase.ExpectedDecision == "clarify")
        {
            if (observed.Decision != "clarify")
                errors.Add("decision=" + observed.Decision);
            if (!string.IsNullOrWhiteSpace(observed.Answer))
                errors.Add("unexpected_answer=" + observed.Answer);
            if (observed.EvidenceIds.Count != 0)
                errors.Add("unexpected_answer_ids=" + string.Join(',', observed.EvidenceIds));
        }
        else
        {
            if (observed.Decision != "continue")
                errors.Add("decision=" + observed.Decision);
            if (canaryCase.ExpectedNextCapability is null
                && (canaryCase.ExpectedDecision == "research"
                    ? observed.NextCapability != "research"
                    : observed.NextCapability is not ("documents_context" or "research")))
                errors.Add("next_capability=" + observed.NextCapability);
            if (!string.IsNullOrWhiteSpace(observed.Answer))
                errors.Add("unexpected_answer=" + observed.Answer);
            if (observed.EvidenceIds.Count != 0)
                errors.Add("unexpected_answer_ids=" + string.Join(',', observed.EvidenceIds));
        }
        if (observed.PromptTokens is not > 0)
            errors.Add("prompt_tokens=" + observed.PromptTokens);
        if (observed.CompletionTokens is not > 0)
            errors.Add("completion_tokens=" + observed.CompletionTokens);
        if (observed.ServerPromptTokensEvaluated is not > 0)
            errors.Add("server_prompt_tokens_evaluated=" + observed.ServerPromptTokensEvaluated);
        if (observed.ServerPromptMilliseconds is not > 0)
            errors.Add("server_prompt_ms=" + observed.ServerPromptMilliseconds);
        if (observed.ServerPredictedTokens is not > 0)
            errors.Add("server_predicted_tokens=" + observed.ServerPredictedTokens);
        if (observed.ServerPredictedMilliseconds is not > 0)
            errors.Add("server_predicted_ms=" + observed.ServerPredictedMilliseconds);
        return errors;
    }

    private static T ReadProperty<T>(object instance, string propertyName)
        => (T)instance.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(instance)!;

    private static string NormalizeLlmBaseUrl(string value)
        => value.Trim().TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? value.Trim().TrimEnd('/')[..^3]
            : value.Trim().TrimEnd('/');

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "RAG.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
               ?? throw new InvalidOperationException("Could not locate repository root.");
    }

    private sealed class RecordingNativeLlmAdapter(OpenAiLlmClient inner)
        : ISourceBackedAgentLlmClient,
          ISourceBackedAgentStructuredLlmClient
    {
        public int StructuredCallCount { get; private set; }
        public List<string> ContractNames { get; } = new();
        public List<string> RawOutputs { get; } = new();
        public List<int> MaximumTokens { get; } = new();
        public List<int> PromptCharacters { get; } = new();

        public Task<SourceBackedAgentCompletion> CompleteAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            int maxTokens,
            CancellationToken ct,
            double? temperatureOverride = null,
            bool requireToolCall = false)
            => throw new InvalidOperationException(
                "The canary forbids an unstructured fallback call.");

        public async Task<SourceBackedAgentCompletion> CompleteStructuredAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            LlmStructuredOutputContract contract,
            int maxTokens,
            CancellationToken ct,
            double? temperatureOverride = null)
        {
            StructuredCallCount++;
            ContractNames.Add(contract.Name);
            MaximumTokens.Add(maxTokens);
            PromptCharacters.Add(messages.Sum(static message =>
                message.Content?.Length ?? 0));
            var completion = await inner.ChatOnceStructuredCompletionAsync(
                messages.Select(static message =>
                    (message.Role, message.Content ?? string.Empty)).ToArray(),
                temperatureOverride ?? 0,
                Math.Clamp(maxTokens, 128, 640),
                contract,
                ct);
            RawOutputs.Add(completion.Content);
            return completion;
        }
    }

    private sealed class UnusedToolExecutor : ISourceBackedAgentToolExecutor
    {
        public Task<ToolResults> ExecuteToolCallAsync(
            SourceBackedIntake intake,
            string toolName,
            JsonElement arguments,
            CancellationToken ct)
            => throw new InvalidOperationException(
                "The isolated canary must not execute retrieval tools.");
    }

    private sealed record CanaryCase(
        string Id,
        string Domain,
        string Question,
        string Language,
        string QuestionFocus,
        string SemanticPlan,
        string ExpectedDecision,
        IReadOnlyList<string> ExpectedEvidenceIds,
        IReadOnlyList<string> RequiredPatterns,
        IReadOnlyList<string> Excerpts,
        string? ExpectedAdequacy = null,
        string? ExpectedNextCapability = null,
        IReadOnlyList<string>? ExpectedLeadEvidenceIds = null);

    private sealed record ObservedTransaction(
        string Decision,
        string NextCapability,
        string AnswerAdequacy,
        bool RequestedDeliverableComplete,
        bool MissingUserInputPreventsUniqueResult,
        string VisibleContextEvidenceId,
        bool ProtocolValid,
        IReadOnlyList<string> EvidenceIds,
        IReadOnlyList<string> LeadEvidenceIds,
        IReadOnlyList<string> PresentedEvidenceIds,
        int SourceWindowItemCount,
        int EvidencePoolBudget,
        int EvidencePoolEligibleItemCount,
        int EvidencePoolTruncatedItemCount,
        string Reason,
        string RenderedAnswer,
        string? ProtocolError,
        string RawOutput,
        int StructuredCallCount,
        IReadOnlyList<string> ContractNames,
        IReadOnlyList<int> MaximumTokens,
        IReadOnlyList<int> PromptCharacters,
        int? PromptTokens,
        int? CompletionTokens,
        int? ServerCacheTokens,
        int? ServerPromptTokensEvaluated,
        double? ServerPromptMilliseconds,
        int? ServerPredictedTokens,
        double? ServerPredictedMilliseconds,
        long ElapsedMilliseconds)
    {
        public string Answer => RenderedAnswer;
    }

    private sealed record CanaryResult(
        string Id,
        string Domain,
        string Question,
        string ExpectedDecision,
        IReadOnlyList<string> ExpectedEvidenceIds,
        IReadOnlyList<string> RequiredPatterns,
        ObservedTransaction Observed,
        IReadOnlyList<string> Errors);
}
