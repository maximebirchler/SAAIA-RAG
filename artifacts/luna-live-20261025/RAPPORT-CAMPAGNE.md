# SAAIA — reprise économique des essais OpenAI, 25 octobre 2026

## Périmètre et état

Ce rapport complète `artifacts/global-audit-paid-20261025/RAPPORT-FINAL.md`, qui décrit l'audit et les corrections hors ligne publiés dans `b3b0511` puis `842ab11`. Les preuves antérieures n'ont pas été réécrites. Les résultats ci-dessous concernent une nouvelle campagne locale ; ils ne remplacent ni une validation native WinUI ni une acceptation du planning de repas.

L'autorisation courante permet de remplacer la clé SAAIA et de tester avec un modèle payant au maximum de génération 5.6. Cette campagne choisit uniquement `gpt-5.6-luna`, sans changement automatique de modèle. Son plafond cumulé est de 0,50 USD, premier échec compris, sans achat de crédits. Le client reste configuré pour son modèle local. Aucun serveur distant ni PostgreSQL habituel sur 5432 n'est modifié.

État de cette campagne : **clôturée sur le périmètre décrit ci-dessous**. La priorité suivante, confirmée par l'utilisateur, est l'audit et la fiabilisation de l'ingestion complète et de l'OCR. Les essais payants sont suspendus pendant ce chantier. Le profil du cas 04 est une proposition non exécutée.

## Clé remplacée

L'ancienne clé renvoyait `401 expired_secret_key`. Une nouvelle clé dédiée SAAIA a été créée dans le projet existant, avec permissions restreintes aux modèles et à la génération utilisée par le backend. Elle est conservée dans le magasin DPAPI de l'utilisateur Windows ; aucune valeur de clé n'est dans le dépôt, les profils de campagne ou les preuves publiques. L'interface indique une expiration au 1er novembre 2026.

Le contrôle gratuit `GET /v1/models/gpt-5.6-luna` a renvoyé 200 avec l'identifiant exact attendu. Ce contrôle ne prouve pas le solde ni le quota de génération. Les appels payants suivants, eux, ont réellement abouti au fournisseur.

## Premier essai réel : échec utile, pas une preuve d'incapacité du modèle

Question sur le vrai PDF : pour combien de personnes les ingrédients du bœuf bourguignon sont-ils prévus, et quelle quantité de vin rouge faut-il ? L'attente vérifiée dans le document est **4 personnes et 1,5 litre, page physique 5**.

Le worker a terminé son exécution, mais le résultat `insufficient_documentation` constitue un échec sémantique pour ce cas. Deux appels Luna, une tentative HTTP chacun, ont coûté **0,0009242 USD selon le journal local et les tarifs figés**. Aucune réservation n'est restée ouverte. Le solde et la facture fournisseur ne sont pas réconciliés par ce journal.

Les traces exactes montrent un Planner valide puis un Review valide demandant une reformulation. Aucun passage du PDF n'a été fourni au modèle. Les trois recherches ont renvoyé zéro résultat brut. Le code a ensuite retourné l'insuffisance avant l'appel du Writer. Luna n'a donc pas échoué à comprendre une recette qu'on lui aurait donnée : il ne l'avait pas reçue.

Les recherches initiales mélangeaient nom complet du fichier et contenu attendu ; le code ajoutait lui-même une recherche de ce type. La troisième recherche, produite par Luna, avait déjà retiré le nom du fichier. Il fallait expliquer son zéro séparément.

## Cause du troisième zéro, isolée sans appel payant

Une matrice gratuite a rejoué les trois recherches dans une base neuve, avec trois filtres : chemin, chemin avec catégorie, identifiant documentaire. Les données et requêtes du premier essai ont été conservées ; le diagnostic n'a créé aucun job avancé.

Dans le cluster en locale `C`, le parseur PostgreSQL 16.14 traite `ingrédients…` comme un seul lexème. Le mot `ingrédients` est visuellement présent, mais son lexème seul est absent de l'index. Les cinq autres termes de la troisième requête correspondent. La conjonction de six termes échoue donc dans les trois variantes de filtre.

