using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class DocumentLanguageResolverTests
{
    [Theory]
    [InlineData("FR_ch", "fr-ch")]
    [InlineData("nl-BE,fr;q=0.8", "nl-be")]
    [InlineData("pt_BR+eng", "pt-br")]
    [InlineData("und", "und")]
    public void NormalizeLanguageTag_accepts_bcp47_like_document_tags(string input, string expected)
    {
        Assert.Equal(expected, DocumentLanguageResolver.NormalizeLanguageTag(input));
    }

    [Theory]
    [InlineData("De handleiding beschrijft onderhoud en veiligheidscontroles voor de installatie.", "nl")]
    [InlineData("يشرح هذا المستند اجراءات السلامة والصيانة للمعدات.", "ar")]
    [InlineData("本文件介绍安全联锁状态和维护要求。", "zh")]
    [InlineData("Документ описывает требования безопасности и техническое обслуживание.", "ru")]
    [InlineData("Документ описує вимоги безпеки та технічне обслуговування.", "uk")]
    [InlineData("Bu belge guvenlik gereksinimleri ve bakim adimlarini aciklar.", "tr")]
    public void DetectDominantLanguage_handles_non_ui_languages(string text, string expected)
    {
        Assert.Equal(expected, DocumentLanguageResolver.DetectDominantLanguage(text));
    }

    [Theory]
    [InlineData("The team meets this morning and shares notes with everyone before they leave.", "en")]
    [InlineData("Les personnes arrivent avec leurs notes et vous parlent dans cette salle.", "fr")]
    [InlineData("Las personas llegan con sus notas para esta reunion y hablan entre ellas.", "es")]
    [InlineData("De mensen komen met hun notities en deze groep spreekt niet te snel.", "nl")]
    public void DetectDominantLanguage_uses_function_words_not_document_domain_terms(string text, string expected)
    {
        Assert.Equal(expected, DocumentLanguageResolver.DetectDominantLanguage(text));
    }
}
