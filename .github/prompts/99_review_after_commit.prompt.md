# Prompt — 99 Review après commit (auto-audit) — STRICT

Objectif : éviter les erreurs type “suppression de docs”, “migration oubliée”, “dérive du scope”.

## Checklist (doit être complète)
1) **Scope**
   - Le commit correspond-il à UN jalon ?
   - Y a-t-il du “bonus” non demandé ? (si oui -> proposer de split)

2) **Fichiers supprimés / renommés**
   - Vérifier qu’aucun fichier critique n’a été supprimé/renommé :
     - `docs/CHANGELOG.md`
     - `docs/00_README.md`
     - `docs/04_CONTRACTS/*`
   - Si suppression/rename : proposer rollback immédiat.

3) **Contrats**
   - Les docs de contrat existent et sont cohérentes :
     - `docs/04_CONTRACTS/README.md` ne référence pas un fichier manquant.
   - Si un contrat change : exemples JSON + tests correspondants.

4) **DB / migrations**
   - Toute nouvelle colonne/table => migration SQL présente
   - Index importants ajoutés si nécessaire
   - Doc DB mise à jour si impact.

5) **Tests**
   - build OK
   - smoke tests OK
   - endpoints touchés testés.

6) **Docs**
   - `docs/Plan_action_v2.7_status.md` mis à jour
   - `docs/CHANGELOG.md` mis à jour

7) **Prochain jalon**
   - proposer le prochain commit minimal (pas de grosse liste)