Le témoin avec `COLLATE unicode` appliqué aux expressions n'a rien corrigé : cette hypothèse a été testée et rejetée. Le parseur dépend du `LC_CTYPE` de la base. Une nouvelle base UTF-8, en locale libc Windows `English_United States.1252`, retrouve la page avec la requête inchangée dans les trois filtres. Des témoins génériques vérifient aussi les majuscules accentuées, le tiret long et l'ellipse ; les recherches à accent différent et la fausse conjonction restent négatives.

Cette cause est prouvée pour notre banc de test. La locale de la base du serveur habituel n'a pas été vérifiée ni modifiée. Aucun changement de collation, migration ou réindexation distante n'est prétendu réalisé.

## Autres défauts corrigés ou en validation

- Le document demandé devient un filtre `documentHint`, séparé de la requête de contenu. La requête ciblée du modèle est prioritaire ; la recherche mécanique redondante ne la déplace plus.
- La recherche directe peut examiner le résultat d'une première reformulation et poursuivre dans les plafonds existants. Elle conserve la réserve nécessaire à la rédaction et, si activé, au contrôle. Les doublons et le budget documentaire épuisé arrêtent la boucle.
- Les retours au modèle distinguent résolution documentaire, résultats des phases de récupération et retrievers dégradés. Ces diagnostics sont compacts et sans copies de snippets. Un chemin résolu n'accorde aucun droit de lecture ni `sourceKey` inventé.
- Le message sans preuve décrit l'échec à retrouver un passage vérifié, sans affirmer l'absence de la réponse dans le document.
- La préparation précédente du corpus utilisait du texte brut de page et des candidats exacts simplifiés. La nouvelle préparation réutilise les composants C# de production pour extraction native, sections, unités, chunks, candidats exacts et contextualisation. Le helper est précompilé et son backend doit correspondre au hash figé de la campagne. Python transporte et vérifie ces objets ; il ne réimplémente pas leurs algorithmes.
- Le transport SQL de diagnostic sous Windows conserve l'UTF-8 dans des fichiers dédiés, sépare stdout et stderr et borne l'exécution. Les premières tentatives de diagnostic incomplètes restent conservées ; elles ne comptent pas comme des validations.

La projection native reste une projection contrôlée sur des pages sélectionnées. Elle ne valide pas le worker complet d'ingestion, Docling, l'OCR, les profils documentaires ou le rappel vectoriel. TEI/Qdrant restent des substituts locaux explicitement déclarés dans cette campagne.

## Vérifications et résultat final

Les dernières sources compilées passent **5 104 tests hors ligne, aucun échec ni test ignoré par l'adaptateur** : backend 2 583, contrats 20, cœur client 82, agent client 2 419. Le manifeste `tests/unit-final-v7/manifest.json` confirme l'absence de modification des sources pendant l'exécution. Il contient aussi les sept cas de contexte physique et d'ordre, les seize cas de conservation de contexte, les sept cas de sonde, six nouveaux cas de transport du contexte physique et le test de titre historique renommé. Un filtre intermédiaire conservait son ancien nom et ne l'avait pas exécuté ; il n'est donc pas compté dans cette sélection intermédiaire. L'audit d'ingestion suivant a déjà repéré un ancien test OCR pouvant retourner sans lancer son traitement : ce total ne constitue donc surtout pas une preuve d'OCR réel.

Le contrôle d'architecture final examine 1 637 sources : **aucune violation, 21 fichiers au seuil de revue de taille**. Le helper natif et le backend ont été reconstruits normalement en Release, sans avertissement ni erreur. Cette campagne n'a pas rejoué l'interface WinUI native ; les tests de l'agent client ne sont pas une preuve d'interaction graphique.

