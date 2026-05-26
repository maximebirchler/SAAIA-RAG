using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class ProductRuntimeDomainNeutralityTests
{
    private static readonly string[] RuntimeRoots =
    [
        Path.Combine("backend", "SAAIA.Backend"),
        Path.Combine("client", "SAAIA.Client.WinUI"),
        Path.Combine("contracts", "SAAIA.Contracts")
    ];

    private static readonly string[] SourceExtensions =
    [
        ".cs",
        ".xaml",
        ".json",
        ".ps1"
    ];

    private static readonly string[] ForbiddenCorpusTokens =
    [
        "cuisine",
        "cat_cuisine",
        "ingredient",
        "ingredients",
        "ingr[e",
        "recette",
        "recipe",
        "rece\"",
        "reci\"",
        "JeCuisine",
        "Top30",
        "Nobilia",
        "Facilitemps",
        "Chefbot",
        "Moulinex",
        "Cemea",
        "30-recettes-preferees",
        "nobilia-recettes",
        "si-on-cuisinait",
        "livre-recette-sist",
        "facilitemps.pdf",
        "chefbot_livre",
        "Tag249008277_1_MOULINEX",
        "cuisson",
        "cooking",
        "assaisonnez",
        "battez",
        "enfournez",
        "portez",
        "prechauffez",
        "préchauffez",
        "epluchez",
        "épluchez",
        "poivrez",
        "saupoudrez",
        "transvasez",
        "cuill",
        "oeufs",
        "œufs",
        "servings",
        "lunchs",
        "meals"
    ];

    [Fact]
    public void Product_runtime_does_not_embed_cuisine_fixture_or_document_specific_terms()
    {
        var repoRoot = FindRepoRoot();
        var violations = new List<string>();

        foreach (var runtimeRoot in RuntimeRoots)
        {
            var root = Path.Combine(repoRoot, runtimeRoot);
            Assert.True(Directory.Exists(root), $"Runtime root not found: {root}");

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (!SourceExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    continue;

                var text = File.ReadAllText(file);
                foreach (var token in ForbiddenCorpusTokens)
                {
                    if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
                    {
                        var relative = Path.GetRelativePath(repoRoot, file);
                        violations.Add($"{relative}: forbidden corpus token '{token}'");
                    }
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Product runtime must stay domain-neutral. Corpus/category-specific helpers belong in tests, tools, or fixtures only." +
            Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RAG.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test output directory.");
    }
}
