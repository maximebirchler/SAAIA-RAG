# Validation réelle du client WinUI — clôture du 25 octobre 2026

Le parcours client demandé a été exercé dans le vrai exécutable WinUI, avec de vrais échanges HTTP, le vrai backend et PostgreSQL isolés. Les 33 parcours de la matrice possèdent désormais des preuves dans leurs périmètres déclarés. Ce total est une **projection de couverture sur plusieurs builds**, pas 33 scénarios rejoués intégralement sur le dernier binaire. Les compléments comprennent 20 contrôles organisés en six séquences, puis une vérification native ciblée du build final 11.

Plusieurs défauts concrets ont été trouvés puis corrigés. Le client reste disponible après une suppression confirmée même si la mise à jour de l’historique échoue ; la suppression backend est atomique ; une question acceptée comme conversation générale ne retombe plus dans des recherches documentaires contradictoires. La dernière vérification confirme aussi la présence effective des sondes attendues et le libellé correct des sources.

**Aucun test Terra ni appel à un fournisseur payant n’a été effectué.** Cette campagne valide les mécanismes du parcours avec des fournisseurs HTTP contrôlés. Elle ne valide ni la qualité sémantique du planning de repas ni les performances d’un modèle réel. Le statut produit demeure **TESTE_NON_APPROUVE**.

## Ce qui a été utilisé et ce que les preuves signifient

L’interface a réellement été ouverte, manipulée, fermée et redémarrée. Les données de discussions synthétiques ont réellement été écrites ou supprimées dans la base isolée. Des échecs ciblés et des délais bornés ont été injectés dans un proxy local pour tester la récupération. Le protocole des fournisseurs a été exercé par de vrais appels HTTP locaux ; leurs réponses restaient déterministes. Les temps artificiellement injectés ne mesurent donc aucune latence d’inférence.

Les captures natives, événements du proxy, projections SQL, résultats TRX, empreintes de sources et empreintes de DLL se complètent. Aucun résultat de test unitaire n’est présenté comme une observation de l’interface. Une projection publique d’une capture ne constitue pas une seconde exécution.

## Défauts identifiés et corrections vérifiées

| Problème réel | Cause et correction | Validation retenue |
|---|---|---|
| Suppression de discussion susceptible de laisser un état backend partiel | Le DELETE du parent et les cascades associées devaient partager la transaction de l’audit. Une transaction explicite couvre désormais l’ensemble. | Reproduction d’échec puis 7 tests d’intégration réussis, avec vérification du rollback et des orphelins. |
| Suppression réellement réussie annoncée « Suppression non confirmée » après un GET historique en erreur | Le client attendait le rafraîchissement avant de retirer l’élément local. Après ACK du DELETE, il retire désormais la cible avant ce GET faillible ; une discussion survivante connue peut être rechargée. | Reproduction native build08, puis suppressions nominales et injection d’un unique GET500 réussies build09, avec redémarrage. |
| Question générale acceptée par le routeur ensuite détournée vers des replis documentaires | Deux replis lexicaux poursuivaient leur traitement malgré une décision générale sans outil. Ils respectent désormais cette décision acceptée. | Échec causal conservé, 3 tests comportementaux verts, 163 tests voisins verts, puis même phrase rejouée avec un vrai flux sur build10. |
| Champ de sonde absent des journaux publics malgré l’appel de diagnostic | Le champ `entry` était filtré par la politique normale. La sonde utilise maintenant le champ déjà autorisé `source_step`, avec test du mode `metadata_only`. | Vérification native build11 : les deux étapes sont présentes dans CLIENT_PROBE et RAG_TRACE, avec fin réussie ; 9 contrôles sur 9. |
| Libellé de carte « type contenu contenu » | La traduction affichait deux fois la même notion. Le rôle est maintenant intitulé « rôle contenu », avec correction dans les six langues. | Carte avancée persistée observée dans le vrai build11, sans modification de son identité documentaire. |

