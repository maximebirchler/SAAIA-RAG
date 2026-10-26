# Harnais d'ingestion : publication, recherche et dépendances

État à la préparation du lot : code des outils gelé, exécution centralisée par
l'orchestrateur. Aucun service, build .NET, appel HTTP ou arrêt de processus
n'a été effectué par l'agent chargé de ce lot.

## Corrections préparées

- Le démarrage E5 contrôle le SHA256 réel des poids avant le premier service,
  puis l'identité complète du runtime est comparée au profil observé.
- Le stop valide manifestes, spécifications, PID, dates CIM/processus,
  exécutables, lignes de commande, parentés et ports avant mutation. Il arrête
  les enfants exacts, attend la sortie des wrappers, puis contrôle l'absence
  des processus et ports. Les manifestes antérieurs à quatre rôles restent
  compatibles. Une identité inconnue ou une attente expirée bloque le succès.
- La sonde vectorielle v2 refuse les vecteurs constants et ex aequo ; trois
  permutations doivent donner la bonne correspondance avec une marge minimale
  de 0,001. Le profil de modèle attendu est indépendant de `/info`. La
  collection de contrôle est neuve et son absence après nettoyage est vérifiée.
- Le runner exporte le plan vectoriel actif du document. Le vérificateur
  calcule séparément l'UUID Qdrant dérivé de l'UUID canonique et du plan complet,
  suivant l'ordre d'octets du Guid .NET. Point, payload, document et job terminé
  doivent correspondre. Les vérités SQL et citations gardent l'UUID canonique.
- `-VerifyCanonicalSearch` réalise une recherche canonique bornée par document
  et page à partir d'un fait critique indépendant, deux recherches négatives,
  puis la lecture du chunk trouvé. Le vérificateur compare identité et texte
  des hits aux données SQL autoritaires, et refuse la dégradation silencieuse.
- Le schéma `ingestion_real_acceptance_v1` conserve la provenance d'un PDF
  original : hash, annotations indépendantes et pages physiques. Aucun
  générateur ou pin de rendu synthétique n'est déclaré pour ce schéma.

## Preuves déjà rapportées par l'orchestrateur

La première version des gardes de dépendances a passé 33 cas offline ; le stop
NOEXEC a validé les quatre rôles de dependencies-v5 sans arrêt. La sonde
vectorielle v2 réelle a réussi. Ces preuves figées ne sont pas réécrites.

Le contrôle SHA pré-démarrage ajoute trois cas offline à cette base. Les
témoins UUID/plan actif et recherche-vers-lecture sont nouveaux et attendent
la suite centralisée du lot courant. Les vecteurs de référence Python ne sont
pas présentés ici comme une exécution indépendante du code C#.

## Limites conservées

Les captures synthétiques des tests Python n'exécutent ni OCR, ni worker, ni
SQL, ni Qdrant. Le contrôle de recherche canonique est une régression littérale
sur des faits annotés ; il ne mesure pas le rappel dense général. La petite
sonde vectorielle sémantique ne valide pas à elle seule les recherches du
corpus documentaire. Transformers CPU est un adaptateur local, pas le binaire
TEI. Le contrôle de sortie des wrappers laisse leur boucle de copie des logs
se terminer ; il ne constitue pas une attestation de synchronisation disque.

La première campagne complète doit être exécutée sans `-ValidateLifecycle`.
Le hook de cycle de vie est limité au scan synthétique jetable ; son helper
est livré et revu séparément. Les observations privées, bases, PDF et secrets
synthétiques ne sont pas automatiquement sélectionnés pour Git.

Le statut produit demeure `TESTE_NON_APPROUVE` jusqu'aux preuves d'acceptation
réelles du périmètre annoncé.
