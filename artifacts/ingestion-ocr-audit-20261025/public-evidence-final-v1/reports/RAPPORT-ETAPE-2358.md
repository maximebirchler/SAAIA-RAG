# Avancement ingestion — 25 octobre 2026, 23 h 58 UTC

Ce rapport décrit les changements depuis le message Telegram de 23 h 06 UTC. Aucun nouvel appel LLM payant. Le coût estimé de la précédente campagne Luna reste 0,00678686 USD. Le produit reste TESTE_NON_APPROUVE.

## Ce qui a effectivement fonctionné

La suite unitaire complète a terminé avec 5 206 PASS, 0 échec et 1 skip explicite : le test de l'ancien moteur OCR externe, non installé/qualifié. Ces résultats portent sur la version antérieure aux derniers changements de concurrence ; une nouvelle validation sera donc nécessaire après ceux-ci.

Le vrai worker a ingéré deux documents français, l'un scanné et l'autre mixte, soit quatre pages physiques. Le parcours partait de tables PostgreSQL vides et de PDF copiés dans un répertoire isolé : aucun résultat documentaire n'a été injecté artificiellement dans la base. Les jobs ont fini, 13 faits critiques ont été conservés aux bonnes pages, les bundles canoniques et checksums SQL ont été vérifiés, et cinq points ont été relus dans le vrai Qdrant. L'API de lecture a fourni les faits attendus. Le backend et son PostgreSQL de test ont ensuite été arrêtés, sans job actif.

Limite précise de cet essai : les vecteurs étaient alors des vecteurs de protocole 3D déterministes. Il prouve l'exécution et la publication du worker, pas la qualité de la recherche sémantique. Il précède également le dernier correctif de concurrence.

Le modèle d'embeddings multilingue E5-base fonctionne maintenant réellement, hors ligne, sur ce PC. Son identité, sa révision et le SHA des poids sont contrôlés. Un test séparé avec le vrai Qdrant a retrouvé le bon passage parmi trois sujets différents dans trois permutations d'ordre, avec une marge de similarité d'environ 0,081. Les entrées mal formées et trop longues ont été refusées. La collection de ce test a été supprimée puis son absence vérifiée. C'est un contrôle sémantique limité, pas une validation de rappel sur tout le corpus.

## Défauts supplémentaires découverts et reproduits

Cinq tests PostgreSQL ont démontré qu'un ancien worker pouvait encore agir après la perte de son bail : publier, modifier l'état d'une extraction ou continuer une opération après un heartbeat rejeté. C'est un vrai problème de concurrence, indépendant de la qualité du LLM. Le correctif est en cours : identité unique à chaque reprise, contrôle du bail dans la transaction et annulation observée de l'opération devenue obsolète.

Les points Qdrant doivent également être séparés par plan d'ingestion. Sinon un worker retardé peut écraser les vecteurs d'une génération plus récente. Le plan publié sera enregistré en SQL et la recherche n'acceptera que les points correspondant à ce plan. Les numéros de génération ne devront plus revenir en arrière après annulation ou suppression/recréation.

Quatre autres tests PostgreSQL ont reproduit un manque de rappel : des anciens points pouvaient remplir la première fenêtre de résultats et masquer les points actifs. Le correctif récupère une fenêtre plus large, dans une limite stricte de trois recherches et sans recalculer l'embedding. Si ce budget ne permet pas une recherche complète, le diagnostic le dira explicitement ; il ne devra pas faire croire au modèle que la documentation n'existe pas.

Ces défauts sont reproduits et les corrections sont en cours de vérification. Je ne les présente pas encore comme validés.

## La suite immédiate

Après stabilisation de ces corrections : tests ciblés, suites complètes, puis ingestion des douze PDF du corpus figé avec Docling, de vrais embeddings locaux et le vrai Qdrant. Les vérifications couvriront la publication, la lecture et la recherche, puis les reprises et changements de génération.

J'ai aussi préparé le PDF de recettes original complet : 36 pages, source identifiée par son hash et 14 attentes physiques fixées avant cette campagne. Un lecteur PDF indépendant retrouve exactement ces 14 faits. Cette préparation n'est pas encore une ingestion réussie ; elle servira à vérifier le vrai parcours sur un document représentatif du problème initial.

Toutes ces opérations restent locales et isolées. L'ancien OCRmyPDF/Tesseract et le binaire TEI ne sont pas qualifiés par les essais Docling/Transformers. Les preuves privées et bases de test restent séparées des fichiers qui seront commités. Je continue avant de refaire des essais payants de génération.
