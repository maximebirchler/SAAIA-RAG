# Résolution backend B1–B4

Les quatre points backend de l'audit ont reçu un correctif ciblé. Le coordinateur a exécuté les reproductions : **5 cas rouges avec les cinq échecs attendus**, puis **5/5 PASS** après correction. Je n'ai lancé aucun build, test, serveur, modèle ou opération Git de mon côté. Les données des reproductions appartiennent aux nouvelles bases PostgreSQL de la campagne ; les manifests du coordinateur déclarent les clusters arrêtés et supprimés.

Les preuves sont `red-backend/tests/manifest.json` et `green-backend-initial/tests/manifest.json`, avec leurs TRX voisins. Les SHA-256 des TRX ont été relus et correspondent aux manifests. Les deux exécutions indiquent `sourceChangedDuringRun=true` pendant les éditions parallèles du chantier global : ces résultats sont des preuves comportementales ciblées, **pas encore l'ancrage final d'un arbre entièrement gelé**. La suite complète finale orchestrée par le coordinateur doit fournir cet ancrage. Le renommage purement diagnostique en `advanced.provider_cleanup` a été effectué après ce premier vert ; il reste inclus dans la validation finale à venir.

## B1 — Durée de vie du provider et bail

`AdvancedAnalysisWorker.ExecuteProviderWithLeaseAsync` recontrôle le bail avant de démarrer le provider : une préparation lente ne peut plus lancer une requête sur une propriété déjà perdue. Le renouvellement réutilise la même sonde de lease.

Le provider est désormais encadré par un `try/finally` : sur une perte retournée, une exception du renouvellement, une annulation du service ou une erreur du provider, la tâche enfant est annulée si elle reste active, puis son achèvement est observé avant la sortie. Une erreur observée pendant la clôture ne remplace pas l'erreur d'origine. La sonde `advanced.provider_cleanup` indique l'annulation demandée et l'observation effective ; sa durée mesure bien la clôture, pas la durée totale du modèle.

La reproduction arme un trigger PostgreSQL **depuis le provider démarré**, puis refuse le renouvellement suivant. Avant correction, le worker revenait sans annuler le provider ; après correction, le test vérifie son annulation, son achèvement, un seul appel et aucune publication de résultat. Les providers sont tenus de respecter leur jeton : la clôture attend volontairement leur achèvement plutôt que de permettre une tentative concurrente non maîtrisée. Les appels HTTP de l'implémentation réelle disposent aussi de leur timeout.

## B2 — Création/annulation et audit atomiques

`AdvancedAnalysisEndpoints.CreateAsync` et `CancelAsync` englobent maintenant l'écriture du job et `AuditWriter.WriteAsync` dans la même transaction PostgreSQL. L'audit reçoit explicitement cette transaction. Le résultat métier est acquitté après commit, et les replays de création conservent l'idempotence existante.

Les deux reproductions refusent l'INSERT d'audit par un trigger de la base synthétique. Avant correction, une création laissait un job `queued` malgré l'erreur, et une annulation laissait `canceled`. Après correction, la création ne laisse aucune ligne supplémentaire et l'annulation conserve statut/révision/drapeau initiaux, avec les comptes d'audit attendus.

Les nouvelles sondes `advanced.job_create` et `advanced.job_cancel` exposent des identifiants, la phase mécanique et `commit_acknowledged`, sans texte de demande ni secret. Un chemin exceptionnel non terminé reste `incomplete` avec un commit non acquitté ; aucun succès n'est déduit d'une simple écriture commencée. Les refus métier et replays ont des issues explicites.

## B3 — Admission concurrente par tenant/utilisateur

La création prend un verrou **transactionnel** PostgreSQL dérivé du couple tenant/utilisateur avant le contrôle d'idempotence, le recomptage de file et l'insertion. Le verrou couvre toutes ses discussions et s'applique entre processus backend. Les autres propriétaires restent indépendants. Une collision du hash ne pourrait que réduire la concurrence, sans autoriser un dépassement du quota. La session est lue avec `FOR KEY SHARE` pendant la transaction.

La reproduction bloque l'insertion du premier propriétaire dans la base isolée, met deux créations distinctes en concurrence, et vérifie aussi qu'un autre utilisateur du même tenant ainsi que le même nom d'utilisateur d'un autre tenant restent admis. Avant correction : **202/202** et quota dépassé. Après correction : **202/429** et exactement un job actif pour le propriétaire limité.

## B4 — Temps des outils échoués et reprise

Le chronométrage commence dès l'admission de l'appel, inclut l'attente de capacité, la résolution du scope et la revalidation des preuves. Une fonction locale charge la durée **exactement une fois**, sur succès comme sur échec ; la même valeur sert au compteur mémoire, à l'observation et à l'événement durable. L'écriture de cet événement est mesurée séparément par `advanced.tool_trace_persistence`, pour éviter de prétendre stocker sa propre durée complète dans la ligne qu'elle est encore en train d'écrire.

La reproduction bloque une lecture canonique, puis la laisse échouer sur une identité modifiée. Le rouge montrait **67 ms** dans l'événement durable contre **0** dans le compteur mémoire. Le vert exige une durée positive identique en mémoire, dans l'historique et après reconstruction du gateway, ainsi qu'un seul appel consommé. Le plafond de nombre et les erreurs de source conservent leur rôle.

## Intégration coordonnée et limites restantes

Dans le fichier endpoint partagé, les ajouts demandés par l'auditeur client ont été intégrés : validation du nouveau `ConversationContext`, lignes jusqu'à 80 et colonnes jusqu'à 12 via les constantes communes. Aucun contrat, prompt ou garde financier n'a été modifié par cette sous-tâche. Aucune migration ni suppression de code ancien n'a été nécessaire.

La revue indépendante du contexte a ensuite trouvé un oubli distinct : le clone de handoff dans `ApplyPlannerSelectionMode` ne recopiait pas le nouveau contexte lorsqu'une grille était raffinée. Le coordinateur a confié ce complément à cette sous-tâche. Le nouveau test `Grid_planner_refinement_preserves_non_evidence_conversation_for_the_actual_writer` exerce les deux raffinements `one_per_cell→named_item` et `named_item→content_claim` : le load change effectivement, puis le test exige les tours utilisateur/assistant et la révocation de contrainte dans les vrais payloads Planner et Writer. Le coordinateur a reproduit **2/2 échecs rouges** dans `red-context-clone` : contexte initial présent, contexte Writer null. Le correctif recopie maintenant `ConversationContext` dans le clone, sans modifier son statut non probant. **Le vert de ce complément reste attendu dans la prochaine suite Backend** ; le premier vert de cinq cas B1–B4 ne le couvre pas.

Le garde de scope obligatoire nouvellement introduit est testé comme composant, mais n'a encore aucun producteur WinUI démontré ; il ne faut pas le présenter comme une restriction configurée bout en bout. La suppression de la réimposition d'un simple scope candidat est distincte des autorisations backend.

Les cinq tests ne prouvent ni la qualité d'un modèle, ni le planning de repas, ni un parcours natif supplémentaire. Aucun montant n'a été dépensé par ces corrections. Les garde-fous financiers et le contexte conversationnel relèvent des volets coordonnés séparément. Le statut produit demeure `TESTE_NON_APPROUVE` tant que les preuves globales d'acceptation ne sont pas réunies.
