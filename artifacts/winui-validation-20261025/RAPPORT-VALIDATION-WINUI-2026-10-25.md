# Validation réelle du client WinUI — 25 octobre 2026

## Décision à ce stade

Le parcours principal a été exercé dans la vraie fenêtre Windows, les défauts reproduits ont été corrigés et la suite finale passe **2 458 tests**. La matrice compte **21 parcours validés dans leur périmètre, 10 partiels avec variantes déclarées et 2 suppressions en attente de confirmation**. L'annulation d'une suppression a été vérifiée ; la confirmation utilisateur demandée pendant la campagne n'a pas encore été reçue.

**Le parcours complet n'est donc pas déclaré approuvé. Terra n'a pas été appelé, le planning de repas n'a pas été retesté et le produit reste `TESTE_NON_APPROUVE`.** La matrice finale distingue les parcours observés, les variantes partielles et les opérations en attente. Les variantes complémentaires ne deviennent pas validées par déduction des tests unitaires.

Lecture détaillée : [matrice publiable avec provenance des occultations](execution-matrix-public.json) et [explication des 33 parcours](notes-final.md).

## Ce qui a réellement été exécuté

- Client WinUI normal Release x64, sans hooks de test, piloté dans sa fenêtre native.
- Backend de production et PostgreSQL de test isolés sur cette machine. Aucun changement au serveur client ou à la base PostgreSQL habituelle sur le port 5432.
- Deux PDF synthétiques portant le même nom, avec chemins, pages et empreintes distincts. La recherche documentaire réellement traversée utilise SQL/BM25 ; la fixture vectorielle renvoie volontairement des vecteurs vides. Ce lot ne valide donc pas la recherche vectorielle réelle.
- Qwen3-4B-Instruct-2507 réellement exécuté sur cette machine pour les réponses documentaires directes. Modèle et runtime existants utilisés en lecture seule, sans téléchargement.
- Fournisseur HTTP déterministe local pour les contrats de streaming et l'analyse avancée. Le vrai worker, les jobs et leur persistance ont été exercés ; le texte final de ces analyses est contrôlé. Cela ne prouve pas la qualité sémantique d'un grand modèle.
- Aucun appel Terra, OpenAI, RunPod ou autre fournisseur payant. Les profils de test imposent `Local` et `ProductionLocal` et retirent les variables fournisseur héritées.

## Défauts corrigés et conséquence pour l'utilisateur

| Défaut constaté | Correction et résultat observé |
|---|---|
| Les paramètres pouvaient s'ouvrir pendant un tour et modifier ses réglages. | Admission partagée et verrou maintenu pendant le dialogue ; configuration et paramètres indisponibles pendant le travail actif. |
| Avec la connexion automatique désactivée, le client pouvait rester sans action exploitable. | Écran « Client déconnecté » avec configuration et connexion manuelle ; reprise vérifiée sans changer cette préférence. |
| Une nouvelle URL pouvait être enregistrée alors que les appels continuaient vers l'ancien serveur. | Reconnexion effective et remise à zéro des objets, messages et sources de l'ancienne connexion ; une adresse inaccessible échoue explicitement, puis l'adresse corrigée fonctionne. Les suivis déjà engagés conservent leur propre connexion. |
| Un échec de sauvegarde de la question effaçait le brouillon et affichait une bulle donnant l'impression d'un envoi réussi. | Confirmation de persistance obligatoire avant modèle et affichage ; brouillon conservé, avertissement visible, aucun modèle lancé avant confirmation et aucun retry automatique. |
| Une écriture lente pouvait maintenir l'interface bloquée sans borne claire ; le bouton d'annulation avait un état trompeur. | Délai de confirmation de 30 secondes, état « Annulation en cours… » réellement désactivé, libération du tour et texte récupérable. Une sauvegarde tardive reste possible et n'est pas présentée comme absente. |
| Un échec de chargement de discussion était peu visible. | Alerte persistante et localisée ; ancien historique conservé, puis chargement correct après rétablissement. Même canal d'erreur pour création, renommage et suppression. |
| Le chemin documentaire précis était réduit au nom du fichier, y compris dans un adaptateur non couvert par les anciens tests. | Transport du chemin canonique à travers routage, réparation et intake ; restauration d'un chemin explicite seulement s'il est unique. Les homonymes ne sont pas choisis arbitrairement. |
| L'ouverture d'une révision citée introuvable affichait un diagnostic trop imprécis. | « Version citée indisponible », cause diagnostique dédiée et refus sûr. L'absence de chemin peut être récupérée uniquement si la révision exacte est retrouvée par son empreinte. |
| Quand le modèle local était absent, un texte technique apparaissait et certaines sondes concluaient à un succès. | Panne typée à la frontière du fournisseur local, message exploitable et propagation de l'échec terminal jusqu'à `turn.end`. Les échecs intermédiaires récupérés et l'insuffisance documentaire attendue restent distincts. |
| Les tests et les diagnostics pouvaient utiliser le profil habituel. | Racine explicite isolée pour le client et pour les tests ; secrets DPAPI, préférences, journaux, modèles gérés et support suivent cette racine. Le comportement normal reste inchangé sans cette option. |

