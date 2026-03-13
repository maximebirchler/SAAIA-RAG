# ToolAgent cleanup note

Constat sur la dernière archive `RAG.zip` :
- il n'y a plus de fichiers `ApiClient.ToolPhase2.cs` ni `ToolAgentOrchestrator.ToolsPhase2.cs` dans la base de référence ;
- les collisions observées venaient donc très probablement de fichiers résiduels présents localement dans le repo de travail.

Ce patch fait deux choses :
1. empêche MSBuild de compiler ces anciens fichiers s'ils réapparaissent ;
2. fournit un script de nettoyage pour les supprimer physiquement s'ils existent encore.
