# Prompt — 01 Implémenter UN jalon (commit par commit) — STRICT

Tu implémentes **exactement UN** jalon (ou un sous-jalon explicitement nommé).

## CONTRAINTES ABSOLUES (anti-erreurs)
1) **INTERDIT** de supprimer ou renommer des fichiers existants de `docs/` sans instruction explicite.
   - Exemples interdits : supprimer `docs/CHANGELOG.md` ou `docs/04_CONTRACTS/API_RAG_SEARCH.md`.
   - Si tu penses devoir le faire : STOP et demande.
2) **INTERDIT** de créer des fichiers “scratch” (summary/start_here/final_…) n’importe où.
   - Si tu dois documenter : uniquement dans `docs/09_MILESTONES/<M#.#>/` et max 1–2 fichiers.
3) **INTERDIT** de créer des fichiers au root du repo.
4) **OBLIGATOIRE** : si tu modifies DB/schema → fournir une migration SQL + mettre à jour la doc DB.
5) **OBLIGATOIRE** : mettre à jour (dans le même commit) :
   - `docs/Plan_action_v2.7_status.md`
   - `docs/CHANGELOG.md` (ou le restaurer s’il manque)

## DOIT RESPECTER CDC v2.7
- Serveur = NO LLM
- Contrats = `docs/04_CONTRACTS/*`
- Erreurs = `docs/04_CONTRACTS/ERROR_MODEL.md`

## FORMAT DE SORTIE (obligatoire)
1) **Objectif & DoD**
2) **Plan d’implémentation** (3–8 étapes max)
3) **Fichiers modifiés/créés** (liste *exacte* avec paths)
4) **Implémentation** (courte, testable, sans dérive)
5) **Tests** (commandes exactes)
6) **Docs mises à jour** (status + changelog)
7) **Message de commit proposé** (préfixe: `server:` / `client:` / `docs:` / `infra:`)
8) **Auto-check** : confirmer “aucun fichier doc supprimé/renommé”, “aucun fichier root ajouté”

## NOTE
Ne jamais “refactor large” si ce n’est pas requis par le jalon.
