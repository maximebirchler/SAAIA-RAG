# Protocole local du vrai worker — préparation

Ce document décrit le parcours prévu ; ce n'est pas un résultat d'exécution.
Les processus Docling/Qdrant sont fournis séparément par l'orchestrateur. Le
runner ne doit pas les arrêter ni accéder à une infrastructure distante.

1. Lire le manifeste Gold v2 et choisir explicitement les cas. Vérifier tous
   les hashes PDF avant lancement. Premier sous-ensemble : scan français
   intégral et document français natif/raster/natif.
2. Produire une configuration sans secret, sous le nouveau dossier de campagne.
   L'overlay ne peut toucher que `Ingestion`, `DocumentIntelligence` et `Rag`.
   Les URLs sont des origines HTTP à IP loopback, sans credentials ni chemin.
   Aucun provider LLM n'est activé. Le backend garde auth/PG synthétiques du
   bootstrap ; worker canonique activé, scanner/watcher/ancien OCR désactivés.
3. Démarrer un nouveau cluster PostgreSQL et le backend déjà compilé dans un
   dossier jamais utilisé. Vérifier l'ownership existant PID/date/exécutable/
   postmaster/data-directory/port. Ne toucher ni 5432 ni profil client habituel.
4. Constater que les tables métier sont vides. Copier les PDF sources, vérifier
   encore leur hash, puis appeler une seule fois `POST /ingestion/scan`.
   Aucun `INSERT` de page, unité, chunk, canonique ou document n'est permis.
5. Suivre les vrais jobs via API jusqu'à `done` ou à un état terminal d'échec.
   Conserver les réponses privées et les sondes du backend. Un job `failed`,
   `paused`, `canceled`, absent ou dépassant la durée bornée ne passe pas.
6. Lire SQL en transaction READ ONLY : document publié, révision/source exactes,
   chunks/checksums UTF-8, bundle canonique gzip et ancres. Décompresser le
   bundle et vérifier taille/hash avant toute évaluation. Contrôler séparément
   les faits dans chaque page canonique et dans le contexte de recherche.
7. Lire les vrais points Qdrant par lots d'IDs issus des chunks publiés. Exiger
   exactement chaque ID, payload `chunk_id` égal à l'ID et
   `ingestion_plan_fingerprint` égal au plan stocké dans le job. Collection
   nouvelle par campagne ; l'existence d'une collection ou un compteur seul
   ne vaut pas vérification des points.
8. Relire les pages par `/documents/context`, identité sous `document`, et
   comparer les faits exacts aux pages canoniques. Les chunks API sont entiers
   et peuvent chevaucher plusieurs pages : cela ne remplace pas le témoin de
   page physique.
9. En `finally`, demander l'annulation des jobs restants de ce cluster, constater
   leur état, puis arrêter seulement les helpers et PG possédés au moyen du
   stop existant. Conserver DB, sources, logs et rapports. Si un arrêt ne peut
   être prouvé, le résultat reste en échec avec nettoyage incomplet explicite.

L'adaptateur d'embeddings 3D déterministe peut exercer le protocole du worker et
le stockage Qdrant réel. Il ne valide ni TEI, ni le modèle d'embeddings, ni la
qualité de recherche dense : `vectorValidated=false` doit rester visible. Une
preuve distincte pourra valider ce dernier étage quand le vrai TEI sera prêt.
L'installation d'un moteur OCR et son endpoint `health` ne prouvent pas une
conversion ; les pages raster doivent réellement traverser la chaîne.
