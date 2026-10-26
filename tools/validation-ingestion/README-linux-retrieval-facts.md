# Rappel lexical de toutes les annotations — Linux, sans LLM

Le runner `invoke-linux-retrieval-facts.py` ne démarre/construit rien, ne seed
aucune donnée canonique, n'admet et n'annule aucun job. Il est NOEXEC par défaut.
L'opérateur prépare/redémarre le namespace possédé ; après `--execute`, le
runner vérifie les cinq CID et les arrête dans son `finally`, sans supprimer
volume, image, base ou preuve.

## Portée

La campagne Gold actuelle contient 90 annotations pour 12 documents ; le livre
réel contient 16 annotations pour un document. Le probe parcourt toutes les
annotations du manifeste complet du PASS précédent, sans exclusion silencieuse.
Il utilise chaque chaîne factuelle comme requête lexicale source/page. Il ne
prétend pas valider les formulations naturelles, l'abstention ou le raisonnement
conversationnel d'un LLM.

Le PASS corpus doit concerner ce même owner/runId, manifeste et ensemble de cas.
Les hashes de la configuration montée et de la DLL actuellement chargée sont
comparés aux voisins `runtime-ownership.json` et `backend-binary-identity.json`
de ce PASS. Les chemins restent sous la racine UUID exacte, sans symlink. Les
sources originales et copies de travail sont séparées et hashées avant/après.
Tous les providers/workers LLM, scanner et watcher doivent rester désactivés.

La publication SQL **courante** est observée : le scan peut avoir avancé après
lifecycle/cancellation/récupération. Le probe n'exige donc pas un seul job DONE
historique par document. Il exige zéro job actif, les sources attendues, des
révisions et plans actifs valides, les checksums de chunks et de bundle exacts,
et tous les faits sur leurs pages physiques dans le bundle canonique actuel.
Le texte d'un chunk ou sa metadata ne peut pas remplacer cette preuve physique.

## Budget et vérifications

- Une recherche positive par annotation, avec docId, sourceBackedCanonical,
  pageStart=pageEnd annotée, topK/maxPerDoc/maxPerPage=20.
- Une lecture du hit par chunkId avec identité doc/source/révision, contenu
  UTF8 exact et ancre trouvée. Une lecture déjà effectuée pour le même tuple
  docId/révision/chunkId/sourceHash peut être réutilisée ; les publications sont
  vérifiées stables en fin. Les facts suivants sont tout de même recherchés.
- Deux recherches négatives par document : UUID absent (zéro hit) et nonce
  absent (zéro exact/sparse, voisins denses actuels autorisés).
- Aucun retriever dégradé masqué ; hit/version/hash/texte/pages sont comparés
  aux colonnes SQL courantes, avec chevauchement de pages légitime.

Plafond strict du harnais : `2*faits + 2*documents`, soit 204 appels Gold et 34
livre, **238 au maximum**. Les tentatives échouées comptent aussi. Aucun retry
de transport, aucune requête `/ready` cachée ; la provenance vient du PASS réel
du même runtime et de sa continuité inspectée. Les API de recherche/lecture
actuelles sont les observations fonctionnelles nouvelles. Les limites propres
des retrievers backend restent distinctes de ce compteur HTTP du harnais.

Les deux exécutions utilisent le **même** `--campaign-deadline-utc`, futur,
avec timezone et au plus 15 minutes au premier lancement. Le probe n'entame
plus d'appel lorsque cette échéance est atteinte et borne les timeouts HTTP à
15 secondes et au temps restant. Le cleanup de sécurité peut prolonger la
fermeture au-delà de la fenêtre de mesure ; il reste nécessaire et n'autorise
aucune recherche supplémentaire. Le timestamp de fin réel permet de le voir.

## Commande

À lancer d'abord sans `--execute`. Les chemins sont ceux de la préparation
opérateur ; ne pas renommer des preuves historiques pour satisfaire la garde.

```bash
root=/home/maxime/saaia-ingestion-qualification/UUID_GOLD
python3 -B tools/validation-ingestion/invoke-linux-retrieval-facts.py \
  --owner "$root/owner.json" \
  --manifest "$root/corpus/gold/manifest.json" \
  --corpus-proof "$root/evidence/gold-final-v1/execution-result.json" \
  --source-directory "$root/corpus/gold/sources" \
  --output "$root/evidence/retrieval-facts-final-v1" \
  --api-key-file "$root/private/backend-api-key.txt" \
  --campaign-deadline-utc DATE_UTC_EXPLICITE
```

Répéter avec `--execute` pour Gold puis la recette dans son namespace UUID
distinct, en conservant la même échéance. L'opérateur choisit l'échéance au
départ, pas une nouvelle fenêtre après chaque échec. La clé bootstrap synthétique
est lue uniquement après Execute et les préflights ; aucune clé LLM n'intervient.

Les réponses détaillées et annotations restent dans `private-observations`.
`retrieval-facts-result.json` contient les factIds, hashes, pages, rangs,
identités, checks, latences et compteurs, sans texte de document ni requête brute.
Une nouvelle baseline/finale SQL prouve la stabilité des publications et de
l'historique jobs. Un active job inattendu n'est pas annulé ; la clôture échoue
et seuls les cinq CID possédés sont arrêtés.

Un préflight refusé avant l'entrée du try/finally nécessite, comme les autres
runners, le stop owned indépendant de l'opérateur. Un résultat unsigned
Development conserve son inéligibilité Production. Les tests unitaires de ce
probe utilisent de faux transports et des captures synthétiques ; ils ne sont
pas une preuve de ces 238 recherches réelles.
