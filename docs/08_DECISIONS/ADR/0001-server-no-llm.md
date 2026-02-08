# ADR-0001 — Serveur sans LLM (CDC v2.7)

## Statut
Accepté.

## Contexte
Objectif : on-prem, confidentialité, éviter la complexité de scaling côté serveur.

## Décision
- Le serveur ne génère pas la réponse finale.
- Le serveur fournit uniquement retrieval + stockage + ingestion.
- Le client embarque un LLM local (OpenAI-compatible) pour planification + synthèse.

## Conséquences
- Charge GPU/CPU distribuée sur les postes clients.
- Le serveur reste plus simple et robuste.
