# A871 — incohérence entre l'identité OCR de l'inventaire et celle du résultat final

## Verdict

A871 s'arrête dans le Candidate Explorer avec
`advanced_native_tool_protocol_invalid`. Le provider OpenAI a pourtant terminé
la réponse et Terra a envoyé un lot de vingt candidats, cinq par rôle. Le rejet
provient de la normalisation locale de SAAIA.

Deux titres documentaires étaient répartis par la mise en page OCR sur des
segments non contigus. Le modèle les a reconstruits dans le bon ordre, avec la
bonne source et les bons EvidenceId, mais le parseur Candidate Inventory
exigeait encore que tous les mots forment une sous-chaîne contiguë.

## Coût

A871 a consommé trois appels :

- Planner : 0,00516240 USD ;
- Explorer initial : 0,07599250 USD ;
- Explorer suivi 1 : 0,10186800 USD, réponse provider reçue puis rejetée par la
  normalisation locale.

Le total A871 est 0,18302290 USD. Le registre global contient 1 142 écritures,
totalise 40,89260930 USD et laisse 4,10739070 USD jusqu'au plafond de 45 USD.
Son SHA-256 est
`AF67E3471E729ABF85A1BB8CF28E5C7A76A5D967511854412F9E2230E5B46CF0`.

## Deux rejets exactement reproduits

Le premier corps contenait le titre du sandwich sous la forme :

```text
SANDWICH
/pers.
COMPLET ET ÉQUILIBRÉ
SELON VOS ENVIES
```

Le second corps plaçait `Mousse yaourt` avant un bloc d'ingrédients et
`et fruits rouges` après le marqueur `Page 11`. Dans les deux cas, les titres
reconstruits par Terra respectaient les mots et leur ordre documentaire.

Le moteur de validation finale savait déjà reconnaître de manière bornée :

- certains petits tokens de service OCR au milieu d'un titre ;
- un titre divisé autour d'un marqueur de page explicite.

Candidate Inventory utilisait une vérification différente et plus naïve. Le
même titre pouvait donc être admis dans la réponse finale mais refusé au moment
où l'Explorer voulait le mémoriser.

## Correction et validation

Le parseur Candidate Inventory utilise désormais la reconnaissance d'identité
documentaire bornée déjà appliquée au résultat final. La source opaque doit
toujours correspondre, l'EvidenceId doit être actuellement visible, le corps ne
peut pas être un simple localisateur et l'ordre des mots reste obligatoire.

Deux tests reprennent exactement les motifs observés : le token `/pers.` et le
titre séparé par `Page 11`. La suite provider réussit maintenant 303 tests sur
303, zéro échec.

Le corpus postflight est identique au préflight, la configuration est restaurée,
le backend temporaire est arrêté, les ports sont libres et aucun job avancé ne
reste actif. Les trois traces privées sont conservées sous
`artifacts/reprise-pc-20260908/a871-terra-candidate-explorer-pilot-20260928-183210/`.
Le produit reste `TESTE_NON_APPROUVE`.
