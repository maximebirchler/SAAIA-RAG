# A844 — compacter les métadonnées répétées des retours natifs

Préenregistrement avant modification produit, régression contrôlée et pilote.

A843 clos : bd02202a, job 1816ba1c-948a-46ac-babd-ff27e98cc103, trois
appels, 0,12947970 USD. Le modèle reçoit réellement le budget documentaire :
huit opérations consommées et vingt-quatre restantes. Son premier batch a une
fenêtre de cinq pages ; refus atomique sans I/O, puis correction par le modèle
à quatre pages. Les huit lectures corrigées réussissent, seize événements au
total avec les huit recherches du Planner. Aucun résultat terminal ni aucune
évaluation sémantique complète. Ressources closes, registre 36,46125760 USD / 40,
reste calculé 3,53874240 USD.

Le job échoue ensuite avant le quatrième appel sur
advanced_native_tool_history_limit_exceeded. Le dernier bloc Responses observé
mesure 8 377 caractères JSON compacts, deux objets de raisonnement chiffré de
4 152 et 1 828 caractères et huit appels. Les retours liés à ces appels répètent
une longue instruction commune et des champs nuls, dépassant le plafond total
de 16 384 caractères. Ne pas attribuer cet arrêt à une incapacité à corriger
les pages : le retour de correction a fonctionné et les lectures ont réussi.

Hypothèse : retirer la répétition de consigne et les métadonnées nulles permet
de transmettre ce type de batch valide sous le plafond existant. Intervention
généraliste sur les retours de tous les outils documentaires : une instruction
commune par batch ; conserver les huit call IDs et leurs sorties, status,
operation, identités de source résolues, compteurs, IDs visibles bornés et leur
nombre omis, diagnostics et éventuel feedback. Omettre seulement des champs
sans valeur et la répétition textuelle identique. Tous les éléments Responses
originaux, y compris le raisonnement opaque, restent intacts. Pas de troncature
de ce raisonnement, de preuve ou d'appel pour faire passer la taille.

Plafond historique 16 384, contexte et preuves canoniques inchangés, trente-deux
opérations documentaires, même enveloppe A837 high/8192, low/512, douze appels,
une tentative HTTP, 1,25 USD maximum. A842 et A843 inchangés. Le plafond reste
capable de refuser un bloc opaque ou batch trop grand : aucune promesse d'historique
illimité. Réservation monétaire sur le corps réellement transmis toujours requise.

Test causal Responses avec état opaque simulé et batch de huit lectures valide :
échec de taille mesuré avant correction, puis transmission de tous les outputs
et du contenu canonique, état opaque identique. Tester aussi le refus d'un bloc
qui dépasse encore le plafond sans accepter une troncature. Contrôles des deux
protocoles, liaison d'outputs, mémoire et budgets, puis suite complète.
Ne pas présenter un état opaque simulé comme un raisonnement réel du fournisseur.

Un pilote connu sur nouvelle version propre figée après contrôles, analyse avant
répétition. Vingt sélections approuvées et audit physique, trois consécutifs figés
avant banc inédit et WinUI. TESTE_NON_APPROUVE, Goal actif, aucun achat automatique.
