using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static string BuildSummaryRetrievalQuery(
        ResolvedDocRef doc,
        string strategy,
        string language,
        string level,
        ToolMemory.SourceRef? sourceMetadata = null)
    {
        var primaryLanguage = NormalizeDocumentLanguageTag(language).Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        var core = primaryLanguage switch
        {
            "fr" => strategy switch
            {
                "about" => "vue d ensemble objectif sujets principaux portee contexte",
                "store" => "resume objectif sections principales faits importants exemples valeurs contraintes details utiles",
                _ => "resume objectif sections principales sujets faits importants contraintes"
            },
            "es" => strategy switch
            {
                "about" => "vision general objetivo temas principales alcance contexto",
                "store" => "resumen objetivo secciones principales hechos importantes ejemplos valores restricciones detalles utiles",
                _ => "resumen objetivo secciones principales temas hechos importantes restricciones"
            },
            "pt" => strategy switch
            {
                "about" => "visao geral objetivo temas principais ambito contexto",
                "store" => "resumo objetivo secoes principais fatos importantes exemplos valores restricoes detalhes uteis",
                _ => "resumo objetivo secoes principais temas fatos importantes restricoes"
            },
            "de" => strategy switch
            {
                "about" => "uberblick zweck hauptthemen umfang kontext",
                "store" => "zusammenfassung zweck hauptabschnitte wichtige fakten beispiele werte einschrankungen nutzbare details",
                _ => "zusammenfassung zweck hauptabschnitte themen wichtige fakten einschrankungen"
            },
            "it" => strategy switch
            {
                "about" => "panoramica scopo temi principali ambito contesto",
                "store" => "riassunto scopo sezioni principali fatti importanti esempi valori vincoli dettagli utili",
                _ => "riassunto scopo sezioni principali temi fatti importanti vincoli"
            },
            "en" => strategy switch
            {
                "about" => "overview purpose main topics scope context",
                "store" => "summary purpose main sections important facts examples values constraints useful details",
                _ => "summary purpose main sections topics important facts constraints"
            },
            "nl" => strategy switch
            {
                "about" => "overzicht doel hoofdonderwerpen bereik context",
                "store" => "samenvatting doel hoofdsecties belangrijke feiten voorbeelden waarden beperkingen nuttige details",
                _ => "samenvatting doel hoofdsecties onderwerpen belangrijke feiten beperkingen"
            },
            "pl" => strategy switch
            {
                "about" => "przeglad cel glowne tematy zakres kontekst",
                "store" => "streszczenie cel glowne sekcje wazne fakty przyklady wartosci ograniczenia przydatne szczegoly",
                _ => "streszczenie cel glowne sekcje tematy wazne fakty ograniczenia"
            },
            "sv" => strategy switch
            {
                "about" => "oversikt syfte huvudamnen omfattning kontext",
                "store" => "sammanfattning syfte huvudavsnitt viktiga fakta exempel varden begransningar anvandbara detaljer",
                _ => "sammanfattning syfte huvudavsnitt amnen viktiga fakta begransningar"
            },
            "da" => strategy switch
            {
                "about" => "overblik formal hovedemner omfang kontekst",
                "store" => "resume formal hovedafsnit vigtige fakta eksempler vaerdier begraensninger nyttige detaljer",
                _ => "resume formal hovedafsnit emner vigtige fakta begraensninger"
            },
            "no" or "nb" or "nn" => strategy switch
            {
                "about" => "oversikt formal hovedtema omfang kontekst",
                "store" => "sammendrag formal hovedseksjoner viktige fakta eksempler verdier begrensninger nyttige detaljer",
                _ => "sammendrag formal hovedseksjoner emner viktige fakta begrensninger"
            },
            "fi" => strategy switch
            {
                "about" => "yleiskuva tarkoitus paaaiheet laajuus konteksti",
                "store" => "yhteenveto tarkoitus paaosiot tarkeat faktat esimerkit arvot rajoitukset hyodylliset tiedot",
                _ => "yhteenveto tarkoitus paaosiot aiheet tarkeat faktat rajoitukset"
            },
            "cs" => strategy switch
            {
                "about" => "prehled ucel hlavni temata rozsah kontext",
                "store" => "shrnuti ucel hlavni casti dulezita fakta priklady hodnoty omezeni uzitecne detaily",
                _ => "shrnuti ucel hlavni casti temata dulezita fakta omezeni"
            },
            "sk" => strategy switch
            {
                "about" => "prehlad ucel hlavne temy rozsah kontext",
                "store" => "zhrnutie ucel hlavne casti dolezite fakty priklady hodnoty obmedzenia uzitocne detaily",
                _ => "zhrnutie ucel hlavne casti temy dolezite fakty obmedzenia"
            },
            "sl" => strategy switch
            {
                "about" => "pregled namen glavne teme obseg kontekst",
                "store" => "povzetek namen glavni odseki pomembna dejstva primeri vrednosti omejitve uporabne podrobnosti",
                _ => "povzetek namen glavni odseki teme pomembna dejstva omejitve"
            },
            "hr" => strategy switch
            {
                "about" => "pregled svrha glavne teme opseg kontekst",
                "store" => "sazetak svrha glavni odjeljci vazne cinjenice primjeri vrijednosti ogranicenja korisni detalji",
                _ => "sazetak svrha glavni odjeljci teme vazne cinjenice ogranicenja"
            },
            "ro" => strategy switch
            {
                "about" => "prezentare scop subiecte principale domeniu context",
                "store" => "rezumat scop sectiuni principale fapte importante exemple valori constrangeri detalii utile",
                _ => "rezumat scop sectiuni principale subiecte fapte importante constrangeri"
            },
            "hu" => strategy switch
            {
                "about" => "attekintes cel fo temak terjedelem kontextus",
                "store" => "osszefoglalas cel fo szakaszok fontos tenyek peldak ertekek korlatozasok hasznos reszletek",
                _ => "osszefoglalas cel fo szakaszok temak fontos tenyek korlatozasok"
            },
            "tr" => strategy switch
            {
                "about" => "genel bakis amac ana konular kapsam baglam",
                "store" => "ozet amac ana bolumler onemli olgular ornekler degerler kisitlar faydali ayrintilar",
                _ => "ozet amac ana bolumler konular onemli olgular kisitlar"
            },
            "id" => strategy switch
            {
                "about" => "gambaran umum tujuan topik utama cakupan konteks",
                "store" => "ringkasan tujuan bagian utama fakta penting contoh nilai batasan detail berguna",
                _ => "ringkasan tujuan bagian utama topik fakta penting batasan"
            },
            "vi" => strategy switch
            {
                "about" => "tong quan muc dich chu de chinh pham vi boi canh",
                "store" => "tom tat muc dich phan chinh su kien quan trong vi du gia tri rang buoc chi tiet huu ich",
                _ => "tom tat muc dich phan chinh chu de su kien quan trong rang buoc"
            },
            _ => string.Empty
        };

        var levelCue = BuildSummaryRetrievalLevelCue(primaryLanguage, level);

        var metadataTerms = BuildLiveSummaryRetrievalMetadataTerms(doc, sourceMetadata);
        var fallbackTerms = string.IsNullOrWhiteSpace(core) && string.IsNullOrWhiteSpace(levelCue)
            ? BuildLiveSummaryFallbackRetrievalTerms(doc, sourceMetadata)
            : string.Empty;
        return string.Join(' ', new[] { doc.DocName, metadataTerms, core, levelCue, fallbackTerms }
            .Where(static value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string BuildSummaryRetrievalLevelCue(string primaryLanguage, string level)
        => primaryLanguage switch
        {
            "fr" => level switch
            {
                "short" => "court concis",
                "long" => "detaille complet",
                _ => "moyen utile"
            },
            "es" => level switch
            {
                "short" => "breve conciso",
                "long" => "detallado completo",
                _ => "medio util"
            },
            "pt" => level switch
            {
                "short" => "curto conciso",
                "long" => "detalhado completo",
                _ => "medio util"
            },
            "de" => level switch
            {
                "short" => "kurz knapp",
                "long" => "detailliert vollstaendig",
                _ => "mittel nuetzlich"
            },
            "it" => level switch
            {
                "short" => "breve conciso",
                "long" => "dettagliato completo",
                _ => "medio utile"
            },
            "nl" => level switch
            {
                "short" => "kort bondig",
                "long" => "gedetailleerd volledig",
                _ => "gemiddeld nuttig"
            },
            "pl" => level switch
            {
                "short" => "krotkie zwiezle",
                "long" => "szczegolowe pelne",
                _ => "srednie przydatne"
            },
            "sv" => level switch
            {
                "short" => "kort koncis",
                "long" => "detaljerad fullstandig",
                _ => "medel anvandbar"
            },
            "da" => level switch
            {
                "short" => "kort praecis",
                "long" => "detaljeret fuldstaendig",
                _ => "middel nyttig"
            },
            "no" or "nb" or "nn" => level switch
            {
                "short" => "kort konsis",
                "long" => "detaljert fullstendig",
                _ => "middels nyttig"
            },
            "fi" => level switch
            {
                "short" => "lyhyt tiivis",
                "long" => "yksityiskohtainen kattava",
                _ => "keskitaso hyodyllinen"
            },
            "cs" => level switch
            {
                "short" => "kratke strucne",
                "long" => "podrobne uplne",
                _ => "stredni uzitecne"
            },
            "sk" => level switch
            {
                "short" => "kratke strucne",
                "long" => "podrobne uplne",
                _ => "stredne uzitocne"
            },
            "sl" => level switch
            {
                "short" => "kratko jedrnato",
                "long" => "podrobno popolno",
                _ => "srednje uporabno"
            },
            "hr" => level switch
            {
                "short" => "kratko sazeto",
                "long" => "detaljno potpuno",
                _ => "srednje korisno"
            },
            "ro" => level switch
            {
                "short" => "scurt concis",
                "long" => "detaliat complet",
                _ => "mediu util"
            },
            "hu" => level switch
            {
                "short" => "rovid tomor",
                "long" => "reszletes teljes",
                _ => "kozepes hasznos"
            },
            "tr" => level switch
            {
                "short" => "kisa oz",
                "long" => "ayrintili tam",
                _ => "orta yararli"
            },
            "id" => level switch
            {
                "short" => "pendek ringkas",
                "long" => "rinci lengkap",
                _ => "sedang berguna"
            },
            "vi" => level switch
            {
                "short" => "ngan gon",
                "long" => "chi tiet day du",
                _ => "vua huu ich"
            },
            "en" => level switch
            {
                "short" => "short concise",
                "long" => "detailed complete",
                _ => "medium useful"
            },
            _ => string.Empty
        };

    private static string BuildLiveSummaryRetrievalMetadataTerms(ResolvedDocRef doc, ToolMemory.SourceRef? sourceMetadata)
    {
        var terms = new List<string>();
        if (!string.IsNullOrWhiteSpace(doc.CategoryPath))
            terms.Add(doc.CategoryPath!);
        if (!string.IsNullOrWhiteSpace(doc.CategoryRef))
            terms.Add(doc.CategoryRef!);
        if (!string.IsNullOrWhiteSpace(doc.Category))
            terms.Add(doc.Category!);
        if (!string.IsNullOrWhiteSpace(sourceMetadata?.CategoryRef))
            terms.Add(sourceMetadata!.CategoryRef!);
        if (!string.IsNullOrWhiteSpace(sourceMetadata?.CategoryPath))
            terms.Add(sourceMetadata!.CategoryPath!);

        if (sourceMetadata?.ProfileSignals is { } profileSignals)
        {
            terms.AddRange(profileSignals.Keywords);
            terms.AddRange(profileSignals.Entities);
            terms.AddRange(profileSignals.Topics);
            terms.AddRange(profileSignals.HypotheticalQuestions);
            terms.AddRange(profileSignals.Limits);
            terms.AddRange(profileSignals.MatchedTerms);
        }

        foreach (var card in sourceMetadata?.MatchedContentCards ?? [])
        {
            if (!string.IsNullOrWhiteSpace(card.Title))
                terms.Add(card.Title.Trim());
            terms.AddRange(ExtractLiveSummaryEvidenceRetrievalTerms(card.Evidence));
            foreach (var signal in card.Signals ?? [])
            {
                if (!string.IsNullOrWhiteSpace(signal))
                    terms.Add(signal.Trim());
            }
        }

        return JoinLiveSummaryRetrievalTerms(terms, 48);
    }

    private static string BuildLiveSummaryFallbackRetrievalTerms(ResolvedDocRef doc, ToolMemory.SourceRef? sourceMetadata)
    {
        var terms = new List<string?>
        {
            doc.DocName,
            sourceMetadata?.DocName,
            sourceMetadata?.Label
        };
        terms.AddRange(ExtractLiveSummaryPathRetrievalTerms(doc.DocPath));
        terms.AddRange(ExtractLiveSummaryPathRetrievalTerms(sourceMetadata?.DocPath));
        return JoinLiveSummaryRetrievalTerms(terms, 16);
    }

    private static IEnumerable<string> ExtractLiveSummaryPathRetrievalTerms(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            yield break;

        var normalizedPath = path.Replace('\\', '/');
        var fileName = Path.GetFileNameWithoutExtension(normalizedPath);
        if (!string.IsNullOrWhiteSpace(fileName))
            yield return fileName;
    }

    private static IEnumerable<string> ExtractLiveSummaryEvidenceRetrievalTerms(JsonElement? evidence)
    {
        if (!evidence.HasValue || evidence.Value.ValueKind != JsonValueKind.Object)
            yield break;

        var root = evidence.Value;
        var sourceText = TryGetString(root, "sourceText") ?? TryGetString(root, "source_text") ?? TryGetString(root, "SourceText");
        if (!string.IsNullOrWhiteSpace(sourceText))
            yield return sourceText.Trim();

        var scaleBasis = TryGetObject(root, "scaleBasis") ?? TryGetObject(root, "scale_basis") ?? TryGetObject(root, "ScaleBasis");
        if (scaleBasis.HasValue)
        {
            var count = TryGetDouble(scaleBasis.Value, "count") ?? TryGetDouble(scaleBasis.Value, "value") ?? TryGetDouble(scaleBasis.Value, "Count") ?? TryGetDouble(scaleBasis.Value, "Value");
            var label = TryGetString(scaleBasis.Value, "label") ?? TryGetString(scaleBasis.Value, "Label");
            var basisSourceText = TryGetString(scaleBasis.Value, "sourceText") ?? TryGetString(scaleBasis.Value, "source_text") ?? TryGetString(scaleBasis.Value, "SourceText");
            var basisText = BuildLiveSummaryFactRetrievalTerm(count, null, label, basisSourceText);
            if (!string.IsNullOrWhiteSpace(basisText))
                yield return basisText;
        }

        var quantityFacts = TryGetArray(root, "quantityFacts") ?? TryGetArray(root, "quantity_facts") ?? TryGetArray(root, "QuantityFacts");
        if (quantityFacts.HasValue)
        {
            foreach (var term in ExtractLiveSummaryEvidenceFactArrayTerms(quantityFacts.Value, includeKind: false).Take(8))
                yield return term;
        }

        var facts = TryGetArray(root, "facts") ?? TryGetArray(root, "Facts");
        if (facts.HasValue)
        {
            foreach (var term in ExtractLiveSummaryEvidenceFactArrayTerms(facts.Value, includeKind: true).Take(8))
                yield return term;
        }

        var reasons = TryGetArray(root, "nonScalableReasons") ?? TryGetArray(root, "non_scalable_reasons") ?? TryGetArray(root, "NonScalableReasons");
        if (reasons.HasValue)
        {
            foreach (var reason in reasons.Value.EnumerateArray())
            {
                var compact = CompactLiveSummaryEvidenceValue(reason);
                if (!string.IsNullOrWhiteSpace(compact))
                    yield return compact;
            }
        }
    }

    private static IEnumerable<string> ExtractLiveSummaryEvidenceFactArrayTerms(JsonElement facts, bool includeKind)
    {
        if (facts.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var fact in facts.EnumerateArray())
        {
            if (fact.ValueKind == JsonValueKind.String)
            {
                var text = fact.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    yield return text.Trim();
                continue;
            }

            if (fact.ValueKind != JsonValueKind.Object)
                continue;

            var value = TryGetDouble(fact, "value") ?? TryGetDouble(fact, "Value");
            var valueText = value.HasValue
                ? value.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                : TryGetString(fact, "value") ?? TryGetString(fact, "Value");
            var unit = TryGetString(fact, "unit") ?? TryGetString(fact, "Unit");
            var label = TryGetString(fact, "label") ?? TryGetString(fact, "Label");
            var sourceText = TryGetString(fact, "sourceText") ?? TryGetString(fact, "source_text") ?? TryGetString(fact, "SourceText");
            var kind = includeKind ? TryGetString(fact, "kind") ?? TryGetString(fact, "Kind") : null;
            var term = BuildLiveSummaryFactRetrievalTerm(valueText, unit, label, sourceText, kind);
            if (!string.IsNullOrWhiteSpace(term))
                yield return term;
        }
    }

    private static string? BuildLiveSummaryFactRetrievalTerm(double? value, string? unit, string? label, string? sourceText)
        => BuildLiveSummaryFactRetrievalTerm(
            value?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            unit,
            label,
            sourceText);

    private static string? BuildLiveSummaryFactRetrievalTerm(string? value, string? unit, string? label, string? sourceText, string? kind = null)
        => string.Join(" ", new[]
        {
            kind?.Trim(),
            value?.Trim(),
            unit?.Trim(),
            label?.Trim(),
            sourceText?.Trim()
        }.Where(static item => !string.IsNullOrWhiteSpace(item)));

    private static string JoinLiveSummaryRetrievalTerms(IEnumerable<string?> terms, int maxTerms)
        => string.Join(' ', terms
            .Select(NormalizeLiveSummaryRetrievalTerm)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxTerms));

    private static string? NormalizeLiveSummaryRetrievalTerm(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = Regex.Replace(value.Trim(), @"\s+", " ", RegexOptions.CultureInvariant);
        return normalized.Length <= 120 ? normalized : normalized[..117] + "...";
    }
}