Les sondes ajoutées suivent les identités de connexion, de discussion, de tour, de job et de document, ainsi que les longueurs et indicateurs de transport des chemins. Elles ne recopient pas librement le contenu documentaire dans les métadonnées.

## Résultats qui apportent une preuve concrète

### Question documentaire avec Qwen réel

Question : « Dans le document “Validation/Guide-Validation.pdf”, quel est le code ORION ? Cite la page. »

Avant correction, le chemin de 31 caractères devenait un nom de 20 caractères et la résolution trouvait deux homonymes. Neuf tests de composition échouaient avant le correctif. Après correction, le chemin reste de 31 caractères, une seule identité exacte est retenue et Qwen répond **4826 [E2]**, avec le guide Validation à la page 2. Sur le premier cas réussi, le traitement dure **12,435 secondes avec neuf appels locaux**, sans analyse avancée.

Le PDF réellement ouvert a été observé à **2 sur 2**, avec le marqueur attendu. Son SHA-256 correspond à la révision citée. Le guide Archive homonyme porte le code 9999 et n'a pas été ouvert à sa place. Après la correction finale de gestion de panne, une seconde réponse Qwen réelle retrouve de nouveau 4826 et la page 2.

Preuves : [chaîne causale documentaire](native-qwen-document-path-causal-proof/proof.json), observations natives 65–67 et 86–88 dans [le journal d'observations](native-ui/observations.jsonl).

### Persistance, annulation et délai

Un POST utilisateur retardé de 45 secondes a été testé. Le client revient au bout d'environ 30 secondes avec le brouillon et l'avertissement ; aucun appel modèle n'a lieu. La base confirme ensuite une seule écriture tardive utilisateur, sans assistant et sans retry automatique. Ce résultat ne garantit pas l'idempotence d'un futur retry manuel après réponse inconnue : cette limite est explicitement conservée.

Preuve : [contrôle de délai](local-fixture-02/persistence-deadline-proof.json). Les erreurs HTTP initiales, l'annulation pendant sauvegarde, l'annulation de génération, le rechargement et l'envoi clavier unique possèdent leurs observations séparées.

### Analyse avancée durable

Une fermeture réelle pendant un job puis un redémarrage reprennent le même job et la même bulle. Le changement de discussion n'envoie pas la réponse dans une autre conversation. Le contrôle durable confirme l'absence de recréation.

Un autre job réel a reçu, par un proxy local ciblé, deux réponses 503, une révision régressive et une identité de discussion incohérente. Le client rejette les instantanés invalides, reprend le suivi et persiste finalement le vrai terminal de révision 4 dans une seule bulle. Les douze autres discussions du snapshot restent identiques.

Preuves : [reprise après fermeture](local-fixture-02/advanced-resume-proof.json), [défauts de polling, 12 contrôles réussis](local-fixture-02/advanced-polling-faults-proof.json). Le fournisseur avancé reste une fixture : aucune réussite du planning n'en est déduite.

### Modèle absent puis récupération

L'endpoint local volontairement fermé produit une fin bornée, un message indiquant de vérifier le démarrage puis de contacter l'intégrateur si nécessaire, aucune source inventée et une issue diagnostique `failed/local_model_unavailable`. Le test négatif ne devient pas une réponse documentaire réussie.

L'adresse a ensuite été corrigée depuis Configuration ; « Tester /v1/models » trouve Qwen. Un nouveau tour dans la même instance du client répond correctement avec sa source. Cette récupération valide l'usage d'un service local existant ; elle ne simule pas une réinstallation complète sur une machine vierge.

### Sources, présentation et aide

Deux assistants contrôlés dans une même discussion conservent chacun leur propre source, extrait, page et empreinte. Les chemins absents, empreintes incorrectes et véritable fichier momentanément absent sont distingués. Le PDF déplacé temporairement a été restauré byte pour byte.

L'aide consulte le catalogue réel de deux catégories et deux documents sans modèle pour la commande déterministe. Le changement de langue et d'apparence, la fermeture des paramètres sans appliquer, le renommage et son annulation, l'affichage normal/maximisé et une fenêtre de **1036 × 641** ont été observés. Le défilement volontaire pendant un long streaming et certaines variantes de nommage restent complémentaires non exécutées.

## Build, tests et support finaux

| Contrôle | Résultat |
|---|---|
| Build normal Release x64, lot 08 | Réussi ; 0 avertissement, 0 erreur ; 22,08 secondes |
| SHA-256 de l'assembly réellement lancé | `B69435EFD628A9C7DA1F2A9677CA5D75AC4C3D3FE45A2E8A79208C8F675E16DA` |
| Client Core Unit | 80 / 80 |
| Client WinUI / ToolAgent Unit | 2 378 / 2 378 |
| Total final | **2 458 réussis, 0 échec, 0 ignoré** |
| Sources pendant la suite | Empreinte avant/après identique ; `SourceChangedDuringRun=false` |
| Support exporté depuis la vraie UI finale | 14 contrôles réussis ; 51 entrées, 0 pièce en échec, 0 secret connu ou texte source/chat trouvé |

La suite s'exécute hors réseau payant, avec `NoRestore`, sorties testhooks AnyCPU distinctes du binaire normal x64 et profil isolé. [Résumé de la suite finale](full-client-unit-build08-acceptance-summary.json), [manifeste](final-core-client-unit-build08/manifest.json), [build](production-fixes-build-08.log), [inspection support](local-fixture-02/support-bundle-review-build08-final.json).

Le dernier support comprend 1 tour dans son historique mémoire depuis ce redémarrage et 1 007 événements de sondes issus du journal. Les pièces optionnelles absentes sont déclarées ; leur absence n'est pas masquée. Le ZIP brut, les profils, secrets, base et captures incidentes d'autres applications restent locaux et exclus de Git.

Une limite de diagnostic subsiste : ce ZIP contient les événements de l'ancienne analyse et de sa reprise, mais pas leur champ `Configuration.originTraceId/jobId`. L'historique mémoire a été renouvelé au redémarrage. Le lien est prouvé séparément par les métadonnées persistées et la preuve SQL ; ce ZIP seul ne suffit donc pas à reconstruire cette relation. Il ne faut pas confondre ce manque d'autonomie du support avec une reprise de job perdue.

L'assistant d'erreur du cas W06 est durable, avec sa consigne finale, mais sans `Sources` et donc sans diagnostic lié dans ce champ. Sa trace d'échec reste disponible dans le journal. La réponse Qwen de récupération possède, elle, les identifiants et la source canonique persistés. [Preuve W06 finale](w06-native-build08-final/proof.json), [limite du support](support-advanced-origin-chain-final-review.json).

Les exécutions rouges et rejetées restent conservées comme historique : ancien build bloqué par un fichier verrouillé, ancien test de modèle géré incompatible avec le profil isolé, premier contrôle de seed ayant mal compté un tableau JSON. Ce dernier avait déjà créé exactement deux messages ; la lecture a été corrigée et vérifiée sans rejouer les POST. Aucun de ces essais n'est ajouté au total accepté.

## Isolation et honnêteté des preuves

Le contrôle du profil habituel compte 556 fichiers. Un ancien lancement de tests, avant l'isolation de l'outil, a ajouté 10 913 octets au journal `client_startup.log`. Les 555 autres fichiers et le préfixe original de ce journal sont identiques ; `secure.json` est inchangé. Depuis correction de l'isolation, ce journal reste lui aussi identique. Il n'a pas été restauré ou tronqué pour masquer cet incident.

La fermeture volontaire du premier client pendant une analyse a laissé une trace d'origine sans événement terminal. Le vérificateur strict doit continuer à signaler cette trace incomplète. Une comptabilité séparée la relie à la fermeture observée et à la reprise, sans inventer de fin et sans utiliser un assouplissement général. Les comptes finaux et les preuves d'arrêt se trouvent dans les rapports associés à la matrice finale.

Au scellement : **1 007 sondes, 139 tours, une interruption volontaire expliquée, zéro tour incomplet inexpliqué et zéro autre échec structurel**. Les 43 requêtes du proxy partagé sont rapprochées de leur véritable profil et de leurs identifiants, sans mélanger les périmètres. [Revue finale](final-readonly-review.json), [vérificateur strict conservé avec son rejet attendu](trace-integrity-build08-final.json), [comptabilité de l'interruption](trace-interruption-accounting-final.json).

Le client et les auxiliaires de test sont arrêtés : dix ports fermés, journaux des wrappers conservés, aucune donnée supprimée et PostgreSQL habituel toujours présent. [Preuve d'arrêt, 8 contrôles réussis](local-fixture-02/owned-stop-proof-final.json).

Le code et l'outillage sont commités sous `90eee645537b1ff9d8345d09972967ccb93c6189`. Les 97 fichiers de ce commit correspondent aux sources courantes sous les règles de normalisation Git. [Lien sources, tests et binaire](source-delivery-provenance.json). Les preuves privées originales restent locales ; leur projection publiable conserve leur provenance et signale toute occultation.

## Ce qui reste avant le test Terra

1. Recevoir la confirmation pour supprimer **uniquement** les discussions fictives de cette campagne, puis vérifier suppression non active, active et dernière discussion. L'annulation seule a déjà été exercée.
2. Clore cette porte dans la matrice et compléter les variantes nécessaires à une approbation exhaustive : la matrice détaille les 10 parcours partiels, dont redémarrages exacts de certains profils, annulation après deltas déjà affichés et défilement pendant streaming. Les variantes non exécutées ne sont pas réputées acquises.
3. Ensuite seulement, préparer un test Terra borné du planning avec inspection des entrées, outils, sources, coût et critères d'acceptation. Ce futur test n'est pas exécuté dans ce lot.

Cette validation apporte des preuves du fonctionnement des chemins exercés et des corrections. Elle ne permet pas de déclarer le logiciel parfait, tout le corpus validé ou les limites du petit modèle entièrement mesurées.
