# A858 — reprise durable de l'inventaire de candidats

## Verdict du palier

L'inventaire A857 est maintenant sauvegardé dans le job PostgreSQL et restauré
après une reprise de worker. Une interruption réessayable ne fait donc plus
perdre les titres exacts, les rôles, les EvidenceId de localisation et de corps,
ni les clés opaques de source déjà attribuées.

Ce palier démontre la continuité technique de l'état de recherche. Il ne
démontre pas encore qu'un Explorer autonome trouve une banque complète de
candidats, ni que le planning 5 × 4 réussit end-to-end. Le produit reste
`TESTE_NON_APPROUVE`.

## État persisté et bornes

La migration `068_advanced_analysis_research_checkpoint.sql` ajoute au job un
checkpoint JSONB nullable. Sa taille sérialisée est limitée à 65 536 octets et
la racine doit être un objet. Le schéma applicatif versionné
`saaia.advanced-analysis-research-checkpoint.v1` contient au plus :

- 64 candidats, avec les mêmes bornes de titre, note, rôles et EvidenceId que
  l'inventaire A857 ;
- 64 associations entre identité documentaire canonique et clé
  `internal-source-N` ;
- uniquement les associations effectivement utilisées par les candidats.

Le checkpoint ne duplique aucun contenu documentaire. Les corps restent dans
le stockage canonique ; la reprise reconstruit les preuves depuis le handoff et
l'historique des outils, puis revalide chaque EvidenceId avant de réutiliser
l'inventaire. La mémoire opérationnelle ne devient donc pas une preuve.

## Conditions d'écriture et de reprise

Une sauvegarde n'est acceptée que si le job :

- appartient au tenant attendu ;
- est encore `running` ;
- possède le lease du worker courant et un lease non expiré ;
- n'a pas reçu de demande d'annulation.

L'inventaire est sauvegardé après une observation automatique nouvelle, après
un appel valide à `save_candidate_inventory`, et après la projection des choix
du Writer. Dans un tour qui enregistre des candidats puis demande une recherche,
la sauvegarde précède la recherche : un incident réseau sur celle-ci ne perd
pas l'état déjà validé.

À la reprise, le provider refuse le checkpoint avant toute recherche si le
schéma, une borne, un rôle, une source, un EvidenceId, un titre ou une
transition d'état n'est plus cohérent avec les preuves canoniques visibles. Un
corps devenu navigation est également refusé. Le code ne tente pas de réparer
silencieusement une mémoire obsolète.

Les allocations de nouvelles clés sources ignorent les valeurs restaurées.
Une source déjà nommée conserve donc son identité même si l'ordre des extraits
change et une nouvelle source ne peut pas reprendre son numéro.

## Preuves exécutées

Un contrôle provider simule une première instance qui enregistre `Porridge`,
ses rôles, `E1` et `internal-source-1`, puis subit une interruption de transport.
Une seconde instance, avec le même gateway, restaure le checkpoint et voit les
mêmes valeurs dans son prompt. Un second contrôle injecte `E404` dans un
checkpoint : le provider renvoie
`advanced_research_checkpoint_invalid` après le Planner et avant toute nouvelle
recherche.

Le contrôle PostgreSQL réel enchaîne : écriture sous un worker A, expiration du
lease, récupération du job, claim par un worker B, puis relecture du même
candidat et de la même identité. Une lecture avec un autre tenant retourne
`null`. La connexion active a été lue depuis la configuration locale ignorée du
backend sans afficher de secret. Le fichier `.env` d'infrastructure NextCloud
essayé auparavant ne correspond plus au mot de passe de l'instance PostgreSQL
locale active ; cette dérive de configuration est séparée du mécanisme testé.

Résultats :

- suite backend complète avec PostgreSQL réel, avant l'ajout du dernier cas
  négatif : 2 449 réussites, zéro échec, trois tests live ignorés, 13 min 15 s ;
- suite provider sur l'état final : 291 réussites, zéro échec ;
- contrôle PostgreSQL ciblé sur l'état final : une réussite, zéro échec ;
- compilation incluse dans le contrôle provider : zéro erreur et zéro
  avertissement ;
- aucun appel OpenAI et aucun coût fournisseur.

## Limites et suite

Le checkpoint est propre au job. Il ne constitue pas encore une mémoire entre
deux discussions et n'a pas à le devenir pour valider le planning courant. Il
conserve les identités et décisions bornées, pas le texte documentaire.

La lacune principale reste en amont : Writer assure encore exploration et
rédaction dans une même phase, ce qui lui permet de conclure trop tôt après une
banque partielle. Le prochain palier doit séparer un Explorer qui remet au
Writer soit un dossier borné avec la couverture attendue, soit des lacunes
explicites après épuisement justifié des routes disponibles. A813/A817 servent
ensuite d'oracle hors réseau pour vérifier le transport complet de vingt corps,
sans coder leurs titres ni leur domaine dans le produit.

Le registre OpenAI reste à 39,99436060 USD sur 40. Le reliquat de 0,00563940 USD
est inférieur à la réservation minimale et ne justifie aucun achat. Le prochain
travail est entièrement hors réseau.
