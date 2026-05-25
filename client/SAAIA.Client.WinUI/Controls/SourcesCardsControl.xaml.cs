using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Models;
using SAAIA.Client.WinUI.Services;

namespace SAAIA.Client.WinUI.Controls;

public sealed partial class SourcesCardsControl : UserControl
{
    private string _uiLanguage = ClientUiText.NormalizeLanguage(AppSettings.Load().UiLanguage);

    public SourcesCardsControl()
    {
        InitializeComponent();
        ApplyUiLanguage();
        Visibility = Visibility.Collapsed;
    }

    public void ApplyUiLanguage(string? uiLanguage = null)
    {
        _uiLanguage = ClientUiText.NormalizeLanguage(uiLanguage ?? AppSettings.Load().UiLanguage);
        SourcesHeaderText.Text = GetSourcesHeaderText(_uiLanguage);
        ApplyCardLanguage(Items);
        RefreshItemsSource();
        ApplyButtonLanguage();
    }

    public IList<SourceCard> Items
    {
        get => (IList<SourceCard>)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public static readonly DependencyProperty ItemsProperty =
        DependencyProperty.Register(
            nameof(Items),
            typeof(IList<SourceCard>),
            typeof(SourcesCardsControl),
            new PropertyMetadata(new List<SourceCard>(), OnItemsChanged));

    private static void OnItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (SourcesCardsControl)d;

        var list = e.NewValue as IList<SourceCard> ?? new List<SourceCard>();
        self.ApplyCardLanguage(list);
        self.ItemsHost.ItemsSource = list;
        self.ApplyButtonLanguage();

        self.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not SourceCard s)
            return;

        var page = s.PageStart ?? s.PageEnd;
        var result = await DocumentLauncher.TryOpenAsync(s.DocPath, page);
        if (result.Success)
            return;

