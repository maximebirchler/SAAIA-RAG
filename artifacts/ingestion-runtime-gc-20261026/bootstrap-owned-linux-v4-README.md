# Bootstrap signé des trois namespaces automatiques

Cet opérateur de campagne ne remplace pas l'installateur. Il prépare une configuration de qualification Production sans transférer ni ouvrir la clé privée de signature. Aucun Docker, .NET, SSH ou service n'a été lancé lors de sa préparation ; les tests annexes utilisent des fichiers synthétiques temporaires et un Docker en mémoire.

## Préconditions

Le namespace cible et son owner existent déjà sous `/home/maxime/saaia-ingestion-qualification/<UUID>`. Le profil déclaré est scanner, watcher ou defaults ; le champ containers doit être vide pour une nouvelle préparation. Une campagne historique avec des CID ne peut pas être requalifiée par `--prepare-only`. Les chemins exacts des données/configuration, les identités synthétiques de base et collection, le réseau dédié et les quatre ports loopback sont contrôlés. Les liens symboliques et traversées sont refusés.

Les caches publics TEI et Docling doivent être déjà copiés sous ce nouveau root, non vides et accessibles aux UID des images. Le script ne télécharge aucun modèle et ne crée pas de cache vide pour prétendre être prêt. Il conserve les montages cache de v3 ; leur accessibilité et la correspondance exacte des poids sont vérifiées ensuite par le vrai démarrage et l'attestation du runner. Le catalogue est un autre namespace possédé dont les cinq images et la configuration ont déjà été qualifiées. Son template doit désactiver explicitement les LLM, CapabilityB et transmissions externes.

Aucune des quatre sorties préparées ne doit exister sans enregistrement v4 : `private/qualification-secrets.json`, `private/api-key.txt`, `private/postgres.env`, `deploy/deployment.config.json`. Le script les crée exclusivement, jamais par écrasement. Une signature préexistante sans préparation est également refusée. Un arrêt en milieu de préparation laisse les fichiers pour inspection ; il ne provoque pas une régénération automatique susceptible de signer d'autres octets.

## Séquence opérateur

Exécuter une fois pour chacun des trois roots, avec son profil réel et le catalogue choisi :

```text
python3 -B bootstrap-owned-linux-v4.py --root <ROOT> --catalog-root <CATALOG_ROOT> --admission-profile scanner --prepare-only
python3 -B bootstrap-owned-linux-v4.py --root <ROOT> --catalog-root <CATALOG_ROOT> --admission-profile scanner --prepare-only --execute
```

Sans `--execute`, seules les métadonnées owner sont lues ; aucun réseau Docker, secret ou configuration privée n'est lu/créé. `--prepare-only --backend-image` est contradictoire et refusé avant effet.

La préparation crée ou réutilise le réseau exact, après inspection du nom, ID, labels de projet/run, driver bridge, politique internal déclarée et absence de conteneur non enregistré. Elle ne remplace pas un réseau existant ni ne change sa politique silencieusement. Les quatre fichiers aléatoires/configuration sont générés une seule fois. Leur SHA, le networkId et les images de dépendances sont persistés dans `owner.bootstrapPreparation`. Le mode owner devient production avec état `config_prepared_waiting_signature` : cet état signifie prêt à signer, jamais signature ou déploiement déjà validé.

Copier ensuite **les octets exacts** des trois configurations dans un répertoire privé du PC. Les configurations contiennent les mots de passe bootstrap de ces namespaces ; elles ne sont pas des preuves publiques. Les signer localement avec ConfigSigner et le fichier de clé privée déjà validé par le coordinateur. Vérifier localement chaque signature avec la clé publique réellement présente dans TrustedKeyring. Ne pas convertir les fins de ligne ni réécrire le JSON. Retourner seulement chaque fichier public `deployment.config.sig` au bon root dans `deploy/`. La clé privée ne doit jamais être transférée au serveur, dans une variable Docker ni dans un artefact de preuve.

Le script ne prétend pas vérifier cryptographiquement Ed25519 avec la bibliothèque standard Python. Il contrôle la présence, la taille, l'encodage Base64 et la longueur Ed25519 du fichier public ; une signature de 64 octets arbitraires ne vaut donc pas validation. La vraie vérification est faite localement avec ConfigSigner, puis obligatoirement par SignedConfigLoader au démarrage Production. Le runner doit observer `/ready` avec `config_signature_verified=true` avant toute qualification Production.

Reprendre les dépendances, puis le backend, avec les mêmes paramètres root/catalogue/profil :

```text
python3 -B bootstrap-owned-linux-v4.py --root <ROOT> --catalog-root <CATALOG_ROOT> --admission-profile scanner --execute
python3 -B bootstrap-owned-linux-v4.py --root <ROOT> --catalog-root <CATALOG_ROOT> --admission-profile scanner --backend-image <TAG_FINAL_EXACT> --execute
```

Chaque reprise vérifie les mêmes octets config/private, le networkId et ses labels. Elle ne relit pas le template pour regénérer la configuration et n'ouvre aucun fichier de clé privée. Une signature doit être présente avant création du backend ; une fois liée, sa modification est refusée. Pour defaults, copier le PDF A prévu **avant le démarrage du backend**, après préparation et selon le protocole du runner, afin que son mtime précède le boot.

Le backend impose `ASPNETCORE_ENVIRONMENT=Production`, `DOTNET_ENVIRONMENT=Production`, `ConfigSignature__SignaturePath=/app/deploy/deployment.config.sig` et `ConfigSignature__AllowUnsignedInDevelopment=false`. Les dépendances doivent déjà être running ; aucun backend signé n'est lancé par-dessus un historique de développement. Les modes unsigned ne sont pas des options de cet opérateur.

## Reprise et limites d'infrastructure

Un verrou exclusif empêche deux opérateurs simultanés. Les conteneurs sont créés **sans être démarrés**, leur CID complet est persisté dans l'owner, puis seulement ils sont démarrés. Une interruption de start conserve donc un CID exact pour reprise/cleanup. Un conteneur créé et enregistré peut être démarré à la reprise ; un conteneur déjà running est contrôlé et réutilisé, sans duplication ; un conteneur exited n'est pas redémarré automatiquement.

Une interruption de Docker create ou une panne d'écriture de l'owner peut laisser un conteneur créé mais arrêté, non enregistré. Le script ne l'adopte pas par son seul nom : l'opérateur doit inspecter les labels/image/montages avant sa récupération ou suppression. Il ne peut pas devenir actif avant la persistance du CID dans le chemin normal. L'outil ne fait aucun nettoyage global, n'utilise pas compose down, ne supprime ni volumes ni modèles, et ne répare pas silencieusement un état partiel.

Les succès de bootstrap ne sont pas des succès de recette. Les limites CPU/RAM et moteurs restent celles de v3 ; aucun GPU, reboot, mise à niveau d'image, appel modèle ou réseau de production n'est introduit. Le champ `productionQualificationPassed` reste false dans sa sortie : seule la campagne réelle peut fournir ses preuves fonctionnelles et de signature.