Le défaut de suppression est particulièrement important : une erreur de lecture après un DELETE confirmé ne rend pas ce DELETE incertain. Le bandeau corrigé affiche exactement **« Discussion supprimée, liste non actualisée »**, et son texte confirme la suppression. La discussion supprimée ne reste plus dans la liste ; une survivante vide est chargée automatiquement ; la saisie redevient disponible. Après fermeture complète et redémarrage du même profil, la discussion supprimée reste absente et l’alerte a disparu.

Les suppressions n’ont concerné que les discussions fictives de campagne explicitement autorisées. La variante combinant suppression inactive et conservation d’une carte source n’a pas été directement rejouée dans ce lot : les témoins A/B/C possédaient des messages sans `Sources`. Les observations de conservation des messages et de la discussion active restent valides ; la preuve des cartes appartient à son scénario indépendant antérieur.

## Les six séquences complémentaires

| Séquence | Parcours complétés | Ce qui a été observé et prouvé |
|---|---|---|
| J01 — première configuration | W02, W04 | Modification d’URL abandonnée par Annuler ; aucune persistance de cette modification ; HTTP401 réel avec une mauvaise clé ; correction dans le même assistant ; application d’une clé synthétique valide ; reconnexion directe après redémarrage. |
| J02 — repli et préférences | W05, W25 | Primaire 5169 sans service, connexion par l’alternative 5177 ; priorité des URL conservée ; choix anglais et thème clair appliqués puis conservés après redémarrage du même profil. |
| J03 — titres | W08 | Titre vide ramené au titre par défaut ; une affectation UIA trop longue refusée sans mutation ; saisie native de 121 caractères limitée à 120, puis sauvegarde de ces 120 ; discussion active témoin inchangée. |
| J04 — flux, défilement et interruption | W15, W27 | Défilement manuel stable pendant la croissance du texte ; retour en bas pendant le flux ; annulation réelle ; contrôles réactivés ; réponse partielle conservée après rechargement ; nouveau tour distinct sans remplacement du partiel interrompu. |
| J05 — transition et fenêtre modale | W16 | GET des messages retardé de 20 secondes, contrôles verrouillés durant la transition puis réactivés ; pas de soumission par Ctrl+Entrée dans la transition observée ; brouillon conservé avec Paramètres ouvert, Ctrl+Entrée sans envoi et fermeture sans Appliquer. |
| J06 — identité des sources et reprise avancée | W20, W21 | Référence réellement ambiguë refusée ; carte canonique ouverte sur le PDF exact page2 ; révision fictive refusée « Version citée indisponible » ; récupération native d’un vrai résultat avancé terminal avec source ; mêmes résultat, message et carte après redémarrage. |

La première séquence possède une preuve durable 9/9 : l’existence et la correspondance de la clé protégée DPAPI ont été contrôlées en mémoire. Aucune clé, aucun blob protégé ni copie de profil n’est publié. La seconde possède une preuve finale 8/8. Les événements du proxy prouvent les requêtes sur l’alternative ; ils ne constituent pas une capture réseau du primaire. L’absence de service primaire et l’observation de l’interface apportent les deux autres éléments de cette démonstration.

Pour les titres, les preuves 5/5 et 7/7 distinguent le refus de l’opération UIA et la vraie saisie native. Le titre final contient exactement 119 caractères R puis X, soit 120 caractères ; le caractère supplémentaire Y est absent. Le message et le témoin actif n’ont pas changé.

Pour J05, il n’existe pas d’horodatage indépendant de la frappe. Le périmètre retenu est donc **la transition de chargement verrouillée**. On ne prétend pas avoir daté une frappe précisément pendant les 20 secondes du GET. Les preuves finales distinguent correctement l’événement de requête et sa réponse ; une première projection qui les comptait comme deux injections est conservée comme rejetée.

## Pourquoi plusieurs tentatives de streaming sont conservées

