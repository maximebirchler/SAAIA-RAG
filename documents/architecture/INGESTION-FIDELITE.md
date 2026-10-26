# Fidélité de l'ingestion : représentations, mesures et limites

Ces règles décrivent les contrôles ajoutés à la chaîne d'ingestion. Un test
unitaire vert, un crop OCR corrigé ou une image conservée ne constitue pas une
approbation globale du backend. La qualification doit relier un PDF hashé à sa
révision canonique, aux chunks effectivement publiés, puis à la recherche et à
la lecture de cette même source sur le binaire et le runtime observés.

## Les représentations doivent conserver les faits et leurs relations

- **Tables.** Les chunks transportent une grille HTML structurée : coordonnées,
  cellules vides observées, coordonnées manquantes, en-têtes, fusions et lignes
  restent distincts. Une coupe respecte les groupes de lignes et les cellules
  indivisibles. Une cellule trop grande donne un refus explicite ; sa fin n'est
  pas tronquée. Les sommaires restent identifiés comme navigation et lisibles
  avec leurs ancres source. La recherche littérale et les profils utilisent une
  surface textuelle dérivée des mêmes cellules, sans interpréter les balises
  comme des mots ni supprimer des valeurs contenant des caractères spéciaux.
- **Observations concurrentes.** Le canonique garde les observations principales
  et les observations natives pour audit. La sélection destinée à la recherche
  privilégie une représentation cohérente et conserve les ajouts réellement
  absents. Une variante native déjà représentée n'est pas ajoutée comme une
  seconde instruction. Les répétitions légitimes, les pages distinctes, les
  signes, décimales et négations doivent rester différenciés. L'égalité d'un
  texte seule ne suffit pas à effacer une occurrence ; l'alias bloc/cellule
  exige aussi un ancrage géométrique unique. Une correction d'ordre physique
  n'est appliquée que lorsque les zones la prouvent ; elle ne signifie pas que
  toutes les pages à colonnes ont été comprises.

## Une grille occupée peut rester physiquement incomplète

Un contrôle complémentaire compare les vrais mots natifs du PDF, avec leurs
positions, au polygone de la table et aux cellules observées. Des mots situés
dans la table mais disjoints de toutes ses cellules signalent une couverture
incertaine. Une zone blanche ne constitue pas une donnée manquante ; la simple
présence du même mot ailleurs sur la page ne rétablit pas sa relation de cellule.

Le contrôle exige une identité source et un repère géométrique compatibles.
Ses limites de mots et de comparaisons sont explicites ; une mesure absente,
partielle, refusée ou sans mots natifs ne devient jamais un compte zéro réussi.
Il conserve les cellules et leurs textes. Son signal ne prétend ni ajouter les
lignes manquantes ni valider la segmentation ou les relations du tableau.

## La relecture d'une table reste reliée à ses fragments

`read_source` peut fournir une vue auxiliaire des lignes d'une même table.
Les textes et références des fragments originaux restent inchangés. La vue
réunit uniquement des observations de la même révision, source, page physique
et table ; les répétitions d'en-têtes doivent avoir les mêmes coordonnées et
les mêmes cellules. Une carte de cellules publiée avec le chunk relie chaque
identifiant aux coordonnées, fusions, rôle et hash du littéral réellement
stocké. L'ordre d'un tableau JSON d'identifiants ne suffit pas à établir ce lien.

Une carte absente ou contradictoire interdit cette reconstruction, sans retirer
le texte HTML original. La pagination respecte les cellules fusionnées et
signale les lignes absentes ou le prochain point de reprise. La vue et les
preuves partagent le budget du prompt ; une grille ne peut pas contourner ce
budget en devenant un champ auxiliaire.

`complete` signifie uniquement que toutes les lignes de la grille canonique
observée sont disponibles dans cette vue. `completenessBasis` explicite ce
périmètre et `sourceFidelityValidated=false` reste distinct : une grille complète
peut reproduire une segmentation inexacte du PDF. La présence de tous les mots
n'établit ni les bonnes frontières de cellules, ni une hiérarchie, ni les
relations d'un schéma.

Une légende de table fournit du contexte seulement si son lien canonique et sa
page sont observés. Elle ne devient pas artificiellement un titre de section.
Un ancien titre rencontré dans le flux de lecture ne prouve pas l'appartenance
d'une table à ce sujet.

## Les diagnostics accompagnent le texte sans le remplacer

Les avertissements des blocs et des pages doivent survivre aux projections,
à SQL, aux résolutions de sources et aux prompts. Un texte non vide, une ancre
valide et un statut `ok` ne constituent pas une vérification sémantique. Le
descripteur `sourceQuality` distingue le statut observé du texte, la candidature
OCR et les avertissements ; `semanticAccuracy=not_verified` ne peut pas devenir
une approbation à partir du seul statut `ok`.

