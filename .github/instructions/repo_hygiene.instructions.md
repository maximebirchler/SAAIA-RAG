---
applyTo: "**"
---

# Repo hygiene — règles globales (OBLIGATOIRE)

## 1) Zéro pollution au root

**NE CRÉE JAMAIS** de fichiers au root du repo pour des "résumés", "start here", "instructions", "final summary", etc.

Les seuls fichiers autorisés au root sont ceux déjà prévus :
- `README.md` (doc projet)
- `SOLUTION.sln` (entrypoint)
- `.gitignore`
- `.github/` (config Copilot + prompts)
- `docs/` (documentation)

### Exception contrôlée : Artefacts de jalon

Si tu dois créer un artefact de documentation pour un jalon **en cours de développement** :
- Place-le sous `docs/09_MILESTONES/<M#.#>/`
- Limite-toi à **1–2 fichiers maximum** (ex: `README.md`, `NOTES.md`)
- **Archive-le ou supprime-le** avant de finaliser le commit

### Anti-pattern à éviter

❌ `QUICK_START.txt`  
❌ `FINAL_SUMMARY.txt`  
❌ `START_HERE_*.md`  
❌ `COMMIT_INSTRUCTIONS.txt`  
❌ `*_SUMMARY.txt`  
❌ `*_COMPLETE.txt`  
❌ `README_*_UPDATE_STATUS_DOCS.md`  

## 2) Docs = modifications ciblées

Quand on te demande une mise à jour docs (ex: "update status + changelog") :

**INTERDIT :**
- ❌ Créer de nouveaux fichiers de support
- ❌ Créer de fichiers "résumé" ou "instructo"

**AUTORISÉ :**
- ✅ Modifier `docs/Plan_action_v2.7_status.md`
- ✅ Modifier `docs/CHANGELOG.md`
- ✅ Modifier `.github/prompts/*.md`
- ✅ Si résumé nécessaire : faire dans le chat, **pas dans un fichier**

## 3) Zones de modification autorisées

```
.github/
├── copilot-instructions.md (✅ modifiable)
├── instructions/
│   └── repo_hygiene.instructions.md (✅ nouvelles instructions)
└── prompts/
    ├── 01_implement_one_milestone.prompt.md (✅ modifiable)
    ├── 02_update_status_docs.prompt.md (✅ modifiable)
    └── ...

docs/
├── Plan_action_v2.7_status.md (✅ modifiable)
├── CHANGELOG.md (✅ modifiable)
├── CDC_v2.7.md (✅ modifiable si bug)
├── 04_CONTRACTS/ (✅ modifiable)
├── 09_MILESTONES/ (✅ artefacts temporaires de jalon)
└── ...

.gitignore (✅ modifiable pour exclure artefacts)
```

## 4) Git discipline

- **Évite `git add -A`** : préfère staging ciblé (fichiers ou `-p`)
- **Ne commit jamais** :
  - `.vs/`, `bin/`, `obj/`, `TestResults/` (déjà dans .gitignore)
  - Fichiers modèles GGUF, caches, build artifacts
  - Fichiers "résumé" / "scratch" du root
- **Commit message** : utilise le format `<type>: <subject>` (ex: `docs: update status`, `server: M2.1 RequestId`)

## 5) Workflow agent IA recommandé

Quand tu dois mettre à jour la doc :

1. **Lis** les documents existants (`docs/00_README.md`, `docs/Plan_action_v2.7.md`, etc.)
2. **Modifie** UNIQUEMENT les fichiers cibles (`.md` dans `docs/` ou `.github/`)
3. **Résume** tout travail temporaire directement dans le chat (pas de fichier)
4. **Commit** avec un message clair et une liste de fichiers modifiés
5. **Nettoie** les artefacts scratch s'il y en a eu

---

## 6) Exemple de bon workflow

```
Task: "Update docs/Plan_action_v2.7_status.md"

✅ CORRECT:
1. Modifie docs/Plan_action_v2.7_status.md
2. Modifie docs/CHANGELOG.md
3. Propose le commit via chat (pas de fichier "résumé")
4. Message: "docs: update status (M0.1 FAIT, M5.1 correction)"

❌ INCORRECT:
1. Crée COMMIT_INSTRUCTIONS.txt
2. Crée FINAL_SUMMARY.txt
3. Crée README_UPDATE_STATUS.md
4. Pollue le root
5. Commit mal organisé
```

---

## 7) Signes d'alerte

Si tu te surprends à faire ceci, ARRÊTE immédiatement :

- ❌ Créer un fichier "résumé" au root
- ❌ Créer un fichier "start here" au root
- ❌ Créer un fichier "commit instructions" au root
- ❌ Ajouter des artefacts "finaux" au root
- ❌ Utiliser `git add -A` sans vérifier

Préfère :
- ✅ Modifier les fichiers cibles uniquement
- ✅ Résumer dans le chat
- ✅ `git add <path>` ou `git add -p`

---

## 8) Révision régulière

- Avant chaque commit, **vérifier le root** : ne doit contenir que fichiers autorisés
- Après chaque commit, **vérifier les fichiers modifiés** : `git log --name-only -1`
- Si artefacts detectés, **nettoyer immédiatement** avant push

---

**Appliqué depuis :** 2026-02-08
**Version :** 1.0
**Obligatoire pour :** Tous les prompts Copilot
