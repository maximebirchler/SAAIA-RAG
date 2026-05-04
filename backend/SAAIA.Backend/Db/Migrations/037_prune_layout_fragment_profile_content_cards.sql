DELETE FROM document_profile_content_cards
WHERE kind <> 'section'
  AND (
    BTRIM(title, ' •·-"''«»') ~ '^[[:lower:]]'
    OR LOWER(BTRIM(title, ' •·-')) ~ '^(materiel|matériel|technique|suggestion|suggestions)([[:space:]]|$|[[:punct:]])'
    OR LOWER(BTRIM(title, ' •·-')) LIKE 'a la fin %'
    OR LOWER(BTRIM(title, ' •·-')) LIKE 'à la fin %'
    OR LOWER(BTRIM(title, ' •·-')) LIKE 'apres %'
    OR LOWER(BTRIM(title, ' •·-')) LIKE 'après %'
    OR LOWER(BTRIM(title, ' •·-')) LIKE 'ceci %'
    OR LOWER(BTRIM(title, ' •·-')) LIKE 'cela %'
    OR LOWER(BTRIM(title, ' •·-')) LIKE 'emportez %'
    OR LOWER(BTRIM(title, ' •·-')) LIKE 'garder %'
    OR LOWER(BTRIM(title, ' •·-')) LIKE 'gardez %'
    OR LOWER(BTRIM(title, ' •·-')) LIKE 'glissez %'
    OR LOWER(BTRIM(title, ' •·-')) LIKE 'manipulez %'
    OR LOWER(BTRIM(title, ' •·-')) LIKE 'on %'
    OR LOWER(BTRIM(title, ' •·-')) LIKE 'pendant ce temps %'
    OR LOWER(BTRIM(title, ' •·-')) LIKE 'pour connaitre %'
    OR LOWER(BTRIM(title, ' •·-')) LIKE 'pour connaître %'
    OR LOWER(BTRIM(title, ' •·-')) LIKE 'pour l %'
    OR LOWER(BTRIM(title, ' •·-')) LIKE 'quellesatisfaction %'
    OR LOWER(BTRIM(title, ' •·-')) LIKE 'rectifiez %'
    OR LOWER(BTRIM(title, ' •·-')) LIKE 'roulez %'
    OR LOWER(BTRIM(title, ' •·-')) LIKE 'saisir %'
    OR LOWER(BTRIM(title, ' •·-')) LIKE 'saisissez %'
    OR LOWER(BTRIM(title, ' •·-')) LIKE 'saupoudrez %'
    OR LOWER(BTRIM(title, ' •·-')) LIKE 'si vous %'
    OR LOWER(BTRIM(title, ' •·-')) LIKE 'voici %'
    OR LOWER(BTRIM(title, ' •·-')) ~ '^(creusez|enfournez|[eé]talez|piquez)([[:space:]]|$)'
    OR (
      title ~ '[[:lower:]]'
      AND (
        title LIKE '%...%'
        OR title LIKE '%…%'
        OR title LIKE '%.%'
        OR title LIKE '%•%'
        OR title LIKE '%€%'
        OR title LIKE '%;%'
        OR title LIKE '%!%'
        OR title LIKE '%?%'
        OR (
          title LIKE '%:%'
          AND (
            CARDINALITY(REGEXP_SPLIT_TO_ARRAY(BTRIM(title), '\s+')) >= 4
            OR title ~ '[0-9]'
          )
        )
      )
    )
  );
