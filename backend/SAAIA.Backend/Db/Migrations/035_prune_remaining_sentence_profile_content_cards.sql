DELETE FROM document_profile_content_cards
WHERE
  LOWER(title) ~ '^\(?recommencer(\s|$)'
  OR LOWER(title) ~ '^(est|fonctionne|contournez)(\s|$)'
  OR LOWER(title) ~ '^(ensuite,?|facultatif\)?|avec des|savoureuse)(\s|$|[[:punct:]])'
  OR (
    CARDINALITY(REGEXP_SPLIT_TO_ARRAY(BTRIM(title), '\s+')) >= 4
    AND LOWER(title) ~ '(^|\s)(id[eé]al|appr[eé]ci[eé]|pourrez|utiliser|utilisez|pr[eé]par[eé]s?|programmer|fonctionne|assaisonnant)(\s|$)'
  )
  OR LOWER(title) LIKE '% à votre goût%'
  OR LOWER(title) LIKE '% par portion%'
  OR LOWER(title) LIKE '%ppréparation%'
  OR LOWER(title) LIKE '%ppreparation%'
  OR LOWER(title) LIKE '%ingredientspreparation%'
  OR LOWER(title) LIKE '%ingrédientpréparation%'
  OR LOWER(title) LIKE '%ingrédientspréparation%';
