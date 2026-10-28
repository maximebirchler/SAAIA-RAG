"""Real FastAPI/HTTPX ASGI wiring; synthetic upstream, no network/PDF/model work.

Run in the pinned image, whose application dependencies already exist. The pure
stdlib response tests remain runnable on developer machines without FastAPI.
"""
import asyncio
import importlib.util
import json
import unittest
from unittest.mock import patch

AVAILABLE = (all(importlib.util.find_spec(name) for name in ("fastapi", "httpx"))
             and (importlib.util.find_spec("multipart") or importlib.util.find_spec("python_multipart")))
if AVAILABLE:
    import httpx
    from saaia_docling import app as APP
    from saaia_docling.response_spool import PayloadDiagnostics, PayloadWorkspace, spool_upstream
    from saaia_docling.text_repair_options import TextRepairOptions
    from saaia_docling.transport_diagnostics import ConversionTransportDiagnostics


@unittest.skipUnless(AVAILABLE, "Run ASGI integration tests in pinned image with existing FastAPI/HTTPX/multipart.")
class ResponseSpoolApplicationTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.workspaces, self.upstream_clients, self.streams = [], [], []
        self.client_type = httpx.AsyncClient
        self.patches = [patch.object(APP, "payload_diagnostics", PayloadDiagnostics()),
                        patch.object(APP, "conversion_transport", ConversionTransportDiagnostics()),
                        patch.object(APP, "conversion_slot", asyncio.Semaphore(1)),
                        patch.object(APP, "REPAIR_ENABLED", False),
                        patch.object(APP, "TEXT_REPAIR", TextRepairOptions(enabled=False)),
                        patch.object(APP, "conversion_execution_metadata", return_value={"fixture": "configuration-only"}),
                        patch.object(APP, "PayloadWorkspace", side_effect=self.workspace)]
        for item in self.patches:
            item.start(); self.addCleanup(item.stop)

    def workspace(self, diagnostics):
        workspace = PayloadWorkspace(diagnostics)
        self.workspaces.append(workspace)
        return workspace

    def streamed_response(self, raw, *, status=200, content_type="application/json", pending=None, headers=None, fail_stream_close=False):
        owner = self

        class Stream(httpx.AsyncByteStream):
            closed = False

            async def __aiter__(self):
                if pending:
                    pending.set()
                    await asyncio.Event().wait()
                for offset in range(0, len(raw), 8192):
                    yield raw[offset:offset + 8192]

            async def aclose(self):
                self.closed = True
                if fail_stream_close:
                    raise httpx.ReadError("synthetic stream closure failure")

        class NoBufferedAccess(httpx.Response):
            @property
            def content(self):
                raise AssertionError("Production forwarding must not buffer .content")

            def json(self, **kwargs):
                raise AssertionError("Production forwarding must not call Response.json()")

        stream = Stream(); owner.streams.append(stream)
        return NoBufferedAccess(status, headers={"content-type": content_type, **(headers or {})}, stream=stream)

    async def request(self, handler, *, form=None, fail_client_close=False):
        transport = httpx.MockTransport(handler)
        base_client = self.client_type

        class ClientCloseFailure(base_client):
            async def __aexit__(self, *args):
                await super().__aexit__(*args)
                raise httpx.ReadError("synthetic client closure failure after transport close")

        def upstream_client(*args, **kwargs):
            client_class = ClientCloseFailure if fail_client_close else base_client
            client = client_class(*args, transport=transport, **kwargs)
            self.upstream_clients.append(client)
            return client

        async with self.client_type(transport=httpx.ASGITransport(app=APP.app), base_url="http://wrapper.invalid") as client:
            with patch.object(APP.httpx, "AsyncClient", side_effect=upstream_client):
                return await client.post("/v1/convert/file", files={"files": ("synthetic.pdf", b"no real PDF was converted", "application/pdf")},
                                         data=form or {})

    def assert_clean(self):
        self.assertTrue(self.workspaces)
        self.assertTrue(all(not workspace.root.exists() for workspace in self.workspaces))
        self.assertTrue(all(client.is_closed for client in self.upstream_clients))
        self.assertTrue(all(stream.closed for stream in self.streams))
        self.assertEqual(APP.conversion_transport.snapshot()["active"], 0)
        self.assertFalse(APP.conversion_slot.locked())

    async def test_success_uses_stream_and_exact_starlette_json_bytes(self):
        original = {"status": "success", "errors": [], "document": {"json_content": {
            "texts": [{"text": "é😀"}], "pages": {"1": {"image": {"uri": "data:image/png;base64," + "A" * 1024**2}}}}}}
        raw = json.dumps(original, ensure_ascii=False).encode("utf-8")

        async def handler(request):
            self.assertEqual(request.headers.get("accept-encoding"), "identity")
            self.assertIn(b'filename="synthetic.pdf"', request.content)
            return self.streamed_response(raw)

        response = await self.request(handler)
        expected = {**original, "saaia_execution": {"fixture": "configuration-only"}}
        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.content, APP.JSONResponse(expected).body)
        self.assertEqual(int(response.headers["content-length"]), len(response.content))
        self.assert_clean()

    async def test_non_success_preserves_status_media_type_and_raw_body(self):
        async def handler(request):
            return self.streamed_response(b"not JSON: upstream capacity", status=429, content_type="text/plain")

        response = await self.request(handler)
        self.assertEqual(response.status_code, 429)
        self.assertEqual(response.content, b"not JSON: upstream capacity")
        self.assertTrue(response.headers["content-type"].startswith("text/plain"))
        self.assert_clean()

    async def test_invalid_json_and_failed_serialization_never_emit_http200(self):
        for raw in (b'{"document":', b'{"status":"success","bad":NaN}'):
            async def handler(request):
                return self.streamed_response(raw)

            response = await self.request(handler)
            self.assertEqual(response.status_code, 502)
            self.assert_clean()

    async def test_declared_oversize_is_refused_before_consumption(self):
        async def handler(request):
            return self.streamed_response(b"{}", headers={"content-length": str(APP.MAXIMUM_RESPONSE_BYTES + 1)})

        response = await self.request(handler)
        self.assertEqual(response.status_code, 502)
        self.assertIn(str(APP.MAXIMUM_RESPONSE_BYTES), response.json()["detail"])
        self.assert_clean()

    async def test_actual_overflow_is_refused_even_if_header_lies(self):
        async def bounded(chunks, workspace):
            return await spool_upstream(chunks, workspace, maximum=32)

        async def handler(request):
            return self.streamed_response(b"x" * 64)

        with patch.object(APP, "spool_upstream", side_effect=bounded):
            response = await self.request(handler)
        self.assertEqual(response.status_code, 502)
        self.assert_clean()

    async def test_upstream_timeout_closes_client_and_cleans_private_workspace(self):
        async def handler(request):
            raise httpx.ReadTimeout("synthetic timeout; no real server")

        response = await self.request(handler)
        self.assertEqual(response.status_code, 502)
        self.assert_clean()
        self.assertEqual(APP.conversion_transport.snapshot()["failed"], 1)

    async def test_stream_closure_failure_is_not_counted_as_success(self):
        async def handler(request):
            return self.streamed_response(b'{"status":"success"}', fail_stream_close=True)

        response = await self.request(handler)
        self.assertEqual(response.status_code, 502)
        self.assert_clean()
        self.assertEqual((APP.conversion_transport.snapshot()["failed"], APP.conversion_transport.snapshot()["success"]), (1, 0))

    async def test_client_closure_failure_is_not_counted_as_success(self):
        async def handler(request):
            return self.streamed_response(b'{"status":"success"}')

        response = await self.request(handler, fail_client_close=True)
        self.assertEqual(response.status_code, 502)
        self.assert_clean()
        self.assertEqual((APP.conversion_transport.snapshot()["failed"], APP.conversion_transport.snapshot()["success"]), (1, 0))

    async def test_canceling_request_closes_stream_client_and_files(self):
        entered = asyncio.Event()

        async def handler(request):
            return self.streamed_response(b"{}", pending=entered)

        task = asyncio.create_task(self.request(handler))
        await entered.wait(); task.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await task
        self.assert_clean()
        self.assertFalse(APP.conversion_transport.snapshot()["upstreamModelExecutionCancellationProven"])

    async def test_existing_heading_contract_still_rejects_before_forwarding(self):
        async def handler(request):
            self.fail("Mismatched controls must not reach upstream")

        wrong = "false" if APP.HEADING_HIERARCHY.enabled else "true"
        response = await self.request(handler, form={"saaia_heading_hierarchy_enabled": wrong})
        self.assertEqual(response.status_code, 409)
        self.assertEqual(self.workspaces, [])


if __name__ == "__main__":
    unittest.main()
