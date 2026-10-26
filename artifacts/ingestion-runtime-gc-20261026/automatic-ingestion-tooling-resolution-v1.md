# Outillage de qualification automatique Linux

État : implémenté et testé hors ligne, en attente de contre-revue et d’exécution
réelle par l’opérateur. Aucun service, HTTP réel, SSH, .NET, Git ou appel LLM n’a
été exécuté par l’auteur. Les sources produit et les anciens runners corpus et
lifecycle ne sont pas modifiés par ce lot.

## Preuve hors ligne

`tests/automatic-ingestion-offline-v1/manifest.json` : 42 tests, 42 PASS,
0 échec, 0 erreur, 0 skip ; inventaire de 21 fichiers avant/après identique.
Il s’agit de 23 tests AUTO et 19 tests retrieval. Les transports du protocole
AUTO sont simulés, les copies atomiques utilisent uniquement des octets factices
dans des répertoires temporaires. Ce résultat ne qualifie aucun runtime Docker.

Les tests couvrent les trois parcours fonctionnels simulés, les trois profils
stricts, la garde LLM intacte, les chemins/tenants/jobs étrangers, la suppression
conditionnelle, les faits physiques indépendants, les UUID de points physiques,
la distinction watcher missing/scanner deleted, les deux workers réellement
running, les limites HTTP, le NOEXEC sans clé et les copies atomiques sans
écrasement d’un contenu inconnu. Le parcours combiné simulé sans observation de
deux workers reste false et garde la variante de course NON_EXECUTED.

## Parcours réel préparé

Trois namespaces neufs et cinq CID exacts par profil, préparés par l’opérateur :
scanner seul, watcher seul, combiné. Le modèle TEI natif est relié à ses argv,
son snapshot effectivement monté, ses poids/artefacts hashés et sa provenance
liée par le backend. La DLL/image finale est épinglée indépendamment. La
configuration est mesurée et hashée avant/après ; tous les LLM restent désactivés.

Les trois sources Gold ont des SHA distincts. A reçoit successivement la source
FR native, la deuxième source native, puis son original ; B et C sont ensuite
ajoutés sans doublon binaire simultané. Le cap scanner de 1 doit permettre leur
publication tout en conservant le témoin présent. Chaque publication contrôle
les faits physiques canoniques, les chunks, le manifeste scellé et tous les
vecteurs courants, puis une recherche littérale et la lecture exacte du chunk.

Le watcher seul doit conserver la publication et les vecteurs dans l’état
missing. Le scanner doit achever la suppression, désactiver le plan, collecter
les anciens points puis recréer une génération supérieure. Les vecteurs de B/C
sont comparés avant/après. Deux workers exigent deux leases w0/w1 running dans
un même SELECT ; une configuration à 2 seule ne suffit jamais. Le profil combiné
requiert cette observation pour son verdict global. La course du retour de
source après delete queued reste NON_EXECUTED si sa fenêtre n’est pas capturée.

Enfin, l’outil annule uniquement les jobs actifs des trois chemins connus,
arrête le producteur backend avant le dernier SELECT d’activité, puis arrête les
cinq CID exacts. DB, volumes, modèles et preuves sont conservés. Les seuls
résultats publics sont des identités, compteurs, SHA et verdicts ; les textes,
SQL, bundles, requêtes, payloads et vecteurs restent privés.

## Ajustement retrieval indépendant

La sonde retrieval neuve compare désormais les jobs historiques après tri par
`(docPath, jobId)`. L’ancien SELECT ordonne seulement par chemin, sans garantir
l’ordre de plusieurs générations du même document. Le tri supprime un faux
échec de stabilité ; il ne masque ni nouvelle entrée ni changement de statut.
Les UUID canoniques, chemins non vides, lignes malformées et doublons sont
contrôlés. Une fixture historique du vérificateur, dépourvue d’identifiant de job,
a été complétée avec la forme SQL réellement attendue ; les refus restent testés.

## Conditions opérateur et limites

Voir `tools/validation-ingestion/README-linux-automatic-ingestion.md` pour la
commande et les préconditions. Pour le combiné, A doit être copié avant le boot
du backend ; pour scanner/watcher seuls, documents et DB doivent être vides.
Les trois originaux restent séparés dans `sources/`. Les timings accélérés
2 s / grâce 5 s / debounce 250 ms sont un profil de campagne, pas les defaults
d’installation et pas un SLA. La limite de qualification est 1 800 s et chaque
phase 300 s maximum ; le cleanup borné est compté séparément. La convergence
locale de trois documents ne valide pas tous les événements réseau, overflow,
SMB/NFS, disque plein, millions de fichiers ou calcul OCR parallèle. Un runtime
Development non signé reste inéligible à la qualification de signature Production.
