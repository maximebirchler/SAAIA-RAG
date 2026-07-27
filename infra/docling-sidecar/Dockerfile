ARG DOCLING_BASE_IMAGE=quay.io/docling-project/docling-serve-cpu@sha256:a70cd391c5d9e7ad23e8d76adffa5e5f03c536b4d1ea007f512c94e0b435ba45
FROM ${DOCLING_BASE_IMAGE}

ARG SAAIA_DOCLING_SOURCE_REVISION=unknown
LABEL com.saaia.docling.source-revision="${SAAIA_DOCLING_SOURCE_REVISION}"

COPY infra/docling-sidecar/saaia_docling /opt/saaia-docling-sidecar/saaia_docling
COPY infra/docling-sidecar/entrypoint.sh /opt/saaia-docling-sidecar/entrypoint.sh

USER 0
RUN chmod 0555 /opt/saaia-docling-sidecar/entrypoint.sh \
    && find /opt/saaia-docling-sidecar -type d -exec chmod 0555 {} + \
    && find /opt/saaia-docling-sidecar -type f -name '*.py' -exec chmod 0444 {} +

ENV PYTHONPATH=/opt/saaia-docling-sidecar \
    SAAIA_DOCLING_UPSTREAM_URL=http://127.0.0.1:5002

USER 1001
ENTRYPOINT ["container-entrypoint"]
CMD ["/opt/saaia-docling-sidecar/entrypoint.sh"]
