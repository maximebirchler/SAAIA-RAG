# SAAIA - Checklist tests UI et demo

Derniere mise a jour: 2026-05-26

## Objectif pour les tests de ce soir

Valider le parcours visible par un utilisateur avant la demo du jeudi 2026-05-28:

- connexion client vers backend;
- question simple avec reponse sourcée;
- question large avec synthese prudente;
- question ambigue avec clarification ou reponse prudente;
- cas sans source fiable sans invention;
- cartes sources lisibles: page, score, langue, qualite, OCR, hash court, content cards;
- comportement multilingue client sur au moins FR et EN.

## Etat serveur a verifier avant de tester

Depuis PowerShell:

```powershell
ssh saaia-server "curl -sk https://127.0.0.1:5122/ready"
```

Attendu:

- `ok: true`;
- `db: true`;
- `tei: true`;
- `qdrant: true`;
- `llm: server-ok` ou LLM backoffice non bloquant si degrade;
- `ocr_ready: true`;
- `rag_search_active: 0` ou faible.

## Questions de smoke test UI

Tester d'abord en francais:

1. `Bonjour, qui es-tu ?`
   - Attendu: reponse naturelle, pas de source obligatoire.

2. `Quels documents parlent de [sujet connu dans une categorie indexee] ?`
   - Attendu: liste de documents sourcée, pas de source arbitraire.

3. `Resume le document [nom ou numero visible dans les sources].`
   - Attendu: resume dans la langue demandee par l'utilisateur, sources visibles.

4. `Je veux préparer un menu pour la semaine avec les documents disponibles, tu peux m'aider ?`
   - Attendu: proposition prudente basee sur les sources, pas d'invention si corpus insuffisant.

5. `Tu es sur que ce document parle de [sujet precis] ?`
   - Attendu: verification sourcée; si doute, le dire clairement.

6. `Trouve-moi le fichier qui parle de quelque chose qui n'existe pas dans le corpus.`
   - Attendu: pas de faux positif confiant; clarification ou aucun document trouve.

Puis refaire un mini-run en anglais:

1. `Which documents mention [known topic]?`
2. `Summarize this document in English.`
3. `If the sources are weak, tell me clearly.`

## Ce qu'il faut regarder visuellement

- Le client reste connecte au backend.
- Pas de message brut JSON.
- Pas de "La reponse n'a pas pu etre generee" sur une question normale.
- Les cartes sources s'affichent et ouvrent le bon document.
- Les metadonnees ne debordent pas: langue, qualite page/document, OCR, signaux, content cards.
- Le LLM ne repete pas en boucle une question de clarification deja posee.
- Si la source est OCR faible ou fragmentaire, la reponse reste prudente.

## Perimetre demo recommande pour jeudi

Montrer un parcours maitrise:

1. connexion backend OK;
2. recherche de documents;
3. question precise sur un document;
4. synthese large sur une categorie;
5. affichage des sources et ouverture du document;
6. exemple multilingue court;
7. exemple de prudence: le systeme refuse d'inventer quand il n'a pas la source.

Eviter pour la demo:

- questions totalement aleatoires sur un corpus non valide;
- corpus en cours d'ingestion;
- categorie fraichement ajoutee mais non relue;
- comparaison complexe sur plusieurs documents non testes;
- promesse de SLA ou de performance definitive.

## Points a dire si on te demande comment ca marche

- Le chat utilisateur tourne avec le LLM local du client.
- Le backend fait retrieval, OCR, indexation, profils documentaires et sources.
- Le LLM serveur est backoffice: il enrichit les documents quand l'ingestion est idle, sans bloquer l'utilisateur.
- Les reponses doivent etre sourcées; si les sources sont faibles, SAAIA doit le dire.
