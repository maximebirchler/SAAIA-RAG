DELETE FROM document_profile_content_cards
WHERE
  LOWER(title) ~ '^(ajouter|ajoutez?|add|au bout de|couper|coupez?|cut|dans le robot|decorer|décorer|disposer|enlever|ensuite|faites?|fermer|filtrer|gouter|goûter|lancez?|laissez?|melanger|mélanger|mettez?|mettre|mixez?|nettoyer|ouvrir|placer|preparer|préparer|programmer|puis|quand|raclez?|recommencer|remplacez?|retirer|salez?|servir|triturer|verser|versez?|verifier|vérifier)(\s|$)'
  OR LOWER(title) ~ '^(ce|cela|celle|celui|cette|elle|elles|facultatif\)?|il|ils|it|pour cette|pour le|pour la|pour les|se|sel,?\s|si vous|this|vous)(\s|$)'
  OR (
    CARDINALITY(REGEXP_SPLIT_TO_ARRAY(BTRIM(title), '\s+')) >= 5
    AND LOWER(title) ~ '(^|\s)(est|sont|doit|doivent|peut|peuvent|pouvez|permet|permettent|recommande|recommandons|utilisez|utiliser|trouver|trouvez|preparez|préparez|melangez|mélangez|ajoutez|ouvrez|fermez|retirez|servez|is|are|can|must|should|allows?|use|uses|using|prepare|prepared|serves?)(\s|$)'
  )
  OR LOWER(title) LIKE '%ingredientspreparation%'
  OR LOWER(title) LIKE '%ingrédientpréparation%'
  OR LOWER(title) LIKE '%ingrédientspréparation%'
  OR LOWER(title) LIKE '%ingredients preparation%'
  OR LOWER(title) LIKE '%par portion%'
  OR LOWER(title) LIKE '%ppréparation%'
  OR LOWER(title) LIKE '%ppreparation%'
  OR LOWER(title) LIKE '% à votre goût'
  OR LOWER(title) LIKE '%be a master%'
  OR LOWER(title) LIKE '%become a chef%'
  OR title ~ '[[:lower:]][[:upper:]]'
  OR title ~ '[[:upper:]]{2,}[[:upper:]][[:lower:]]+'
  OR title ~ '[[:lower:]][0-9]'
  OR title ~ '[[:alpha:]][0-9]{1,3}$'
  OR LOWER(title) ~ '[[:alpha:]]{28,}';
