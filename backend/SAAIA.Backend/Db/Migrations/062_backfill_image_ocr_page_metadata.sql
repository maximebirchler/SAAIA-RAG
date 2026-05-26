WITH latest_image_page_diagnostics AS (
  SELECT DISTINCT ON (run.tenant_id, run.revision_id, parsed.page_number)
    run.tenant_id,
    run.revision_id,
    parsed.page_number,
    NULLIF(diag.item->>'status', '') AS image_ocr_status,
    NULLIF(diag.item->>'reason', '') AS image_ocr_reason,
    CASE
      WHEN COALESCE(diag.item->>'ocrWordCount', '') ~ '^-?[0-9]+$'
        THEN (diag.item->>'ocrWordCount')::int
      ELSE NULL
    END AS image_ocr_word_count,
    CASE
      WHEN COALESCE(diag.item->>'ocrCharCount', '') ~ '^-?[0-9]+$'
        THEN (diag.item->>'ocrCharCount')::int
      ELSE NULL
    END AS image_ocr_char_count,
    CASE
      WHEN COALESCE(diag.item->>'exitCode', '') ~ '^-?[0-9]+$'
        THEN (diag.item->>'exitCode')::int
      ELSE NULL
    END AS image_ocr_exit_code,
    CASE
      WHEN lower(COALESCE(diag.item->>'timedOut', '')) IN ('true', 'false')
        THEN (diag.item->>'timedOut')::boolean
      ELSE NULL
    END AS image_ocr_timed_out
  FROM document_processing_runs run
  CROSS JOIN LATERAL jsonb_array_elements(
    CASE
      WHEN jsonb_typeof(run.payload #> '{ocrDiagnostics,imagePageDiagnostics}') = 'array'
        THEN run.payload #> '{ocrDiagnostics,imagePageDiagnostics}'
      ELSE '[]'::jsonb
    END
  ) AS diag(item)
  CROSS JOIN LATERAL (
    SELECT CASE
      WHEN COALESCE(diag.item->>'pageNumber', '') ~ '^[0-9]+$'
        THEN (diag.item->>'pageNumber')::int
      ELSE NULL
    END AS page_number
  ) parsed
  WHERE run.action = 'upsert'
    AND run.status = 'done'
    AND run.revision_id IS NOT NULL
    AND parsed.page_number IS NOT NULL
    AND NULLIF(diag.item->>'status', '') IS NOT NULL
  ORDER BY
    run.tenant_id,
    run.revision_id,
    parsed.page_number,
    run.finished_at DESC NULLS LAST,
    run.started_at DESC NULLS LAST,
    run.created_at DESC
)
UPDATE document_page_index page
SET metadata = (COALESCE(page.metadata, '{}'::jsonb)
    - 'imageOcrStatus'
    - 'imageOcrReason'
    - 'imageOcrWordCount'
    - 'imageOcrCharCount'
    - 'imageOcrExitCode'
    - 'imageOcrTimedOut')
  || jsonb_strip_nulls(jsonb_build_object(
      'imageOcrStatus', latest.image_ocr_status,
      'imageOcrReason', latest.image_ocr_reason,
      'imageOcrWordCount', latest.image_ocr_word_count,
      'imageOcrCharCount', latest.image_ocr_char_count,
      'imageOcrExitCode', latest.image_ocr_exit_code,
      'imageOcrTimedOut', latest.image_ocr_timed_out))
FROM latest_image_page_diagnostics latest
WHERE page.tenant_id = latest.tenant_id
  AND page.revision_id = latest.revision_id
  AND page.page_number = latest.page_number
  AND (
    COALESCE(page.metadata->>'imageOcrStatus', '') IS DISTINCT FROM COALESCE(latest.image_ocr_status, '')
    OR COALESCE(page.metadata->>'imageOcrReason', '') IS DISTINCT FROM COALESCE(latest.image_ocr_reason, '')
    OR COALESCE(page.metadata->>'imageOcrWordCount', '') IS DISTINCT FROM COALESCE(latest.image_ocr_word_count::text, '')
    OR COALESCE(page.metadata->>'imageOcrCharCount', '') IS DISTINCT FROM COALESCE(latest.image_ocr_char_count::text, '')
    OR COALESCE(page.metadata->>'imageOcrExitCode', '') IS DISTINCT FROM COALESCE(latest.image_ocr_exit_code::text, '')
    OR COALESCE(page.metadata->>'imageOcrTimedOut', '') IS DISTINCT FROM COALESCE(latest.image_ocr_timed_out::text, '')
  );
