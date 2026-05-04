using System.Linq;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class DocumentProfileProjectorTests
{
    [Fact]
    public void Project_builds_generic_profile_from_document_structure()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(1, "Inerting safety controls require oxygen monitoring and nitrogen purge validation.", 10, 78, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Inerting Safety", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "Inerting safety controls require oxygen monitoring and nitrogen purge validation.", 78, 10, [2])
        };
        var exact = new[]
        {
            new ExtractedExactMatchEntry(0, 0, 0, 1, 1, "EN 15281", "en 15281", 7, 2, [3], "standard_ref")
        };

        var profile = DocumentProfileProjector.Project("ATEX/CEN TR 15281.pdf", pages, sections, units, exact);

        Assert.Equal("deterministic_v1", profile.ProfileVersion);
        Assert.Contains("CEN TR 15281.pdf", profile.SummaryText, StringComparison.Ordinal);
        Assert.Contains("inerting", profile.Keywords);
        Assert.Contains("EN 15281", profile.Entities);
        Assert.Contains("Inerting Safety", profile.Topics);
        Assert.Contains(profile.HypotheticalQuestions, q => q.Contains("inerting", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("EN 15281", profile.SearchText, StringComparison.Ordinal);
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "Inerting Safety", StringComparison.Ordinal));
        Assert.True(profile.TokenCount > 0);
        Assert.Equal(32, profile.Checksum.Length);
    }

    [Fact]
    public void Project_builds_generic_content_cards_from_titles_without_category_hardcoding()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "LOCKOUT TAGOUT PROCEDURE\nIsolate energy before maintenance.\nSAUCE BEARNAISE\n20 min. Ingredients: butter, egg yolks.",
                16,
                112,
                [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Operational Playbook", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "LOCKOUT TAGOUT PROCEDURE\nIsolate energy before maintenance.", 58, 7, [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "SAUCE BEARNAISE\n20 min. Ingredients: butter, egg yolks.", 54, 8, [3])
        };

        var profile = DocumentProfileProjector.Project(
            "Mixed/Playbook.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "Operational Playbook", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "LOCKOUT TAGOUT PROCEDURE", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "SAUCE BEARNAISE", StringComparison.Ordinal));
        Assert.Contains("LOCKOUT TAGOUT PROCEDURE", profile.SearchText, StringComparison.Ordinal);
        Assert.Contains("SAUCE BEARNAISE", profile.SearchText, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_extracts_embedded_uppercase_title_when_followed_by_measurement()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "Ajoutez 15 cl d'eau puis lancez le robot pour 12 min. Servez avec des steaks.6 personnes12 min5 minSAUCE AU POIVRE50 g de parmesan Sel Poivre.",
                24,
                146,
                [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                1,
                1,
                "Ajoutez 15 cl d'eau puis lancez le robot pour 12 min. Servez avec des steaks.6 personnes12 min5 minSAUCE AU POIVRE50 g de parmesan Sel Poivre.",
                146,
                24,
                [2])
        };

        var profile = DocumentProfileProjector.Project(
            "Mixed/Robot.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "SAUCE AU POIVRE", StringComparison.Ordinal));
        Assert.Contains("SAUCE AU POIVRE", profile.SearchText, StringComparison.Ordinal);
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "SAUCE AU POIVRE50", StringComparison.Ordinal));
    }

    [Fact]
    public void Project_discards_layout_noise_from_content_card_titles()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "INGRÉDIENTSPRÉPARATION :1 gros oignon d'env. 150 g\nRumsteck aux oignons grillés [Index: ] OCR_CODE\nPour 4 personnesUne soupe simple\nCouv-etudiants.indd Toutes les pages",
                24,
                160,
                [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Recipe Book", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "INGRÉDIENTSPRÉPARATION :1 gros oignon d'env. 150 g", 52, 6, [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "Rumsteck aux oignons grillés [Index: ] OCR_CODE", 45, 4, [3]),
            new ExtractedDocumentUnit(2, 0, 1, 1, "Pour 4 personnesUne soupe simple", 33, 5, [4]),
            new ExtractedDocumentUnit(3, 0, 1, 1, "Couv-etudiants.indd Toutes les pages", 37, 4, [5])
        };

        var profile = DocumentProfileProjector.Project(
            "Cuisine/Recipes.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "Rumsteck aux oignons grillés", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.Contains("[Index", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("INGRÉDIENTS", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Pour 4 personnes", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.Contains(".indd", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Project_discards_instruction_sentence_and_repeated_header_cards_without_category_rules()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "BE A MASTER. BECOME A CHEFPROTÉINESLÉGUMESHYDRATES DE CARBONEPRODUITS LAITIERS\n" +
                "Ouvrir le robot et retirer le panier vapeur avec précaution.\n" +
                "Programmer 10 secondes, vitesse 6 et fermer.\n" +
                "Amuse-bouches et apéritifsSaladesAccompagnements\n" +
                "CRÈME DE PETIT POIS ET D’AVOCATLa texture de l’avocat en fait un excellent ingrédient.\n" +
                "Basilic ou herbes aromatiques, à votre goût3 petites courgettes (environ 500 g)Préparation\n" +
                "(Recommencer cette opération si nécessaire jusqu’à ce que tous les ingrédients soient bien mélangés)\n" +
                "est idéal\n" +
                "Avec des gants et\n" +
                "Ensuite, programmer 5 secondes à vitesse 9\n" +
                "Savoureuse et très polyvalente, outre les pâtes, vous pourrez l’utiliser\n" +
                "fonctionne parfaitement aux vitesses 3 et 4\n" +
                "Utilisez le même couteau\n" +
                "Sel, à votre goût\n" +
                "LOCKOUT TAGOUT PROCEDURE\nIsolate energy before maintenance.",
                42,
                260,
                [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "BE A MASTER. BECOME A CHEFPROTÉINESLÉGUMESHYDRATES", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "Ouvrir le robot et retirer le panier vapeur avec précaution.", 61, 9, [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "Programmer 10 secondes, vitesse 6 et fermer.", 45, 6, [3]),
            new ExtractedDocumentUnit(2, 0, 1, 1, "Amuse-bouches et apéritifsSaladesAccompagnements", 50, 4, [4]),
            new ExtractedDocumentUnit(3, 0, 1, 1, "CRÈME DE PETIT POIS ET D’AVOCATLa texture de l’avocat en fait un excellent ingrédient.", 90, 12, [5]),
            new ExtractedDocumentUnit(4, 0, 1, 1, "Basilic ou herbes aromatiques, à votre goût3 petites courgettes (environ 500 g)Préparation", 91, 11, [6]),
            new ExtractedDocumentUnit(5, 0, 1, 1, "(Recommencer cette opération si nécessaire jusqu’à ce que tous les ingrédients soient bien mélangés)", 93, 10, [7]),
            new ExtractedDocumentUnit(6, 0, 1, 1, "est idéal", 9, 2, [8]),
            new ExtractedDocumentUnit(7, 0, 1, 1, "Avec des gants et", 17, 4, [9]),
            new ExtractedDocumentUnit(8, 0, 1, 1, "Ensuite, programmer 5 secondes à vitesse 9", 41, 6, [10]),
            new ExtractedDocumentUnit(9, 0, 1, 1, "Savoureuse et très polyvalente, outre les pâtes, vous pourrez l’utiliser", 70, 10, [11]),
            new ExtractedDocumentUnit(10, 0, 1, 1, "fonctionne parfaitement aux vitesses 3 et 4", 43, 7, [12]),
            new ExtractedDocumentUnit(11, 0, 1, 1, "Utilisez le même couteau", 23, 4, [13]),
            new ExtractedDocumentUnit(12, 0, 1, 1, "Sel, à votre goût", 17, 4, [14]),
            new ExtractedDocumentUnit(13, 0, 1, 1, "LOCKOUT TAGOUT PROCEDURE\nIsolate energy before maintenance.", 58, 7, [15])
        };

        var profile = DocumentProfileProjector.Project(
            "Mixed/NoisyHeaders.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "LOCKOUT TAGOUT PROCEDURE", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.Contains("BECOME A CHEF", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Ouvrir", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Programmer", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.Contains("SaladesAccompagnements", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.Contains("AVOCATLa", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.Contains("goût3", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Recommencer", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "est idéal", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Avec des", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Ensuite", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Savoureuse", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("fonctionne", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Utilisez", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Sel,", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Project_keeps_compact_how_to_titles_but_discards_procedural_leads()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "Faire fondre du chocolat\nFAIRE UN BAC DE CUISINE-MOI EN PREMIER\nAssaisonnez la crème de sel et mettez-la dans un joli saladier.\n",
                26,
                132,
                [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Kitchen Techniques", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "Faire fondre du chocolat", 25, 4, [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "FAIRE UN BAC DE CUISINE-MOI EN PREMIER", 40, 7, [3]),
            new ExtractedDocumentUnit(2, 0, 1, 1, "Assaisonnez la crème de sel et mettez-la dans un joli saladier.", 65, 10, [4]),
            new ExtractedDocumentUnit(3, 0, 1, 1, "Badigeonner Étendre, à l’aide d’un pinceau, une préparation liquide", 68, 9, [5]),
            new ExtractedDocumentUnit(4, 0, 1, 1, "Préchauffez le four à température maximale sur la position grill", 63, 9, [6]),
            new ExtractedDocumentUnit(5, 0, 1, 1, "Réaliser la sauce à la crème.Verser le porto dans la poêle des escalopes", 76, 12, [7])
        };

        var profile = DocumentProfileProjector.Project(
            "Mixed/Techniques.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "Faire fondre du chocolat", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "FAIRE UN BAC DE CUISINE-MOI EN PREMIER", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Assaisonnez", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Badigeonner", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Préchauffez", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Réaliser", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Project_discards_layout_sentence_fragments_without_losing_plain_titles()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "Pain aux céréales\nLa congélation des légumes frais\nMatériel •2 terrines•1 bol\nAprès le signal, répartir l’huile uniformément dans la poêle\n",
                28,
                148,
                [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Technique guide", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "Pain aux céréales", 17, 3, [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "La congélation des légumes frais", 32, 5, [3]),
            new ExtractedDocumentUnit(2, 0, 1, 1, "Matériel •2 terrines•1 bol •1 passoire à pieds•1 fouet", 60, 8, [4]),
            new ExtractedDocumentUnit(3, 0, 1, 1, "Après le signal, répartir l’huile uniformément dans la poêle", 61, 9, [5]),
            new ExtractedDocumentUnit(4, 0, 1, 1, "bien mélan-ger", 15, 2, [6]),
            new ExtractedDocumentUnit(5, 0, 1, 1, "repas .............................................................................................................. 10", 94, 2, [7]),
            new ExtractedDocumentUnit(6, 0, 1, 1, "Cuisson : 30 minutes", 20, 3, [8]),
            new ExtractedDocumentUnit(7, 0, 1, 1, "On garde les verres au réfrigérateur jusqu’au service.", 54, 8, [9])
        };

        var profile = DocumentProfileProjector.Project(
            "Mixed/LayoutFragments.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "Pain aux céréales", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "La congélation des légumes frais", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Matériel", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Après", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("bien", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("repas", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Cuisson", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("On garde", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Project_discards_catalog_page_parameter_and_sentence_leads_without_category_rules()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "COURGE SPAGHETTI I page 48LE GRANOLA A KAKI I page 47\n" +
                "Temps de preparationen minutes\n" +
                "Pour vous aider a substituer certains ingredients, consultez l outil D a la page 56\n" +
                "Repartir la preparation dans des moules a muffins legerement huiles\n" +
                "Melangez, puis garnissez les blancs de cette preparation\n" +
                "A l aide de la spatule, ramenez la preparation vers le centre\n" +
                "SAUCE VERTE\nIngredients: persil, ail, huile.",
                55,
                397,
                [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Generic guide", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "COURGE SPAGHETTI I page 48LE GRANOLA A KAKI I page 47", 60, 10, [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "Temps de preparationen minutes", 29, 4, [3]),
            new ExtractedDocumentUnit(2, 0, 1, 1, "Pour vous aider a substituer certains ingredients, consultez l outil D a la page 56", 80, 13, [4]),
            new ExtractedDocumentUnit(3, 0, 1, 1, "Repartir la preparation dans des moules a muffins legerement huiles", 67, 9, [5]),
            new ExtractedDocumentUnit(4, 0, 1, 1, "Melangez, puis garnissez les blancs de cette preparation", 55, 8, [6]),
            new ExtractedDocumentUnit(5, 0, 1, 1, "A l aide de la spatule, ramenez la preparation vers le centre", 62, 12, [7]),
            new ExtractedDocumentUnit(6, 0, 1, 1, "SAUCE VERTE\nIngredients: persil, ail, huile.", 43, 6, [8])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/Cards.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "SAUCE VERTE", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.Contains("page 48", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Temps de preparation", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Pour vous aider", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Repartir", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Melangez", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("A l aide", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Project_keeps_high_signal_glued_titles_when_card_limit_is_reached()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                122,
                "Salez et poivrez puis lancez le programme.6 personnes23 min10 minSAUCE BÉARNAISEPour les détenteurs d'un appareil connecté. Ingredients: beurre, jaunes d'oeufs.",
                22,
                157,
                [1])
        };
        var sections = Enumerable.Range(0, 95)
            .Select(i => new ExtractedDocumentSection(i, $"Section catalogue {i:00}", 1, 1, 122, 122, null))
            .ToArray();
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                122,
                122,
                "Salez et poivrez puis lancez le programme.6 personnes23 min10 minSAUCE BÉARNAISEPour les détenteurs d'un appareil connecté. Ingredients: beurre, jaunes d'oeufs.",
                157,
                22,
                [2])
        };
        var exact = new[]
        {
            new ExtractedExactMatchEntry(
                0,
                0,
                0,
                122,
                122,
                "Salez et poivrez puis lancez le programme.6 personnes23 min10 minSAUCE BÉARNAISEPour les détenteurs d'un appareil connecté. Ingredients: beurre, jaunes d'oeufs.",
                "salez et poivrez puis lancez le programme 6 personnes23 min10 minsauce bearnaisepour les detenteurs d un appareil connecte ingredients beurre jaunes d oeufs",
                157,
                22,
                [3],
                "verbatim_excerpt")
        };

        var profile = DocumentProfileProjector.Project("Cuisine/Robot.pdf", pages, sections, units, exact);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "SAUCE BÉARNAISE", StringComparison.Ordinal));
        Assert.Contains("SAUCE BÉARNAISE", profile.SearchText, StringComparison.Ordinal);
        Assert.Contains("sauce bearnaise", profile.SearchText, StringComparison.Ordinal);
        Assert.True(profile.ContentCards.Count <= 240);
    }

    [Fact]
    public void Project_extracts_mixed_case_titles_glued_to_long_numeric_layout_suffixes()
    {
        const string title = "Concombres\u00e0 la romaine";
        var text = $"Copyright 2003{title}110077 Ingredients: concombres, creme, moutarde. Preparation: melanger et servir frais.";
        var pages = new[]
        {
            new ExtractedPdfPage(33, text, 14, text.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Catalogue", 1, 1, 33, 33, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 33, 33, text, text.Length, 14, [2])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/CompactNumericSuffix.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, title, StringComparison.Ordinal));
        Assert.Contains(title, profile.SearchText, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_balances_content_cards_across_deep_documents()
    {
        var pages = Enumerable.Range(1, 130)
            .Select(page => new ExtractedPdfPage(page, $"Page {page}", 2, 6, BitConverter.GetBytes(page)))
            .ToArray();
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Deep manual", 1, 1, 1, 130, null)
        };
        var earlyUnits = Enumerable.Range(0, 270)
            .Select(i => new ExtractedDocumentUnit(
                i,
                0,
                1 + (i % 45),
                1 + (i % 45),
                $"EARLY TOPIC {i:000}\nReusable detail for early topic {i}.",
                58,
                8,
                BitConverter.GetBytes(i)))
            .ToArray();
        var late = new ExtractedDocumentUnit(
            999,
            0,
            122,
            122,
            "SAUCE BÉARNAISEPour les détenteurs d'un appareil connecté. Ingredients: beurre, jaunes d'oeufs.",
            94,
            10,
            BitConverter.GetBytes(999));
        var lateExact = new ExtractedExactMatchEntry(
            999,
            0,
            999,
            122,
            122,
            "Salez et poivrez puis lancez le programme.6 personnes23 min10 minSAUCE BÉARNAISEPour les détenteurs d'un appareil connecté. Ingredients: beurre, jaunes d'oeufs.",
            "salez et poivrez puis lancez le programme 6 personnes23 min10 minsauce bearnaisepour les detenteurs d un appareil connecte ingredients beurre jaunes d oeufs",
            151,
            20,
            BitConverter.GetBytes(1000),
            "verbatim_excerpt");

        var profile = DocumentProfileProjector.Project(
            "Cuisine/Deep.pdf",
            pages,
            sections,
            earlyUnits.Append(late).ToArray(),
            [lateExact]);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "SAUCE BÉARNAISE", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => card.PageStart == 122);
        Assert.True(profile.ContentCards.Count <= 240);
    }

    [Fact]
    public void Project_prefers_embedded_content_title_over_instruction_leads()
    {
        const string text = "Salez et poivrez puis Lancez le programme sauce (Sauce) en vitesse 6 à 70°C pour 8 min avec le bouchon.6 personnes23 min10 minSAUCE BÉARNAISEPour les détenteurs d’un Companion connecté bluetooth, vous pouvez remplacer le programme SAUCE par le mode manuel avec les paramètres indiqués.";
        var pages = new[]
        {
            new ExtractedPdfPage(122, text, 32, text.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Sauces", 1, 1, 122, 122, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 122, 122, text, text.Length, 32, [2])
        };
        var exact = new[]
        {
            new ExtractedExactMatchEntry(
                0,
                0,
                0,
                122,
                122,
                text,
                "salez et poivrez puis lancez le programme sauce sauce en vitesse 6 a 70 c pour 8 min avec le bouchon 6 personnes23 min10 minsauce bearnaisepour les detenteurs d un companion connecte bluetooth",
                text.Length,
                32,
                [3],
                "verbatim_excerpt")
        };

        var profile = DocumentProfileProjector.Project("Cuisine/Sauces.pdf", pages, sections, units, exact);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "SAUCE BÉARNAISE", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Lancez le programme", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Project_promotes_embedded_uppercase_titles_before_parameter_fragments()
    {
        const string text = "Salez et poivrez puis Lancez le programme sauce (Sauce) en vitesse 6 à 70°C pour 8 min avec le bouchon.6 personnes23 min10 minSAUCE BÉARNAISEPour les détenteurs d’un Companion connecté bluetooth, vous pouvez remplacer le programme SAUCE par le mode manuel avec les paramètres indiqués.";
        var pages = new[]
        {
            new ExtractedPdfPage(122, text, 32, text.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Robot reference", 1, 1, 122, 122, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 122, 122, text, text.Length, 32, [2])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/CompactLayout.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        var pageTitles = profile.ContentCards
            .Where(card => card.PageStart == 122)
            .Select(card => card.Title)
            .ToArray();

        Assert.Contains("SAUCE BÉARNAISE", pageTitles);
        Assert.DoesNotContain(pageTitles, title => title.StartsWith("vitesse ", StringComparison.OrdinalIgnoreCase));
    }
}
