# A850 — ne pas rejeter un corps procédural comme catalogue numérique

Préenregistrement avant changement produit et contrôles.

Le pilote A849 sur `c37f103b` ferme onze appels pour 0,79183200 USD. Ses
trente-deux opérations documentaires réussissent : vingt-cinq recherches et
sept lectures, aucune limite A849 déclenchée. Il produit une grille de vingt
choix et reçoit une correction de support uniquement pour « Charlotte ».
Après correction, il conserve le même choix et la même preuve ; le job échoue
sur `advanced_synthesis_candidate_body_not_supported` avant Critic.

La preuve refusée n'est pas un sommaire : elle contient une liste de matériel,
une section « Technique », plusieurs actions complètes, un repos de quatre à
cinq heures et des suggestions. Le classifieur la marque pourtant `navigation`,
motif `numeric_title_catalog`, vraisemblablement à cause d'un en-tête numérique,
de nombreux marqueurs de liste et de lignes OCR courtes. Le feedback affirme
donc à tort que la preuve ne fait que localiser ou nommer le choix. Changer le
texte du claim ne peut pas réparer ce faux positif.

Correction générale prévue : reconnaître comme contenu un bloc procédural
substantiel comportant assez de mots, une section de procédure, plusieurs
éléments en liste et plusieurs phrases d'action, en l'absence de points de suite
ou de lignes de renvoi de pages. Ne pas utiliser de mot de recette, de titre de
document ou de choix observé. Les vrais sommaires, catalogues de titres et
locateurs de pages conservent leur classification.

Preuve causale : une fixture générique reproduisant la forme OCR doit être
`numeric_title_catalog` avant correction, puis `content` après correction et ne
plus être une preuve d'identité seule. Rejouer les tests du classifieur, des
matériaux de source, des projections/navigation, les régressions advanced et la
suite backend complète. Aucun appel payant avant gel et analyse des contrôles.

Le pilote A849 reste rejeté et ne compte pas comme réussite. Aucun résultat
terminal, Critic, audit physique des vingt cartes, inédit ou WinUI. Registre
39,24170860 USD / 40 ; reste calculé 0,75829140 USD. Le produit reste
`TESTE_NON_APPROUVE`.
