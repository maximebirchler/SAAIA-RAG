# Garde de conformité du texte réellement stocké

Statut : outillage préparé, scripts analysés sans erreur de syntaxe PowerShell. Les nouveaux contrôles hors ligne et la preuve PostgreSQL réelle restent à exécuter par le coordinateur.

Le défaut CRCRLF a montré que la validation du checksum avant écriture SQL ne garantit pas les octets stockés. Une nouvelle garde vérifie les données après `SeedRealCorpus`, avant le démarrage du worker avancé et avant toute admission de job. Les deux parcours, préflight local et essai payé, appellent la même fonction `Get-OwnedPaidCorpusTextConformance`.

Les quatre tables sont contrôlées dans une transaction en lecture seule sur l’instance PostgreSQL détenue par la campagne. Les comptes totaux et les comptes limités à la révision/au tenant attendus doivent correspondre aux comptes du manifeste figé. Le checksum est recalculé par PostgreSQL avec `sha256(convert_to(...,'UTF8'))` :

| Table | Champ couvert par son contrat de checksum |
| --- | --- |
| `document_units` | `text_content` |
| `retrieval_chunks` | `text_content` |
| `contextual_text_entries` | `text_content` |
| `exact_match_entries` | `normalized_text` |

Les comptes manquants, supplémentaires ou rattachés à une autre révision, les checksums différents, les valeurs nulles, les checksums de longueur incorrecte et les observations mal formées empêchent toute admission. Le validateur refuse aussi les tables absentes ou dupliquées, les comptes sous forme de chaînes, les comptes négatifs et le mauvais champ de checksum. Des entrées exactes en nombre nul restent autorisées ; les trois tables de contenu obligatoires doivent être non vides.

La preuve publique `corpus-text-conformance.json` contient uniquement les compteurs, champs fixes, indicateurs de conformité et SHA256 du manifeste. Elle est créée sans écrasement ; même un résultat négatif est conservé. Les réponses et diagnostics SQL restent privés via le mécanisme existant. Aucun extrait du corpus, secret ou message brut d’exception n’est recopié dans cette preuve publique.

Vingt contrôles hors ligne supplémentaires couvrent les observations concordantes, les écarts, les structures invalides, des exécutions simulées de la garde, l’immuabilité des preuves, l’absence de fuite d’une réponse SQL mal formée et la présence de la garde avant les deux chemins d’admission. Les mocks remplacent les fonctions d’environnement et de SQL ; ils ne démarrent aucun PostgreSQL ou backend.

Cette garde valide le contrat de checksum stocké et les comptes, sans prouver à elle seule l’extraction complète, le classement de recherche, les vecteurs ou la qualité de réponse. Le contrat des entrées exactes porte sur `normalized_text` : aucun checksum du texte d’affichage de ces entrées n’est revendiqué.

Aucun service, build, appel API ou lecture de secret n’a été effectué par l’auteur de cette modification. La correction Python d’écriture des fins de ligne est indépendante et appartient au coordinateur.
