DELETE FROM document_profile_content_cards
WHERE
  normalized_title ~ '(^|[[:space:]])p(age|ages?)?[[:space:]]*[0-9]+'
  OR normalized_title LIKE '% table des matieres%'
  OR normalized_title LIKE '% table of contents%'
  OR normalized_title LIKE 'temps de preparation%'
  OR normalized_title LIKE 'temps de cuisson%'
  OR normalized_title LIKE 'nombre de personnes%'
  OR normalized_title LIKE 'number of servings%'
  OR normalized_title LIKE 'serving count%'
  OR normalized_title LIKE 'pour vous %'
  OR normalized_title LIKE 'repartir %'
  OR normalized_title LIKE 'répartir %';
