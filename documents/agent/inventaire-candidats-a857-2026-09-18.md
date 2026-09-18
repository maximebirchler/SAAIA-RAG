# A857 — fondation de l'inventaire de candidats

## Verdict du palier

Le backend dispose maintenant d'un inventaire typé de candidats dans la boucle
de recherche native Writer/Critic. Ce palier corrige deux pertes causales vues
dans A855 : les candidats pouvaient disparaître lors d'un changement de focus,
et les clés opaques `internal-source-N` changeaient lorsque l'ordre des extraits
changeait.

Le palier est une fondation déterministe, pas encore une validation du planning.
Il reste à séparer explicitement l'Explorer du Writer, à rendre son état durable
entre reprises de processus, puis à effectuer un pilote live et les trois
réussites end-to-end exigées. Le statut reste `TESTE_NON_APPROUVE`.

## Contrat implémenté

L'inventaire n'est activé que pour une demande structurée dont le mode de preuve
contient `named_item`, avec les outils natifs et l'espace de travail de recherche
activés. Les chemins ordinaires ne reçoivent pas ce contrat.

Chaque entrée contient :

- une clé bornée, un titre exact et une clé source opaque stable pendant toute
  l'exécution Writer vers Critic ;
- les rôles envisagés dans `targetRoles` et les affectations finales distinctes
  dans `selectedRoles` ;
- un état parmi `navigation_only`, `body_requested`, `body_verified`,
  `selected` et `rejected` ;
- une note bornée, au plus quatre EvidenceId de localisation et quatre
  EvidenceId de corps.

Le modèle reçoit l'outil strict `save_candidate_inventory`. L'appel réalise un
upsert : les candidats omis sont conservés, un même titre/source ne peut pas
être dupliqué, et une mise à jour peut accompagner des recherches documentaires
dans le même tour. Un batch invalide est refusé avant toute nouvelle opération
documentaire.

Les gardes mécaniques imposent notamment :

- au plus 32 mises à jour par appel et 64 candidats dans le contexte ;
- uniquement des rôles, sources et EvidenceId visibles ;
- une même source pour toutes les preuves d'un candidat ;
- aucune preuve classée `navigation` comme corps ;
- un titre réellement présent dans les métadonnées exactes ou dans le contenu
  visible ;
- un corps pour `body_verified` ou `selected`, et un rôle final pour
  `selected`.

Le code ne décide pas si une recette, une procédure ou un produit convient à un
rôle. Il conserve la proposition du modèle, vérifie les identités, sépare
localisateur et corps, calcule les comptes par rôle et garde les preuves visibles.
La mémoire est explicitement présentée comme opérationnelle et ne devient jamais
une preuve documentaire.

## Continuité automatique

Quand le retrieval fournit déjà un `candidateTitle` marqué exact, le backend
crée ou avance automatiquement l'entrée. Un sommaire produit
`navigation_only`; un extrait substantiel du même titre et de la même source la
fait passer à `body_verified`. Le modèle n'a donc pas besoin de rappeler l'outil
pour conserver une donnée structurée déjà certaine.

Lorsque le titre n'existe que dans le texte, le modèle garde la responsabilité
de l'identifier et l'enregistre avec `save_candidate_inventory`. Un contrôle
hors réseau reproduit la forme A855 : Porridge, Pancakes et Scones arrivent dans
trois focus successifs sans métadonnée `candidateTitle`; les trois titres et les
trois corps restent présents à la fin.

Après une réponse `answered`, les choix du Writer sont mécaniquement reportés en
`selected` avant l'appel du Critic. Cette projection ne s'applique que si le
titre choisi apparaît dans une preuve de corps visible et si ces preuves
désignent une source unique. Elle permet notamment au Critic de voir une
sélection même lorsque les corps A817 n'avaient pas de `ExactTitle` structuré.

## Relecture causale des traces A855

L'audit hors réseau des trois traces privées A855 produit
`meal-a857-offline-trace-assessment/a855-trace-assessment.v1.json`, sans appel
fournisseur. Le premier prompt Writer contenait 13 preuves et le suivant 11.
Les trois choix terminaux étaient bien présents dans des corps substantiels,
mais aucun ne possédait de métadonnée de titre exact :

| Choix A855 | Correspondance `candidateTitle` exacte | Corps visible contenant le titre |
|---|---:|---:|
| `PORRIDGE AUX FLOCONS D'AVOINE` | 0 | oui |
| `PANCAKES` | 0 | oui |
| `SCONES AUX CANNEBERGES` | 0 | oui |

Les anciennes projections changeaient aussi les clés sources d'un même
EvidenceId. Le Porridge passait de `internal-source-2` à `internal-source-1` et
un extrait Pancakes de `internal-source-4` à `internal-source-2`. A857 conserve
désormais le dictionnaire source pendant tout le contexte de recherche. Cette
correction est générale : elle dépend des identités documentaires canoniques,
pas du domaine Cuisine.

Les SHA-256 des trois traces d'entrée sont consignés dans l'artefact. Le replay
montre que le nouveau contrat peut accepter les trois titres depuis leurs corps
et que la dérive d'identité est corrigée. Il ne montre pas qu'un modèle live
appellera correctement l'outil ni qu'il trouvera les candidats manquants.

## Contrôles déterministes

Les tests couvrent les protocoles Chat Completions et Responses, l'upsert sans
perte, la stabilité des sources, la transition sommaire vers corps, la rétention
automatique sans appel d'outil, la forme A855 sans métadonnée de titre, la
projection Writer vers Critic, les rôles finaux séparés et le refus atomique des
inventaires invalides.

Le contrôle provider passe avec 289 réussites et zéro échec. La suite backend
complète passe avec 2 447 réussites, zéro échec et trois tests live ignorés. Les
tests live demeurent désactivés et aucun appel OpenAI n'est autorisé par ce
palier.

## Limites restantes et prochain ordre de travail

L'inventaire vit pour l'instant dans le contexte unique Writer/Critic. Il n'est
pas encore sauvegardé dans le job durable si le processus s'interrompt. Le
Writer cumule encore exploration et rédaction ; A857 n'a donc pas encore créé
un Explorer autonome qui remet un dossier de couverture complet au Writer.

Le prochain incrément doit :

1. extraire une phase Explorer utilisant le même provider et le même contrat,
   sans nouveau fournisseur ni logique Cuisine ;
2. lui faire produire un dossier borné de candidats et de lacunes avant la
   rédaction, puis transmettre uniquement les corps vérifiés ;
3. sérialiser cet état dans le job durable avec les identités de révision ;
4. rejouer A813/A817 hors réseau pour contrôler vingt corps et vingt sélections,
   sans reconstruire manuellement les preuves au moment du Writer ;
5. effectuer ensuite un seul pilote live connu sur une capacité avancée de
   nouveau disponible, puis seulement deux répétitions et un holdout si le
   premier réussit.

Le reliquat OpenAI de 0,00563940 USD reste sous le seuil d'admission. Aucune
recharge, location GPU ou nouvelle campagne payante n'est nécessaire pour les
quatre premiers travaux.
