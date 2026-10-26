# Complément retrieval sans LLM — proposition bornée

État : protocole proposé en lecture seule ; aucun service, API, SQL ou provider
exécuté par cet agent. Aucun inventaire de 106 requêtes n'est créé à l'aveugle.
Les runners actuels restent gelés.

## Couverture réellement construite par le code actuel

`canonical_search_checks.py:10–16` sélectionne le premier fait admissible de
chaque cas. `invoke-linux-ingestion.py:383–399` effectue une recherche positive,
deux contre-témoins et une lecture par chunk sélectionné pour ce seul fait.
Il s'agit donc de 12 questions factuelles Gold et une pour le livre, si les deux
campagnes passent, pas de 90+16 recherches positives différentes.

En revanche, `invoke-linux-ingestion.py:374–376` lit toutes les pages physiques
annotées et `verify_worker_publication.py:110` contrôle tous les faits dans ces
lectures, séparément des faits du bundle canonique. Il ne faut pas remplacer
« tous les faits sont conservés/lisibles » par « tous les faits sont retrouvés
par recherche ». Ce sont deux portes distinctes.

Inventaires d'annotations relus sans texte documentaire :

- `config/ingestion-acceptance-gold.v2.json` : 12 cas, 90 faits, aucun marqué
  `retrievalRequired=false`, sur 16 couples document/page distincts.
- Manifeste réel v2 existant : 1 PDF de 36 pages, 16 faits, aucun exclu du
  retrieval ; source SHA256
  `aa11e234fb3336ee76644958e888d83565e25ba875264ba75f7ce22b32d25c18`.
  Les deux faits titres ajoutés après observation d'un défaut restent des
  régressions déclarées, pas une évaluation aveugle.

## Porte A recommandée : rappel lexical exhaustif des annotations existantes

Cette porte est utile avant la reprise des tests LLM : elle ferme un trou réel
entre conservation des données et recherche. Elle ne nécessite aucune nouvelle
ingestion, écriture SQL, clé LLM, question générée ni réglage de modèle.

1. Réutiliser les deux namespaces réellement qualifiés, chacun avec ses CID,
   son manifeste, sa configuration et sa DLL inchangés. L'opérateur peut
   redémarrer les conteneurs arrêtés ; le test ne doit ni les construire ni les
   migrer à l'improviste. Attendre zéro job actif.
2. Capturer une seule baseline SQL READ ONLY des publications courantes : tenant,
   docId, revisionId, sourceHash, indexedVersion, plan actif, chunks checksummés
   et provenance canonique physique. Le cas scan modifié par lifecycle/recovery
   doit utiliser sa **version courante**, pas la version 1 d'une ancienne capture.
3. Parcourir directement les 90 et 16 annotations déjà hashées. Pour chaque
   annotation, envoyer sa chaîne exacte comme requête lexicale avec `docId`,
   `sourceBackedCanonical=true`, `pageStart=pageEnd=page annotée` et les limites
   fixes existantes (topK/maxPerDoc/maxPerPage=20). Une requête, pas de boucle de
   reformulation ou d'élargissement automatique des limites.
4. Exiger au moins un hit courant contenant le fait exact, avec docId/révision/
   sourceHash/chunkId/texte/pageStart/pageEnd conformes aux colonnes SQL. Vérifier
   la provenance physique avec les blocks/anchors canoniques du chunk : un chunk
   chevauchant plusieurs pages est légitime, mais la présence du même chiffre
   sur une page voisine ne peut pas prouver la page demandée.
5. Lire ce hit par `/documents/context?docId=...&chunkId=...` et exiger le même
   texte, chunkId, sourceHash et revisionId, `found` et `anchorFound`. Ne pas
   imposer pageStart==pageEnd pour un chunk multi-page ; vérifier le chevauchement
   et la provenance physique indépendante.
6. Effectuer une fois par document les deux contre-témoins déjà employés : UUID
   de document absent (zéro hit de tout retriever), puis nonce absent dans le
   document courant (zéro exact/sparse ; des voisins denses courants peuvent
   être renvoyés). Aucun faux « vide obligatoire » pour une distance dense.
