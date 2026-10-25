# Revue du correctif et des variantes WinUI restantes

Cette revue porte sur le code gelé après le ciblé client `client-delete-refresh-tests-02` et sur les preuves disponibles avant le retest natif corrigé. Elle ne prétend pas que les compilations de production ou ce retest ont déjà réussi. Les anciennes preuves ne sont pas réécrites.

## Correction client et diagnostic

Le défaut réel est correctement distingué de sa cause : le backend a confirmé la suppression, puis la lecture de l'inventaire a échoué. L'ancien code gardait la cible dans la liste locale et annonçait « Suppression non confirmée ». La capture et l'arbre de l'observation10, rectifiés explicitement par10b, établissent le texte exact ; la première description imprécise de10 est conservée comme historique.

`SessionDeletionWorkflow` attend la réponse DELETE réelle avant tout engagement local. Une erreur de transport laisse les données visibles intactes et conserve le statut de suppression non confirmée. Dès l'acquittement, le callback de `Sessions.cs` retire l'item ciblé avant le GET d'inventaire. Pour une suppression inactive, l'ID chargé, les messages et l'agent restent inchangés. Pour une suppression active, l'ancien ID, le message préparé, les messages et les sources sont effacés avant de charger le survivant.

Si l'actualisation échoue après l'acquittement, l'orchestration ne relance pas DELETE et ne remonte pas une fausse suppression non confirmée. Elle tente un chargement du survivant déjà connu. Un autre échec reste explicitement enregistré et n'est pas présenté comme une récupération réussie. Sans survivant chargé, l'ID reste nul et la saisie désactivée ; Nouveau permet d'ouvrir une discussion utilisable. Le bandeau spécifique confirme la suppression et annonce la liste non actualisée avec ces actions de récupération.

Les sondes distinguent `session.delete.request`, `session.delete.acknowledged`, `session.delete.refresh`, `session.delete.refresh_failed` et `session.delete.recovery` dans une opération `session_delete`. Le résultat dégradé « suppression confirmée mais actualisation échouée » ne se confond pas avec un échec de suppression. Le probe de récupération indique aussi l'absence de discussion chargée. Les noms de discussion, messages, sources et clés ne sont pas ajoutés aux sondes.

Le ciblé accepté passe **20/20** avec sources inchangées pendant le test. Il comprend les15 cas de la nouvelle classe, dont les six langues, et les cinq gardes existants de l'alerte de persistance. Les cas comportementaux utilisent le vrai `ApiClient` avec des réponses HTTP contrôlées ; les états de vue/mémoire du banc restent un substitut testable et ne constituent pas une observation des contrôles WinUI. Le raccordement au gestionnaire compilé est contrôlé séparément. Le premier ciblé,19 succès et un ancien garde structurel échoué, est conservé ; le garde inspecte désormais les vrais callbacks compilés du gestionnaire.

L'architecture reste dans ses plafonds : `MainWindow` agrégé15319/15376 lignes,57 lignes de marge, et aucune hausse de budget. L'orchestration diagnostique est dans un service court plutôt que dans le gestionnaire de fenêtre.

## Relecture croisée du backend

Le correctif backend supprime désormais le parent `chat_sessions` une seule fois, sous le même filtre tenant/utilisateur, en laissant les clés étrangères existantes effectuer les cascades. Le DELETE et l'audit sont dans la même transaction explicite et le succès est retourné après le commit. Les migrations005 et065 fournissent déjà les cascades attendues ; le changement ne réclame pas une migration supplémentaire.

La preuve causale distingue correctement les deux fautes anciennes : panne du DELETE parent laissant un message effacé, et panne de l'audit après suppression déjà enregistrée. Les triggers de test des deux frontières échouent avant correction et passent après. Le périmètre nominal vérifie aussi les refus pour mauvais utilisateur/tenant, l'autre tenant ayant le même UUID et l'absence d'un second audit au deuxième DELETE. La preuve7/7 porte sur la vraie méthode de production et PostgreSQL, pas sur la fenêtre native. Un backend déjà chargé ne bénéficie pas du nouveau code avant son redémarrage.

Limite correcte : `commit_acknowledged=false` ne prouve pas qu'un commit serveur n'a pas eu lieu si seule sa réponse réseau est perdue. Le client doit conserver le statut non confirmé dans ce cas, ce qu'il fait tant qu'il ne reçoit pas de réponse DELETE réussie.

## Portée des suppressions réellement observées

