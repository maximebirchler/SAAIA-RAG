# Contrat du préflight : chunks canoniques et pages physiques

La lecture `/documents/context` renvoie les chunks canoniques entiers qui chevauchent la fenêtre demandée. Un chunk portant les pages 5–6 est donc une réponse légitime à une lecture de la page 5. Le préflight précédent imposait à tort `pageStart == pageEnd == 5` et confondait cette forme du résultat avec l'identité documentaire.

La correction sépare maintenant :

- `canonicalReadExactIdentity` : document, révision et hash exacts ;
- `canonicalReadWholeChunkOverlapVerified` : résultat non vide, trouvé, bornes de chaque chunk dans la source, chevauchement de la page demandée et nombre de pages documentaire identique au manifeste ;
- `wholeChunkContextPatternsMatched` : faits retrouvés dans le contexte réellement fourni par les chunks entiers ;
- `originalPhysicalPage` : mêmes faits recherchés séparément dans le texte original de la page physique exacte du snapshot d'extraction natif.

Le snapshot natif doit correspondre au SHA enregistré dans le manifeste. La page physique doit être unique, appartenir au hash source exact et posséder exactement le checksum textuel du manifeste. Ses voisins ne sont jamais joints à ce contrôle. Des faits présents seulement page 6 ne peuvent ainsi satisfaire une exigence connue page 5, même lorsqu'un chunk 5–6 est correctement retourné par le backend.

Les deux contrôles factuels restent obligatoires pour réussir : la correction du chevauchement ne rend pas acceptable un texte natif ou un chunk qui a perdu les faits attendus. Les motifs manquants de `native-preflight-v1` restent un échec réel à diagnostiquer dans l'extraction et les projections, indépendamment de cette correction de contrat.

Les helpers purs sont utilisés par le vrai point d'entrée du préflight. Les contrôles offline ajoutés couvrent un chunk 5–6 légitime, les identités erronées, le voisinage sans chevauchement, les bornes hors source ou inversées, les résultats vides, le hash textuel/source incorrect, la page manquante et les faits présents uniquement sur une autre page. Leur exécution appartient à l'opérateur parent ; aucune API, base, compilation ou service n'a été lancé par l'auteur de ce correctif.

Fichiers modifiés : `tools/validation-paid/PaidCampaign.Common.ps1`, `Test-OwnedRealCorpus.ps1`, `Test-ProductionCorpusGuards.ps1`. `PaidRuntime.ps1`, les preuves antérieures et le backend restent inchangés dans cette intervention.
