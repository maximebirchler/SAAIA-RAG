using Xunit;

public class IngestionCategoryResolverTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_returns_empty_for_blank_values_without_general_fallback(string? category)
    {
        Assert.Equal(string.Empty, IngestionCategoryResolver.Normalize(category));
    }

    [Fact]
    public void Normalize_trims_and_lowercases_explicit_category()
    {
        Assert.Equal("atex", IngestionCategoryResolver.Normalize("  ATEX  "));
    }

    [Fact]
    public void Derive_uses_first_folder_for_nested_pdf()
    {
        var options = new IngestionOptions
        {
            DefaultCategory = "",
            CategoryFromFirstFolder = true
        };

        var category = IngestionCategoryResolver.Derive("Cuisine/Sauces/Guide.pdf", options);

        Assert.Equal("cuisine", category);
    }

    [Fact]
    public void DeriveFromDocumentPath_uses_first_folder_without_configuration()
    {
        var category = IngestionCategoryResolver.DeriveFromDocumentPath("ATEX/CEN/TR 15281.pdf");

        Assert.Equal("atex", category);
    }

    [Fact]
    public void Derive_returns_empty_for_root_pdf_when_default_category_is_empty()
    {
        var options = new IngestionOptions
        {
            DefaultCategory = "",
            CategoryFromFirstFolder = true
        };

        var category = IngestionCategoryResolver.Derive("Root.pdf", options);

        Assert.Equal(string.Empty, category);
    }

    [Fact]
    public void Derive_uses_configured_default_when_first_folder_derivation_is_disabled()
    {
        var options = new IngestionOptions
        {
            DefaultCategory = "Operations",
            CategoryFromFirstFolder = false
        };

        var category = IngestionCategoryResolver.Derive("Cuisine/Sauces/Guide.pdf", options);

        Assert.Equal("operations", category);
    }

    [Fact]
    public void Derive_keeps_root_pdf_uncategorized_even_when_default_category_is_available()
    {
        var options = new IngestionOptions
        {
            DefaultCategory = "Inbox",
            CategoryFromFirstFolder = true
        };

        var category = IngestionCategoryResolver.Derive("Root.pdf", options);

        Assert.Equal(string.Empty, category);
    }
}
