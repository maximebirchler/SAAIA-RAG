import asyncio
import threading
import unittest

from saaia_docling.transport_diagnostics import ConversionTransportDiagnostics


class ConversionTransportDiagnosticsTests(unittest.TestCase):
    def test_failure_after_response_received_cannot_remain_success(self):
        counters = ConversionTransportDiagnostics()
        with self.assertRaises(OSError):
            with counters.observe() as received:
                received(True)
                raise OSError("stream/client closure failed")
        snapshot = counters.snapshot()
        self.assertEqual((snapshot["success"], snapshot["failed"], snapshot["active"]), (0, 1, 0))

    def test_completed_and_failed_outcomes_are_distinct_and_private(self):
        counters = ConversionTransportDiagnostics()
        self.assertEqual(0, counters.snapshot()["active"])
        with counters.observe() as completed:
            self.assertEqual(1, counters.snapshot()["active"])
            completed(True)
        with counters.observe() as rejected:
            rejected(False)
        with self.assertRaisesRegex(ValueError, "private document"):
            with counters.observe():
                raise ValueError("private document")
        snapshot = counters.snapshot()
        self.assertEqual((0, 3, 3, 1, 1, 1), tuple(snapshot[key] for key in
                         ("active", "started", "finished", "success", "upstream_rejected", "failed")))
        self.assertNotIn("private document", str(snapshot))
        self.assertFalse(snapshot["upstreamModelExecutionCancellationProven"])

    def test_concurrent_threads_are_counted_until_each_work_item_finishes(self):
        counters = ConversionTransportDiagnostics()
        entered = threading.Barrier(3)
        release = threading.Event()

        def work():
            with counters.observe() as completed:
                entered.wait(timeout=5)
                release.wait(timeout=5)
                completed(True)

        threads = [threading.Thread(target=work) for _ in range(2)]
        for thread in threads:
            thread.start()
        try:
            entered.wait(timeout=5)
            self.assertEqual(2, counters.snapshot()["active"])
        finally:
            release.set()
            for thread in threads:
                thread.join(timeout=5)
                self.assertFalse(thread.is_alive())
        self.assertEqual(2, counters.snapshot()["finished"])
        self.assertEqual(0, counters.snapshot()["active"])


class CanceledWaiterTests(unittest.IsolatedAsyncioTestCase):
    async def test_canceling_the_waiter_does_not_hide_a_forwarding_thread_still_running(self):
        counters = ConversionTransportDiagnostics()
        entered = threading.Event()
        release = threading.Event()

        def work():
            with counters.observe() as completed:
                entered.set()
                release.wait(timeout=5)
                completed(True)

        pending = asyncio.create_task(asyncio.to_thread(work))
        try:
            self.assertTrue(await asyncio.to_thread(entered.wait, 5))
            pending.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await pending
            self.assertEqual(1, counters.snapshot()["active"])
        finally:
            release.set()
        # The test's event loop drains its executor on shutdown. The counter
        # is deliberately not forced to zero by cancellation of its waiter.
