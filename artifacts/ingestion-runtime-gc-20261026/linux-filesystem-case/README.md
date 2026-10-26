Ce paquet prépare un test réel du système de fichiers Linux. Les tests Python ne démarrent rien. Le script opérateur est sans exécution par défaut ; seul root lance la campagne sur le serveur isolé.

Prérequis : Python 3 standard, Docker CLI disponible, namespace possédé déjà créé, image de campagne OCR finale construite. L'image attendue est `saaia-ocr-e2e:ocr-e2e-4a1474b00206-final-v1`. L'opérateur fournit son imageID immuable lu dans le JSON de l'essai OCR ; le script compare le tag inspecté puis crée le conteneur avec cet ID, sans build ni pull.

```text
python3 run-owned-linux-case.py
python3 run-owned-linux-case.py --execute --expected-image-id sha256:<ID exact du JSON OCR>
```

Le résultat est écrit dans le nouveau dossier `linux-filesystem-case-4a1474b00206-final-v1` sous `/home/maxime/saaia-ingestion-qualification/4a1474b0-0206-41ae-9bf3-65f495bb8325`. Un dossier existant est refusé. Le processus opérateur impose umask 077 ; aucun `--user 1000` n'est ajouté au conteneur (le SDK a précédemment échoué avant VSTest avec cette surcharge). Les fichiers créés par le moteur sont récupérés via `docker cp <CID exact>:<fichier> -` et lecture d'un unique membre TAR régulier, sans extraire les chemins TAR ni lire directement les sorties root 0600.

Le conteneur n'a aucun réseau, port, appareil ou montage de données. Un seul bind de sortie, CPU 2, RAM 1 Gio, pids 256. Son entrypoint est `dotnet`, et VSTest sélectionne uniquement :

`SAAIA.Backend.Tests.IngestionDuplicateFileCaseIdentityTests.Real_Linux_case_distinct_files_same_size_and_mtime_keep_their_distinct_hashes_and_publication_candidates`

Le test crée trois fichiers synthétiques sous un répertoire temporaire GUID interne au conteneur et les supprime en finally. Il utilise le vrai cache SHA et le vrai planificateur de doublons, sur deux noms distingués uniquement par leur casse, avec taille et mtime identiques. Il ne charge pas ces octets comme un PDF et n'appelle pas le backend, PostgreSQL, Qdrant, OCR, Docling ou un modèle.

Le verdict exige 1 test défini et exécuté, exactement le nom/classe prévu, identité TRX cohérente, 1 PASS, aucun échec ni skip, exit VSTest/conteneur 0, absence d'OOM. Les DLL sont copiées et hashées avant et après l'essai : backend AnyCPU `AFE4D78E…8371B`, tests `1E92BA08…2F70B`, inventaire package-v3 `3901095a…f0a1`. Le SHA du backend Linux de l'image est enregistré séparément. Les chemins du snapshot doivent aussi apparaître dans COREHOST_TRACE ; ceci constitue une preuve des octets et de leur résolution, pas une attestation de modules par profiler.

Après 300 secondes au maximum d'attente VSTest, le finally réinspecte l'identité, l'image, les labels et l'isolation du CID exact avant tout stop. Un arrêt demandé est borné à 20 secondes Docker (40 secondes de timeout opérateur). Le conteneur arrêté est conservé pour inspection, sans commande globale de nettoyage. Une erreur de garde/arrêt rend le verdict négatif. Les commandes annexes ont chacune leur timeout ; la durée opérateur totale comprend ces contrôles en plus des 300 secondes du test.

Portée d'un PASS futur : vrai comportement du système de fichiers Linux pour ce cache et cette sélection de doublons, sur cette image et ces DLL. Ce n'est ni une validation du worker complet, ni de tous les systèmes de fichiers, ni une approbation Production signée. Aucun essai réel n'a été exécuté par l'auteur de ce paquet.
