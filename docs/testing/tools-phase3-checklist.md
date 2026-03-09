# Tools smoke checklist — phase 3

## User tools
- Combien de documents sont sur le serveur ?
- Liste les documents Siemens
- Montre-moi l'arborescence des documents
- Donne-moi les statistiques du catalogue
- Quelle est la source du document 2 ?
- De quoi parle le document 2 ?
- Résume le document 2
- Recherche dans les résumés : sécurité
- Crée un export markdown avec le texte 'bonjour'
- Génère un support bundle
- Donne-moi le diagnostic de performance

## Admin tools
- Quels documents n'ont pas de résumé ?
- Demande un résumé du document 2
- Génère un résumé admin du document 2
- Donne le statut du job <jobId>
- Supprime le résumé du document 2
- Donne-moi la santé du catalogue
- Donne-moi la santé Qdrant
- Relance l'ingestion du document 2
- Liste les jobs admin
- Annule le job <jobId>

## Cas critique à vérifier
- "De quoi parle le document 2 ?" ne doit plus être forcé vers une simple source.
- "Quelle est la source du document 2 ?" doit encore marcher.
