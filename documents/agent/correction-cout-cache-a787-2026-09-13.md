# A787 — Comptabiliser les écritures du cache avant les prochains essais Terra

Le backend ignorait `cache_write_tokens` dans les réponses du fournisseur. Pour Terra, une écriture de cache coûte 1,25 fois le tarif d'entrée ordinaire. Les lectures et écritures doivent donc être retirées de l'entrée ordinaire et tarifées séparément. Source : https://developers.openai.com/api/docs/models/gpt-5.6-terra.

Le parseur lit maintenant ce compteur, conserve les valeurs inconnues, et accepte les compteurs JSON nuls sans exception. Le résultat expose un total uniquement lorsque toutes les réponses disposent de ce compteur. Le validateur refuse les compteurs négatifs ou les lectures et écritures dépassant ensemble l'entrée connue.

Les réservations OpenAI considèrent conservativement que toute entrée non mise en cache peut être écrite. Si le fournisseur omet le compteur, la clôture conserve cette majoration et marque `provider_usage_cache_write_upper_bound`. Un compteur explicitement nul signifie inconnu ; un compteur égal à zéro permet le calcul ordinaire. Le tarif reste configurable pour les autres fournisseurs et vaut par défaut leur tarif d'entrée. La localisation du LLM et le routage restent indépendants de cette correction.

## Preuves mécaniques

Les cas ciblés ont d'abord révélé quatre échecs de tarification et de conservation des compteurs. Un cas supplémentaire avec compteur JSON nul a révélé une exception du parseur ; elle est corrigée. Suite finale : backend 2 222 réussites, zéro échec, trois tests d'intégration non exécutés ; client 2 295 réussites, zéro échec, un test réel non exécuté. Cela ne constitue pas une approbation sémantique du produit.

Preuves : `artifacts/reprise-pc-20260908/a787-cache-write-budget-20260913/backend-a787-v2.trx`, `client-a787.trx` et `accounting-fixture-proof.json`. Le rapprochement comptable a été vérifié sur un registre fictif : précision décimale, conservation du préfixe historique, répétition sans nouvelle écriture, refus d'une observation périmée et d'un montant dépassant les crédits achetés. Aucun appel API pour ces vérifications.

## Budget effectivement disponible

Observation en lecture seule le 13 septembre, vers 12 h 12 : OpenAI affiche 4,74 USD de crédits, recharge automatique désactivée. La consommation d'organisation précédemment observée est 25,26 USD. Le registre de 862 lignes d'appels estimait 21,78460516 USD. L'origine de tout l'écart n'est pas démontrée : les écritures manquantes peuvent y contribuer, et une consommation d'organisation ne donne pas une facture par appel.

`tools/reconcile-llm-dev-billing.ps1` ajoute uniquement une ligne distincte `billing_reconciliation`, qui ne représente aucun appel API : 3,47539484 USD. Le plancher comptable devient 25,26 USD ; les anciennes lignes restent intactes. Une seconde invocation n'ajoute rien. Le rapprochement relève seulement un plancher ; il ne rembourse pas des estimations conservatrices.

Les prochains essais utilisent un plafond comptable de 30 USD correspondant aux 25 puis 5 USD déjà achetés et à l'autorisation d'épuiser les crédits restants. Ce n'est pas une recharge ni une augmentation des dépenses autorisées. Le disponible observé reste 4,74 USD avant le prochain essai. Limite par diagnostic : 0,40 USD, sept appels maximum, une seule tentative de job, raisonnement `low`, critique sémantique activée. Les achats supplémentaires restent interdits.

## Validation réelle encore requise

La correction A786 du Writer est testée mécaniquement. Le prochain essai porte uniquement sur OAuth BH6-015, déjà divulgué, afin de vérifier les identifiants de preuves, les cinq étapes et leurs deux attributs, le critique et les sources physiques/canoniques. TLS et FOMC ont déjà répondu correctement lors du diagnostic A785 ; ils ne sont pas rejoués pour chaque modification indépendante.

Le verdict aveugle BH6 original demeure inchangé. Les diagnostics de cas divulgués ne produisent aucun nouveau score d'acceptation aveugle. Avant une nouvelle banque, son constructeur et son évaluateur devront aussi intégrer les écritures de cache dans leur comptabilité. Le produit reste TESTE_NON_APPROUVE.
