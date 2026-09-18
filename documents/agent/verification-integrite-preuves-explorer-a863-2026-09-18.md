# A863 — vérification d'intégrité des preuves Candidate Explorer

## Verdict du palier

Le profil gelé ne se contente plus de demander les preuves privées A862. Après
un run réussi, il vérifie automatiquement que l'audit PostgreSQL, les traces
fournisseur et leurs empreintes décrivent exactement les jobs de la campagne.
Une capture absente, modifiée ou rattachée à un autre job rejette le postflight
avant la revue sémantique.

Aucun appel fournisseur n'a été exécuté. Le registre reste à 39,9943606 USD sur
40 USD et le produit reste `TESTE_NON_APPROUVE`.

## Chaîne vérifiée

`verify-candidate-explorer-evidence.ps1` lit quatre artefacts du runner :

- `advanced-job-guard-after.json` ;
- `private-advanced-job-audit.json` ;
- `private-development-traces-manifest.json` ;
- `resource-shutdown.json`.

Il vérifie ensuite :

- les versions de schéma et les marqueurs `containsPrivateCorpusMetadata` et
  `mustNotCommit` ;
- le SHA-256 de l'audit dans le garde et dans le rapport d'arrêt ;
- le SHA-256 du manifeste de traces dans le rapport d'arrêt ;
- le nombre exact de jobs enregistré par le profil ;
- les comptes de jobs, événements et checkpoints annoncés par le garde ;
- un statut `succeeded`, un résultat, un checkpoint et au moins un événement
  d'outil pour chaque job ;
- le schéma, la borne et l'existence d'au moins un candidat au corps vérifié
  dans chaque checkpoint ;
- la présence physique, la taille et le SHA-256 de chaque trace ;
- le confinement de chaque chemin de trace sous son répertoire privé ;
- l'identité du job portée par chaque trace ;
- une trace pour chaque job et les rôles `candidate-explorer`, `writer` et,
  lorsque le profil l'active, `critic`.

Le rapport public ne reprend ni identité de job, ni titre de candidat, ni
requête, ni contenu ou réponse fournisseur. Il publie seulement les empreintes
des deux conteneurs privés, les comptes, les rôles techniques et des métriques
agrégées du checkpoint. Son verdict
`PASS_PRIVATE_EVIDENCE_INTEGRITY_REQUIRES_SEMANTIC_REVIEW` signifie uniquement
que la preuve est intacte et complète ; il n'approuve pas la réponse.

## Artefacts également préservés lors d'un échec

La création du manifeste de traces a été déplacée dans le `finally` du runner.
Un essai interrompu conserve ainsi les traces déjà écrites avec leurs tailles
et empreintes, même si la réponse mécanique n'aboutit pas. Un essai déclaré
terminé exige toujours au moins une trace. L'erreur de capture est inscrite dans
`resource-shutdown.json` et ne transforme jamais un échec en réussite.

## Intégration au profil

Le profil exige maintenant `integrityVerificationRequired=true`. Son préflight
refuse un profil Explorer qui omet cette obligation. Après le retour réussi du
runner produit, le profil lance le vérificateur avec le nombre exact de cas ×
répétitions et l'obligation du Critic. Un verdict d'intégrité négatif fait
échouer la campagne avant sa revue humaine.

## Contrôles exécutés

Le harnais synthétique couvre cinq cas :

1. audit, checkpoints et trois rôles intacts : accepté pour revue sémantique ;
2. contenu d'une trace modifié après son manifeste : rejeté ;
3. trace portant l'identité d'un autre job : rejetée ;
4. checkpoint absent : rejeté ;
5. trace du Critic absente alors que le profil l'exige : rejetée.

Les cinq contrôles réussissent. Les scripts du vérificateur, de son test, du
runner produit et du profil passent l'analyse syntaxique PowerShell. Le profil
complet reconnaît l'obligation d'intégrité et reste bloqué avant réseau ; une
copie avec cette obligation désactivée reçoit
`candidate_explorer_evidence_capture_invalid`. Les deux préflights conservent
`externalCallMayHaveOccurred=false`.

La suite .NET complète n'a pas été relancée pour A863, qui ne modifie aucun
fichier .NET. La preuve A862 immédiatement antérieure reste 2 459 tests backend
réussis, zéro échec et trois live ignorés.

## Suite

Le prochain pilote disponible produira donc trois niveaux distincts : résultat
mécanique, intégrité des preuves privées, puis jugement sémantique humain. En cas
d'échec, les traces préservées permettent d'identifier l'étape réellement
responsable sans demander une seconde dépense pour reproduire aveuglément le
problème. En cas de réussite, ce verdict d'intégrité reste une condition
préalable aux répétitions deux et trois ; il ne remplace ni la vérification des
vingt propositions ni celle de leurs sources.
