# Propriété des jobs de validation — A818, 13 septembre 2026

## Problème et comportement obtenu

Le client de test donne à chaque contexte un GUID utilisateur neuf. L'ancien
garde interrogeait `user_id='automated-validation'`, qui est le libellé de la
session et non ce GUID. Il pouvait donc annoncer zéro job pendant qu'un job
réel du test restait en attente ou en cours. Les inventaires historiques de
jobs payants identifiés par UUID restent distincts de ce faux contrôle.

Le client de validation avancée écrit désormais son véritable GUID dans un
manifeste JSONL absolu, ouvert sans création implicite et flushé sur disque,
avant toute requête HTTP. Chaque contexte conserve une identité différente :
aucun partage de mémoire entre cas n'est ajouté. Un fichier absent ou une
écriture impossible arrête le cas avant la création d'un job.

Le runner crée un manifeste neuf dans son répertoire d'artefacts et le passe
au client. À la clôture, après arrêt de son backend, le garde verrouille et
annule uniquement les jobs non terminaux des GUID effectivement enregistrés.
Le contrôle legacy par libellé reste disponible uniquement en lecture seule.
Son utilisation avec `--cancel` est refusée avant connexion à la base.

Le rapport v2 indique explicitement `emptyOwnerScope` et
`globalQueueAudit=false`. Un manifeste initial vide ne certifie donc jamais
l'absence de jobs historiques ou appartenant à d'autres campagnes. Les GUID
ne contiennent ni clés, ni demandes utilisateur, ni contenu documentaire.
Le manifeste est un fichier local du runner, pas une autorisation produit.

## Vérification exécutée

- Client : cinq contrôles ciblés passent, dont identité différente par contexte,
  enregistrement préalable et refus d'un fichier absent.
- PostgreSQL isolé réel : trois contrôles passent. Deux jobs appartenant au
  GUID enregistré sont annulés ; les jobs de deux autres propriétaires restent
  en attente. Le même test reproduit le faux zéro de l'ancien libellé. Un scope
  vide ne modifie pas la file ; un libellé legacy ne peut pas annuler.
- Suite client Release : 2 338 réussites, zéro échec, un test live ignoré.
- Suite backend Release : 2 302 réussites, zéro échec, trois tests live ignorés.
  Les contrôles PostgreSQL ci-dessus sont exécutés séparément avec une base
  réelle ; les suites ordinaires seules ne suffiraient pas à les prouver.
- Parseur PowerShell des deux runners et `git diff --check` : passent.
- PostgreSQL temporaire arrêté : zéro listener restant sur 55432, fichier de
  mot de passe supprimé, environnement du processus restauré.
- Aucun appel de modèle ni coût API nouveau dans cette correction.

Preuves : `artifacts/reprise-pc-20260908/a815-completion-audit-20260913/`
(`ownership-a818-*.trx`, logs) et
`artifacts/reprise-pc-20260908/a671-backend-postgres/a818-owner-guard-final-20260913/`
(`backend-real-postgres.trx`, `resource-shutdown.json`).

## Portée et suite

Cette correction concerne exclusivement l'instrumentation des validations.
Elle ne modifie ni le routage du modèle, ni ses consignes, ni les preuves du
RAG, ni le client WinUI de production. Les GUID frais constituent le scope de
propriété ; ce garde local ne remplace pas l'authentification tenant du produit.
Les anciennes captures figées ne sont pas modifiées. Les anciens runners
figés utilisant l'annulation legacy doivent être remplacés par une copie du
runner source actuel avant une nouvelle campagne.

Le planning autonome reste **TESTE_NON_APPROUVE**. La comparaison des stratégies
génériques d'exploration et de conservation des preuves constitue la prochaine
phase ; une rédaction isolée acceptée n'est pas une validation du produit.
