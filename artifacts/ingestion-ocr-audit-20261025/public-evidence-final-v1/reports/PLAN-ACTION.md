# Fiabilisation de l’ingestion — campagne locale du 25/26 octobre 2026

## Mandat et frontières de preuve

L’utilisateur autorise un travail substantiel sur l’ingestion, OCR compris. La priorité est de conserver les faits et leur origine avant de dépenser à nouveau en génération. Aucun appel LLM payant n’est prévu dans cette campagne. Aucune infrastructure habituelle ou distante n’est modifiée. Le produit reste `TESTE_NON_APPROUVE` jusqu’aux preuves d’acceptation requises ; un corpus fini ne démontre pas une extraction parfaite de tous les documents possibles.

Le jalon précédent, projections natives et contrôles SQL compris, est sauvegardé dans les commits `292db83` et `24b21ab`, poussés sur `SAAIA_V3.1`. Ses 5104 PASS unitaires et 275 PASS PostgreSQL + 2 skips historiques ne sont pas des preuves d’OCR réel. Son coût LLM cumulé estimé était de 0,00678686 USD. Aucun nouvel appel payant depuis ce jalon.

## Étapes d’exécution

1. **Reproduire avant correction.** Audit distinct de l’extraction native/OCR, de Docling/réconciliation/publication et des outils de qualification. Première capture OCR : 24 échecs sur 24 cas ciblés. Capture Docling v2 : 10 échecs et 1 témoin passant. La tentative Docling v1 s’est arrêtée sur une erreur de compilation de fixture ; elle ne prouve aucun comportement produit.
2. **Conserver la source.** Une copie immuable par tentative doit être partagée par tous les extracteurs. Son SHA correspond aux octets réellement lus. Une mutation du fichier original bloque la publication d’une extraction devenue obsolète.
3. **Conserver les résultats OCR.** Ne pas effacer des pages déjà reconnues au profit d’un résultat partiel. Une similarité, un faible nombre de mots ou une confiance faible ne prouvent pas l’inutilité d’un fait. Préserver variantes, signes, nombres, négations, texte brut et provenance ; rendre l’incertitude durable et explicite.
4. **Unifier le canonique.** Conserver les avertissements répétés, les données courtes et les récupérations natives sans inventer leur section. Les pages, unités et chunks doivent dériver d’une même interprétation des charspans/provenances. Une réparation de tableau échouée conserve l’original et un diagnostic de dégradation.
5. **Garantir la reprise et la publication.** Empreinte du plan exact : source, textes, métadonnées, format, modèle/révision, dimensions, cible et code. Une reprise doit vérifier les points réellement présents ; la publication doit vérifier l’ensemble attendu, pas seulement un compteur local ou un acquittement HTTP.
6. **Rendre les sondes honnêtes.** Distinguer pages candidates, tentées, réussies, échouées, inconnues ; ne pas transformer un compteur moteur en pages traitées. Aucune donnée documentaire ni secret dans les journaux ordinaires. Conserver les observations utiles dans les artefacts/provenances prévus.
7. **Corriger la qualification.** Skip explicite lorsqu’un OCR opt-in n’est pas exécuté ; corpus non vide et complet ; paramètres de production ; police et générateur identifiés ; vérification exacte des faits critiques en plus des métriques OCR globales. Une projection ne sera pas renommée publication SQL ou validation du worker.
8. **Exécuter localement.** Préparer un environnement OCR/Docling isolé et des PDF natifs/scannés/mixtes avec attentes indépendantes. Exécuter le vrai worker, vérifier PostgreSQL et les endpoints de recherche/lecture, puis les cas de réingestion, mutation et interruption. Déclarer tout substitut TEI/Qdrant ; qualifier les vrais vecteurs séparément.
9. **Consolider.** Suites ciblées, suites unitaires complètes, PostgreSQL isolé, architecture et intégrité des preuves. Relire les deltas, enregistrer les résultats avec hashes, commit/push explicites, rapport Telegram et compte rendu clair avec limites restantes.

## Dépendances locales observées

Windows, 31,8 GiB de RAM, RTX 4060 (8188 MiB), espace disque suffisant. Python 3.12.14, Poppler et bibliothèques de génération PDF disponibles. Installation de Docling Serve 1.38.1, RapidOCR 3.9.1 et ONNX Runtime 1.23.2 engagée dans `out/ingestion-runtime-20261025/docling-venv`, sans modifier le Python global. Une installation n’est pas une conversion réussie : chargement des modèles, versions/hashes et fonctionnement hors ligne restent à vérifier.

Le contrôle automatique a refusé l’action combinée de téléchargement/installation silencieuse de l’exécutable Tesseract. Aucun succès d’installation Tesseract n’est revendiqué. La préparation du moteur local Docling se poursuit ; les chemins non exécutables resteront explicitement non qualifiés.

## Conditions d’acceptation

Tous les cas prévus doivent avoir été exécutés. Une erreur critique de valeur, signe, négation, page ou relation ne peut être compensée par une moyenne élevée. Les cas incertains restent signalés avec leur preuve source. Publication complète, reprise, isolation tenant/révision et nettoyage doivent être démontrés sur le vrai parcours. Le planning de repas et la validation sémantique du LLM constitueront une phase ultérieure distincte.

## Clôture de cette campagne

Code poussé : `12202cb`. Suites finales : 5 255 PASS unitaires + un skip explicite ; 310 PASS PostgreSQL + deux skips historiques, sources stables. Vrai corpus : 12 PDF/90 faits/37 points ; vrai livre : 36 pages/16 faits/160 points. Le cycle réel v1 démontre l’annulation pendant extraction, ACK explicite et publication conservée. Dans le cycle final v2, la completion gagne la course : annulation NON_EXECUTED, sans faux PASS ; code de contrôle inchangé vérifié. Les deux titres manquants découverts après le premier run du livre ont été corrigés puis contrôlés dans le dernier.

Moteurs réellement exécutés et arrêt vérifié des environnements/dépendances sont décrits dans `RAPPORT-INGESTION.md`. Les états d’installation ci-dessus sont le plan initial, pas l’état final. Zéro nouvel appel LLM payant ; ancien OCR opt-in non qualifié ; limites de GC vectoriel, figures générales et rappel sémantique conservées. Le produit reste `TESTE_NON_APPROUVE`.
