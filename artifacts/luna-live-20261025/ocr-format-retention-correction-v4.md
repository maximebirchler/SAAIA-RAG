# Qualificateurs et valeurs : suppression des règles de forme seules

Statut : correctif préparé après quatre RED causaux, sources gelées ; GREEN à effectuer par le coordinateur.

La campagne `tests/format-cues-red-context-heading-green` a exécuté 41 cas : 37 succès et quatre échecs. Les quatre échecs concernent les deux mêmes faits, en contrat direct puis dans `DocumentUnitExtractor` avec un corps fiable et une qualité physique calculée.

Le nettoyeur supprimait le qualificateur `AC` de `AC PRESSURE LIMITS 240 bar` sur la seule forme de mots en majuscules. Le classificateur de prétendu en-tête considérait `MAXIMUM WORKING PRESSURE 240` comme du bruit sur la seule combinaison de majuscules, longueur et chiffres. Aucune géométrie d’en-tête ni corruption n’était requise. Les mêmes pertes étaient confirmées dans les unités extraites.

Les deux mécanismes sont retirés : `RemoveLeadingShortLayoutPrefixBeforeStructuredLabels` et son motif, ainsi que `LooksLikeShortPageHeaderFooterFragment`. Une recherche dans les sources C# ne trouve plus d’appel ou de définition. Aucun remplacement par une règle de pression, tension, recette ou autre domaine n’est ajouté.

Deux attentes historiques directement liées à ces règles sont précisées : le préfixe court devant des labels structurels est maintenant conservé exactement ; une ligne de texte en majuscules au rôle incertain est conservée avec le corps voisin. Leur forme ne prouve pas qu’elles sont inutiles. Les autres contrôles de corruption, notamment les séquences de lettres isolées, les glyphes endommagés et les fragments réellement coupés, ne sont pas modifiés.

La conservation peut inclure davantage de texte accessoire. Ce compromis protège les qualificateurs et valeurs de source. Les tests et le corpus réel restent nécessaires pour vérifier chaque stade ; ces quatre RED ne prouvent pas une lecture universelle de tous les documents.

Aucun build, service ou appel API n’a été exécuté ici.
