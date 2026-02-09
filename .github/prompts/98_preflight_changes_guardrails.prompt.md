# Prompt — 98 Preflight guardrails (avant staging) — STRICT

Avant toute proposition de commit :

1) Lister les fichiers MODIFIÉS/CRÉÉS/SUPPRIMÉS (table).
2) Si un fichier est SUPPRIMÉ :
   - STOP
   - proposer une alternative (déprécation plutôt que suppression)
   - demander confirmation explicite.

3) Vérifier que le root du repo n’a pas de nouveaux fichiers.
4) Vérifier que `docs/CHANGELOG.md` existe.
5) Vérifier que `docs/04_CONTRACTS/README.md` ne référence pas de fichiers manquants.

Sortie attendue :
- Tableau “Change summary”
- Liste “Risques / corrections”
