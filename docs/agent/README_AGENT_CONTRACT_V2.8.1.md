# SAAIA – Agent Contract v2.8.1

Ce dossier contient le **contrat exécutable** (machine-readable) de l’agent IA RAG SAAIA.

Il est complémentaire au document de spécification humaine :

- **Tools Agent - Agent AI RAG.pdf** (référence fonctionnelle : intents, tools, règles, UI sources)
- **CDC Agent AI - RAG - V2.8.1.pdf** (référence produit)
- **Plan d'action - Agent IA RAG - V2.8.1.pdf** (roadmap)

## Pourquoi versionner ce contrat ?
- Garantir l’alignement **Client WinUI / Backend API / Tests**.
- Valider automatiquement les sorties JSON du **Router** et du **Writer**.
- Éviter les régressions (ex: confusion “count” vs “list”, langue ignorée, bypass du Writer).

## Fichiers
- `intent-catalog.v2.8.1.json` : liste canonique des intents.
- `tool-catalog.v2.8.1.json` : liste canonique des tools (user/admin/optional) + schémas d’args.
- `router.schema.v2.8.1.json` : schéma JSON strict du plan Router.
- `writer.schema.v2.8.1.json` : schéma JSON strict de la sortie Writer.

## Règles clés (verrouillées)
- **Aucune réponse user codée en dur** : le Router planifie, le Writer rédige toujours.
- **Inventaire user = documents indexés uniquement** ; admin = health + pending/missing/outdated.
- **Résumés stockés = admin-only**, stockés **dans la langue du document**. L’utilisateur reçoit :
  - le résumé stocké s’il existe, sinon un **live summary** (skim+anchors) **non stocké**.
- **Admin WinUI discret** : 5 clics sur le logo → login ; auth admin via `X-Admin-Key` (session-only).

## Versioning
Toute modification du CDC/Tools Agent impactant intents/tools/schemas ⇒ incrémenter la version (`v2.8.2`, etc.).