Le témoin réel `native-fidelity-final-v2.json` vérifie **14 faits sur 14 à tous les étages**, sur les pages physiques sélectionnées. Les colonnes et les phrases inclinées sont conservées ; les indications courtes atteignent les chunks publiés. Les titres attestés du PDF bornent à nouveau leurs propres sections. Les fenêtres physiques supplémentaires déclarent explicitement `section_association=not_asserted`, gardent leurs unités et offsets et ne créent pas de titre. Leur ordre a été corrigé après reproduction d'un retour de la page 3 à la page 2 : navigation physique et indices restent maintenant cohérents.

Le préflight réel final `native-preflight-v3/corpus-preflight.json` passe aussi : catalogue d'un document, trois résultats canoniques, lecture des trois chunks de la page 5, identité documentaire/révision/hash exacts et faits présents dans la page physique elle-même. La soumission avancée renvoie 202 puis le job synthétique est annulé avec worker désactivé ; aucun secret payant n'est lu et aucun appel fournisseur n'est enregistré. Les helpers sont arrêtés, aucun job actif ne subsiste. Les quatre sentinelles du parseur Unicode passent.

Les contrôles de campagne passent : **57 gardes de dépense, d'exécution et de conformité SQL**, 21 gardes de transport/provenance, 12 tests Python dont le vrai helper natif et la fidélité des octets SQL écrits. Aucun de ces tests ne dépense de crédit.

La suite PostgreSQL finale `tests/postgres-final-v3` passe **275 tests et conserve deux skips historiques explicitement motivés**, sans échec ni modification des sources. Les deux nouveaux cas prouvent le transport de métadonnées physiques sur la route `ChunkId`. Les routes carte, ancre et agrégat de pages ont aussi été relues ; leurs nouveaux marqueurs ne disposent pas chacun d'un nouveau témoin PG dédié. Le cluster dédié a été arrêté et supprimé ; aucun listener ne reste et la base habituelle n'a pas été modifiée. Le produit demeure **TESTE_NON_APPROUVE**.

## Essais Luna réussis et limites découvertes dans leurs traces

Le cas 02 répond : « Les ingrédients sont prévus pour 4 personnes et il faut 1,5 l de vin rouge. » La preuve citée appartient à la révision attendue, page 5. La première recherche du modèle était trop conjonctive et ne trouvait rien ; le contrôleur a effectivement reçu le diagnostic, demandé une recherche plus simple, obtenu trois preuves puis demandé la rédaction. Quatre appels, deux recherches, aucun retry HTTP ; **8 049 ms et 0,0029062 USD estimés**.

Le cas 03 compare deux pages : **2 h 45 pour le bœuf bourguignon page 5, 20 minutes pour la tartiflette page 6, différence 2 h 25**. Le modèle a décomposé la recherche, reformulé les requêtes puis demandé une lecture canonique des pages 6–7. Les deux preuves citées correspondent aux pages attendues. Quatre appels, cinq événements d'outils, aucun retry HTTP ; **7 442 ms et 0,00295646 USD estimés**. Il s'agit de deux passages d'un même PDF, pas d'une validation de synthèse entre plusieurs documents.

**Coût cumulé de toute cette campagne : 0,00678686 USD estimés**, premier cas échoué compris, pour dix appels fournisseur au total. Toutes les réservations sont réglées localement et tous les environnements sont arrêtés. Le solde et la facture OpenAI ne sont pas réconciliés par ces journaux.

La relecture des requêtes exactes a révélé deux défauts malgré ces réponses correctes. Premièrement, les marqueurs de contexte physique présents dans l'index étaient perdus par le résolveur d'évidence : la portée et la réserve d'association ne parvenaient pas au modèle. Le correctif relit les champs déjà stockés, les transmet avec des codes bornés et une consigne commune, et empêche `read_source` / `find_source_text` de transformer la première ligne non attribuée en titre exact. Les RED ont reproduit cinq échecs Unit et deux échecs PostgreSQL ; leurs validations finales sont vertes.

