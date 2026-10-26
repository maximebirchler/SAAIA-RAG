# Contre-revue indépendante du harnais Linux de cycle de vie

Revue du gel du 26 octobre 2026, effectuée en lecture seule des outils et des preuves de tests déjà enregistrées. Aucun produit, outil ou test modifié ; aucun test, service, Docker, SQL, HTTP, Git ou fournisseur payant exécuté par le relecteur. Le fichier JSON voisin contient les empreintes détaillées.

## Conclusion

Aucun nouveau défaut matériel de faux PASS, de périmètre de suppression ou d'ownership identifié dans ce gel. Les 15 empreintes du dernier inventaire de tests correspondent encore aux octets actuels ; les inventaires avant et après sont identiques. Cela autorise la préparation de l'exécution réelle par l'opérateur, sans constituer cette exécution.

Trois défauts ont été signalés pendant la revue, puis corrigés par le propriétaire du harnais :

- La configuration montée doit maintenant correspondre exactement au SHA256 du `runtime-ownership.json` du corpus réellement PASS. La DLL, le namespace, les cinq CID et le manifeste sont également liés au précédent résultat.
- La préservation des autres documents compare désormais leurs publications SQL et les vecteurs/payloads physiques complets, avec identité canonique vérifiée et empreinte ordonnée. Des points manquants ou modifiés empêchent le PASS.
- Le cleanup ne dépend plus du maintien des octets du PDF. Les gardes de chemins sans lien symbolique et d'ownership subsistent ; seuls les job IDs réellement admis par ce runner et le document synthétique attendu peuvent être annulés. Un job actif inconnu reste intact et empêche la clôture validée.

## Contrôles relus

Le mode NOEXEC précède l'accès aux secrets synthétiques, à Docker et au réseau. Le mode Execute exige le précédent corpus réel PASS, sa configuration et sa DLL exactes, les cinq conteneurs possédés et une cible Qdrant limitée au service possédé ou à son loopback déclaré. Les cibles historiques/externes sont refusées pour cette campagne.

Les observations SQL utilisent des SELECT sous transaction READ ONLY. La seule source modifiable est une copie distincte du PDF synthétique hashé. Le parcours vérifie génération/révision/plan et IDs physiques nouveaux, publication B protégée pendant le GC, puis suppression et recréation avec progression stricte. Le point tardif est un point A réellement capturé : vecteur et payload sont remis identiquement, sans fabrication de contenu ni de plan. Son retrait exige l'absence réelle et un nouveau sweep durable. Les réponses HTTP d'observation exigent un statut 200 complet.

Les requêtes administrateur utilisent les en-têtes synthétiques attendus. Les réponses de jobs sont adaptées au contrat Pascal/camel et les ACK sont contrôlés. Les captures documentaires et vectorielles restent privées ; le résumé public contient des compteurs et empreintes. Le finally annule seulement les jobs admis, vérifie leur terminaison, restaure uniquement la copie connue et arrête les cinq CID exacts sans suppression de volumes ou de données.

## Preuve disponible et portée

Le manifeste `tests/linux-lifecycle-offline-v1/manifest.json` et la fin de son journal confirment **51 tests purs PASS, zéro échec et zéro skip**, sans changement de source pendant le run. Cette preuve a été relue, sans réexécution. Ses transports et vecteurs sont simulés ; elle ne qualifie aucun moteur OCR, worker, SQL, Qdrant ou GC réel.

L'exécution réelle doit encore produire ses propres captures. La réinjection contrôlée ne prouve pas l'annulation d'une requête HTTP du worker en vol. Si une tentative d'annulation perd face à la fin du job, son état reste NON_EXECUTED. Le remplacement par un document défectueux reste hors de cette campagne bornée. Un runtime Development unsigned conserve `deploymentQualificationPassed=false`.

Limite de prélèvement sans faux PASS : un sweep périodique suivant peut remplacer le dernier code `reappearance_removed` par `absence_observed` avant son observation. Le harnais échoue alors de façon bornée malgré un retrait réussi ; il ne transforme pas l'absence seule en preuve du retrait tardif.

## Ancrage du gel

- `invoke-linux-lifecycle.py` : `ef1fe8dde00c5e6182d45fa9612cd8606518ea7e4a6a89d033f4d660dd8c2662`
- `linux_lifecycle_checks.py` : `b592022ed7873a7f9693df9c9f5ea827701bd2178343206988af038a0b3f6606`
- `README-linux-lifecycle.md` : `796ea83394440389ebdfd6664b805e5231720e01aeed9fe9d16a32ec3047a086`
- `test_linux_lifecycle.py` : `41f9261d00e5eeb188b790822c003ff645b5d43bc19ea5eef83bd05384264e98`
- Manifeste des 51 tests : `4c49ebb217cbd1991624ad93235a374d89b6c13848f9c605ea822662b518076d`
