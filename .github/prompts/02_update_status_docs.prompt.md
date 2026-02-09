# Prompt — 02 Mettre à jour la doc de statut après un commit — STRICT

Objectif : mettre à jour proprement :
- `docs/Plan_action_v2.7_status.md`
- `docs/CHANGELOG.md`

## CONTRAINTES ABSOLUES
- **NE CRÉE AUCUN NOUVEAU FICHIER**
- **NE MODIFIE QUE** les 2 fichiers ci-dessus.
- N’écris aucun brouillon ailleurs (pas de .txt, pas de “summary”, pas de “start here”).
- UTF-8 uniquement.

## Contenu attendu
Dans `Plan_action_v2.7_status.md` :
- mentionner le commit (hash si connu) + jalon
- indiquer “vérifié” vs “non vérifié”
- lister les tests exécutés (même si c’est “curl OK”)

Dans `CHANGELOG.md` :
- ajouter une entrée (date, titre, bullets)
- inclure fichiers touchés + impact API (none/compatible/breaking)