La première tentative n’a pas produit de flux : le mauvais repli documentaire l’avait détournée. Elle a servi de reproduction et reste une preuve rouge. La deuxième a validé le flux et le défilement mais s’est terminée naturellement ; elle ne prouve donc pas Annuler. La troisième a réellement été annulée et sa persistance a été vérifiée.

Après correction du routage, la **phrase originale** a été rejouée sur build10. Les observations natives 38 à 44 prouvent le flux, un défilement stable pendant les deltas, le retour en bas, l’interruption et le nouveau tour. Les deux preuves durables de ce retest sont à 8/8. Le texte partiel de 644 caractères garde le même SHA et le même statut après environ 88 secondes et après rechargement ; le tour suivant est distinct et terminé. Ces chiffres décrivent une réponse de fixture, pas la capacité d’un modèle réel.

La vérification finale build11, observations 52 et 53, a confirmé la réponse **CONTROLE FINAL BUILD11**, une fin de flux réussie et les sondes `source_step` effectivement admises dans les deux journaux. La carte avancée conservée affiche désormais le rôle corrigé. Elle n’implique pas que toutes les autres observations aient été rejouées sur11.

## Ce que la reprise avancée valide exactement

Un vrai job avancé a été créé par l’API contrôlée, puis exécuté par le vrai worker avec les preuves documentaires reçues. Le résultat terminal n’a pas été injecté dans l’interface. Seul le message assistant initial en attente a été préparé afin que le vrai tracker du client puisse le reprendre.

Le client a récupéré le même job `08655291-6f0f-4d34-8f58-fa8a9dffd49b`, révision4, dans le même message `f0e3aba3-2ef0-4f11-9257-0f92b6858116`. Le code ORION4826, l’identifiant de citation C1 et la page2 correspondent à la preuve reçue. Les vérifications SQL et HTTP confirment le même résultat et les mêmes sources après fermeture complète et redémarrage. Les preuves finales comprennent 13/13 contrôles, puis une stabilité ultime build11 à 6/6 sans nouvel appel.

La création de ce job depuis le bouton natif n’est **pas** l’objet de J06 : elle a eu lieu par API avant l’observation. Le compte testé possède un seul job cible ; deux jobs existent globalement, dont celui de la première tentative rejetée. Aucun nouveau job ni appel fournisseur n’a été créé pendant la reprise native. L’attribution de certains GET de redémarrage utilise l’ordre et les identifiants des journaux, corroborés par l’interface et SQL ; chaque ligne ne possède pas son propre UTC. Les GET de contrôle de l’observateur sont exclus de la revendication d’activité native.

Les ouvertures PDF ont été observées dans Edge : URI exacte `Validation/Guide-Validation.pdf#page=2`, sélecteur de page2 et texte ORION4826. Les captures Edge originales contiennent d’autres éléments du navigateur et restent privées. Les fichiers publics 47 et50 sont des projections minimales hashées de ces originaux : URI, sélecteur de page et texte du document, sans onglets, favoris ni image originale.

## Tests, sources et exécutables

Le dernier passage Client/Core contient **2 476 tests réussis**, aucun échec ni test ignoré : 80 Core et 2 396 Client. Les sources avant/après sont identiques. Le build de production11 a terminé sans avertissement ni erreur ; le contrôle d’architecture a réussi. L’ancrage indépendant est [full-core-client-build11-production-anchor.json](full-core-client-build11-production-anchor.json).

Les résultats backend2 430 et contrats20 du passage complet antérieur restent applicables à leurs sources et dépendances vérifiées inchangées. La couverture composée est donc de **4 926 cas distincts répartis sur plusieurs passages**. Il n’y a pas eu un unique lancement de 4 926 tests. On n’additionne pas les anciennes suites aux nouvelles suites qui les recouvrent, ni les tests ciblés aux mêmes cas de la suite complète.

