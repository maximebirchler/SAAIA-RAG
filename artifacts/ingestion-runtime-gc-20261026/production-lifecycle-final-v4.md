# Opérateur de reprise lifecycle puis remplacement défaillant

`run-production-lifecycle-final-v4.py` réutilise les cinq CID existants de la nouvelle racine Gold Production. Il ne crée aucun conteneur et ne modifie ni propriétaire, ni configuration, ni source produit, ni runner gelé. Les deux sorties doivent être neuves : `evidence/lifecycle-gc-final-v4` puis `evidence/failed-replacement-final-v4`. Le dossier opérateur est lui aussi neuf : `evidence/production-lifecycle-operator-final-v4`.

Trois fichiers sont nécessaires dans le catalogue : le nouvel opérateur, `production-lifecycle-final-v4-source-pins.json` et les anciennes sources qu'il référence, déjà présentes pour les campagnes principales. Le registre public final-v4 sélectionne uniquement `gold-production` ; aucun signing-input privé n'est lu. Le modèle, une clé de signature et un fournisseur payant ne sont jamais sollicités par cet opérateur.

Le plan sans `--execute` lit seulement le registre public et contrôle les chemins, les métadonnées et les empreintes explicitement fournies. Il ne lit ni owner, ni configuration, ni clé bootstrap, n'importe aucun helper opérationnel et ne lance aucun Docker ou HTTP. Les preuves futures peuvent encore être absentes à ce stade.

```text
python3 -B "$CAT/run-production-lifecycle-final-v4.py" \
  --registry "$CAT/production-config-signing-inputs-final-v4.json" \
  --role gold-production \
  --backend-image "$OBSERVED_IMAGE_ID" \
  --backend-assembly-sha256 "$OBSERVED_LINUX_DLL_SHA256" \
  --corpus-proof "$GOLD/evidence/gold-production-final-v1/execution-result.json" \
  --attempt-cancel
```

Après lecture de ce plan, ajouter `--execute` autorise explicitement les deux reprises des cinq CID. Les identités d'image et de DLL doivent être les observations réelles de root ; aucun tag ni identifiant prédit ne convient.

Avant le premier démarrage de chaque phase, l'opérateur exige le vrai PASS Gold signé, sa clôture, ses SHA owner/manifeste/configuration/binaire, le cas synthétique attendu et les cinq conteneurs possédés tous arrêtés. Il démarre PostgreSQL, Qdrant, TEI et Docling par leurs CID exacts, attend leurs véritables contrôles de santé, puis démarre le backend exact. Le DLL chargé et la vraie réponse `/ready` Production signée sont vérifiés avant le runner. La barrière de santé indique une disponibilité instantanée avant reprise, sans prétendre une conversion OCR échauffée ni une disponibilité permanente.

Chaque phase a son `finally` d'arrêt et une réinspection des cinq CID, même si le démarrage est partiel ou si le runner échoue. Aucun volume ni image n'est supprimé. Une pile déjà en cours d'exécution avant cette reprise est refusée sans arrêt opportuniste. La phase de remplacement n'est admise qu'après la nouvelle preuve lifecycle exacte, terminale, signée, avec GC tardif, générations distinctes, publication relue et source restaurée. Un exit code zéro seul ne suffit jamais.

Les dix-huit fichiers source épinglés sont contrôlés avant l'exécution, avant chaque phase et après sa clôture ; leur dérive empêche un PASS. Les journaux, owners et configurations restent privés. Le résumé de l'opérateur contient seulement ses états, dates, empreintes et contrôles de clôture. Le SHA du manifeste de pins est à vérifier indépendamment lors du transfert, avec celui de l'opérateur.

Les tests de cet opérateur utilisent uniquement des fichiers JSON synthétiques et de faux transports. Ils ne qualifient aucun service réel. Les runners métier existants restent les auteurs des preuves d'ingestion, de GC, de remplacement et d'annulation. `--attempt-cancel` demande la tentative bornée déjà prévue par le runner : une fenêtre manquée reste `NON_EXECUTED`, elle ne devient pas une annulation prouvée.

Un délai externe de 3 600 secondes borne chaque processus de runner. Si celui-ci dépasse cette borne, l'opérateur garde le résultat en échec et arrête les cinq CID ; il ne prétend pas avoir obtenu un accusé d'annulation ou zéro job actif qu'il n'a pas observé. Les données et volumes restent disponibles pour le diagnostic.
