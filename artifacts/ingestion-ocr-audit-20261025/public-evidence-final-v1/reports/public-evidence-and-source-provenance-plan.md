# Preuves publiques et lien avec le code testé

Cette proposition prépare notre revue interne avant le staging déjà autorisé
par l’utilisateur. Elle ne demande aucune nouvelle permission utilisateur.
L’orchestrateur réalise le staging ; cet agent n’effectue aucune opération Git,
aucun staging et aucun export de preuves réelles. La préparation ne lance ni
moteur, ni service, ni requête réseau et ne modifie aucun produit.

## Sélection et état réellement observé

La proposition `public-evidence-proposal-v*.json` la plus récente énumère les
rapports publics, résumés bornés et campagnes terminées par leurs chemins et
SHA256 exacts. Une modification ultérieure d’un octet impose une nouvelle
proposition ; les versions antérieures restent historiques.

Les RED restent RED. Les skips, anciennes suites incomplètes et dérives de
sources restent visibles. Les captures arrêtées en compilation sans compteurs
comportementaux/TRX utilisables ne sont pas projetées comme des tests exécutés.
Les enveloppes PostgreSQL et contrôles Python sont distingués des campagnes
TRX auxquelles ils renvoient. Une campagne encore active ne devient pas un
candidat par anticipation.

Les preuves actuellement disponibles comprennent le vrai worker E5/Qdrant sur
les douze PDF après corrections, et l’ingestion réelle séparée du PDF de
recettes de 36 pages. Leurs hashes de DLL et limites restent ceux de chaque
exécution. Elles ne valident ni un run unique sur un binaire final futur, ni la
génération d’un planning par un modèle. Les derniers tests/diagnostics et le
rapport final du parent sont ajoutés seulement une fois réellement terminés.

## Exporteur testé, export réel non exécuté

`prepare-public-evidence.py` vise un répertoire neuf de cette campagne. Sans
`--execute`, il n’écrit rien. Son dernier dry-run réel a écrit zéro fichier et
n’a créé aucun dossier d’export. Le script a été exécuté seulement sur des
fixtures synthétiques : **12 tests Python PASS, aucun skip** ; preuves et
limites dans `tests/public-evidence-export-v1/proof-v1.json` et
`public-evidence-export-safety-review-v1.md`. Aucun export de véritables preuves
n’a encore été réalisé par cet agent.

Lors d’une future exécution coordonnée par le parent, l’exporteur :

1. Vérifie les chemins relatifs, extensions, limites de répertoire et hashes
   explicites ; les chemins absolus et les composants `.`/`..` sont refusés.
   Les projets ne peuvent pas construire un chemin de sortie.
2. Copie seulement les rapports/résumés proposés et inventaires de sources.
3. Produit des manifestes publics conservant résultats, compteurs, identités
   d’assemblies et de sources, builds demandés, dérives et limites ; les champs
   capturés non prévus des cases ne sont pas recopiés.
4. Reconstruit le XML TRX depuis une liste explicite de champs de résultat,
   définitions de méthodes, IDs de run/test/exécution/liste, dates et compteurs.
   Ce n’est plus une simple suppression de sous-arbres : aucun nœud/attribut
   arbitraire, Output, environnement ou identité locale n’est transmis.
5. Expose les noms de méthode sans arguments, avec `originalTestNameSha256`
   calculé sur le nom original exact. Les variantes restent distinguables par
   SHA ; les cases du manifeste sont reliées aux noms projetés du TRX. Les
   hashes des TRX/manifestes originaux restent dans la provenance. Les
   compteurs de la projection doivent correspondre à ceux du manifeste.
6. Scanne les octets publics pour les motifs courants de clés/mots de passe,
   puis produit `selection.json` avec chemins et hashes des octets exportés.

Le scan ne compare aucun vrai secret et ne prouve pas l’absence de tout format
imaginable. Le source de l’exporteur déclenche conservativement son propre
motif sur la déclaration de regex de mot de passe ; il n’est pas candidat à sa
propre copie. Son SHA256 exact et sa preuve de tests restent référencés. Aucun
contournement de scan n’est ajouté pour rendre cette copie possible.

Les champs historiques `approvalGranted=false`/`PROPOSED_NOT_APPROVED` du
format signifient que l’outil ne délivre pas lui-même une approbation de
publication ou de produit. Ils ne constituent ni une demande de permission
supplémentaire ni un retrait de l’autorisation utilisateur de staging.

Sont exclus : private-observations/private*, local-fixture, runtime*,
dependencies*, environment.json, SQL, bases/data directories, modèles, stores
de clés, captures client/PDF/images, réponses brutes Docling et logs bruts.
Les TRX originales ne sont pas proposées au staging ; seules leurs projections
publiques bornées le sont. Les PDF et leurs annotations privées restent exclus.

## Préserver les octets des preuves

La règle est déjà présente dans `.gitattributes` :

```
artifacts/ingestion-ocr-audit-20261025/** -text
```

Elle préserve les octets des preuves de cette campagne malgré les règles LF
générales des sources JSON/Markdown/Python. Après le staging, l’orchestrateur
doit néanmoins comparer les hashes des blobs de l’index aux octets exportés.
La présence de la règle ne prouve pas, à elle seule, que chaque blob de l’index
est identique ; cet agent n’a réalisé ni staging ni lecture de l’index Git.

## Relier sources, DLL, exécutions et index

Un run final doit conserver des sources stables pendant build et tests,
sourceBefore/sourceAfter identiques et le SHA256 de la DLL effectivement
chargée. Le Git HEAD d’un checkout modifié ne remplace pas cette preuve. Un
run `--no-build` ne prouve pas à lui seul quels octets ont compilé la DLL.

Pour les sources, distinguer le hash des octets testés dans le working tree,
celui après l’éventuelle normalisation EOL Git et celui du blob réellement
staged. L’égalité après normalisation explique un delta de fins de ligne ;
elle ne prouve pas une recompilation depuis ce blob, car des littéraux
multilignes ou générateurs peuvent dépendre des octets physiques.

La normalisation des sources a été exécutée avant les builds concernés et sa
preuve est conservée dans `source-eol-normalization-v1.json`. La clôture finale
exige encore de rapprocher les sources et DLL des derniers runs de l’index
réellement préparé par l’orchestrateur. Les anciennes preuves gardent leur
statut historique et leur DLL d’origine si elles ne couvrent pas le dernier
état du code. Les pièces encore attendues figurent séparément dans la
proposition et dans la liste minimale ; aucun hash ni PASS n’est anticipé.
