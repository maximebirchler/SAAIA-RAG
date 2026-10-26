# Portabilité Linux des reprises Docling

Cet opérateur lance uniquement les 26 cas `DoclingClientTests.ConvertPdfAsync_transient_retry_*` déjà compilés dans l'image OCR finale v2. Les handlers HTTP sont faux et s'exécutent en mémoire. Aucun backend applicatif, Docling, PostgreSQL, Qdrant, modèle ou API externe n'est démarré.

Le lancement exige l'identifiant Docker immuable capturé par l'opérateur OCR, vérifie les deux DLL et l'inventaire embarqué avant et après exécution, utilise `--network none`, 2 CPU, 1 Gio, 256 PID, un seul bind de sortie et une échéance de 360 secondes. Le succès exige les 26 résultats `Passed`, les dix méthodes attendues avec leur multiplicité exacte, aucun skip/échec, les codes de sortie à zéro, aucune OOM et l'arrêt du conteneur exact possédé par cette campagne.

La commande sans `--execute` est un plan sans Docker. La commande réelle est réservée à l'opérateur principal sur Linux :

```text
python3 -B run-owned-docling-retry-linux-v1.py --execute --expected-image-id sha256:<identifiant_de_l_image_OCR_v2_observe>
```

Les journaux détaillés et les inspections sont privés. `operator-result.json` porte le verdict et les empreintes publiques ; il ne constitue pas une attestation par profiler des modules chargés. L'opérateur conserve le conteneur arrêté pour inspection, et ne supprime rien.

Le premier GREEN Windows des nouveaux tests avait donné **25/26**, avec un échec de la primitive `File.Move(overwrite)` du témoin de remplacement atomique. La fixture a ensuite utilisé `File.Replace`, sans élargir les droits de partage en écriture du produit. Ce premier essai reste un échec historique, pas un 26/26. Les preuves Windows corrigées appartiennent à leurs manifestes centraux ; cet opérateur complète leur portabilité Linux.

Les tests `test-operator.py` ne démarrent aucun processus Docker ou .NET : commandes, inspections, copie et démarrage sont simulés. Ils ne prouvent pas l'exécution réelle du conteneur ni des tests .NET.