Deuxièmement, l'écriture Python du SQL en mode texte Windows transformait des CRLF en CRCRLF après calcul des checksums. Les huit chunks et huit contextes des cas 02/03 étaient concernés ; les réponses restaient lisibles et justes, mais l'identité du PDF ne suffisait pas à certifier l'intégrité des octets textuels. L'écriture est maintenant binaire UTF-8. Le préflight final recalcule en PostgreSQL les SHA256 de **133 unités, 8 chunks, 8 contextes et 169 entrées exactes normalisées** : aucun écart. Un témoin négatif réel altère volontairement une ligne dans sa propre base ; la garde détecte exactement un écart et refuse la suite avec **zéro job et zéro appel fournisseur**. Les preuves de ces deux essais ne sont pas réécrites pour masquer leur limite historique.

Les derniers correctifs de marqueurs et d'octets ont été validés hors ligne et dans les outils canoniques réels. Aucun nouvel appel LLM n'a été effectué après eux : l'utilisateur a confirmé la priorité au chantier d'ingestion complète. Le cas 04 reste donc non exécuté.

## Ce que la préparation native a révélé avant toute nouvelle dépense

La première préparation native a retrouvé le document et des passages, mais a échoué au contrôle des faits : les informations présentes dans le PDF disparaissaient encore en amont du modèle. Ce préflight a été arrêté sans secret fournisseur ni appel payant. Il ne constitue donc pas une réussite du parcours.

Plusieurs filtres confondaient répétition, texte court ou métadonnées avec bruit : indications répétées sur plusieurs pages, durées, qualifications, avertissements et faits d'un seul mot pouvaient disparaître. Les corrections distinguent maintenant conservation dans le contexte et utilité d'un candidat autonome. Les contrôles contre du texte véritablement corrompu ou une page physiquement trop pauvre restent distincts. Les anciens tests qui demandaient une suppression de faits valides sont réconciliés explicitement ; ils ne sont pas présentés comme des régressions corrigées sans explication.

Le PDF contient aussi des colonnes et un encart légèrement incliné. La segmentation initiale fragmentait les mots inclinés en lettres ; un filtre OCR supprimait ensuite ces lettres. Le choix d'une autre segmentation est désormais conditionné par la conservation exacte des caractères de la source, y compris casse, nombres et ponctuation. La présence de groupes minoritaires n'autorise plus leur disparition. Dix témoins géométriques passent, mais une nouvelle observation sur le vrai PDF montre encore un entrelacement de colonnes et un ordre incorrect dans l'encart : cette limite réelle reste à corriger, malgré ces témoins verts.

Enfin, les indications placées avant le premier titre de la page sont attribuées à la section précédente. Le correctif ne doit pas inventer une association avec le titre suivant. Leur conservation comme contexte de page avec provenance physique exacte est en cours d'examen. Les titres présents dans les métadonnées de section sont distingués du texte effectivement perdu.

Le contrôle global intermédiaire `unit-final-v4` a exécuté **2 534 tests : 2 530 réussis, 4 échoués, aucun ignoré**, sans mutation des sources pendant l'exécution. Ces quatre échecs sont analysés, pas masqués par un résultat global positif. La fidélité du corpus réel et une nouvelle validation complète restent nécessaires avant le prochain appel Luna.

## Suite logique

La priorité est maintenant un audit puis une validation réelle de l'ingestion complète : fichiers PDF natifs, scannés et mixtes, OCR, structure, reprise de jobs, publication SQL/vectorielle, fidélité et diagnostics. Les premiers constats sont suivis séparément dans `artifacts/ingestion-ocr-audit-20261025/` ; ils ne sont pas prétendus corrigés par cette campagne native. Des pertes potentielles subsistent notamment dans les filtres Docling et les politiques de remplacement OCR. Le protocole Gold et ses critères numériques doivent également être durcis.

Ensuite seulement : reprendre les essais LLM avec ce corpus réellement ingéré, puis éprouver le planning complet et le parcours conversationnel. Aucun de ces niveaux ne sera déduit d'un simple HTTP 200, d'un worker terminé, d'une projection sur quatre pages ou d'un test ayant retourné sans exécuter son traitement.