Les textes de contexte conservent également ces diagnostics dans leurs
métadonnées SQL. Leur agrégat inclut l'extrait et seulement les unités dont le
texte a effectivement été ajouté comme contexte courant, précédent ou suivant.
`contributorUnitOrdinals` décrit ces ajouts ; il ne remplace pas les appartenances
source de l'extrait. Les textes, checksums et ancres ne changent pas pour porter
ce diagnostic. Une observation partielle reste `mixed_unknown` ; une ancienne
entrée sans observation reste `unavailable`. Un booléen vrai observé est conservé,
mais un faux exige des diagnostics complets chez tous les contributeurs. Une
liste de signaux vide observée et une liste inconnue ne sont pas équivalentes.
Ces états ne constituent toujours pas une validation sémantique du contenu.

L'absence de diagnostics, une valeur mal formée ou un agrégat mêlant des
contributeurs connus et inconnus ne prouvent pas une source propre. Les champs
restent bornés et leurs omissions explicites. Les réserves sur la géométrie,
les images ou une réparation inconclusive n'inventent pas un échec OCR. Une
corruption de caractères réellement observée est un diagnostic différent.

## L'admission TEI porte sur l'entrée effectivement envoyée

Les limites de caractères et de tokens sont des mesures différentes. Le refus
d'une mesure n'est jamais enregistré comme zéro token. L'admission réduit
éventuellement le contexte auxiliaire d'embedding, ou partitionne la source sans
perdre ses données, puis mesure chaque entrée finale avec le vrai tokenizer.
Les tables suivent leur partitionneur structurel. Un groupe indivisible encore
trop grand échoue explicitement. Un HTTP 413 inconnu reste une erreur ; il ne
devient pas une autorisation générale de découper. Les sondes distinguent les
mesures obtenues et les refus de limite de caractères.

## Conserver les pixels ne signifie pas interpréter un dessin

Lorsque Docling exporte des pixels, les PNG embarqués acceptés sont vérifiés et
bornés avant publication. Leur identité lie tenant, PDF source, révision, page,
figure éventuelle et hash des octets. Les octets et le bundle sont publiés dans
la même transaction ; la lecture revalide leur identité et leur intégrité. Une
URL externe ou un chemin de fichier n'est pas résolu comme une image source.
Si seule l'image de la page est disponible, le produit ne prétend pas posséder
un crop précis de la figure. Une ancienne révision sans pixels reste sans
pixels.

La disponibilité visuelle expose actuellement `interpretationStatus=not_performed`
et `imageToolAvailable=false`. Le modèle ne reçoit donc pas implicitement ces
images. Une comparaison de hashes prouve l'identité des octets conservés, pas
l'exactitude d'une flèche, d'une relation graphique ou d'une future interprétation.

## La réparation OCR régionale conserve son désaccord et ses preuves

Le moteur principal n'est pas remplacé globalement. L'option régionale est
désactivée par défaut et ne cible qu'un préfixe manifestement fragmenté avec des
trous typographiques. RapidOCR et Tesseract anglais en PSM6 doivent produire la
même lecture après NFC et espaces uniquement, à forte confiance. Le suffixe
déjà lisible reste identique, contigu et terminal ; les valeurs, signes,
identifiants explicites et mots courts protégés ne peuvent pas disparaître.

Seul `item.text` peut changer. `orig`, ancien texte, boîtes, empreintes, versions,
confiances et observations des deux moteurs restent auditables. Un désaccord,
une limite ou une indisponibilité conserve le texte principal avec une raison.
Les crops, temps et pixels sont bornés ; les poids sont locaux, les sessions
CPU sont vérifiées et l'annulation vise le seul groupe de processus créé.
La configuration observée est distincte du nombre de réparations réellement
effectuées. Les tests de processus simulés et un succès régional doivent être
complétés par les contrôles Linux réels et le parcours complet de publication.

Quatre limites restent explicites :

1. **Diagrammes :** conserver une image et sa légende ne prouve pas les relations
   de ses flèches, ni leur disponibilité dans un outil multimodal du modèle.
2. **Étiquettes :** une courte étiquette mal reconnue peut rester incorrecte ;
   elle n'est pas réécrite par un dictionnaire métier ou une supposition.
3. **Langues :** le moteur secondaire est anglais et le garde de négations est
   fini pour le français, l'anglais et l'allemand. L'accord de deux moteurs ne
   vaut pas validation multilingue ou sémantique universelle.
4. **Mathématiques :** l'OCR général peut classer une formule comme du code et
   perdre des exposants, signes ou fractions. Un modèle spécialisé qui réussit
   sur une région choisie manuellement ne démontre pas une détection automatique
   correcte, ni une publication fidèle de toute la page. Un candidat de modèle
   reste une nouvelle observation à qualifier, jamais une vérité substituée
   silencieusement à la source.

Le détail des réglages et de la provenance régionale est dans le
[README du sidecar](../../infra/docling-sidecar/README.md). Les preuves RED/GREEN,
comparaisons visuelles et qualifications réelles sont conservées séparément dans
les campagnes `artifacts/ingestion-fidelity-20261026` et
`artifacts/ingestion-fidelity-fixes-20261026`, avec leur périmètre et leurs limites.