Les DLL de tests AnyCPU et l’exécutable de production Release x64 ont des empreintes différentes attendues ; leurs sources communes et leurs ancrages sont vérifiés. La DLL WinUI du build11 réellement lancé porte le SHA256 `A587B3A8B7F75C5EC2E7A39544831C54E1CDD147590D50DEC0543F70D2C83A7B`. Le backend de fixture conserve son ancrage `FEF8B7977EC4F4C61E6EBBB78AA11888265BF7FDAE9DD23230577E8947CA893A`.

Les quatre livraisons suivantes ont été confirmées sur le dépôt distant par le responsable de la campagne : `81c8028f6d71384351a6e9000e79bfc852859a20` (suppression/outils), `ccd0f8f7f54bac0ea92538199062326db305f761` (preuves de suppression corrigées), `c1e7b7a894103e26aa5f535df8fccf45181984f5` (routage général), `7943482b1a60909c61abd31652dca38feb91a601` (sonde et libellés finaux). Le commit/push de ce paquet de preuves intervient après le contre-scan ; sa confirmation et son identifiant sont donnés dans le message de livraison. Aucun ordre Git n’a été exécuté par l’auteur de cette projection finale.

## Isolation, arrêt et confidentialité

La comparaison finale confirme **556 fichiers sur556 inchangés**, aucune différence dans les périmètres habituels client, gouvernance, développement LLM et journaux couverts. Ce résultat ne prétend pas comparer atomiquement tout le profil Windows ou tous les identifiants de la machine.

Tous les clients et processus de fixture appartenant à la campagne sont arrêtés. Les six ports possédés 5167,5177,5187,18097,18099 et58590 sont libres au contrôle de clôture. Le PostgreSQL préexistant sur5432 est resté le même processus protégé, PID7844 ; il n’a pas été arrêté. Les données et preuves isolées sont conservées localement.

La sélection publique exclut les répertoires `runtime`, `client-data`, `private*`, les profils et leurs fichiers protégés, environnements, archives ZIP, sorties brutes backend et originaux Edge. Le manifeste décrit la sélection et ses vérifications de chemins, empreintes et secrets connus en mémoire ; un contre-scan indépendant précède le staging. Les captures de l’assistant de connexion contenant des exemples de configuration restent privées ; seules leurs projections sans secret sont publiables. Aucune sélection n’autorise une publication de secrets.

## Limites restantes et suite logique

Les 33 parcours sont couverts dans les périmètres explicités dans la matrice ; cette couverture ne constitue pas une garantie d’absence de défaut. Le HTTP403 et le401 tardif restent couverts par tests unitaires, pas par de nouvelles manipulations natives dans ce lot. L’anglais/thème clair a été inspecté sur un échantillon de l’interface et après redémarrage ; toutes les cartes et tous les états visuels de toutes les langues n’ont pas été parcourus. Les limites de frappe, de création native du job et de carte source combinée à la suppression sont conservées ci-dessus.

Le prochain travail logique est une campagne sémantique Terra distincte sur le planning de repas, après revue de cette clôture. Elle devra commencer par inspecter les documents, l’inventaire des outils, les requêtes et preuves réellement transmises au modèle ; définir les critères de réponse et de citation ; puis exécuter quelques essais reproductibles avec suivi du coût autorisé. Aucun succès fonctionnel de fixture n’est utilisé pour préjuger de cette acceptation. Aucun nouvel achat ni changement de fournisseur n’est décidé par le présent rapport.

Les références principales sont [la matrice des20 contrôles](journey-completion-execution-final.json), [la projection des33 parcours](execution-matrix-consolidated-final.json), [la revue des sondes](RAPPORT-INTEGRITE-SONDES-PUBLIQUES.md), les preuves durables de `local-fixture`, et les observations publiques de `native-ui`. Les anciens échecs, projections rejetées et matrices intermédiaires restent conservés ; ils ne sont pas réécrits comme des réussites.
