# Plan d'action — assainissement de l'architecture et des sondes

Départ : `89912f9d`. Demande autorisant l'implémentation : 24 octobre 2026.
Référence : `audit-structure-et-sondes-2026-10-24.md`.

## Objectif vérifiable

Corriger les constats A01–A07, V01–V05 et S01–S25, et démontrer les comportements par des tests hors ligne et PostgreSQL isolé. Aucun appel payant ni déploiement serveur n'est nécessaire pour ce chantier. Le statut d'acceptation sémantique du produit reste distinct.

La perfection absolue n'est pas un critère testable. La sortie de ce chantier exige : points d'audit explicitement soldés ou limite documentée, mesures honnêtes, parcours corrélables, erreurs/annulations/reprises diagnostiquées, frontières de code renforcées, suppressions justifiées, tests réellement exécutés et dépôt commité/synchronisé.

## Lots et critères d'acceptation

1. **Fiabiliser les tests et mesures.** Profils explicites Unit/IntegrationPg/LiveLocal/LivePaid, vrais skips ou prérequis bloquants, manifeste incluant commit/scénarios ; aucun paid implicite. Corriger faux TTFT/débit/reprise. Tests des fonctions réellement exécutées avec transport simulé.
2. **Installer le contrat de diagnostic commun.** Identifiants de tour/trace/parent/job, durées monotones, issue/cause, métadonnées effectives et limites de collecte. Historique de tours borné indépendant de l'orchestrateur ; `lastN` réel ; temps de bout en bout distinct du temps local. Capture modèle best-effort en production, strict explicite en campagne.
3. **Couvrir les frontières réelles.** HTTP même sur erreur, propagation et stockage job, SQL transitions/retry/leases, worker vivant, disponibilité par capacité, startup/migrations, ingestion/OCR/index, polling, parsing et ouverture source, export support avec manifeste. La sonde doit indiquer indisponible/partiel, pas inventer un succès.
4. **Extraire les responsabilités.** Recherche hors endpoints avec contexte explicite, sans faux HttpContext ; composants de recherche séparés par fonction. Transport/polling avancé autonome, noyau client portable pour les composants purs ; état/transitions explicites. Pipeline d'ingestion par étapes avec résultats et mesures propres.
5. **Assainir et protéger.** Suppressions uniquement après preuve de non-usage (incluant XAML/réflexion/profils). Commentaires conformes au comportement. Profils historiques clairement classés. Aucun fichier source de 10 000 lignes ; limites progressives pour nouvelles concentrations et dépendances interdites. Les fichiers de tests géants sont répartis par familles sans réimplémentation ni perte de scénarios.
6. **Intégrer et vérifier.** Builds Backend/Client/Core/Contracts, suites Unit et IntegrationPg isolée, scénarios erreur/annulation/reprise, tests de sondes sur événements émis, vérification des suppressions et métriques de structure après changement. Relecture indépendante, commits par lots, push et rapport en français dans le chat/Telegram.

## Exécution

- Pistes parallèles : client/diagnostic ; backend/jobs/ingestion ; validation/outillage ; recherche et intégration.
- Fichiers de tests existants : piste validation. Chaque autre piste ajoute ses tests comportementaux dédiés.
- Une seule intégration finale des builds ; aucun écrasement du travail partagé.
- Les déplacements de code préservent d'abord le comportement. Les changements de politiques/prompts ne sont pas mélangés à ce chantier.
- Le registre `suivi-assainissement-2026-10-24.json` conserve, par point d'audit, l'état, les preuves, les tests et limites. Aucun point n'est déclaré terminé sur la seule présence d'un log.
- Les sondes sont des événements structurés à étapes significatives, pas un log à chaque ligne. Les contenus sensibles sont exclus par défaut ; capture explicite et rétention bornée si nécessaire.
- Rapports Telegram : changements et résultats utiles, au moins une fois par heure pendant le travail prolongé ; rapport final autoportant.

## Contrôle des risques

Pas de suppression sur simple absence d'occurrence ; pas de changement d'ancienne migration appliquée ; pas de facture fournisseur pour les tests ; pas de confusion entre succès hors ligne et acceptation sémantique Terra. Les migrations nouvelles sont additives et testées dans une base isolée.

État initial : **EN_COURS**. Aucun point n'est encore soldé par ce plan seul.