La projection `delete-execution-matrix-observed-red.json` rattache les trois suppressions nominales aux preuves UI/API/SQL et la panne de rafraîchissement à son vrai DELETE200 puis au GET500 unique. Le seed A/B/C contient deux messages par discussion et **aucune source** : ce lot ne prouve donc pas la préservation visuelle d'une carte source lors d'une suppression inactive ni l'effacement d'une carte non vide lors d'une suppression active. Les anciens tests de source sont une preuve indépendante, pas cette variante combinée. Aucune inspection directe de toute la mémoire privée de l'agent n'est revendiquée.

## Dix variantes de la campagne précédente

Ces lignes sont une proposition de clôture des limites déclarées de `winui-validation-20261025/execution-matrix-final.json`. Elles ne sont pas exécutées par cette revue. L'ancienne matrice les qualifie de compléments de couverture et ne les marque pas comme un blocage technique Terra ; cela n'autorise pas à annoncer « tous les parcours intégralement validés ».

| Parcours | Variante encore non observée | Validation proposée sans API payante |
| --- | --- | --- |
| W02 Configuration initiale | Conservation exacte des champs après annulation puis redémarrage du même profil | Profil isolé neuf ; ouvrir l'assistant, modifier sans appliquer, fermer, comparer les seuls paramètres non secrets puis redémarrer. Reprendre l'assistant et appliquer une clé synthétique. |
| W04 Authentification refusée | Remplacement d'une clé invalide dans la même instance ;403 et401 tardive surtout unitaires | Backend/proxy local borné ; produire le refus, corriger la clé dans la même fenêtre, reconnecter et vérifier la session. Garder les preuves unitaires de la course401 distinctes d'une vraie séquence native. |
| W05 Bascule URL | Conservation de l'ordre des alternatives et second démarrage du profil | Reprendre le profil de bascule, noter l'ordre non secret avant/après, redémarrer et vérifier l'URL effectivement utilisée. |
| W08 Renommage | Titre vide et limite120 non saisis dans la vraie fenêtre | Deux discussions jetables ; renommer l'inactive avec un titre vide puis120 caractères et un essai dépassant la limite. Vérifier titre durable et discussion active inchangée. |
| W15 Annulation | Arrêt de deltas déjà visibles et tour suivant | Fixture de streaming local avec plusieurs fragments espacés ; attendre du texte visible, annuler, vérifier arrêt stable, sauvegarde de l'interruption et nouveau tour. Aucun modèle réel nécessaire. |
| W16 Admission | Clavier derrière un dialogue et pendant un GET lent | Injection locale bornée de lecture lente ; tenter les contrôles par clavier, confirmer l'absence de nouvel envoi/changement d'identité, puis vérifier récupération après fin de la lecture. |
| W20 Révision ambiguë | Ambiguïté réelle entre révisions sans SHA exact | Sources synthétiques homonymes/révisions distinctes, sans SHA discriminant ; cliquer Ouvrir et constater le refus ou choix explicite, sans ouverture arbitraire. |
| W21 Résultat avancé et sources | Résultat avancé terminal contenant une source, puis nouveau redémarrage | Worker réel et fournisseur local déterministe ; générer un terminal avec une source de test valide, redémarrer, vérifier même job/message, carte et ouverture de la bonne page. Pas de conclusion sur la qualité sémantique du modèle. |
| W25 Préférences | Redémarrage après langue/thème appliqués et couverture des états | Même profil isolé, appliquer langue/thème, fermer normalement puis redémarrer ; vérifier paramètres, affichage et quelques cartes/menus représentatifs. Déclarer le sous-ensemble exact. |
| W27 Navigation | Scroll volontaire pendant les deltas et bouton Aller en bas | Réutiliser le streaming deW15, remonter volontairement, vérifier l'absence de saut forcé et le retour explicite en bas. Conserver les limites de taille de fenêtre déjà déclarées. |

Pour limiter les fixtures et les redémarrages, les groupes utiles sont W15/W16/W27 pour le streaming et l'admission, W02/W04/W05/W25 pour configuration et persistance, puis W08/W20/W21 pour historique et sources. Les captures doivent garder les états avant/action/après ; un résultat unitaire ne devient pas rétrospectivement une validation native.

## Conditions avant une conclusion finale

Il faut encore rattacher le retest du client corrigé et du nouveau backend compilé : suppression inactive, active, dernière, puis DELETE réellement acquitté suivi du GET500. Le défaut corrigé doit être absent de la liste et le bandeau doit confirmer la suppression. Les sondes, l'ID persistant et les états API/SQL doivent correspondre aux mêmes sessions et binaires. Le résultat de la suite complète en cours appartient à son propre manifeste final ; les totaux partiels ne sont pas additionnés comme s'il s'agissait de nouveaux tests distincts.

Aucun test Terra ou planning de repas ne découle automatiquement de ces résultats. Le statut produit reste `TESTE_NON_APPROUVE` jusqu'à ses preuves d'acceptation.
