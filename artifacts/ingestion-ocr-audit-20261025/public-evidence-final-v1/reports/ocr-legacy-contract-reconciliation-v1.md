# Réconciliation de deux anciens contrats de filtre OCR

## Preuve de départ

La campagne [unit-all-v1](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/artifacts/ingestion-ocr-audit-20261025/tests/unit-all-v1/manifest.json>) exécute les quatre projets : Backend 2666 PASS/6 FAIL/1 SKIP sur 2673 cas ; Contracts 20 PASS ; ClientCore 82 PASS ; Client 2419 PASS. Deux des six échecs backend concernent les anciens tests `Ocr_noise_filter_detects_spaced_letter_runs_without_removing_useful_text` et `Ocr_noise_filter_skips_short_broken_uppercase_header_fragments`.

Les cinq cas de `PostgresSourceFidelityTests` sont désormais GREEN dans la campagne [final-gaps-red](<C:/Users/maxim/Desktop/ecom/AAA/SAAIA - RAG/artifacts/ingestion-ocr-audit-20261025/tests/final-gaps-red/manifest.json>) : la campagne globale mixte garde ses deux autres RED indépendants ; elle n'est pas appelée entièrement verte.

## Ancien et nouveau contrat

**Lettres espacées avec phrase lisible.** L'ancien test exigeait de classer toute la ligne comme du bruit dès qu'un long groupe de glyphes isolés précédait une phrase lisible. Cela pouvait retirer la phrase utile, ou des variables/labels légitimes, sans preuve indépendante de corruption. Le nouveau contrat conserve ce texte littéral. Le détecteur ne doit pas assimiler l'espacement des lettres à une preuve de texte inutile.

**Fragments en majuscules terminés par un tiret.** L'ancien test exigeait que `DES-` et `PLATS PRINCI-` soient du bruit uniquement en raison de leur forme. Un titre tronqué, un code ou une continuation restent des observations du document ; leur format ne prouve pas leur inutilité. Le nouveau contrat les conserve et n'invente pas la suite manquante.

## Modification effectuée

Uniquement le fichier de tests `DocumentUnitExtractorTests.cs` a changé lors de cette réconciliation. Aucune garde produit n'a été affaiblie pour faire passer ces deux attentes anciennes.

- Les trois exemples de lettres espacées avec texte lisible deviennent une théorie de conservation, vérifiant à la fois l'absence de classification comme bruit et la conservation exacte du texte.
- Les deux fragments en majuscules et la phrase témoin `OPEN-LOOP calibration remains active.` deviennent une théorie de conservation ; la normalisation de suffixe doit aussi rendre exactement le texte fourni.
- Tous les exemples de texte réellement brouillé présents dans l'ancien test sont maintenus dans un test séparé, sans suppression ni inversion de leurs assertions. Les témoins de valeurs/durées, référence normative et texte technique lisible restent également présents.

Le découpage évite que la première attente devenue obsolète empêche l'exécution des contre-contrôles suivants. Leur nouveau résultat reste à capturer par l'agent principal ; ce rapport ne déduit pas leur succès de la lecture du code.

## État à la remise

Tests gelés après cette modification. Aucun .NET, service, Git ou appel modèle exécuté par l'agent. Les résultats de la prochaine campagne permettront de distinguer une ancienne attente de suppression et un éventuel défaut restant dans une garde de corruption réelle. Le parcours moteur legacy reste non qualifié sans OCRmyPDF/Tesseract réellement exécutés.
