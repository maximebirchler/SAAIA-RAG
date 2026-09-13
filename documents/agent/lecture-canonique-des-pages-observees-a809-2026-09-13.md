# Lecture des pages d'une source observée — A809, 13 septembre 2026

Le planning reste TESTE_NON_APPROUVE après A808 : sept appels réussis, mais aucune des vingt cellules livrée. Les recherches retrouvent séparément des titres et des préparations. Les observations du modèle n'exposaient pas leurs pages physiques ; le modèle ne pouvait demander qu'une nouvelle recherche par mots-clés, susceptible de retrouver le même titre.

## Comportement ajouté

Les observations exposent maintenant `physicalPageStart` et `physicalPageEnd` issus des références canoniques. Ces coordonnées désignent les pages physiques à partir de 1, et peuvent différer des numéros imprimés dans un index.

Le contrôleur de recherche, le rédacteur et le critique disposent de `read_source`. Dans leur objet JSON non final, une action précise `operation: read_source`, un `sourceKey` copié des observations, `pageStart`, `pageEnd` et `topK`. Cette lecture porte sur quatre pages consécutives au maximum. Le backend résout lui-même le document et sa révision depuis le handle observé. Il refuse une source inconnue, une autre révision ou un autre chemin. Le modèle ne reçoit pas les identifiants privés, révisions, noms ou chemins de fichiers.

L'outil lit directement les `retrieval_chunks` de cette révision et du tenant, dans l'ordre des pages et des chunks. Il ne passe pas par le classement vectoriel ou lexical. Chaque chunk conserve sa référence et fait l'objet de la revalidation normale ; le hash et le chemin doivent correspondre à la source observée. Si la fenêtre contient davantage de chunks que `topK`, l'outil retourne une erreur explicite au lieu de présenter une lecture tronquée comme complète.

Le nombre d'outils, leur capacité concurrente, leur temps, le nombre de preuves, le budget JSON complet et la réserve des appels finaux continuent de s'appliquer. Les événements utilisent la famille persistée existante `source_backed_canonical_search`, avec l'opération explicite dans la requête : aucune migration ni ingestion supplémentaire. Une fenêtre vide ne constitue pas une preuve d'absence dans tout le corpus.

Les lectures demandées pendant les revues initiales reçoivent aussi la priorité lors des revues suivantes et de la rédaction. Le focus conserve au plus trois observations par action et vingt au total. Pour une lecture, il garde si disponible un fragment de titre/navigation à la première page et préfère ensuite les corps substantiels les plus longs. Le document, la révision et les pages restent filtrés. Ce classement aide à réunir titre et contenu ; il ne valide pas leur adéquation sémantique.

## Preuves

- Sept contrôles de protocole : six échecs avant la correction, puis sept réussites. Ils vérifient la lecture sans requête lexicale, les pages visibles, le scope privé résolu, et le refus de pages invalides, inversées, absentes, de mauvais type ou trop éloignées.
- Cinq contrôles du gateway réel refusent un document, une révision ou un chemin non observé, une fenêtre excessive et une opération inconnue, avant tout accès à la base. La capacité concurrente est libérée.
- Un contrôle supplémentaire vérifie que le focus garde le titre avec le corps et exclut une autre révision. La première fixture sans titre exact ne caractérisait pas un fragment de navigation et a été corrigée ; cet échec intermédiaire est conservé.
- La suite backend complète termine avec 2 290 réussites, aucun échec et trois tests live ignorés (`canonical-page-full-backend-final.trx`).
- Le replay `meal-a809-canonical-page-read-proof.v2.json` exécute le gateway et le resolver réels sur PostgreSQL en lecture seule. Pour les deux anciens choix C13, omelette pages 61–62, et C19, fruits en beignets pages 65–66, il retrouve six chunks canoniques par fenêtre et le corps substantiel à la page suivante. Le focus garde la page du titre et la préparation. Une limite d'un résultat déclenche l'erreur de dépassement attendue. La fabrique HTTP rejette tout appel : aucun moteur HTTP ni LLM n'est utilisé dans cette preuve.

Ces preuves portent sur deux erreurs connues et sur le mécanisme d'outil. Elles ne constituent ni un nouveau score aveugle ni une réponse de Terra ni l'approbation du planning. Un essai réel sur un commit propre doit encore vérifier l'utilisation de l'outil et les vingt repas distincts avec leurs références exactes. Aucun nouvel achat n'est autorisé ; le solde calculé avant cet essai reste 1,09225720 USD.

## Essai réel et cause des lectures vides

Sur `85c32649bf0a3c9d7f08df6c7a5189b9ff1f934e`, le job `a52e3688-7829-4dbf-85e7-9ebe6bde4b75` utilise effectivement `read_source` trois fois, en plus de 24 recherches. Les trois fenêtres demandées sont 118–121, 128–131 et 132–135 dans la même source. Elles retournent zéro chunk. Le job termine avec une insuffisance, une affirmation négative liée à cinq références et aucune cellule de planning. Les sept appels réussissent ; coût 0,33889800 USD, durée 79,121 secondes. La capture durable est conservée avant l'arrêt des processus ; le postflight confirme le commit stable, le checkout propre et les ports libérés.

L'inventaire SQL en lecture seule constate que cette révision ne possède que 37 chunks, aux pages physiques indexées 1–22. Son sommaire, aux pages physiques 3–4, contient des recettes associées à des numéros imprimés plus élevés. Terra a utilisé ces numéros comme coordonnées physiques. Le statut `indexed`, la version 74 et la révision attendue sont corrects : le filtre de statut n'explique pas les lectures vides. L'étendue physique de la source n'était pas fournie au modèle et le retour d'outil n'expliquait pas cette erreur de coordonnées. A810 traite ces deux manques sans ingestion ni changement des critères d'acceptation. Le solde calculé après cet essai est 0,75335920 USD ; le produit reste TESTE_NON_APPROUVE.
