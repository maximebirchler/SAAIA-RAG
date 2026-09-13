# A786 — réparer une fois les références et le nombre d'unités du Writer

13 septembre 2026. Statut produit : **TESTE_NON_APPROUVE**.

Après A784 et A785, les reprises TLS et FOMC répondent correctement avec leurs
sources. OAuth termine en échec `advanced_writer_evidence_id_invalid` : le
Writer cite au moins une référence inconnue. Sa réponse est rejetée avant
affichage. Ce job a néanmoins consommé trois appels estimés à 0,044904 USD ;
ses compteurs n'étant pas présents dans le résultat terminal client, le coût
est calculé depuis le ledger persistant filtré sur son UUID.

Le backend disposait d'une réparation du JSON Writer pour les marqueurs,
identifiants internes affichés et doublons structurés. Une référence inconnue
ou un nombre incorrect de claims interrompait directement le job. La réparation
unique couvre maintenant aussi ces deux erreurs. Elle reçoit le candidat
comme proposition non validée et les mêmes preuves ; elle doit recopier des
identifiants exacts réellement fournis et soutenir les claims par leur contenu.
Elle ne peut pas fabriquer une référence, choisir le hash le plus proche ni
conserver un fait non soutenu pour préserver la réponse initiale.

Le schéma illustratif `E1` est explicitement décrit comme un exemple, utilisable
seulement si cet ID existe dans la liste de preuves. Les marqueurs de claims,
numéros de pages et lignes ne sont pas des identifiants de preuves. Le prompt
de réparation doit restaurer le nombre d'unités et conserver les axes. Son
instruction de remplacement par des objets distincts est limitée aux sélections
d'objets distincts ; elle ne s'applique pas à un tableau de faits sur des axes
fixés.

Une réparation est possible seulement s'il reste un appel sous le plafond,
avec un appel réservé au critic lorsqu'il est activé. La réparation passe à
nouveau le parser strict. Une référence encore inconnue est rejetée sans
seconde réparation. Les coûts et les limites monétaires continuent de
s'appliquer ; les essais complets de jobs ne sont pas augmentés.

Trois nouveaux scénarios vérifient une référence corrigée, un nombre d'unités
corrigé, et une référence toujours inconnue après la réparation. Les deux
corrections passent ensuite par le critic. Les tests de rejet sans place pour
réparer couvrent deux appels sans critic et trois avec une place réservée au
critic. Le nombre incorrect d'unités reste également rejeté au plafond.

La suite backend Release complète passe : **2 215 réussites, zéro échec,
trois tests live ignorés**. Les tests sont synthétiques et ne consomment aucun
appel OpenAI. La reprise OAuth après A786 reste à exécuter et à vérifier contre
le passage source ; cette modification seule ne valide pas sa réponse.

Preuves : `artifacts/reprise-pc-20260908/a786-bounded-writer-repair-20260913`.
