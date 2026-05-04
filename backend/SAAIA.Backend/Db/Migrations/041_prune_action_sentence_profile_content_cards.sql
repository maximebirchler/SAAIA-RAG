DELETE FROM document_profile_content_cards
WHERE kind <> 'section'
  AND (
    normalized_title ~ '^(a l aide|cassez|farinez|incorporez|melangez|repartissez|ramenez|farcir|epaissir|suivant le|pour des preparations)([[:space:]]|$)'
  );
