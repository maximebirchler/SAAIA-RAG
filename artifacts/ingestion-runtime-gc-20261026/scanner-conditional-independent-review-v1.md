# Contre-revue indépendante du gel scanner/delete conditionnel

Lecture seule du gel `scanner-delete-case-source-freeze-v1.json`, SHA `33045340b4a047e63e15e4ffe1874b3379635474ac09015b1b0b7daeeb8875d7`. Les12 fichiers ancrés ont été comparés aux octets présents :12/12 conformes, aucune divergence. Le relecteur n’a exécuté ni .NET, service, SQL, HTTP, SSH ou Git et n’a modifié aucun fichier du lot relu.

## Conclusion

Aucun nouveau défaut matériel identifié dans le delta gelé. Cette conclusion porte sur la revue du code et des tests prêts ; elle n’anticipe pas leur GREEN central ni le scénario Linux automatique.

La provenance de suppression est transportée explicitement dans le payload et IngestionJob. Les deletes scanner d’absence seuls exigent une source absente. Le worker vérifie d’abord l’exécution courante, puis fournit un callback de présence à CompleteDeleteAsync, invoqué après l’attente des verrous SQL du job et du document. Present/Unknown interrompent la transaction avant désactivation et avant retrait des plans. Les deletes explicites et de déduplication conservent leur contrat volontairement distinct.

La source revenue prend une nouvelle génération réservée hors verrous. La transition JobRepo vérifie propriétaire, bail, tenant/path, docId et version ; elle annule uniquement le delete possédé, préserve l’ancienne publication/révision/hash/plan et insère le nouvel upsert dans la même transaction. Un échec d’INSERT annule l’ensemble ; le trou de génération réservé est conservé sans réutilisation. Un document déjà avancé suit la voie superseded sans écraser la publication suivante.

L’intention conditionnelle d’une demande plus récente coalescée dans un delete running est associée à sa version. La reprise ne copie donc pas aveuglément l’intention de l’ancienne demande : les deux directions true→false et false→true sont testées. Un ancien job sans annotation reste false ; le code ne déduit aucune provenance à partir d’un statut missing.

Le heartbeat reconnaît seulement le terminal canceled de ce propre jeton avec la raison scanner_source_returned_before_delete, en plus des terminaux déjà connus. L’exception typée Unknown remonte après l’arrêt/observation du heartbeat, puis provoque un report sous bail ; la transition ne libère pas le job pendant qu’un monitor tenterait encore de le renouveler.

## Tests prêts et portée

Le lot inclut le test du vrai ProcessDeleteAsync sur PostgreSQL, avec ancienne publication et manifeste scellé, ainsi que les témoins explicite-présent et conditionnel-absent. Les contrôles complémentaires vérifient le callback sous verrou documentaire, rollback sur présence/état inconnu, rollback d’INSERT, refus du propriétaire A après reprise B et maintien de l’intention de version courante.

Ces tests ne sont pas des moteurs réels et cette revue ne les a pas exécutés. Le coordinateur possède les RED et lancera les GREEN. Le protocole Linux automatique doit observer séparément les événements de fichiers, les admissions, les consommateurs et les publications.

## Limites conservées

- La double observation ferme la fenêtre de queue et réobserve après attente SQL ; elle ne crée pas de transaction atomique filesystem↔PostgreSQL. Un retour physique après la dernière observation reste une course du système de fichiers, traitée par les événements/versionnements suivants.
- Les anciennes queues sans annotation ne sont pas reclassables sûrement comme scanner : avant upgrade/activation, les drainer/inspecter selon le protocole opérateur. Aucun ancien writer n’est qualifié par ce correctif.
- La fairness d’inventaire est prouvée sur parcours lisible ; une erreur persistante dans une sous-arborescence peut réinitialiser le curseur et retarder d’autres branches, avec état incomplete et suppressions différées.
- La politique Linux/non-Windows Ordinal et Windows OrdinalIgnoreCase du cache, de RetainOnly et du planner est cohérente dans les sources lues. Son résultat moteur/PG reste celui des captures centrales ; il n’est pas inféré de cette seule revue.