        await ShowErrorAsync(
            result.ErrorTitle ?? ST("Impossible d'ouvrir le fichier", "Could not open the file", "No se pudo abrir el archivo", "Nao foi possivel abrir o ficheiro", "Datei konnte nicht geoeffnet werden", "Impossibile aprire il file", _uiLanguage),
            result.ErrorMessage ?? ST("Erreur inconnue.", "Unknown error.", "Error desconocido.", "Erro desconhecido.", "Unbekannter Fehler.", "Errore sconosciuto.", _uiLanguage));
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        try
        {
            var dlg = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = ClientUiText.Get("dialog.close", _uiLanguage),
                XamlRoot = this.XamlRoot
            };
            await dlg.ShowAsync();
        }
        catch
        {
            // Edge case: no XamlRoot available. Avoid crashing while keeping the UI responsive.
        }
    }

    private void OpenButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
            button.Content = GetOpenButtonText(_uiLanguage);
    }

    private void ApplyButtonLanguage()
    {
        foreach (var button in EnumerateButtons(ItemsHost))
        {
            if (button.Tag is SourceCard)
                button.Content = GetOpenButtonText(_uiLanguage);
        }
    }

    private void ApplyCardLanguage(IList<SourceCard>? items)
    {
        if (items is null)
            return;

        foreach (var item in items)
        {
            item.PagesLabel = GetPagesLabel(item, _uiLanguage);
            item.ScoreLabel = GetScoreLabel(item, _uiLanguage);
            item.MetadataLabel = GetMetadataLabel(item, _uiLanguage);
        }
    }

    private void RefreshItemsSource()
    {
        if (ItemsHost is null)
            return;

        var items = Items;
        ItemsHost.ItemsSource = null;
        ItemsHost.ItemsSource = items;
    }

    private static IEnumerable<Button> EnumerateButtons(DependencyObject root)
    {
        if (root is null)
            yield break;

        var childCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Button button)
                yield return button;

            foreach (var nested in EnumerateButtons(child))
                yield return nested;
        }
    }

    internal static string GetSourcesHeaderText(string? uiLanguage = null)
        => ST("Sources", "Sources", "Fuentes", "Fontes", "Quellen", "Fonti", uiLanguage);

    internal static string GetOpenButtonText(string? uiLanguage = null)
        => ST("Ouvrir", "Open", "Abrir", "Abrir", "Oeffnen", "Apri", uiLanguage);

    internal static string GetPagesLabel(SourceCard source, string? uiLanguage = null)
    {
        if (source.PageStart is null && source.PageEnd is null)
            return string.Empty;

        var prefix = ST("p.", "p.", "p.", "p.", "S.", "p.", uiLanguage);
        if (source.PageStart is not null && source.PageEnd is null)
            return $"{prefix} {source.PageStart}";
        if (source.PageStart is null && source.PageEnd is not null)
            return $"{prefix} {source.PageEnd}";
        return source.PageStart == source.PageEnd
            ? $"{prefix} {source.PageStart}"
            : $"{prefix} {source.PageStart}-{source.PageEnd}";
    }

    internal static string GetScoreLabel(SourceCard source, string? uiLanguage = null)
    {
        if (source.Score is null)
            return string.Empty;

        var label = ST("score", "score", "score", "score", "Score", "score", uiLanguage);
        return $"{label} {source.Score.Value.ToString("0.###", CultureInfo.InvariantCulture)}";
    }

    internal static string GetMetadataLabel(SourceCard source, string? uiLanguage = null)
    {
        var parts = new List<string>();
        var docLanguage = LocalizedStrings.LocalizedSourceLanguageName(source.DocLanguage, uiLanguage);
        var profileLanguage = LocalizedStrings.LocalizedSourceLanguageName(source.ProfileLanguage, uiLanguage);
        var normalizedDocLanguage = LocalizedStrings.NormalizeSourceLanguageIdentifier(source.DocLanguage);
        var normalizedProfileLanguage = LocalizedStrings.NormalizeSourceLanguageIdentifier(source.ProfileLanguage);
        if (!string.IsNullOrWhiteSpace(docLanguage)
            && !string.IsNullOrWhiteSpace(profileLanguage)
            && !string.Equals(normalizedDocLanguage, normalizedProfileLanguage, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"{SourceCardLabel("document_language", uiLanguage)} {Shorten(docLanguage, 24)}");
            parts.Add($"{SourceCardLabel("profile_language", uiLanguage)} {Shorten(profileLanguage, 24)}");
        }
        else
        {
            var language = FirstNonBlank(docLanguage, profileLanguage);
            if (!string.IsNullOrWhiteSpace(language))
                parts.Add($"{SourceCardLabel("language", uiLanguage)} {Shorten(language!, 24)}");
        }

        var pageQuality = FirstNonBlank(source.PageQualityStatus);
        var documentQuality = FirstNonBlank(source.DocumentQualityStatus);
        if (!string.IsNullOrWhiteSpace(pageQuality)
            && !string.IsNullOrWhiteSpace(documentQuality)
            && !string.Equals(
                NormalizeBackendIdentifier(pageQuality),
                NormalizeBackendIdentifier(documentQuality),
                StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(BuildQualityPart(
                SourceCardLabel("page_quality", uiLanguage),
                pageQuality,
                source.PageExtractionConfidence ?? source.ExtractionConfidence,
                uiLanguage));
            parts.Add(BuildQualityPart(
                SourceCardLabel("document_quality", uiLanguage),
                documentQuality,
                source.DocumentExtractionConfidence ?? source.ExtractionConfidence,
                uiLanguage));
        }
        else
        {
            var quality = FirstNonBlank(pageQuality, documentQuality, source.QualityStatus, source.TextStatus);
            if (!string.IsNullOrWhiteSpace(quality))
            {
                parts.Add(BuildQualityPart(
                    SourceCardLabel("quality", uiLanguage),
                    quality,
                    GetConfidenceForQuality(source, quality),
                    uiLanguage));
            }
        }

        if (source.OcrAttempted)
            parts.Add(SourceCardLabel("ocr_attempted", uiLanguage));
        if (source.OcrApplied)
            parts.Add(SourceCardLabel("ocr_applied", uiLanguage));
        if (source.OcrRecommended)
            parts.Add(SourceCardLabel("ocr_recommended", uiLanguage));

        AddDiagnosticMetadataParts(parts, source.ExtractionDiagnosticSummary, uiLanguage);

        if (source.ManualReviewRecommended)
            parts.Add(SourceCardLabel("review_recommended", uiLanguage));

        var evidenceRole = LocalizedSelectionHintRole(source.SelectionHintEvidenceRole, uiLanguage);
        if (!string.IsNullOrWhiteSpace(evidenceRole))
            parts.Add($"{SourceCardLabel("evidence_role", uiLanguage)} {Shorten(evidenceRole, 32)}");

        var selectionScore = GetSelectionScore(source);
        if (selectionScore.HasValue)
            parts.Add($"{SourceCardLabel("selection_score", uiLanguage)} {selectionScore.Value.ToString(CultureInfo.InvariantCulture)}");

        var contentRole = LocalizedContentRole(source.ContentRole, uiLanguage);
        if (!string.IsNullOrWhiteSpace(contentRole))
            parts.Add($"{SourceCardLabel("content_role", uiLanguage)} {Shorten(contentRole, 32)}");

        if (source.ContentDensityScore.HasValue)
            parts.Add($"{SourceCardLabel("content_density", uiLanguage)} {source.ContentDensityScore.Value.ToString("0.##", CultureInfo.InvariantCulture)}");

        if (source.RetrievalNavigationScore.HasValue)
            parts.Add($"{SourceCardLabel("retrieval_navigation", uiLanguage)} {source.RetrievalNavigationScore.Value.ToString("0.##", CultureInfo.InvariantCulture)}");

        var navigationReason = FormatBackendReason(source.NavigationReason);
        if (!string.IsNullOrWhiteSpace(navigationReason))
            parts.Add($"{SourceCardLabel("navigation_reason", uiLanguage)} {Shorten(navigationReason, 36)}");

        var sourceHash = ShortSourceHash(source.SourceHash);
        if (!string.IsNullOrWhiteSpace(sourceHash))
            parts.Add($"{SourceCardLabel("hash", uiLanguage)} {sourceHash}");

        if (source.MatchedContentCards is { Count: > 0 } matchedContentCards)
        {
            var cardsPart = BuildMatchedContentCardsMetadataPart(matchedContentCards, uiLanguage, source.DocLanguage);
            if (!string.IsNullOrWhiteSpace(cardsPart))
                parts.Add(cardsPart);
        }

        var profileSignalsPart = BuildProfileSignalsMetadataPart(source.ProfileSignals, uiLanguage);
        if (!string.IsNullOrWhiteSpace(profileSignalsPart))
            parts.Add(profileSignalsPart);

        var category = FirstNonBlank(source.CategoryPath, source.CategoryRef, source.Category);
        if (!string.IsNullOrWhiteSpace(category))
        {
            var label = SourceCardLabel("category", uiLanguage);
            parts.Add($"{label} {Shorten(category!, 72)}");
        }

        return string.Join(" | ", parts);
    }

    private static string BuildMatchedContentCardsMetadataPart(
        IReadOnlyList<SourceContentCard> cards,
        string? uiLanguage,
        string? docLanguage)
    {
        var parts = new List<string>();
        var titles = string.Join(", ", cards
            .Select(static card => card.Title)
            .Where(static title => !string.IsNullOrWhiteSpace(title))
            .Take(3));
        if (!string.IsNullOrWhiteSpace(titles))
        {
            var suffix = cards.Count > 3 ? $" +{cards.Count - 3}" : string.Empty;
            parts.Add($"{SourceCardLabel("content_cards", uiLanguage)} {Shorten(titles, 72)}{suffix}");
        }

        var ids = cards
            .Select(static card => ShortSourceHash(card.ContentCardId))
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();
        if (ids.Length > 0)
            parts.Add($"{SourceCardLabel("content_card_ids", uiLanguage)} {string.Join(", ", ids)}");

        var evidence = BuildContentCardEvidenceSummary(cards, uiLanguage, docLanguage);
        if (!string.IsNullOrWhiteSpace(evidence))
            parts.Add($"{SourceCardLabel("content_card_evidence", uiLanguage)} {evidence}");

        return string.Join("; ", parts);
    }

    private static string? BuildProfileSignalsMetadataPart(SourceProfileSignals? signals, string? uiLanguage)
    {
        if (signals is null)
            return null;

        var parts = new List<string>();
        AddProfileSignalList(parts, "profile_terms", signals.MatchedTerms, uiLanguage, maxItems: 3);
        AddProfileSignalList(parts, "profile_keywords", signals.Keywords, uiLanguage, maxItems: 3);
        AddProfileSignalList(parts, "profile_topics", signals.Topics, uiLanguage, maxItems: 2);

        var version = FormatBackendReason(signals.ProfileVersion);
        if (!string.IsNullOrWhiteSpace(version))
            parts.Add($"{SourceCardLabel("profile_version", uiLanguage)} {Shorten(version, 32)}");

        if (signals.MatchCount is > 0)
            parts.Add($"{SourceCardLabel("profile_matches", uiLanguage)} {signals.MatchCount.Value.ToString(CultureInfo.InvariantCulture)}");

        return parts.Count == 0
            ? null
            : $"{SourceCardLabel("profile_signals", uiLanguage)} {string.Join("; ", parts)}";
    }

    private static void AddProfileSignalList(
        ICollection<string> parts,
        string labelKey,
        IReadOnlyList<string>? values,
        string? uiLanguage,
        int maxItems)
    {
        if (values is not { Count: > 0 })
            return;

        var compact = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maxItems, 1, 6))
            .ToArray();
        if (compact.Length == 0)
            return;

        var suffix = values.Count > compact.Length ? $" +{values.Count - compact.Length}" : string.Empty;
        parts.Add($"{SourceCardLabel(labelKey, uiLanguage)} {Shorten(string.Join(", ", compact), 72)}{suffix}");
    }

    private static string? BuildContentCardEvidenceSummary(
        IReadOnlyList<SourceContentCard> cards,
        string? uiLanguage,
        string? docLanguage)
    {
        var parts = new List<string>();
        var factCount = cards.Sum(static card => CountEvidenceFacts(card.Evidence));
        var factsLabel = SourceCardLabel("content_card_facts", uiLanguage);
        var confidences = cards
            .Select(static card => TryGetEvidenceConfidence(card.Evidence))
            .Where(static confidence => confidence.HasValue)
            .Select(static confidence => confidence!.Value)
            .ToArray();
        var confidenceText = confidences.Length == 0
            ? null
            : confidences.Max().ToString("0%", CultureInfo.InvariantCulture);
        if (confidences.Length > 0)
        {
            parts.Add(factCount > 0
                ? $"{factsLabel} {factCount}, {confidenceText}"
                : confidenceText!);
        }
        else
        {
            if (factCount > 0)
                parts.Add($"{factsLabel} {factCount}");
        }

        var scaleBasis = cards
            .Select(card => TryGetScaleBasisText(card.Evidence, uiLanguage))
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        if (!string.IsNullOrWhiteSpace(scaleBasis))
            parts.Add($"{SourceCardLabel("content_card_scale_basis", uiLanguage)} {Shorten(scaleBasis!, 32)}");

        var nonScalableCount = cards.Sum(static card => CountNonScalableReasons(card.Evidence));
        if (nonScalableCount > 0)
            parts.Add($"{SourceCardLabel("content_card_non_scalable", uiLanguage)} {nonScalableCount.ToString(CultureInfo.InvariantCulture)}");

        var evidenceLanguage = cards
            .Select(static card => TryGetEvidenceLanguage(card.Evidence))
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        var normalizedEvidenceLanguage = LocalizedStrings.NormalizeSourceLanguageIdentifier(evidenceLanguage);
        var normalizedDocLanguage = LocalizedStrings.NormalizeSourceLanguageIdentifier(docLanguage);
        if (!string.IsNullOrWhiteSpace(evidenceLanguage)
            && !string.IsNullOrWhiteSpace(normalizedEvidenceLanguage)
            && !string.Equals(normalizedEvidenceLanguage, normalizedDocLanguage, StringComparison.OrdinalIgnoreCase))
        {
            var localizedLanguage = LocalizedStrings.LocalizedSourceLanguageName(evidenceLanguage, uiLanguage);
            if (!string.IsNullOrWhiteSpace(localizedLanguage))
                parts.Add($"{SourceCardLabel("language", uiLanguage)} {Shorten(localizedLanguage, 24)}");
        }

        var factDetails = BuildContentCardFactDetails(cards);
        if (!string.IsNullOrWhiteSpace(factDetails))
            parts.Add($"{SourceCardLabel("content_card_values", uiLanguage)} {Shorten(factDetails!, 72)}");

        var nonScalableReasons = BuildContentCardNonScalableReasonsText(cards, uiLanguage);
        if (!string.IsNullOrWhiteSpace(nonScalableReasons))
            parts.Add($"{SourceCardLabel("content_card_reasons", uiLanguage)} {Shorten(nonScalableReasons!, 72)}");

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static string? BuildContentCardFactDetails(IReadOnlyList<SourceContentCard> cards)
    {
        var details = cards
            .SelectMany(static card => ExtractContentCardFactDetails(card.Evidence))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();

        return details.Length == 0 ? null : string.Join("; ", details);
    }

    private static IEnumerable<string> ExtractContentCardFactDetails(JsonElement? evidence)
    {
        if (evidence is not { ValueKind: JsonValueKind.Object } value)
            yield break;

        var quantityFacts = TryGetArray(value, "quantityFacts", "quantity_facts", "QuantityFacts");
        if (quantityFacts is { ValueKind: JsonValueKind.Array } quantities)
        {
            foreach (var fact in quantities.EnumerateArray())
            {
                var detail = BuildContentCardFactDetail(fact);
                if (!string.IsNullOrWhiteSpace(detail))
                    yield return detail!;
            }
        }

        var facts = TryGetArray(value, "facts", "Facts");
        if (facts is not { ValueKind: JsonValueKind.Array } genericFacts)
            yield break;

        foreach (var fact in genericFacts.EnumerateArray())
        {
            var detail = BuildContentCardFactDetail(fact);
            if (!string.IsNullOrWhiteSpace(detail))
                yield return detail!;
        }
    }

    private static string? BuildContentCardFactDetail(JsonElement fact)
    {
        if (fact.ValueKind == JsonValueKind.String)
            return NormalizeEvidenceDisplayText(fact.GetString());

        if (fact.ValueKind != JsonValueKind.Object)
            return null;

        var label = NormalizeEvidenceDisplayText(TryGetString(fact, "label", "Label", "name", "Name", "title", "Title"));
        var valueText = NormalizeEvidenceDisplayText(TryGetValueText(fact, "value", "Value", "count", "Count"));
        var unit = NormalizeEvidenceDisplayText(TryGetString(fact, "unit", "Unit"));
        var kind = NormalizeEvidenceDisplayText(TryGetString(fact, "kind", "Kind", "type", "Type"));
        var numericText = string.Join(" ", new[] { valueText, unit }
            .Where(static value => !string.IsNullOrWhiteSpace(value)));

        if (!string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(numericText))
            return $"{label} {numericText}";
        if (!string.IsNullOrWhiteSpace(label))
            return label;
        if (!string.IsNullOrWhiteSpace(numericText) && !string.IsNullOrWhiteSpace(kind))
            return $"{kind} {numericText}";
        if (!string.IsNullOrWhiteSpace(numericText))
            return numericText;
        return kind;
    }

    private static string? BuildContentCardNonScalableReasonsText(IReadOnlyList<SourceContentCard> cards, string? uiLanguage)
    {
        var reasons = cards
            .SelectMany(card => ExtractContentCardNonScalableReasons(card.Evidence, uiLanguage))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();

        return reasons.Length == 0 ? null : string.Join("; ", reasons);
    }

    private static IEnumerable<string> ExtractContentCardNonScalableReasons(JsonElement? evidence, string? uiLanguage)
    {
        if (evidence is not { ValueKind: JsonValueKind.Object } value)
            yield break;

        var reasons = TryGetArray(value, "nonScalableReasons", "non_scalable_reasons", "NonScalableReasons");
        if (reasons is not { ValueKind: JsonValueKind.Array } array)
            yield break;

        foreach (var reason in array.EnumerateArray())
        {
            var text = reason.ValueKind == JsonValueKind.String
                ? reason.GetString()
                : reason.ValueKind == JsonValueKind.Object
                    ? TryGetString(reason, "reason", "Reason", "label", "Label", "kind", "Kind")
                    : null;
            var normalized = LocalizedContentCardReason(text, uiLanguage);
            if (!string.IsNullOrWhiteSpace(normalized))
                yield return normalized!;
        }
    }

    private static string? LocalizedContentCardReason(string? reason, string? uiLanguage)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return null;

        var normalized = NormalizeBackendIdentifier(reason);
        return normalized switch
        {
            "safety_or_parameter_context" or "technical_parameter_context" => ST(
                "contexte sécurité ou paramètre",
                "safety or parameter context",
                "contexto de seguridad o parámetro",
                "contexto de segurança ou parâmetro",
                "Sicherheits- oder Parameterkontext",
                "contesto di sicurezza o parametro",
                uiLanguage),
            "time_limit" or "time_context" or "duration_context" => ST(
                "contrainte de temps",
                "time constraint",
                "restricción de tiempo",
                "restrição de tempo",
                "Zeitvorgabe",
                "vincolo di tempo",
                uiLanguage),
            "temperature" or "temperature_context" => ST(
                "température",
                "temperature",
                "temperatura",
                "temperatura",
                "Temperatur",
                "temperatura",
                uiLanguage),
            "pressure" or "pressure_context" => ST(
                "pression",
                "pressure",
                "presión",
                "pressão",
                "Druck",
                "pressione",
                uiLanguage),
            "speed" or "speed_context" => ST(
                "vitesse",
                "speed",
                "velocidad",
                "velocidade",
                "Geschwindigkeit",
                "velocità",
                uiLanguage),
            "voltage" or "voltage_context" => ST(
                "tension",
                "voltage",
                "tensión",
                "tensão",
                "Spannung",
                "tensione",
                uiLanguage),
            "page" or "page_reference" or "page_context" => ST(
                "référence de page",
                "page reference",
                "referencia de página",
                "referência de página",
                "Seitenverweis",
                "riferimento di pagina",
                uiLanguage),
            "currency" or "currency_context" or "price_context" => ST(
                "montant",
                "amount",
                "importe",
                "montante",
                "Betrag",
                "importo",
                uiLanguage),
            _ => ST(
                "contrainte documentee",
                "documented constraint",
                "restriccion documentada",
                "restricao documentada",
                "dokumentierte Vorgabe",
                "vincolo documentato",
                uiLanguage)
        };
    }

    private static int CountEvidenceFacts(JsonElement? evidence)
    {
        if (evidence is not { ValueKind: JsonValueKind.Object } value)
            return 0;

        var count = 0;
        if (value.TryGetProperty("facts", out var facts) && facts.ValueKind == JsonValueKind.Array)
            count += facts.GetArrayLength();
        if (value.TryGetProperty("quantityFacts", out var quantityFacts) && quantityFacts.ValueKind == JsonValueKind.Array)
            count += quantityFacts.GetArrayLength();
        if (value.TryGetProperty("quantity_facts", out quantityFacts) && quantityFacts.ValueKind == JsonValueKind.Array)
            count += quantityFacts.GetArrayLength();
        return count;
    }

    private static double? TryGetEvidenceConfidence(JsonElement? evidence)
    {
        if (evidence is not { ValueKind: JsonValueKind.Object } value)
            return null;
        if (value.TryGetProperty("confidence", out var confidence)
            && confidence.ValueKind == JsonValueKind.Number
            && confidence.TryGetDouble(out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string? TryGetScaleBasisText(JsonElement? evidence, string? uiLanguage)
    {
        if (evidence is not { ValueKind: JsonValueKind.Object } value)
            return null;

        var scaleBasis = TryGetObject(value, "scaleBasis", "scale_basis", "ScaleBasis");
        if (scaleBasis is not { ValueKind: JsonValueKind.Object } basis)
            return null;

        var count = TryGetDouble(basis, "count", "value", "Count", "Value");
        var label = LocalizedScaleBasisLabel(TryGetString(basis, "label", "Label"), uiLanguage);
        if (count is null && string.IsNullOrWhiteSpace(label))
            return null;

        var countText = count is null
            ? string.Empty
            : count.Value.ToString(Math.Abs(count.Value - Math.Round(count.Value)) < 0.0001 ? "0" : "0.##", CultureInfo.InvariantCulture);
        return $"{countText} {label}".Trim();
    }

    private static string? LocalizedScaleBasisLabel(string? label, string? uiLanguage)
    {
        var normalized = NormalizeBackendIdentifier(label);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.IsNullOrWhiteSpace(label) ? null : label;

        return normalized switch
        {
            "person" or "persons" or "personne" or "personnes" or "people" => ST("personnes", "people", "personas", "pessoas", "Personen", "persone", uiLanguage),
            "unit" or "units" or "unite" or "unites" => ST("unités", "units", "unidades", "unidades", "Einheiten", "unità", uiLanguage),
            "item" or "items" or "element" or "elements" => ST("éléments", "elements", "elementos", "elementos", "Elemente", "elementi", uiLanguage),
            "piece" or "pieces" or "part" or "parts" => ST("pièces", "pieces", "piezas", "peças", "Teile", "pezzi", uiLanguage),
            "batch" or "batches" or "lot" or "lots" => ST("lots", "batches", "lotes", "lotes", "Chargen", "lotti", uiLanguage),
            "set" or "sets" or "serie" or "series" => ST("séries", "sets", "series", "séries", "Sätze", "serie", uiLanguage),
            _ => label
        };
    }

    private static int CountNonScalableReasons(JsonElement? evidence)
    {
        if (evidence is not { ValueKind: JsonValueKind.Object } value)
            return 0;

        var reasons = TryGetArray(value, "nonScalableReasons", "non_scalable_reasons", "NonScalableReasons");
        return reasons is { ValueKind: JsonValueKind.Array } array ? array.GetArrayLength() : 0;
    }

    private static string? TryGetEvidenceLanguage(JsonElement? evidence)
    {
        if (evidence is not { ValueKind: JsonValueKind.Object } value)
            return null;

        return TryGetString(value, "language", "Language", "lang", "Lang");
    }

    private static JsonElement? TryGetObject(JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Object)
                return property;
        }

        return null;
    }

    private static JsonElement? TryGetArray(JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Array)
                return property;
        }

        return null;
    }

    private static string? TryGetString(JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
                return property.GetString();
        }

        return null;
    }

    private static string? TryGetValueText(JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (!value.TryGetProperty(name, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.String)
                return property.GetString();
            if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number))
                return number.ToString(Math.Abs(number - Math.Round(number)) < 0.0001 ? "0" : "0.###", CultureInfo.InvariantCulture);
        }

        return null;
    }

    private static string? NormalizeEvidenceDisplayText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim().Replace('_', ' ');
        normalized = Regex.Replace(normalized, @"\s+", " ", RegexOptions.CultureInvariant);
        return normalized.Length <= 80 ? normalized : normalized[..77] + "...";
    }

    private static double? TryGetDouble(JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (!value.TryGetProperty(name, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number))
                return number;
            if (property.ValueKind == JsonValueKind.String
                && double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            {
                return number;
            }
        }

        return null;
    }

    private static void AddDiagnosticMetadataParts(
        List<string> parts,
        SourceExtractionDiagnosticSummary? diagnostics,
        string? uiLanguage)
    {
        if (diagnostics is null)
            return;

        if (!string.IsNullOrWhiteSpace(diagnostics.OcrFailureReason))
        {
            parts.Add($"{SourceCardLabel("ocr_failure", uiLanguage)} {Shorten(LocalizedOcrReason(diagnostics.OcrFailureReason, uiLanguage), 36)}");
        }
        else if (diagnostics.OcrTimedOut == true)
        {
            parts.Add($"{SourceCardLabel("ocr_failure", uiLanguage)} {Shorten(LocalizedOcrReason("timeout", uiLanguage), 36)}");
        }

        if (!string.IsNullOrWhiteSpace(diagnostics.OcrAppliedReason))
            parts.Add($"{SourceCardLabel("ocr_reason", uiLanguage)} {Shorten(LocalizedOcrReason(diagnostics.OcrAppliedReason, uiLanguage), 36)}");

        if (diagnostics.NativeOcrRecommended == true)
            parts.Add(SourceCardLabel("native_ocr_recommended", uiLanguage));

        if (!string.IsNullOrWhiteSpace(diagnostics.OcrMode))
            parts.Add($"{SourceCardLabel("ocr_mode", uiLanguage)} {Shorten(LocalizedOcrMode(diagnostics.OcrMode, uiLanguage), 42)}");

        if (!string.IsNullOrWhiteSpace(diagnostics.OcrLanguages))
            parts.Add($"{SourceCardLabel("ocr_languages", uiLanguage)} {Shorten(diagnostics.OcrLanguages!, 32)}");

        if (diagnostics.OcrDurationMs is > 0)
            parts.Add($"{SourceCardLabel("ocr_duration", uiLanguage)} {FormatDuration(diagnostics.OcrDurationMs.Value)}");

        if (diagnostics.OcrAttemptedPageCount is > 0)
        {
            var value = diagnostics.OcrSkippedPageCount is > 0
                ? $"{diagnostics.OcrAttemptedPageCount.Value}+{diagnostics.OcrSkippedPageCount.Value}"
                : diagnostics.OcrAttemptedPageCount.Value.ToString(CultureInfo.InvariantCulture);
            parts.Add($"{SourceCardLabel("ocr_pages", uiLanguage)} {value}");
        }

        if (diagnostics.OcrPagesWithNovelTextCount is > 0)
            parts.Add($"{SourceCardLabel("ocr_novel_pages", uiLanguage)} {diagnostics.OcrPagesWithNovelTextCount.Value.ToString(CultureInfo.InvariantCulture)}");

        if (!string.IsNullOrWhiteSpace(diagnostics.NativeTextStatus)
            && !string.Equals(NormalizeBackendIdentifier(diagnostics.NativeTextStatus), "ok", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"{SourceCardLabel("native_text", uiLanguage)} {Shorten(LocalizedQualityStatus(diagnostics.NativeTextStatus, uiLanguage), 36)}");
        }

        if (diagnostics.PageCount is > 0)
            parts.Add($"{SourceCardLabel("document_pages", uiLanguage)} {diagnostics.PageCount.Value.ToString(CultureInfo.InvariantCulture)}");

        if (diagnostics.TextPageCount is > 0)
            parts.Add($"{SourceCardLabel("text_pages", uiLanguage)} {diagnostics.TextPageCount.Value.ToString(CultureInfo.InvariantCulture)}");

        if (diagnostics.EmptyPageCount is > 0)
            parts.Add($"{SourceCardLabel("empty_pages", uiLanguage)} {diagnostics.EmptyPageCount.Value.ToString(CultureInfo.InvariantCulture)}");

        if (diagnostics.SparsePageCount is > 0)
            parts.Add($"{SourceCardLabel("sparse_pages", uiLanguage)} {diagnostics.SparsePageCount.Value.ToString(CultureInfo.InvariantCulture)}");

        if (diagnostics.PageReviewRecommendedCount is > 0)
            parts.Add($"{SourceCardLabel("page_review_count", uiLanguage)} {diagnostics.PageReviewRecommendedCount.Value.ToString(CultureInfo.InvariantCulture)}");

        if (diagnostics.PageWarningCount is > 0)
            parts.Add($"{SourceCardLabel("page_warning_count", uiLanguage)} {diagnostics.PageWarningCount.Value.ToString(CultureInfo.InvariantCulture)}");

        if (diagnostics.ImagePageCount is > 0)
            parts.Add($"{SourceCardLabel("image_pages", uiLanguage)} {diagnostics.ImagePageCount.Value.ToString(CultureInfo.InvariantCulture)}");
    }

    private static int? GetSelectionScore(SourceCard source)
    {
        var scores = new[]
        {
            source.SelectionHintActionabilityScore,
            source.SelectionHintSupportScore,
            source.SelectionHintFragmentScore,
            source.SelectionHintNavigationScore
        }.Where(static score => score.HasValue)
            .Select(static score => score!.Value)
            .ToArray();

        if (scores.Length == 0)
            return null;

        var penalty = Math.Max(0, source.SelectionHintQualityPenalty ?? 0);
        return Math.Max(0, scores.Max() - penalty);
    }

    private static string BuildQualityPart(string label, string quality, double? confidence, string? uiLanguage)
    {
        var qualityText = LocalizedQualityStatus(quality, uiLanguage);
        var confidenceText = confidence is null
            ? string.Empty
            : $" {confidence.Value.ToString("0%", CultureInfo.InvariantCulture)}";
        return $"{label} {Shorten(qualityText, 42)}{confidenceText}";
    }

    private static double? GetConfidenceForQuality(SourceCard source, string quality)
    {
        var normalized = NormalizeBackendIdentifier(quality);

        if (!string.IsNullOrWhiteSpace(normalized)
            && string.Equals(normalized, NormalizeBackendIdentifier(source.PageQualityStatus), StringComparison.OrdinalIgnoreCase)
            && source.PageExtractionConfidence is not null)
        {
            return source.PageExtractionConfidence;
        }

        if (!string.IsNullOrWhiteSpace(normalized)
            && string.Equals(normalized, NormalizeBackendIdentifier(source.DocumentQualityStatus), StringComparison.OrdinalIgnoreCase)
            && source.DocumentExtractionConfidence is not null)
        {
            return source.DocumentExtractionConfidence;
        }

        return source.ExtractionConfidence ?? source.PageExtractionConfidence ?? source.DocumentExtractionConfidence;
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string SourceCardLabel(string key, string? uiLanguage)
        => LocalizedStrings.SourceCardLabel(key, uiLanguage);

    private static string LocalizedQualityStatus(string status, string? uiLanguage)
        => LocalizedStrings.LocalizedSourceQualityStatus(NormalizeBackendIdentifier(status), uiLanguage);

    private static string LocalizedSelectionHintRole(string? role, string? uiLanguage)
        => LocalizedStrings.LocalizedSourceSelectionHintRole(NormalizeBackendIdentifier(role), uiLanguage);

    private static string LocalizedContentRole(string? role, string? uiLanguage)
    {
        var normalized = NormalizeBackendIdentifier(role);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        return normalized switch
        {
            "content" => SourceCardLabel("content_role.content", uiLanguage),
            "navigation" => SourceCardLabel("content_role.navigation", uiLanguage),
            "mixed_navigation_content" => SourceCardLabel("content_role.mixed_navigation_content", uiLanguage),
            _ => FormatBackendReason(normalized)
        };
    }

    private static string FormatBackendReason(string? reason)
    {
        var normalized = NormalizeBackendIdentifier(reason);
        return string.IsNullOrWhiteSpace(normalized)
            ? string.Empty
            : normalized.Replace('_', ' ');
    }

    private static string LocalizedOcrReason(string? reason, string? uiLanguage)
        => LocalizedStrings.LocalizedSourceOcrReason(NormalizeBackendIdentifier(reason), uiLanguage);

    private static string LocalizedOcrMode(string? mode, string? uiLanguage)
        => LocalizedStrings.LocalizedSourceOcrMode(NormalizeBackendIdentifier(mode), uiLanguage);

    private static string FormatDuration(long milliseconds)
    {
        if (milliseconds < 1000)
            return $"{milliseconds.ToString(CultureInfo.InvariantCulture)}ms";

        var seconds = milliseconds / 1000d;
        return $"{seconds.ToString(seconds < 10 ? "0.#" : "0", CultureInfo.InvariantCulture)}s";
    }

    private static string NormalizeBackendIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Trim().Replace('-', '_').Replace(' ', '_');
        normalized = Regex.Replace(normalized, "([A-Z]+)([A-Z][a-z])", "$1_$2", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, "([a-z0-9])([A-Z])", "$1_$2", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, "_+", "_", RegexOptions.CultureInvariant);
        return normalized.Trim('_').ToLowerInvariant();
    }

    private static string ShortSourceHash(string? sourceHash)
    {
        if (string.IsNullOrWhiteSpace(sourceHash))
            return string.Empty;

        var trimmed = sourceHash.Trim();
        return trimmed.Length <= 10 ? trimmed : trimmed[..10];
    }

    private static string Shorten(string value, int maxLength)
    {
        value = value.Trim();
        if (value.Length <= maxLength)
            return value;

        return value[..Math.Max(0, maxLength - 3)] + "...";
    }

    private static string ST(string fr, string en, string es, string pt, string de, string it, string? uiLanguage = null)
    {
        var lang = ClientUiText.NormalizeLanguage(uiLanguage ?? AppSettings.Load().UiLanguage);
        return lang switch
        {
            "en" => en,
            "es" => es,
            "pt" => pt,
            "de" => de,
            "it" => it,
            _ => fr
        };
    }
}
