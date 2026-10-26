# Transfert du contexte de build — complément de correction

État : **lot déploiement final gelé**, inventaire `deployment-corrections-source-manifest-v2.json`. Ce document complète `deployment-corrections-resolution.md` et conserve son manifeste v1 comme ancrage historique. Aucun service, Docker, build .NET, Git, réseau ou transfert serveur exécuté.

Le chemin distant utilisait encore un `tar` récursif de répertoires backend/contrats avec des exclusions partielles. Les règles `.dockerignore` protègent le contexte du build Docker, mais ne filtrent pas une archive créée au préalable. Le correctif précédent de provenance ne suffisait donc pas à sécuriser ce transfert.

`Get-DeploymentSourceFiles` constitue maintenant l’inventaire partagé par le hash de sources et la sélection de transfert. `Get-BackendArchiveSourceFiles` réunit les deux inventaires backend/wrapper Docling et le compose requis. `New-BackendSourceArchive` ne reçoit que cette liste de **fichiers explicites**, transmise en UTF8 avec séparateurs NUL à `tar --null -T`. Aucun répertoire récursif n’est fourni au transfert. Les entrées privées/générées sont exclues avant lecture de contenu ou archivage. Le seul fichier de liste temporaire est créé de manière exclusive et supprimé individuellement en `finally`.

Les témoins placent uniquement des faux fichiers synthétiques aux emplacements `.env`, `appsettings.Local.json`, configuration signée, `keys/*.pem` et `obj/`. Ils vérifient leur exclusion et la présence des vraies entrées de compilation. Aucune clé ni configuration privée existante n’a été lue pour ces contrôles.

Preuves conservées :

- `tests/deployment-transfer-red-v1/result.json` : **2 échecs et 15 verts**. Le premier prouve le transfert récursif toujours présent ; le second marque le contrat de sélection filtrée encore absent avant son implémentation.
- `tests/deployment-transfer-green-v1/result.json` : **17/17 verts**, sur les vrais helpers/installer et le transport d’archive simulé. Le témoin d’archive a été déplacé de la commande AST historique au nouveau helper réellement appelé par le déploiement.
- `tests/deployment-transfer-real-tar-v1/result.json` : archive TAR réellement créée **sur la seule sandbox synthétique**, puis relue par son inventaire. **11 fichiers attendus, 11 archivés, 0 différence et 0 entrée interdite.** Aucun TAR de sources réelles ou de secrets n’a été créé.
- `deployment-identity-python-green-v2.log` : **7/7 verts**, dont cohérence PS/Python après extraction de la responsabilité d’inventaire.
- Manifeste v2 : **6 AST PowerShell sans erreur**, 15 fichiers de sources avec tailles/SHA finales.

Les limites de qualification du rapport initial restent valables : signature cryptographique, installateur Linux, build/image/DLL réellement chargés et moteurs exacts ne sont pas validés par ces tests hors ligne. Les sources doivent rester figées entre calcul de leur identité et construction ; cette liste filtrée ne transforme pas des fichiers modifiables en instantané atomique. Le harnais de qualification Linux possède son propre transfert explicite et n’utilise pas ce déploiement historique.
