# A848 — rendre la limite d'extraits à l'orchestrateur

Préenregistrement avant modification produit, contrôles et pilote.

A847 sur 7a4ae4b1 : cinq appels / 0,32866290 USD ; le job
c1d998eb-48b4-44af-a816-9330d1d80254 échoue sur
accumulated_evidence_limit_exceeded. Trente opérations réussies : huit
recherches initiales, vingt lectures et deux recherches littérales ; une recherche
sémantique supplémentaire échoue, trente et un événements.
Les références admises contiennent 504 IDs uniques. Le plafond d'accumulation
est 512 ; déduplication canonique déjà existante, ne pas attribuer cet arrêt
à un simple comptage des retours répétés. Les états historiques transmis
observés étaient sous 16 384 ; aucun effet causal live de 32 768 revendiqué.
Pas de réponse, critique, save ou vingt unités approuvées. Registre clos
38,18713150 USD / 40, reste calculé 1,81286850 USD ; ressources closes.

La recherche qui dépasserait la réserve est entièrement refusée avant admission
de nouveaux extraits. Le problème est le traitement de cette limite comme échec
fatal du job alors que des preuves valides sont déjà admises et des appels de
synthèse restent disponibles. Hypothèse : un retour opérationnel typé permet
au modèle de synthétiser sur la vue canonique restante ou de décrire honnêtement
une limite de recherche, au lieu d'interrompre tout le travail.

Conserver le plafond 512 et toutes les preuves déjà admises. Exposer la capacité
maximale/consommée/restante d'extraits dans le budget réel, nullable pour les
adaptateurs qui ne la connaissent pas. À la limite d'accumulation, le gateway
continue de refuser atomiquement l'admission et de journaliser un événement
failed sans preuve ; ajouter les compteurs de refus à son exception typée.

La boucle de recherche du provider, uniquement après le Planner et pendant
la recherche de synthèse, transforme cette seule limite connue en observation
opérationnelle, sans preuve nouvelle. Les autres erreurs (tenant, révision,
scope, révalidation, protocole, annulation, persistence) continuent à échouer.
Conserver les retours réussis du même batch ; les opérations suivantes sont
explicitement marquées non exécutées, sans I/O. Chaque call ID natif conserve
son retour honnête et tous les états Responses originaux restent intacts.

Suspendre la nouvelle recherche après ce refus et fournir le diagnostic commun
dans la prochaine vue au Writer puis au Critic, avec les preuves actuelles.
Pas de nouvelle allocation ni tentative API offerte : utiliser les réserves
existantes. Pas d'absence du corpus déduite de cette limite. La décision
sémantique finale reste au modèle, dans le contrat et les source guards actuels.
Le Planner initial conserve son comportement existant, pas de promesse de
synthèse sans preuve initiale après refus de sa première collecte.

Test causal avec gateway simulé qui refuse les nouveaux extraits : arrêt fatal
avant correction, puis modèle réellement appelé avec refus lié, preuves
précédentes conservées et terminal prouvé. Deux transports ; cas partiel et
opérations suivantes sans I/O ; erreur de sécurité toujours fatale. PostgreSQL
réel ciblé : plafond intact, refus sans nouveau corpus admis, événement failed,
compteurs exacts ; suites adaptées puis complète. Pas de test mécanique présenté
comme validation de vingt repas.

Un pilote connu après gel propre ; profil A847 inchangé (32 768 historique,
512 extraits, trente-deux opérations, high/8192, low/512, douze appels,
une tentative HTTP, 1,25 USD maximum). Analyser avant toute répétition.
Trois réussites figées exigées avant banc inédit et WinUI. TESTE_NON_APPROUVE.
