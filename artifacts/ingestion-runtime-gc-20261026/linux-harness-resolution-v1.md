# Harnais Linux réel et attestation TEI — résolution

Le harnais est implémenté et gelé. **91 tests Python purs passent, sans échec, erreur ou skip, avec sources inchangées pendant la suite finale.** Aucune exécution Linux, connexion SSH, requête HTTP réelle, commande Docker, lecture de secret, inférence, opération Git ou build .NET n’a été faite par cet agent. L’exécution réelle appartient à l’opérateur qui a préparé la pile isolée.

Le RED initial `tests/tei-harness-red-v1` comporte cinq tests : trois témoins passent et deux cas positifs TEI sont rejetés par le code antérieur. Ces deux erreurs causales sont l’attestation refusant un vrai profil `/info` TEI et le vérificateur refusant le mode `tei_cpu`. La capture RED n’avait pas d’inventaire source avant/après ; aucune stabilité source RED n’est prétendue. La suite finale `tests/linux-tei-harness-green-v3` fournit les compteurs et les deux inventaires identiques. Les passages verts précédents sont historiques.

## Contrats ajoutés

`embedding_attestation.py` sépare le profil Transformers du profil natif `saaia-tei-embedding-profile.v1`. Les valeurs attendues sont figées avant les requêtes. Les champs TEI `model_id`, `model_sha`, `version`, `max_input_length` et les autres champs réellement présents restent natifs : aucun champ propre au validateur Transformers n’est ajouté à `/info`. Si `model_sha` est réellement null, il reste null et une méthode explicitement distincte `owned_command_and_weights` devient nécessaire. Avec `--revision`, l’opérateur a observé une révision native non nulle sur sa nouvelle instance ; ce fait n’est pas rétroactivement appliqué à l’ancienne production.

La provenance des poids utilise l’image Docker réelle, les argv `--model-id`, `--revision` et `--huggingface-hub-cache`, le snapshot résolu à travers les binds inspectés et l’inventaire hashé. Les sous-montages plus spécifiques ne peuvent pas masquer un autre fichier derrière une copie correcte non utilisée. Les hashes et la liste exacte des fichiers sont contrôlés avant et après le passage. Le fichier ONNX exécuté par TEI CPU1.6 est distinct des safetensors de l’ancienne campagne Transformers. `config.json`, `tokenizer.json`, `1_Pooling/config.json` et les poids restent requis ; les autres fichiers présents, notamment `sentence_bert_config.json`, sont inventoriés. Un `tokenizer_config.json` absent n’est pas inventé ou téléchargé. La limite `kernelFileOpenTracingPerformed=false` est explicite.

`probe-local-vectors.py` accepte les deux moteurs sans les confondre. Pour TEI, il teste une entrée malformée et la politique de longueur déclarée, sans exiger que ce moteur générique applique les règles de préfixe du validateur. Les quatre sorties d’embeddings restent contrôlées pour leur modèle, cardinalité, index, dimension 768, normalisation et finitude. Les vecteurs constants, les ex aequo et les marges insuffisantes restent refusés sur trois permutations, dans le calcul cosinus puis Qdrant. Le scope de la preuve dit qu’il s’agit d’info native et de classement synthétique ; il ne prétend ni provenance des poids par le probe seul, ni ingestion ou rappel documentaire général. Les redirections HTTP et les proxys sont refusés.

`verify_worker_publication.py` accepte explicitement `tei_cpu` uniquement avec `embeddings_real=true`. Les contrôles canoniques, les checksums, les révisions, les ancres physiques, les contre-témoins de recherche, les IDs déterministes des points et les plans actifs restent inchangés. Ce nouveau libellé ne fait pas passer `vectorValidated` à true.

## Parcours Docker possédé

`invoke-linux-ingestion.py` consomme cinq conteneurs déjà préparés ; il ne fait aucun build, pull, installation, `compose up` ou SSH. Sans `--execute`, il ne lit ni clé, ni poids, ni configuration de déploiement, et ne contacte pas Docker ou HTTP. La racine et les chemins sont bornés à `/home/maxime/saaia-ingestion-qualification/<UUID>`. Chaque CID complet, image ID, label de projet/service/run, bind sans symlink, réseau, port loopback et limite CPU/mémoire est vérifié. Les modes privilégié/host PID/IPC et les demandes GPU/devices sont refusés. Le client impose le socket Unix Docker local et ne dépend pas d’un contexte distant.

