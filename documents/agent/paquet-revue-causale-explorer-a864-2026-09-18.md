# A864 — paquet de revue causale Candidate Explorer

## Verdict du palier

Les preuves intègres A863 deviennent maintenant un support lisible pour la
revue humaine du prochain pilote. Le vérificateur produit une annexe privée qui
présente, par job, le checkpoint des candidats et la séquence des outils. Le
préparateur de revue sémantique refuse un run Explorer sans verdict d'intégrité
positif, vérifie l'empreinte de cette annexe puis l'incorpore au paquet humain
déjà utilisé pour relire la réponse et les preuves canoniques.

Aucun appel fournisseur n'a été exécuté. Ce palier prépare la lecture d'un futur
résultat ; il ne valide pas Terra et le produit reste `TESTE_NON_APPROUVE`.

## Annexe privée

Après une vérification A863 réussie,
`candidate-explorer-evidence-review.private.md` conserve :

- l'identité durable du job ;
- chaque titre exact retenu par l'Explorer ;
- son état `navigation_only`, `body_requested`, `body_verified`, `selected` ou
  `rejected` ;
- la source opaque, les rôles proposés et sélectionnés ;
- les EvidenceId de navigation et de corps ;
- la note courte du modèle ;
- la séquence des outils avec état, latence, erreur, requête et références de
  preuve.

Les champs textuels sont ramenés sur une ligne avant leur insertion Markdown.
Le fichier est écrit atomiquement seulement si toute la chaîne d'intégrité est
valide. Son SHA-256 est inscrit dans l'assessment public.

Le contrôle synthétique vérifie explicitement que le titre et la requête privés
apparaissent dans cette annexe, mais jamais dans
`candidate-explorer-evidence-assessment.public.json`.

## Liaison avec la revue sémantique

Lorsque le sceau de campagne indique Candidate Explorer,
`prepare-advanced-semantic-review.ps1` exige exactement un assessment A863. Il
refuse le paquet si :

- son schéma ou son verdict ne correspond pas au contrat attendu ;
- il autorise la sortie des artefacts privés hors du workspace ;
- les nombres de jobs ou de checkpoints ne correspondent pas aux lignes
  enregistrées ;
- l'annexe privée manque ou son SHA-256 a changé.

L'annexe est ensuite ajoutée au fichier privé de revue qui contient déjà la
question, les critères préenregistrés, la réponse, les claims et les textes
canoniques. Le reviewer pourra donc répondre dans le même paquet à deux
questions distinctes : la réponse finale est-elle correcte et soutenue, puis à
quelle étape l'Explorer a-t-il trouvé, perdu ou mal affecté un candidat ?

Le manifeste public ajoute uniquement le verdict d'intégrité, les SHA-256 et
les comptes de jobs, événements et traces. Il ne copie ni titre, ni requête, ni
EvidenceId, ni identité de job.

Le préparateur ne traite plus aveuglément tout fichier JSONL du répertoire de
campagne. Il reconnaît les résultats de banque par leur contrat `row` et
`answer`, ce qui exclut le manifeste JSONL des propriétaires de jobs introduit
pour l'isolation A862.

## Contrôles exécutés

- cinq fixtures A863 sur cinq réussies ;
- la fixture valide vérifie aussi l'empreinte de l'annexe privée et la
  séparation privé/public ;
- une campagne synthétique propre contenant à la fois un résultat et le
  manifeste JSONL des propriétaires écarte correctement ce manifeste, reconnaît
  la ligne de résultat puis refuse l'absence d'assessment A863 avant toute
  tentative de connexion PostgreSQL ;
- analyse syntaxique réussie du vérificateur, de son test, du runner, du profil
  et du préparateur de revue ;
- `git diff --check` propre.

La préparation complète contre une vraie campagne Explorer n'est pas exécutable
tant qu'aucun pilote A862/A863 n'existe. Elle reste donc à prouver sur le premier
run disponible. Aucun fichier .NET n'a changé depuis la suite A862 à 2 459
réussites, zéro échec et trois live ignorés.

## Usage lors du prochain calcul

Après l'unique pilote gelé, l'ordre de décision sera :

1. assessment mécanique de la réponse ;
2. assessment A863 d'intégrité des preuves ;
3. génération du paquet A864 ;
4. revue humaine de la réponse et de chaque candidat ;
5. correction limitée à l'étape démontrée fautive, ou répétitions deux et trois
   si les deux revues réussissent.

Cette séquence évite de confondre une réponse bien formée avec une découverte
réussie, et évite aussi de rejeter le modèle lorsque la perte se situe dans le
transport ou la synthèse.
