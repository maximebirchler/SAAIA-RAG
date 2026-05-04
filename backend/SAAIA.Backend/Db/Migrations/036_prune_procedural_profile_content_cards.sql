DELETE FROM document_profile_content_cards
WHERE kind <> 'section'
  AND (
    LOWER(BTRIM(title, ' •·-')) ~ '^(abaissez|ajoutez?|arrosez|assaisonnez|badigeonnez|battez|beurrez|choisissez|couvrez|d[eé]coupez|d[eé]posez|dressez|[eé]crasez|[eé]gouttez|enduisez|[eé]pluchez|garnissez|incorporez|lavez|m[eé]langez|p[eé]trissez|pr[eé]chauffez|ramenez|recouvrez|remettez|r[eé]servez|sortez)(\s|$)'
    OR (
      LOWER(BTRIM(title, ' •·-')) ~ '^(arroser|badigeonner|casser|cuire|d[eé]poser|[eé]plucher|faire|garnir|hacher|incorporer|laisser|laver|m[eé]langer|porter|r[eé]aliser|recouvrir|r[eé]server|rincer)(\s|$)'
      AND title ~ '[[:lower:]]'
      AND (
        CARDINALITY(REGEXP_SPLIT_TO_ARRAY(BTRIM(title), '\s+')) >= 5
        OR title ~ '[\.;:•]'
      )
    )
  );
