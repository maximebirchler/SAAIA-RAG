# A851 — Replay causal du Critic sur le planning A849

## Décision avant appel

Le solde OpenAI visible le 18 septembre 2026 est de 0,58 USD, avec recharge
automatique désactivée. Le pilote complet A849 a consommé 0,79183200 USD en
onze appels et n'a pas atteint le Critic. Le répéter intégralement ne peut donc
pas établir une nouvelle preuve complète dans le solde restant.

A851 isole l'étape encore inconnue : la capacité du Critic Terra à relire le
candidat terminal A849 après la correction A850. Le Planner et le Writer sont
rejoués localement à coût nul à partir de leurs sorties capturées. Seul le
Critic, et au maximum sa réparation de protocole, est envoyé à Terra.

## Fixture et confidentialité

La fixture privée est extraite des traces locales A849 par
`tools/prepare-meal-critic-replay.py`. Elle contient le résultat Planner, le
candidat Writer, les 53 preuves qui étaient visibles et la forme de la demande.
Elle exclut les en-têtes d'autorisation, la clé API, le raisonnement chiffré,
l'historique natif et les enveloppes brutes du fournisseur. La fixture et les
traces du replay restent sous `artifacts/` et ne sont pas versionnées.

L'envoi des preuves privées à OpenAI est couvert par l'autorisation explicite
de l'utilisateur pour la campagne Terra et l'utilisation de tous les crédits
achetés. Aucune recharge ni acquisition de crédit n'est autorisée ou réalisée.

## Garde-fous préenregistrés

- candidat Git propre et poussé avant l'appel payant ;
- niveau OpenAI Tier 1 relu en interface, Terra à 500 000 TPM, 500 RPM et
  900 000 TPD ;
- dépense organisation visible : 39,42 / 100 USD ; solde : 0,58 USD ;
- registre local rapproché à 39,42 USD avant l'appel ;
- plafond initial A851 : 0,20 USD ; un seul Critic live et, seulement si son
  JSON est invalide, une réparation live dans le même plafond ;
- réponses Planner et Writer synthétiques avec usage fournisseur nul et
  inscription explicite de coût nul dans le registre ;
- un seul job, quatre appels fournisseur maximum en comptant les deux replays ;
- traces de développement locales et sans en-tête d'autorisation ;
- aucun verdict produit avant lecture sémantique du résultat.

## Critères de lecture

Le replay est mécaniquement valide si A850 accepte le candidat capturé, si le
troisième appel est bien un Critic Terra et si le résultat final respecte le
protocole de vingt cellules distinctes avec preuves visibles.

La qualité n'est pas déduite de ce succès mécanique. L'audit vérifie en
particulier si Terra conserve ou corrige les placements discutables du candidat
A849 : `Charlotte` au petit-déjeuner, `Coulis de framboises` comme collation,
et les plats légers ou accompagnements utilisés seuls. Il vérifie aussi que le
Critic ne remplace pas ces choix par des inventions, qu'il conserve vingt choix
distincts et qu'il ne présente pas l'organisation de SAAIA comme une
classification imposée par les documents.

Le produit reste `TESTE_NON_APPROUVE`, quel que soit le résultat d'un replay
unique. Le solde restant après A851 sera attribué seulement après diagnostic du
résultat, à une correction ciblée ou à une mesure complémentaire qui puisse
changer une décision d'architecture.
