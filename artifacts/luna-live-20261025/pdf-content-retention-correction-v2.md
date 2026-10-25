# Conservation générale du texte PDF avant indexation

État : deuxième RED causal capturé, correction générale gelée ; GREEN et nouveau préflight réel à exécuter par l’opérateur. Ce rapport remplace la politique numérique seulement décrite dans `pdf-content-retention-correction-v1.md`, conservé comme historique. Aucun build, service ou fournisseur exécuté par l’auteur de ces changements.

## Pourquoi le premier correctif était insuffisant

Le premier diagnostic établissait que des valeurs répétées étaient supprimées par la fréquence et par le remplacement des chiffres en `#`. Mais exclure les seuls chiffres ne protégeait ni un avertissement répété, ni une fraction Unicode comme `½`. La même règle pouvait effacer une légende expliquant le document. Le tuple d’entrée contient le texte et l’identité physique de la page ; il ne fournit aucune preuve de rôle, de géométrie ou d’inutilité de ces lignes.

Deux cas RED indépendants ont donc été ajoutés : un avertissement opérationnel répété et une capacité avec fraction Unicode. La fixture PDF réelle générique a aussi été étendue avec ces lignes. L’opérateur a constaté trois échecs sur dix-sept tests ciblés, les quatorze autres passant. Les assertions ne contiennent aucun fait attendu du PDF de recettes et les règles produit n’utilisent aucun vocabulaire métier.

## Politique finale

Toute la machinerie de fréquence, de regroupement de motifs et de suppression des lignes répétées a été retirée. Les faits, avertissements, titres, légendes et en-têtes incertains restent présents, qu’ils contiennent des chiffres ASCII, d’autres formes numériques ou aucun chiffre. Si aucun compteur n’est retiré, le texte original est retourné sans reconstruction de ses espaces ou lignes.

Une suppression ne reste possible que pour une ligne entièrement constituée d’un compteur explicitement étiqueté `Page` ou `p.`, correspondant à la page physique courante et, s’il figure, au total physique, à un bord textuel. Les nombres nus, les ratios, les nombres encadrés de tirets et les en-têtes contenant aussi un numéro restent conservés. Un contrôle supplémentaire vérifie qu’une étiquette avec une autre identité physique ou placée au milieu du corps reste inchangée.

Les anciennes attentes de suppression de `ACME REPORT`, d’un en-tête de confidentialité ou d’une légende de révision ne prouvaient pas l’innocuité de leur effacement. Les noms et assertions de ces tests expriment désormais leur conservation. La suppression des compteurs explicites dispose toujours de contrôles négatifs distincts. L’ancien effacement arbitraire de `235` entre des lignes de prose reste corrigé : aucune preuve ne permettait de le qualifier de pagination.

Une recherche sur les sources C# backend confirme zéro référence pour les dix anciens helpers privés supprimés ou remplacés. Le point d’entrée existant `RemoveRepeatedPageBoilerplate` est conservé pour compatibilité ; ses helpers internes portent désormais le nom du traitement réellement effectué.

## Limites et vérifications attendues

Conserver des en-têtes incertains peut augmenter le bruit ou la taille du corpus. Cela est préféré à l’effacement irréversible d’une preuve avant indexation. Une éventuelle classification future du texte répété devra conserver sa provenance et établir son rôle ; sa fréquence seule ne suffit pas.

Le mélange de colonnes observé dans le texte natif de la page 5 reste une limitation connue et n’est pas annoncé comme résolu ici. La quantité de vin existait dans ce texte mais était perdue dans la projection de chunks, corrigée et testée dans un autre périmètre. Le nombre de convives était, lui, perdu dès le nettoyage des pages. Le préflight doit prouver les faits dans la page physique attendue, indépendamment des chunks qui chevauchent une page voisine.

Dix-huit tests PDF ciblés sont maintenant préparés. Aucun résultat GREEN nouveau ni succès du cas payé n’est revendiqué dans ce rapport. Le statut produit reste `TESTE_NON_APPROUVE`.