7. Vérifier en fin que les identités SQL/source/révision/plan de la baseline
   n'ont pas changé et qu'aucun job nouveau n'a été admis. Le parcours n'écrit
   aucune donnée applicative ; les POST de recherche ne sont pas des admissions.

Budget explicite maximal : 106 recherches positives + 106 lectures + 26
recherches négatives = **238 appels locaux**, séquentiels, au plus 15 secondes
par appel et 15 minutes de délai global. Aucun retry de transport côté harnais.
Le backend conserve ses propres limites connues des retrievers ; leurs métriques
doivent être capturées et aucun retriever dégradé ne doit être ignoré. Un échec
doit être diagnostiqué sur sa capture, pas suivi d'une réingestion identique ou
de multiples campagnes sans changement causal.

Les captures détaillées et les chaînes d'annotations restent privées. Le résumé
public contient uniquement namespace/source-manifest hash, DLL/config hashes,
factId/page, rang et identités de la preuve, nombre de hits/lectures, checks
booléens, latences, codes d'échec, source stable et compteur d'admissions zéro.
Cette proposition n'ajoute pas de nouveau manifeste géant de questions : les
annotations existantes sont le seul inventaire source.

Les recherches déjà exécutées peuvent être réévaluées hors réseau pour leurs
factIds, mais une ancienne capture ne prouve pas une publication modifiée depuis
le lifecycle. Pour une porte cohérente sur l'état courant, un seul passage frais
de ces 238 appels est plus clair qu'un assemblage d'epochs incompatibles.

## Porte B mesurée séparément : quelques formulations naturelles

La porte A prouve un rappel lexical de faits connus, pas le ranking de toutes
les questions utilisateur. Un complément raisonnable est de figer **six
questions positives** avant leur exécution, plus deux questions réellement non
documentées : pression/tolérance, tableau, avertissement/négation, scan FR,
scan EN et une question du livre. Pour chaque positive, un humain ou un contrôle
indépendant du PDF établit à l'avance le docId, les factIds et les pages attendus.
Le texte des questions ne doit pas être copié d'une sortie du modèle ou du chunk.

Ce petit contrôle peut supprimer le filtre page en gardant le document choisi,
avec un topK fixe (par exemple 10) et des critères annoncés avant lecture des
résultats. Il doit publier rangs/latences et provenance physique, sans augmenter
topK après un échec pour annoncer un PASS rétroactif. Il ne faut pas multiplier
90 formulations artificielles à partir des réponses connues : cela n'ajouterait
pas une validation indépendante de la pertinence.

Les questions non documentées ne doivent pas exiger artificiellement zéro voisin
dense. Sans LLM/raisonneur, ce test peut prouver que les faits demandés ne sont
pas présents dans les preuves retournées et que les identités sont courantes ;
il ne peut pas qualifier à lui seul l'abstention conversationnelle du produit.
Si le ranking naturel échoue alors que le fait est bien indexé et retrouvable
lexicalement, il s'agit d'une limite retrieval à mesurer séparément, pas d'une
nouvelle preuve d'échec OCR ou d'une raison de consommer l'API LLM.

## Portée d'acceptation

Le PASS ingestion actuel, le PASS moteur Docling/OCR, le PASS lifecycle/GC et le
rappel lexical exhaustif ferment des contrats différents. La signature
Production manquante ne disparaît pas ; un runtime unsigned Development reste
un diagnostic réel et utile, avec son inéligibilité déclarée. Le GC observé ne
couvre que les plans/manifests enregistrés ; aucun nettoyage universel de vieux
points legacy non enregistrés n'est déduit de cette campagne.

Aucune de ces portes ne justifie « ingestion parfaite pour tout document ».
Elles fournissent une base reproductible avant la suite du produit, avec une
couverture source connue, des échecs conservés et une frontière claire entre
données conservées, retrouvées, lisibles et raisonnement final.