Un bridge interne peut être exigé. L’exception `dedicated_bridge` reste explicite et produit une observation `externalEgressBlockedByDockerInternalNetwork=false` : elle ne devient pas une preuve de blocage d’egress. Aucun réseau d’un autre projet ne peut être attaché.

L’exécution exige une DB d’ingestion vide et des PDF correspondant aux hashes du manifeste. Elle copie uniquement les sources sélectionnées, déclenche le scan API, attend les vrais jobs terminés et capture la publication. Les SELECT restent dans des transactions READ ONLY, avec UTF8 via stdin, sans shell ni mot de passe dans la commande. Les checksums des quatre tables sont recalculés sur leur texte réellement stocké. Les lectures physiques, recherches source-backed, nonce sans correspondance lexicale, document absent et lectures depuis le hit sont contrôlés avec les mêmes vérificateurs que sous Windows. Aucun seed canonique n’est utilisé.

La DLL Linux réellement présente dans le conteneur est hashée. Les révisions effectives exposées par `/ready` doivent correspondre au profil/configuration, plutôt qu’à une simple valeur non vide. Pour Qdrant, l’image ID de configuration et un RepoDigest de manifeste inspecté sont deux types distincts, jamais assimilés arbitrairement.

## Production et diagnostic

`qualificationMode=production` exige le véritable environnement Production et une signature vérifiée. Le mode `development_diagnostic` exige l’environnement Development observé et reste `deploymentQualificationEligible=false`, `deploymentQualificationPassed=false`, même si les tests d’ingestion sont verts. Une configuration locale supplémentaire qui pourrait remplacer le fichier contrôlé est refusée. Le mode, la signature et la source de leurs observations sont conservés dans le résultat. L’absence actuelle de la clé de signature Production ne peut donc pas être masquée par un succès Development.

Les chemins LLM et transmissions externes sont explicitement désactivés avant l’admission. Aucune clé fournisseur ne doit exister dans le conteneur de test. La clé synthétique du backend n’est lue qu’après `--execute` et validation de l’ownership ; elle n’est jamais écrite dans les preuves publiques.

## Arrêt et limites de clôture

Après preuve de DB initialement vide, le `finally` n’annule que des jobs du corpus sélectionné et attend un état sans job actif. Il prévalide l’ensemble des ressources avant le premier stop et arrête les cinq CID exacts dans un ordre déterminé. Le verdict requiert simultanément zéro job actif, tous les conteneurs possédés arrêtés et absence de leurs bindings Docker publiés. Une observation fausse ou manquante empêche le succès. Le contrôle des bindings Docker ne prétend pas prouver l’absence d’un listener hôte étranger.

`stop-linux-runtime.py` est un stop indépendant avec les mêmes gardes et une frontière `--execute`. Aucun `prune`, `down -v`, retrait de volumes, effacement d’image, suppression de modèle ou suppression de preuve n’est réalisé. Les erreurs détaillées et les captures SQL/API restent privées ; les preuves publiques portent des états, compteurs, hashes et raisons de phase.

Les commandes et schémas d’entrée sont décrits dans `tools/validation-ingestion/README-linux.md`. Chaque nouvelle campagne exige un périmètre DB neuf ; le harnais ne tronque pas une base pour la réutiliser. Les captures et sorties ne doivent jamais écraser un passage précédent.

## Ce qui reste à exécuter

Le présent lot valide le code du harnais par transports simulés explicites. L’opérateur doit encore exécuter et conserver le vrai parcours backend/PG/Docling/TEI/Qdrant dans sa pile Linux, puis la campagne de documents et les contrôles de cycle de vie prévus. Une qualification Production reste distincte d’un diagnostic Development, de la petite sonde vectorielle et de l’acceptation générale du produit. Aucun planning de repas ni appel LLM payant ne fait partie de ce lot.
