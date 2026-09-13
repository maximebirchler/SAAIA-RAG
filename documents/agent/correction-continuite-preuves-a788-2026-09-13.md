# A788 — Préserver les conditions situées en fin d'extrait

Le diagnostic OAuth A787 répond, sans drapeau mécanique, avec dix claims et cinq sources physiquement et canoniquement vérifiées. Quatre appels, 61,768 secondes, coût estimé 0,1138985 USD incluant les écritures de cache retournées par le fournisseur. Aucune réparation Writer n'a été nécessaire lors de cette répétition ; elle ne prouve donc pas que cette réparation a été exercée en situation réelle.

La réponse demeure rejetée sur le fond. Les étapes D et E décrivent des actions génériques et omettent la vérification de l'URI de redirection propre au flux authorization-code demandé. Le passage canonique RFC 6749 page 25 contient pourtant les cinq étapes et leurs vérifications : 1 513 caractères. La préparation des preuves n'en transmettait que les 700 premiers au Writer, au contrôleur de recherche et au Critic. Les étapes D et E sont dans la partie coupée. Le modèle a donc utilisé des descriptions générales présentes ailleurs dans le pool.

## Correction

Pour les demandes non structurées et les tableaux de faits sur des sujets déjà fixés, chaque extrait peut maintenant conserver jusqu'à 2 400 caractères. Le plafond global configuré de contexte reste appliqué, sans augmentation automatique pour cette famille. Les tableaux de sélection de nouveaux objets conservent la limite de 700 caractères et leur règle existante de couverture. Cela protège notamment le coût des grilles de repas.

Chaque preuve indique `originalContentLength` et `contentTruncated`, pour distinguer le texte transmis de la source complète. Les instructions du Writer et du Critic demandent de préserver les acteurs, objets transférés et conditions de validation de la procédure spécifique. Une vérité générale ne suffit pas à compléter une étape spécifique. Les identifiants de source restent opaques ; aucun nom de fichier ni chemin du corpus n'est ajouté aux requêtes du fournisseur.

## Preuves et limites

Deux tests ont échoué avant correction : une condition décisive placée après 700 caractères était perdue et aucun indicateur de coupure n'était disponible. Ils vérifient maintenant le contenu réellement envoyé au Writer et au Critic, ainsi que l'indication de coupure pour un extrait dépassant 2 400 caractères. La suite backend finale passe 2 224 tests, sans échec, avec trois intégrations réelles non exécutées. Les protections existantes contre la transmission de noms et chemins du corpus restent validées.

Artifacts : `artifacts/reprise-pc-20260908/a788-comparison-sources-20260913/late-condition-red.trx` et `backend-a788-v2.trx`. Le résultat OAuth rejeté, ses claims et les textes canoniques sont conservés sous `a787-cache-write-budget-20260913/oauth-r2/evidence`. Le verdict BH6 original reste inchangé.

Un nouvel essai OAuth divulgué reste nécessaire pour vérifier la correction sémantique. Il ne constitue pas une acceptation aveugle. La comparaison AR5/AR6 nécessite encore une vérification de la provenance par édition, et la clarification injustifiée sur les deux manuels ainsi que la décision locale sur une installation inconnue sont des défauts séparés. Produit TESTE_NON_APPROUVE.
