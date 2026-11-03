ALTER TABLE advanced_analysis_tool_events DROP CONSTRAINT ck_advanced_analysis_tool_event_name;
ALTER TABLE advanced_analysis_tool_events ADD CONSTRAINT ck_advanced_analysis_tool_event_name
  CHECK (tool_name IN ('source_backed_canonical_search', 'source_backed_canonical_read',
                       'source_backed_canonical_find', 'source_backed_canonical_navigation'));
