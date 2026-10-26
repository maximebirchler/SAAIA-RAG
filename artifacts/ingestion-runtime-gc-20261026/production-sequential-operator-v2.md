# Opérateur séquentiel Production

Le script `run-production-sequential-v2.py` est un artefact opérateur, sans build, signature, SSH, changement de produit ni modification de runner. Il ne s'exécute pas à l'import. Sans `--execute`, il lit seulement le registre de préparation et décrit les phases.

Au moment de sa livraison, Gold manuel, lifecycle et remplacement échoué sont déjà PASS ; la recette est en cours et le scanner automatique est lancé séparément. **La sélection par défaut est donc watcher puis defaults uniquement.** Le script ne touche pas ces autres campagnes. Les cinq rôles existent dans le registre pour contrôler leur identité et permettre un usage explicite futur ; ne jamais sélectionner une phase déjà en cours dans un autre processus.

```text
python3 -B run-production-sequential-v2.py
python3 -B run-production-sequential-v2.py --role watcher --role defaults --execute
```

Le registre attendu est `/home/maxime/saaia-ingestion-qualification/48e5eba2-ea16-41da-98a7-65472c7f6dd6/production-config-signing-inputs-v1.json`, schéma `saaia-isolated-production-config-signing-inputs.v1`, liste prepared des cinq rôles. Chaque root UUID, profil et SHA de configuration sont contrôlés. Les corpus sont `corpus/gold/manifest.json` + `corpus/gold/sources` ou `corpus/recipe/manifest.json` + `corpus/recipe/sources`. Les profils TEI sont `tei-profile-frozen-v1.json`. Les trois AUTO utilisent leur propre `backend-binary-identity.json`, copie des octets de l'image finale identique ; aucune propriété de signature Development n'est héritée de cette copie de SHA.

L'image est `saaia-qual-backend:48e5eba2ea16-final-v3`, inspectée puis utilisée par son imageID immuable. Le bootstrap v4 et les runners relus sont épinglés par SHA dans l'opérateur. La configuration déjà signée n'est jamais régénérée. Les dépendances déjà possédées et running sont réutilisées. **Si les cinq conteneurs sont déjà running, aucun second appel de démarrage backend n'est effectué** : leurs identités et l'image sont contrôlées, puis la disponibilité signée est attendue.

Pour defaults, `fr-native-critical-v2.pdf` est hashé contre l'annotation Gold, copié exclusivement vers `documents/auto-a.pdf` avant création du backend, puis son mtime est comparé au StartedAt observé. Aucun fichier inconnu n'est écrasé ; l'ajout du fichier après un backend déjà démarré est refusé. Le reste des mutations est laissé au runner gelé.

La boucle `/ready` est bornée à 300 secondes par défaut (option 30–900), avec vérification des conteneurs possédés, absence de proxy et refus des redirections. La réponse doit attester la signature vérifiée, l'éligibilité Production, la provenance canonique et TEI768. Les runners vérifient ensuite leurs propres options/provenances et le champ effectif de concurrence. Une simple réponse HTTP200 ou une configuration nommée Production ne suffit pas.

Chaque runner AUTO a son timeout opérateur de 2400 secondes, incluant sa clôture ; les observations et délais internes restent ceux du runner gelé. Sorties : `evidence/automatic-watcher-production-v1` et `evidence/automatic-defaults-production-v1`. L'opérateur utilise un dossier distinct `evidence/production-operator-ROLE-v1` et refuse tout dossier de sortie existant. Il ne remplace aucune preuve passée et ne réessaie pas implicitement un résultat final.

Un PASS exige exit0, passed=true, deploymentQualificationPassed=true, qualificationMode=production et signature vérifiée dans le résultat réel. En finally, les CID enregistrés sont relus et validés par le bootstrap avant arrêt, dans l'ordre backend puis dépendances. Les ports Docker doivent être libérés ; aucun prune, rm global, volume, modèle ou namespace étranger n'est touché. Le runner conserve sa propre preuve de zéro job actif ; l'opérateur n'en invente pas si un timeout l'a interrompu. Les conteneurs arrêtés et données restent disponibles pour inspection.

Exécuter les phases séquentiellement et s'arrêter au premier échec. Les signatures privées restent sur le PC. Les logs détaillés sont privés, les résumés opérateur ne contiennent que rôle, IDs de run, SHA et verdicts. Aucun appel payant n'est introduit.

Cette version v2 garde la v1 intacte. Elle ajoute les SHA explicites du runner AUTO et des deux helpers de diagnostics correspondant au GREEN central 69/69. Le contrat accepte metrics objet et diagnostics omis/null/vides selon leurs types DTO ; une panne réelle ou un type incorrect ne devient pas sain. Les contrôles identité/faits/lecture ne changent pas.
