# Contre-revue sémantique de l'essai réel 02

Revue indépendante en lecture seule du 25 octobre 2026, fondée sur les quatre traces exactes de requêtes/réponses et le résultat terminal de cet essai. Aucun nouvel appel fournisseur, service ou test .NET n'a été exécuté par l'auteur de la revue. Aucun secret, contenu documentaire long ou identifiant de profil n'est reproduit ici.

## Résultat réellement établi

L'essai répond correctement à la question directe sur le nombre de personnes et la quantité de vin : **4 personnes et 1,5 litre**, avec une citation de la page physique 5. Les deux valeurs figurent explicitement dans la même preuve courante transmise au Writer. Le claim final cite exactement l'identifiant de cette preuve ; le résultat terminal conserve le document, sa révision, son hash source et les bornes 5–5. Le hash source correspond au témoin natif final de la même campagne.

La réponse ne cite ni un sommaire, ni une simple résolution de nom de document, ni une preuve d'une autre page. Elle n'ajoute aucune quantité ou condition absente. Un seul claim couvre les deux faits liés au même sujet, et son marqueur apparaît une seule fois dans la réponse. Aucun identifiant interne de preuve ou source n'est affiché dans le texte utilisateur.

Ce résultat valide cette extraction documentaire directe avec **gpt-5.6-luna**. Il ne valide pas un planning, une sélection multisource, une conversation de suivi, Terra ou l'ensemble du parcours WinUI.

## Ce que le modèle a reçu et fait

1. Le Planner reçoit la demande entière, un objectif de réponse courte et le document ciblé par le profil de cet essai. Il propose une requête qui rassemble encore trop de termes de la question. Cette recherche ne retourne aucune preuve.
2. Le premier contrôleur reçoit réellement ce résultat vide, les diagnostics de recherche et la résolution documentaire réussie. Il ne confond pas cette résolution avec une preuve. Il reformule vers les deux mots distinctifs du sujet, en conservant le document ciblé séparément dans `documentHint`.
3. Cette recherche reformulée retourne trois extraits courants de la page 5. Le contrôleur suivant reçoit leurs textes non tronqués et décide `ready`.
4. Le Writer reçoit ces trois mêmes extraits, la demande complète et le budget restant. Il utilise la première preuve, qui contient les deux informations demandées, puis rend le résultat `answered` conforme au contrat JSON.

Les prompts du contrôleur et du Writer présentent `search_corpus`, `read_source` et `find_source_text`, avec leurs paramètres et leurs conditions d'identité. Dans ce mode, il s'agit d'opérations choisies en JSON et exécutées par l'orchestrateur ; aucune fonction native n'est déclarée dans les requêtes HTTP de cet essai. La lecture `read_evidence` n'est pas disponible ici. Les catégories sont fournies comme résultats documentaires de classement et les extraits sont explicitement traités comme données non fiables pour les instructions.

La limite de quatre appels modèle est respectée. À l'entrée du Writer, la recherche documentaire dispose encore de capacité, mais il ne reste plus d'appel modèle pour une nouvelle recherche : `researchAllowed=false` et `remainingModelCalls=0` sont donc cohérents. Il n'y a pas de second appel caché, de retry ou de critic supplémentaire. Aucune nouvelle recherche identique n'est exécutée.

## Coût et répétitions observés

| Appel | Tokens d'entrée | Tokens de sortie | Coût comptabilisé USD |
| --- | ---: | ---: | ---: |
| Planner | 1414 | 61 | 0,000356 |
| Contrôleur 1 | 3106 | 55 | 0,0006872 |
| Contrôleur 2 | 4756 | 14 | 0,0005774 |
| Writer | 5780 | 108 | 0,0012856 |
| Total | 15056 | 238 | 0,0029062 |

Les quatre sommes concordent exactement avec les compteurs du résultat terminal. Chaque trace indique une seule tentative HTTP et son hash de requête est correct. Le cumul historique à la fin de l'essai 02 est de **0,0038304 USD**, premier essai inclus. Ces montants sont les valeurs comptabilisées par les traces et le résultat, sans lecture du solde fournisseur.

Les trois preuves ont des identités et textes distincts. Deux extraits se recouvrent cependant sur 367 caractères : la fenêtre physique réutilise un morceau de corps pour conserver ses métadonnées marginales. Ce recouvrement est visible et explique une partie de la redondance documentaire ; il ne crée ni nouvelle preuve indépendante ni double citation finale. Le volume de règles générales reste important pour une réponse courte, notamment dans le Writer, mais cet essai ne mesure pas l'effet d'une réduction des prompts. Il n'autorise donc pas à retirer ces règles sans comparaison causale.

## Défaut matériel découvert pendant cette revue

La troisième preuve provient d'une fenêtre `physical_page_context_v1`, dont la composition canonique indique un suffixe physique sans association de section affirmée. Ces métadonnées existent dans la projection, mais **ne parviennent pas au modèle** dans cet essai : les objets envoyés ont `sourceStructure=null`, sans type, composition ou marqueur de relation physique non attribuée.

La cause est localisée dans le transport, pas dans la réponse produite : le résolveur et son contrat de structure ne relisent et ne transportent que les anciens champs section/heading/voisinage ; le mapper de prompt ne peut donc pas exposer la portée physique. La prudence ajoutée aux entrées contextuelles de l'index ne suffit pas lorsque le modèle reçoit directement le texte canonique d'un chunk.

La réponse de cet essai demeure étayée : elle cite la première preuve, pas cette fenêtre marginale. Le défaut doit néanmoins être corrigé avant de demander au modèle d'attribuer des qualificatifs marginaux à des objets ou de préparer un planning. Les métadonnées physiques doivent rester séparées des faits et ne doivent pas fabriquer un titre, une identité ou une relation sémantique. Des tests RED de résolveur et de payload ont été préparés avant la correction ; leur résultat et le correctif seront documentés dans une preuve distincte. Ce rapport conserve l'état réellement envoyé lors de l'essai 02.

Un second écart de transport est mesuré : les trois textes du prompt sont égaux aux chunks natifs après normalisation des espaces, mais contiennent au total 46 caractères CR supplémentaires. Aucune valeur factuelle n'est altérée dans cet essai. Le chemin SQL/retours à la ligne et la vérification des checksums sont examinés séparément ; cet écart n'est pas présenté comme déjà corrigé ici.

## Portée de la réussite

La reformulation après un résultat vide, le passage des nouvelles observations au contrôleur suivant et le grounding de la réponse sont démontrés dans cet essai. La première recherche vide montre aussi que le contrat de requêtes compactes ne garantit pas encore que le Planner choisira d'emblée la meilleure formulation.

Le document est imposé par le profil du cas ; ce résultat ne prouve pas le choix autonome du bon document par le routeur WinUI. Le corpus natif et ses pages sélectionnées ne constituent pas une ingestion exhaustive du worker. Le substitut de l'index vectoriel et le reranking non exécuté ne sont pas validés par le succès du chemin sparse. La validation du planning reste à réaliser après fermeture du défaut de transmission décrit ci-dessus.
