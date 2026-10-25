# Suffixes fiables : correction du pouvoir destructif des marqueurs

Statut : sources gelées après un RED causal ; GREEN à exécuter par le coordinateur.

La campagne `tests/source-fidelity-red-green-v3` a exécuté 57 cas : 54 succès et trois échecs. Deux échecs concernent les nouveaux suffixes OCR : la classification les reconnaît comme non bruyants, mais le nettoyage retire les durées ou l’avertissement. Le troisième échec relève de l’autre chantier de fidélité du contexte de page.

Le correctif traite l’ensemble du mécanisme inventorié dans `ocr-metadata-marker-deletion-audit.md` :

- un marqueur ne dispense plus des seuils de longueur ;
- un marqueur ne provoque plus directement le retrait du suffixe ou de toute l’entrée ;
- les neuf contrôles de contenu utile ne sont plus invalidés par un simple marqueur ;
- les deux contrôles de fragment ne sont plus invalidés par le seul label de temps/durée/préparation.

Le motif du label reste un repère de zone potentielle, mais les suppressions doivent passer un diagnostic de dommage indépendant. Les contrôles de bruit OCR, de disposition endommagée et de fragments restent distincts. Le motif `NoisyMetadataLeadMarkerRegex`, devenu sans appel, est supprimé. Le helper de fragment est renommé pour exprimer sa dépendance à un supplément endommagé plutôt qu’à un label sémantique.

Les deux RED positifs exigent une conservation exacte après un corps complet, y compris l’avertissement et les valeurs 50/40 minutes. Deux contre-contrôles supplémentaires réutilisent des scans dont le caractère corrompu est déjà testé ailleurs : le texte brut et le suffixe doivent être reconnus comme du bruit, puis ce suffixe doit encore être retiré en conservant le corps fiable. Les assertions historiques de corruption ne sont pas inversées en bloc pour obtenir un succès.

Des attentes historiques qui assimilaient de simples marqueurs à du bruit peuvent encore s’avérer incorrectes. Chaque échec doit être examiné avec son texte et son stade de perte ; ce rapport ne déclare pas automatiquement ces assertions obsolètes.

Aucune compilation, service, requête API ou dépense n’a été exécutée par l’auteur du correctif.
